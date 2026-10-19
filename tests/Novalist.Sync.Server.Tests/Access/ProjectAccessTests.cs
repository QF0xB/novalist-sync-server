using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Access;

public class ProjectAccessTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_MakesTheCallerOwnerAtEpochZero()
    {
        var user = await database.CreateUserAsync();
        var projectId = Guid.NewGuid();

        await CallAsync(access => access.CreateAsync(user.Id, projectId, Ct));

        await using var db = database.CreateContext();
        var project = await db.Projects.SingleAsync(p => p.Id == projectId, Ct);
        Assert.Equal(0, project.CurrentEpoch);
        Assert.Equal(0, project.LastServerSeq);
        Assert.Equal(MembershipRole.Owner, (await db.Memberships.SingleAsync(m => m.ProjectId == projectId, Ct)).Role);
    }

    [Fact]
    public async Task Create_RetriedByTheOwner_IsFine()
    {
        var user = await database.CreateUserAsync();
        var projectId = Guid.NewGuid();

        await CallAsync(access => access.CreateAsync(user.Id, projectId, Ct));
        await CallAsync(access => access.CreateAsync(user.Id, projectId, Ct));
    }

    [Fact]
    public async Task Create_WithIdOfAnotherUsersProject_IsConflictAndGrantsNothing()
    {
        var owner = await database.CreateUserAsync();
        var other = await database.CreateUserAsync();
        var projectId = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));

        await Assert.ThrowsAsync<ConflictException>(() => CallAsync(access => access.CreateAsync(other.Id, projectId, Ct)));

        await Assert.ThrowsAsync<AccessDeniedException>(() => CallAsync(access => access.EnsureCanReadAsync(other.Id, projectId, Ct)));
    }

    [Theory]
    [InlineData(MembershipRole.Owner, true, true, true)]
    [InlineData(MembershipRole.Contributor, true, true, false)]
    [InlineData(MembershipRole.Viewer, true, false, false)]
    public async Task Roles_GrantTheDocumentedAccess(MembershipRole role, bool canRead, bool canWrite, bool isOwner)
    {
        var owner = await database.CreateUserAsync();
        var member = await database.CreateUserAsync();
        var projectId = role == MembershipRole.Owner
            ? await database.CreateProjectAsync((member.Id, MembershipRole.Owner))
            : await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (member.Id, role));

        Assert.Equal(canRead, await IsAllowedAsync(access => access.EnsureCanReadAsync(member.Id, projectId, Ct)));
        Assert.Equal(canWrite, await IsAllowedAsync(access => access.EnsureCanWriteAsync(member.Id, projectId, Ct)));
        Assert.Equal(isOwner, await IsAllowedAsync(access => access.EnsureIsOwnerAsync(member.Id, projectId, Ct)));
    }

    [Fact]
    public async Task NonMember_GetsNoAccess()
    {
        var owner = await database.CreateUserAsync();
        var outsider = await database.CreateUserAsync();
        var projectId = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));

        Assert.False(await IsAllowedAsync(access => access.EnsureCanReadAsync(outsider.Id, projectId, Ct)));
        Assert.False(await IsAllowedAsync(access => access.EnsureCanWriteAsync(outsider.Id, projectId, Ct)));
        Assert.False(await IsAllowedAsync(access => access.EnsureIsOwnerAsync(outsider.Id, projectId, Ct)));
    }

    [Fact]
    public async Task UnknownProject_LooksExactlyLikeNotBeingAMember()
    {
        var owner = await database.CreateUserAsync();
        var outsider = await database.CreateUserAsync();
        var existingId = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        var unknownId = Guid.NewGuid();

        var existing = await Assert.ThrowsAsync<AccessDeniedException>(
            () => CallAsync(access => access.EnsureCanReadAsync(outsider.Id, existingId, Ct)));
        var unknown = await Assert.ThrowsAsync<AccessDeniedException>(
            () => CallAsync(access => access.EnsureCanReadAsync(outsider.Id, unknownId, Ct)));

        Assert.Equal(existing.Message.Replace(existingId.ToString(), "<id>"), unknown.Message.Replace(unknownId.ToString(), "<id>"));
    }

    [Fact]
    public async Task RemoveMember_ByOwner_RemovesAllAccess()
    {
        var owner = await database.CreateUserAsync();
        var member = await database.CreateUserAsync();
        var projectId = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (member.Id, MembershipRole.Contributor));

        await CallAsync(access => access.RemoveMemberAsync(owner.Id, projectId, member.Id, Ct));

        Assert.False(await IsAllowedAsync(access => access.EnsureCanReadAsync(member.Id, projectId, Ct)));
    }

    [Fact]
    public async Task RemoveMember_ByNonOwner_IsDeniedAndRemovesNothing()
    {
        var owner = await database.CreateUserAsync();
        var contributor = await database.CreateUserAsync();
        var projectId = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (contributor.Id, MembershipRole.Contributor));

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => CallAsync(access => access.RemoveMemberAsync(contributor.Id, projectId, owner.Id, Ct)));

        Assert.True(await IsAllowedAsync(access => access.EnsureIsOwnerAsync(owner.Id, projectId, Ct)));
    }

    [Fact]
    public async Task RemoveMember_OwnerRemovingThemselves_IsConflict()
    {
        var owner = await database.CreateUserAsync();
        var projectId = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));

        await Assert.ThrowsAsync<ConflictException>(
            () => CallAsync(access => access.RemoveMemberAsync(owner.Id, projectId, owner.Id, Ct)));

        Assert.True(await IsAllowedAsync(access => access.EnsureIsOwnerAsync(owner.Id, projectId, Ct)));
    }

    private async Task CallAsync(Func<ProjectAccess, Task> call)
    {
        await using var db = database.CreateContext();
        await call(new ProjectAccess(db));
    }

    private async Task<bool> IsAllowedAsync(Func<ProjectAccess, Task> check)
    {
        try
        {
            await CallAsync(check);
            return true;
        }
        catch (AccessDeniedException)
        {
            return false;
        }
    }
}
