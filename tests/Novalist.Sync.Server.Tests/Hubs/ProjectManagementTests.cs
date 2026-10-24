using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;
using NSec.Cryptography;
using System.Security.Cryptography;

namespace Novalist.Sync.Server.Tests.Hubs;

public sealed class ProjectManagementTests(DatabaseFixture database)
{
    [Fact]
    public async Task OnlyAVerifiedOwnerCanManageProjectAccessAndDeletedIdentitiesStayUnavailable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database);
        await using var owner = await ConnectAsync(server, ct);
        await using var guest = await ConnectAsync(server, ct);
        var project = await database.CreateProjectAsync((owner.UserId, MembershipRole.Owner), (guest.UserId, MembershipRole.Contributor));
        await guest.Hub.InvokeAsync("JoinProject", project, ct);
        var members = await owner.Hub.InvokeAsync<ProjectMember[]>("ListProjectMembers", project, ct);
        Assert.Equal(2, members.Length);
        Assert.Equal(ProjectRoles.Owner, members.Single(member => member.UserId == owner.UserId).Role);
        Assert.Equal(ProjectRoles.Contributor, members.Single(member => member.UserId == guest.UserId).Role);
        await Assert.ThrowsAsync<HubException>(() => guest.Hub.InvokeAsync<ProjectMember[]>("ListProjectMembers", project, ct));
        await Assert.ThrowsAsync<HubException>(() => guest.Hub.InvokeAsync("RemoveProjectMember", project, owner.UserId, ct));
        await Assert.ThrowsAsync<HubException>(() => guest.Hub.InvokeAsync("DeleteProject", project, ct));
        var invitation = Guid.NewGuid();
        var invalid = await Assert.ThrowsAsync<HubException>(() => owner.Hub.InvokeAsync("CreateInvitation", project,
            new CreateInvitation(Guid.Empty, DateTimeOffset.UtcNow.AddHours(1)), ct));
        Assert.Contains("expiry within seven days", invalid.Message);
        await owner.Hub.InvokeAsync("CreateInvitation", project, new CreateInvitation(invitation, DateTimeOffset.UtcNow.AddHours(1)), ct);
        var ended = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var notification = guest.Hub.On<Guid>("ProjectAccessEnded", id => ended.TrySetResult(id));
        await owner.Hub.InvokeAsync("RemoveProjectMember", project, guest.UserId, ct);
        Assert.Equal(project, await ended.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        await Assert.ThrowsAsync<HubException>(() => guest.Hub.InvokeAsync("JoinProject", project, ct));
        await Assert.ThrowsAsync<HubException>(() => guest.Hub.InvokeAsync<InvitationGrant>("RedeemInvitation", invitation, ct));
        var renewed = Guid.NewGuid();
        await owner.Hub.InvokeAsync("CreateInvitation", project, new CreateInvitation(renewed, DateTimeOffset.UtcNow.AddHours(1)), ct);
        await guest.Hub.InvokeAsync<InvitationGrant>("RedeemInvitation", renewed, ct);
        await guest.Hub.InvokeAsync("JoinProject", project, ct);
        var replay = guest.Hub.StreamAsync<SequencedEnvelope>("CatchUp", project, 0L, ct);
        await foreach (var _ in replay.WithCancellation(ct)) Assert.Fail("The empty project should have no updates.");
        await owner.Hub.InvokeAsync("DeleteProject", project, ct);
        await Assert.ThrowsAsync<HubException>(() => owner.Hub.InvokeAsync("JoinProject", project, ct));
        await Assert.ThrowsAsync<HubException>(() => owner.Hub.InvokeAsync("CreateProject", project, ct));
        await Assert.ThrowsAsync<HubException>(() => guest.Hub.InvokeAsync("CreateProject", project, ct));
    }



    private async Task<Participant> ConnectAsync(SyncServer server, CancellationToken ct, User? user = null)
    {
        user ??= await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(user.Id);
        var signing = Key.Create(SignatureAlgorithm.Ed25519);
        using var receiving = Key.Create(KeyAgreementAlgorithm.X25519);
        await using (var db = database.CreateContext())
        {
            var stored = await db.Devices.SingleAsync(value => value.Id == device, ct);
            stored.SigningPublicKey = signing.PublicKey.Export(KeyBlobFormat.RawPublicKey);
            stored.ReceivingPublicKey = receiving.PublicKey.Export(KeyBlobFormat.RawPublicKey);
            await db.SaveChangesAsync(ct);
        }
        var hub = server.CreateHubConnection(TestTokens.Create(user.Subject), device);
        try
        {
            await hub.StartAsync(ct);
            var challenge = await hub.InvokeAsync<DeviceConnectionChallenge>("GetDeviceChallenge", ct);
            await hub.InvokeAsync("AuthenticateDevice", SignatureAlgorithm.Ed25519.Sign(signing, DeviceProof.EncodeConnection(challenge)), ct);
            return new(user.Id, device, signing, hub);
        }
        catch { await hub.DisposeAsync(); signing.Dispose(); throw; }
    }

    private sealed class Participant(Guid userId, Guid deviceId, Key key, HubConnection hub) : IAsyncDisposable
    {
        public Guid UserId => userId;
        public Guid DeviceId => deviceId;
        public HubConnection Hub => hub;
        public async ValueTask DisposeAsync() { await hub.DisposeAsync(); key.Dispose(); }
    }
}
