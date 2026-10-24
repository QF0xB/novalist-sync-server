using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Novalist.Sync.Server.Hubs;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class InvitationEndpointsTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly SyncServer server = new(database);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Invitation_AdmitsOneSignedInAccount_AndRetryDoesNotAdmitAnother()
    {
        var owner = await database.CreateUserAsync();
        var guest = await database.CreateUserAsync();
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        var device = await database.CreateDeviceAsync(guest.Id);
        var invitation = Guid.NewGuid();
        using var ownerHttp = server.CreateClient(TestTokens.Create(owner.Subject));
        using var guestHttp = server.CreateClient(TestTokens.Create(guest.Subject));
        using var outsiderHttp = server.CreateClient(TestTokens.Create(Guid.NewGuid().ToString()));
        await using var guestHub = new TestClient(
            server.CreateHubConnection(TestTokens.Create(guest.Subject), device), guest.Id, device);
        await guestHub.Connection.StartAsync(Ct);
        await Assert.ThrowsAsync<HubException>(() => guestHub.JoinAsync(project));

        using var created = await ownerHttp.PostAsJsonAsync($"/projects/{project}/invitations",
            new { invitationId = invitation, expiresAt = DateTimeOffset.UtcNow.AddHours(1) }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        using var redeemed = await guestHttp.PostAsJsonAsync("/invitations/redeem", new { invitationId = invitation }, Ct);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        Assert.Equal(project, (await redeemed.Content.ReadFromJsonAsync<Grant>(Ct))?.ProjectId);
        await guestHub.JoinAsync(project);
        Assert.Equal(1, await guestHub.PushAsync(TestData.Envelope(project, device, 1)));
        using var retry = await guestHttp.PostAsJsonAsync("/invitations/redeem", new { invitationId = invitation }, Ct);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        using var stolen = await outsiderHttp.PostAsJsonAsync("/invitations/redeem", new { invitationId = invitation }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, stolen.StatusCode);
    }

    private sealed record Grant(Guid ProjectId);

    [Fact]
    public async Task OnlyOwnerCanCreateOrRevoke_AndRevokedLinksCannotBeClaimed()
    {
        var owner = await database.CreateUserAsync();
        var member = await database.CreateUserAsync();
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner),
            (member.Id, MembershipRole.Contributor));
        using var ownerHttp = server.CreateClient(TestTokens.Create(owner.Subject));
        using var memberHttp = server.CreateClient(TestTokens.Create(member.Subject));
        using var anonymousHttp = server.CreateClient(token: null);
        var invitation = new { invitationId = Guid.NewGuid(), expiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        var path = $"/projects/{project}/invitations";
        using var denied = await memberHttp.PostAsJsonAsync(path, invitation, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var created = await ownerHttp.PostAsJsonAsync(path, invitation, Ct);
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        using var unsigned = await anonymousHttp.PostAsJsonAsync("/invitations/redeem", invitation, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        using var deniedRevoke = await memberHttp.PostAsJsonAsync(path + "/revoke", invitation, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, deniedRevoke.StatusCode);
        using var revoked = await ownerHttp.PostAsJsonAsync(path + "/revoke", invitation, Ct);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        using var claim = await memberHttp.PostAsJsonAsync("/invitations/redeem", invitation, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, claim.StatusCode);
    }

    public async ValueTask DisposeAsync() => await server.DisposeAsync();

    [Fact]
    public async Task ExpiredInvitationCannotBeClaimed()
    {
        var clock = new AdjustableClock();
        await using var timedServer = new SyncServer(database, clock);
        var owner = await database.CreateUserAsync();
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        using var ownerHttp = timedServer.CreateClient(TestTokens.Create(owner.Subject));
        var invitation = new { invitationId = Guid.NewGuid(), expiresAt = clock.Now.AddMinutes(1) };
        using var created = await ownerHttp.PostAsJsonAsync($"/projects/{project}/invitations", invitation, Ct);
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        clock.Now = clock.Now.AddMinutes(1);
        using var claim = await ownerHttp.PostAsJsonAsync("/invitations/redeem", invitation, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, claim.StatusCode);
    }

    [Fact]
    public async Task AUsedInvitationCannotRestoreRemovedMembership()
    {
        var owner = await database.CreateUserAsync();
        var guest = await database.CreateUserAsync();
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        using var ownerHttp = server.CreateClient(TestTokens.Create(owner.Subject));
        using var guestHttp = server.CreateClient(TestTokens.Create(guest.Subject));
        var invitation = new { invitationId = Guid.NewGuid(), expiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        using var created = await ownerHttp.PostAsJsonAsync($"/projects/{project}/invitations", invitation, Ct);
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        using var claim = await guestHttp.PostAsJsonAsync("/invitations/redeem", invitation, Ct);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AccessRevocation>().RemoveMemberAsync(owner.Id, project, guest.Id, Ct);
        using var retry = await guestHttp.PostAsJsonAsync("/invitations/redeem", invitation, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, retry.StatusCode);
    }

    [Fact]
    public async Task ConcurrentDifferentInvitationsCanAdmitTheSameAccount()
    {
        var owner = await database.CreateUserAsync();
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        using var ownerHttp = server.CreateClient(TestTokens.Create(owner.Subject));
        using var guestHttp = server.CreateClient(TestTokens.Create(Guid.NewGuid().ToString()));
        var invitations = Enumerable.Range(0, 8).Select(_ => new
        { invitationId = Guid.NewGuid(), expiresAt = DateTimeOffset.UtcNow.AddHours(1) }).ToArray();
        foreach (var invitation in invitations)
        {
            using var created = await ownerHttp.PostAsJsonAsync($"/projects/{project}/invitations", invitation, Ct);
            Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        }
        var claims = await Task.WhenAll(invitations.Select(invitation => guestHttp.PostAsJsonAsync("/invitations/redeem", invitation, Ct)));
        try { Assert.All(claims, claim => Assert.Equal(HttpStatusCode.OK, claim.StatusCode)); }
        finally { foreach (var claim in claims) claim.Dispose(); }
    }

    private sealed class AdjustableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task RevocationArrivingBeforeCreationPreventsDelayedCreation()
    {
        var owner = await database.CreateUserAsync();
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        using var ownerHttp = server.CreateClient(TestTokens.Create(owner.Subject));
        var invitation = new { invitationId = Guid.NewGuid(), expiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        var path = $"/projects/{project}/invitations";
        using var revoked = await ownerHttp.PostAsJsonAsync(path + "/revoke", invitation, Ct);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        using var delayed = await ownerHttp.PostAsJsonAsync(path, invitation, Ct);
        Assert.Equal(HttpStatusCode.Conflict, delayed.StatusCode);
        using var claim = await ownerHttp.PostAsJsonAsync("/invitations/redeem", invitation, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, claim.StatusCode);
        Assert.DoesNotContain(server.Logs.Entries, entry =>
            (entry.Message + entry.Exception + entry.ScopeText).Contains(invitation.invitationId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InvitationAllowsAnExistingViewerToEdit()
    {
        var owner = await database.CreateUserAsync();
        var viewer = await database.CreateUserAsync();
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (viewer.Id, MembershipRole.Viewer));
        var device = await database.CreateDeviceAsync(viewer.Id);
        using var ownerHttp = server.CreateClient(TestTokens.Create(owner.Subject));
        using var viewerHttp = server.CreateClient(TestTokens.Create(viewer.Subject));
        var invitation = new { invitationId = Guid.NewGuid(), expiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        using var created = await ownerHttp.PostAsJsonAsync($"/projects/{project}/invitations", invitation, Ct);
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        using var claim = await viewerHttp.PostAsJsonAsync("/invitations/redeem", invitation, Ct);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        await using var client = new TestClient(server.CreateHubConnection(TestTokens.Create(viewer.Subject), device), viewer.Id, device);
        await client.Connection.StartAsync(Ct);
        await client.JoinAsync(project);
        Assert.Equal(1, await client.PushAsync(TestData.Envelope(project, device, 1)));
    }

    [Fact]
    public async Task CreationRetryKeepsOriginalInvitation_AndConcurrentClaimsHaveOneWinner()
    {
        var owner = await database.CreateUserAsync();
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        using var ownerHttp = server.CreateClient(TestTokens.Create(owner.Subject));
        using var first = server.CreateClient(TestTokens.Create(Guid.NewGuid().ToString()));
        using var second = server.CreateClient(TestTokens.Create(Guid.NewGuid().ToString()));
        var invitation = new { invitationId = Guid.NewGuid(), expiresAt = DateTimeOffset.UtcNow.AddHours(1).AddTicks(1) };
        var path = $"/projects/{project}/invitations";
        using var created = await ownerHttp.PostAsJsonAsync(path, invitation, Ct);
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        using var retry = await ownerHttp.PostAsJsonAsync(path, invitation, Ct);
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        var claims = await Task.WhenAll(
            first.PostAsJsonAsync("/invitations/redeem", invitation, Ct),
            second.PostAsJsonAsync("/invitations/redeem", invitation, Ct));
        try
        {
            Assert.Single(claims, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(claims, r => r.StatusCode == HttpStatusCode.Forbidden);
        }
        finally { foreach (var response in claims) response.Dispose(); }
    }
}
