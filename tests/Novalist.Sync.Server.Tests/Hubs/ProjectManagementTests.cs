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

    [Fact]
    public async Task RotationNotifiesRemainingMembersThroughTheHub()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database);
        await using var owner = await ConnectAsync(server, ct);
        await using var remaining = await ConnectAsync(server, ct);
        await using var removed = await ConnectAsync(server, ct);
        var project = await database.CreateProjectAsync((owner.UserId, MembershipRole.Owner),
            (remaining.UserId, MembershipRole.Contributor), (removed.UserId, MembershipRole.Contributor));
        await using (var db = database.CreateContext())
        {
            foreach (var participant in new[] { owner, remaining })
                db.ProjectDeviceAuthorizations.Add(new()
                {
                    ProjectId = project,
                    DeviceId = participant.DeviceId,
                    Epoch = 0,
                    Certificate = RandomNumberGenerator.GetBytes(182)
                });
            await db.SaveChangesAsync(ct);
        }
        await remaining.Hub.InvokeAsync("JoinProject", project, ct);
        await removed.Hub.InvokeAsync("JoinProject", project, ct);
        var changed = new TaskCompletionSource<(Guid Project, uint Epoch)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var notification = remaining.Hub.On<Guid, uint>("ProjectEpochChanged", (id, epoch) => changed.TrySetResult((id, epoch)));
        var rotation = Guid.NewGuid();
        await owner.Hub.InvokeAsync("BeginProjectEpoch", new BeginProjectEpoch(project, rotation, 0, 0), ct);
        var checkpoint = TestData.Envelope(project, owner.DeviceId, 1);
        checkpoint = checkpoint with { Header = (EnvelopeHeader.Parse(checkpoint.Header) with { Epoch = 1 }).ToBytes() };
        await owner.Hub.InvokeAsync<long>("StageProjectEpoch", rotation, checkpoint, ct);
        var grants = new[] { owner, remaining }.Select(participant => new ProjectEpochGrant(participant.DeviceId,
            RandomNumberGenerator.GetBytes(182), RandomNumberGenerator.GetBytes(128))).ToArray();
        await owner.Hub.InvokeAsync("CommitProjectEpoch", new CommitProjectEpoch(rotation, removed.UserId, grants), ct);
        Assert.Equal((project, 1U), await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        await Assert.ThrowsAsync<HubException>(() => removed.Hub.InvokeAsync<ProjectEpochStatus>("GetProjectEpoch", project, ct));
    }

    [Fact]
    public async Task VerifiedOwnerCanReserveAndCancelFromAnotherDeviceWhileOtherCallersCannot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database);
        var account = await database.CreateUserAsync();
        await using var owner = await ConnectAsync(server, ct, account);
        await using var another = await ConnectAsync(server, ct, account);
        await using var guest = await ConnectAsync(server, ct);
        var project = await database.CreateProjectAsync((owner.UserId, MembershipRole.Owner), (guest.UserId, MembershipRole.Contributor));
        var rotation = Guid.NewGuid();
        await using var unverified = server.CreateHubConnection(TestTokens.Create(account.Subject), owner.DeviceId);
        await unverified.StartAsync(ct);
        await Assert.ThrowsAsync<HubException>(() => unverified.InvokeAsync<ProjectEpochStatus>("ReserveProjectEpoch", project, rotation, ct));
        await Assert.ThrowsAsync<HubException>(() => guest.Hub.InvokeAsync<ProjectEpochStatus>("ReserveProjectEpoch", project, rotation, ct));
        await guest.Hub.InvokeAsync("JoinProject", project, ct);
        Assert.Equal(1, await guest.Hub.InvokeAsync<long>("Push", TestData.Envelope(project, guest.DeviceId, 1), ct));
        var boundary = await owner.Hub.InvokeAsync<ProjectEpochStatus>("ReserveProjectEpoch", project, rotation, ct);
        Assert.Equal(new ProjectEpochStatus(0, 1), boundary);
        Assert.Equal(boundary, await owner.Hub.InvokeAsync<ProjectEpochStatus>("ReserveProjectEpoch", project, rotation, ct));
        await Assert.ThrowsAsync<HubException>(() => guest.Hub.InvokeAsync("CancelProjectEpoch", rotation, ct));
        await Assert.ThrowsAsync<HubException>(() => unverified.InvokeAsync("CancelProjectEpoch", rotation, ct));
        var waiting = TestData.Envelope(project, guest.DeviceId, 2);
        await Assert.ThrowsAsync<HubException>(() => guest.Hub.InvokeAsync<long>("Push", waiting, ct));
        await another.Hub.InvokeAsync("CancelProjectEpoch", rotation, ct);
        await another.Hub.InvokeAsync("CancelProjectEpoch", rotation, ct);
        Assert.Equal(2, await guest.Hub.InvokeAsync<long>("Push", waiting, ct));
        await using var db = database.CreateContext();
        Assert.False(await db.ProjectEpochStages.AnyAsync(stage => stage.Id == rotation, ct));
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
