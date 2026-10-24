using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;
using Npgsql;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class InvitationConcurrencyTests(DatabaseFixture database)
{

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActivationAndRedemptionUseTheSameLockOrder(bool activateFirst)
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var activated = await database.CreateUserAsync();
        var redeemed = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(activated.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        var invitation = Guid.NewGuid();
        await using (var setup = database.CreateContext())
        {
            await setup.Devices.Where(value => value.Id == device).ExecuteUpdateAsync(
                set => set.SetProperty(value => value.SigningPublicKey, RandomNumberGenerator.GetBytes(32)), ct);
            await new InvitationAccess(setup, new(setup, database.DeliveryGate), TimeProvider.System).CreateAsync(owner.Id, project,
                new(invitation, DateTimeOffset.UtcNow.AddHours(1)), ct);
        }
        var pause = new SqlCommandPause("INSERT INTO memberships");
        await using var first = database.CreateContext(pause);
        await using var second = database.CreateContext();
        await second.Database.OpenConnectionAsync(ct);
        var process = ((NpgsqlConnection)second.Database.GetDbConnection()).ProcessID;
        Task Activate(SyncDbContext db) => new InvitationAccess(db, new(db, database.DeliveryGate), TimeProvider.System)
            .ActivateAsync(activated.Id, device, new(invitation, RandomNumberGenerator.GetBytes(32)), ct);
        Task Redeem(SyncDbContext db) => new InvitationAccess(db, new(db, database.DeliveryGate), TimeProvider.System).RedeemAsync(redeemed.Id, invitation, ct);
        var initial = activateFirst ? Activate(first) : Redeem(first);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            var following = activateFirst ? Redeem(second) : Activate(second);
            await DatabaseLocks.WaitUntilBlockedAsync(database, process, ct);
            pause.Release.TrySetResult();
            await initial;
            await Assert.ThrowsAsync<AccessDeniedException>(() => following);
            var members = await second.Memberships.Where(value => value.ProjectId == project).Select(value => value.UserId).ToListAsync(ct);
            Assert.Equal(2, members.Count);
            Assert.Contains(activateFirst ? activated.Id : redeemed.Id, members);
        }
        finally { pause.Release.TrySetResult(); }
    }
}
