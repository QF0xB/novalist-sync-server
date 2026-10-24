using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;
using Novalist.Sync.Server.Updates;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class PresenceConcurrencyTests(DatabaseFixture database)
{
    [Fact]
    public async Task OfflineMembersDoNotAcquireAccountGatesOrEnterTheRecipientSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var guest = await database.CreateUserAsync();
        var offline = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var guestDevice = await database.CreateDeviceAsync(guest.Id);
        var offlineDevice = await database.CreateDeviceAsync(offline.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (guest.Id, MembershipRole.Contributor),
            (offline.Id, MembershipRole.Contributor));
        await using var offlineOperation = await database.DeliveryGate.AccountAsync(offline.Id, ct);
        await using var db = database.CreateContext();
        var presence = new PresenceAccess(db, new(db, database.DeliveryGate), new(db, database.DeliveryGate), database.DeliveryGate);
        await presence.DeliverAsync(owner.Id, device, TestData.Envelope(project, device, 1, MsgType.Presence),
            new HashSet<Guid> { guestDevice }, (_, recipients, _) =>
            {
                Assert.Contains(guestDevice, recipients);
                Assert.DoesNotContain(offlineDevice, recipients);
                return Task.CompletedTask;
            }, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    [Fact]
    public async Task DeviceRevocationWaitsWithoutADatabaseLockAndLaterPresenceExcludesTheDevice()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var guest = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var guestDevice = await database.CreateDeviceAsync(guest.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (guest.Id, MembershipRole.Contributor));
        await using var sending = database.CreateContext();
        await using var revoking = database.CreateContext();
        var presence = new PresenceAccess(sending, new(sending, database.DeliveryGate), new(sending, database.DeliveryGate), database.DeliveryGate);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = presence.DeliverAsync(owner.Id, device, TestData.Envelope(project, device, 1, MsgType.Presence), new HashSet<Guid> { device, guestDevice },
            async (_, recipients, token) =>
            {
                Assert.Contains(guestDevice, recipients);
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }, ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            var revoke = new DeviceRegistry(revoking, database.DeliveryGate).RevokeAsync(guest.Id, guestDevice, ct);
            Assert.False(revoke.IsCompleted);
            Assert.Null(revoking.Database.CurrentTransaction);
            Assert.Null(sending.Database.CurrentTransaction);
            await using var writing = database.CreateContext();
            Assert.Equal(1, await new UpdateLog(writing).AppendAsync(owner.Id, TestData.Envelope(project, device, 2), ct)
                .WaitAsync(TimeSpan.FromSeconds(5), ct));
            release.TrySetResult();
            await delivery;
            await revoke;
            await presence.DeliverAsync(owner.Id, device, TestData.Envelope(project, device, 3, MsgType.Presence), new HashSet<Guid> { device, guestDevice },
                (_, recipients, _) =>
                {
                    Assert.Contains(device, recipients);
                    Assert.DoesNotContain(guestDevice, recipients);
                    return Task.CompletedTask;
                }, ct);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RotationDuringPreflightRejectsStaleOrRemovedSendersBeforeDelivery(bool removeSender)
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var guest = await database.CreateUserAsync();
        var ownerDevice = await database.CreateDeviceAsync(owner.Id);
        var guestDevice = await database.CreateDeviceAsync(guest.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (guest.Id, MembershipRole.Contributor));
        await using var rotating = database.CreateContext();
        var epochs = new ProjectEpochAccess(rotating, new(rotating, database.DeliveryGate));
        await epochs.RegisterAsync(owner.Id, ownerDevice, project, 0, RandomNumberGenerator.GetBytes(182), ct);
        var rotation = Guid.NewGuid();
        await epochs.BeginAsync(owner.Id, ownerDevice, new(project, rotation, 0, 0), ct);
        var checkpoint = TestData.Envelope(project, ownerDevice, 1);
        checkpoint = checkpoint with { Header = (EnvelopeHeader.Parse(checkpoint.Header) with { Epoch = 1 }).ToBytes() };
        await epochs.StageAsync(owner.Id, ownerDevice, rotation, checkpoint, ct);
        var pause = new SqlCommandPause("FROM memberships");
        await using var sending = database.CreateContext(pause);
        var presence = new PresenceAccess(sending, new(sending, database.DeliveryGate), new(sending, database.DeliveryGate), database.DeliveryGate);
        var sender = removeSender ? guest.Id : owner.Id;
        var device = removeSender ? guestDevice : ownerDevice;
        var delivered = false;
        var delivery = presence.DeliverAsync(sender, device, TestData.Envelope(project, device, 1, MsgType.Presence), new HashSet<Guid> { device, guestDevice },
            (_, _, _) => { delivered = true; return Task.CompletedTask; }, ct);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await epochs.CommitAsync(owner.Id, ownerDevice, new(rotation, guest.Id,
                [new(ownerDevice, RandomNumberGenerator.GetBytes(182), RandomNumberGenerator.GetBytes(128))]), ct);
            pause.Release.TrySetResult();
            if (removeSender) await Assert.ThrowsAsync<AccessDeniedException>(() => delivery);
            else await Assert.ThrowsAsync<PushRejectedException>(() => delivery);
            Assert.False(delivered);
        }
        finally { pause.Release.TrySetResult(); }
    }

    [Fact]
    public async Task RotationWaitsForPresenceDeliveryAndThenExcludesTheRemovedMember()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var guest = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var guestDevice = await database.CreateDeviceAsync(guest.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (guest.Id, MembershipRole.Contributor));
        await using var rotating = database.CreateContext();
        var epochs = new ProjectEpochAccess(rotating, new(rotating, database.DeliveryGate));
        await epochs.RegisterAsync(owner.Id, device, project, 0, RandomNumberGenerator.GetBytes(182), ct);
        var rotation = Guid.NewGuid();
        await epochs.BeginAsync(owner.Id, device, new(project, rotation, 0, 0), ct);
        var checkpoint = TestData.Envelope(project, device, 1);
        checkpoint = checkpoint with { Header = (EnvelopeHeader.Parse(checkpoint.Header) with { Epoch = 1 }).ToBytes() };
        await epochs.StageAsync(owner.Id, device, rotation, checkpoint, ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sending = database.CreateContext();
        var presence = new PresenceAccess(sending, new(sending, database.DeliveryGate), new(sending, database.DeliveryGate), database.DeliveryGate);
        var envelope = TestData.Envelope(project, device, 1, MsgType.Presence);
        var delivery = presence.DeliverAsync(owner.Id, device, envelope, new HashSet<Guid> { device, guestDevice }, async (id, recipients, cancellationToken) =>
        {
            Assert.Equal(project, id);
            Assert.Contains(guestDevice, recipients);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        }, ct);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            var commit = epochs.CommitAsync(owner.Id, device, new(rotation, guest.Id,
                [new(device, RandomNumberGenerator.GetBytes(182), RandomNumberGenerator.GetBytes(128))]), ct);
            Assert.False(commit.IsCompleted);
            Assert.Null(rotating.Database.CurrentTransaction);
            Assert.Null(sending.Database.CurrentTransaction);
            release.TrySetResult();
            await delivery;
            await commit;
            await Assert.ThrowsAsync<PushRejectedException>(() => presence.DeliverAsync(owner.Id, device, envelope, new HashSet<Guid> { device, guestDevice },
                (_, _, _) => throw new InvalidOperationException("Stale presence was delivered."), ct));
            var current = envelope with { Header = (EnvelopeHeader.Parse(envelope.Header) with { Epoch = 1 }).ToBytes() };
            await presence.DeliverAsync(owner.Id, device, current, new HashSet<Guid> { device, guestDevice }, (_, recipients, _) =>
            {
                Assert.Contains(device, recipients);
                Assert.DoesNotContain(guestDevice, recipients);
                return Task.CompletedTask;
            }, ct);
        }
        finally { release.TrySetResult(); }
    }
}
