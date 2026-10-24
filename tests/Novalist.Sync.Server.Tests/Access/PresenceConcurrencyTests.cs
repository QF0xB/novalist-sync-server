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


}
