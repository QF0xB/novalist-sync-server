using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Access;

public class UserDirectoryTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetOrCreate_FirstLogin_CreatesUserWithServerGeneratedV7Id()
    {
        var subject = Guid.NewGuid().ToString();
        await using var db = database.CreateContext();

        var user = await new UserDirectory(db).GetOrCreateAsync(Principal(subject), Ct);

        Assert.Equal(7, user.Id.Version);
        Assert.Equal(TestTokens.Issuer, user.Issuer);
        Assert.Equal(subject, user.Subject);
    }

    [Fact]
    public async Task GetOrCreate_LaterLogin_ReturnsTheSameUser()
    {
        var subject = Guid.NewGuid().ToString();

        var first = await GetOrCreateAsync(Principal(subject));
        var second = await GetOrCreateAsync(Principal(subject));

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task GetOrCreate_SameSubjectFromAnotherIssuer_IsAnotherUser()
    {
        var subject = Guid.NewGuid().ToString();

        var first = await GetOrCreateAsync(Principal(subject));
        var second = await GetOrCreateAsync(TestTokens.Principal(("iss", "https://other-idp.test"), ("sub", subject)));

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task GetOrCreate_StoresVerifiedEmailAndUserName()
    {
        var user = await GetOrCreateAsync(Principal(
            Guid.NewGuid().ToString(),
            ("email", "alice@example.com"),
            ("email_verified", "true"),
            ("preferred_username", "alice")));

        Assert.Equal("alice@example.com", user.Email);
        Assert.Equal("alice", user.UserName);
    }

    [Theory]
    [InlineData("false")]
    [InlineData(null)]
    public async Task GetOrCreate_UnverifiedEmail_IsNotStored(string? verified)
    {
        (string, string)[] claims = verified is null
            ? [("email", "victim@example.com")]
            : [("email", "victim@example.com"), ("email_verified", verified)];

        var user = await GetOrCreateAsync(Principal(Guid.NewGuid().ToString(), claims));

        Assert.Null(user.Email);
    }

    [Fact]
    public async Task GetOrCreate_RefreshesProfileOnEveryLogin()
    {
        var subject = Guid.NewGuid().ToString();
        await GetOrCreateAsync(Principal(subject, ("email", "old@example.com"), ("email_verified", "true"), ("preferred_username", "old")));

        await GetOrCreateAsync(Principal(subject, ("preferred_username", "new")));

        await using var db = database.CreateContext();
        var stored = await db.Users.SingleAsync(u => u.Subject == subject, Ct);
        Assert.Equal("new", stored.UserName);
        Assert.Null(stored.Email);
    }

    [Theory]
    [InlineData("iss")]
    [InlineData("sub")]
    public async Task GetOrCreate_TokenWithoutIdentityClaim_IsDenied(string missing)
    {
        var claims = new[] { ("iss", TestTokens.Issuer), ("sub", Guid.NewGuid().ToString()) }
            .Where(c => c.Item1 != missing)
            .ToArray();

        await Assert.ThrowsAsync<AccessDeniedException>(() => GetOrCreateAsync(TestTokens.Principal(claims)));
    }

    [Fact]
    public async Task GetOrCreate_ConcurrentFirstLogins_CreateExactlyOneUser()
    {
        var subject = Guid.NewGuid().ToString();

        var users = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => GetOrCreateAsync(Principal(subject))));

        Assert.Single(users.Select(u => u.Id).Distinct());
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.Users.CountAsync(u => u.Subject == subject, Ct));
    }

    private static System.Security.Claims.ClaimsPrincipal Principal(string subject, params (string, string)[] extra) =>
        TestTokens.Principal([("iss", TestTokens.Issuer), ("sub", subject), .. extra]);

    private async Task<Data.User> GetOrCreateAsync(System.Security.Claims.ClaimsPrincipal principal)
    {
        await using var db = database.CreateContext();
        return await new UserDirectory(db).GetOrCreateAsync(principal, Ct);
    }
}
