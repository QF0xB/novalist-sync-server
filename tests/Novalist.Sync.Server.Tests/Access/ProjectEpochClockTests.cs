using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;
using Novalist.Sync.Server.Updates;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class ProjectEpochClockTests(DatabaseFixture database)
{
    [Fact]
    public async Task BegunCheckpointExpiresAndCanBeReplacedUsingDatabaseTime()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        var clock = new DatabaseTimeOverride(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var db = database.CreateContext(clock);
        var epochs = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
        var rotation = Guid.NewGuid();
        await epochs.BeginAsync(owner.Id, device, new(project, rotation, 0, 0), ct);
        Assert.Equal(clock.Now, (await db.ProjectEpochStages.AsNoTracking().SingleAsync(stage => stage.Id == rotation, ct)).CreatedAt);
        var checkpoint = TestData.Envelope(project, device, 1);
        checkpoint = checkpoint with { Header = (EnvelopeHeader.Parse(checkpoint.Header) with { Epoch = 1 }).ToBytes() };
        Assert.Equal(1, await epochs.StageAsync(owner.Id, device, rotation, checkpoint, ct));
        clock.Now += ProjectEpochAccess.ReservationLifetime;
        await Assert.ThrowsAsync<ConflictException>(() => epochs.StageAsync(owner.Id, device, rotation, checkpoint, ct));
        var replacement = Guid.NewGuid();
        await epochs.BeginAsync(owner.Id, device, new(project, replacement, 0, 0), ct);
        Assert.False(await db.ProjectEpochStages.AnyAsync(stage => stage.Id == rotation, ct));
        Assert.Equal(clock.Now, (await db.ProjectEpochStages.AsNoTracking().SingleAsync(stage => stage.Id == replacement, ct)).CreatedAt);
    }

    [Fact]
    public async Task ReservationFencingAndCleanupFollowTheDatabaseClockDespiteHostSkew()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        var clock = new DatabaseTimeOverride(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var db = database.CreateContext(clock);
        var epochs = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
        var rotation = Guid.NewGuid();
        await epochs.ReserveAsync(owner.Id, device, project, rotation, ct);
        Assert.Equal(clock.Now, (await db.ProjectEpochStages.AsNoTracking().SingleAsync(stage => stage.Id == rotation, ct)).CreatedAt);
        var cleanup = new EphemeralStateCleanup(db);
        await cleanup.PruneAsync(ct);
        Assert.True(await db.ProjectEpochStages.AnyAsync(stage => stage.Id == rotation, ct));
        var update = TestData.Envelope(project, device, 1);
        await Assert.ThrowsAsync<PushRejectedException>(() => new UpdateLog(db).AppendAsync(owner.Id, update, ct));
        clock.Now += ProjectEpochAccess.ReservationLifetime;
        await cleanup.PruneAsync(ct);
        Assert.False(await db.ProjectEpochStages.AnyAsync(stage => stage.Id == rotation, ct));
        Assert.Equal(1, await new UpdateLog(db).AppendAsync(owner.Id, update, ct));
    }
}
