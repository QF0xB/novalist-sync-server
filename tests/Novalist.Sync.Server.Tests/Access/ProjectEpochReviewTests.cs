using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class ProjectEpochReviewTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CancellationCannotDeleteAReplacementReservationForAnotherProject()
    {
        var owner = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var first = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        var second = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner));
        var rotation = Guid.NewGuid();
        await using var setup = database.CreateContext();
        await new ProjectEpochAccess(setup, new(setup, database.DeliveryGate)).ReserveAsync(owner.Id, device, first, rotation, Ct);
        var pause = new SqlCommandPause("SELECT * FROM projects");
        await using var cancelling = database.CreateContext(pause);
        var cancel = new ProjectEpochAccess(cancelling, new(cancelling, database.DeliveryGate)).CancelAsync(owner.Id, device, rotation, Ct);
        try
        {
            await await Task.WhenAny(pause.Entered.Task, cancel).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.True(pause.Entered.Task.IsCompleted);
            await setup.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM project_epoch_stages WHERE id = {rotation};
                INSERT INTO project_epoch_stages (id, project_id, device_id, previous_epoch, previous_server_seq, created_at, count)
                VALUES ({rotation}, {second}, {device}, 0, 0, clock_timestamp(), 0)
                """, Ct);
            pause.Release.TrySetResult();
            await Assert.ThrowsAsync<ConflictException>(() => cancel);
            Assert.Equal(second, await setup.ProjectEpochStages.Where(stage => stage.Id == rotation).Select(stage => stage.ProjectId).SingleAsync(Ct));
        }
        finally { pause.Release.TrySetResult(); await Assert.ThrowsAsync<ConflictException>(() => cancel); }
    }

    [Fact]
    public async Task CommitRechecksTheInitiatorAfterWaitingForAccountLocks()
    {
        var prepared = await PrepareAsync();
        var pause = new SqlCommandPause("SELECT * FROM users");
        await using var committing = database.CreateContext(pause);
        var service = new ProjectEpochAccess(committing, new(committing, database.DeliveryGate));
        var commit = service.CommitAsync(prepared.Owner, prepared.Device, new(prepared.Rotation, prepared.Member, [Grant(prepared.Device)]), Ct);
        try
        {
            await await Task.WhenAny(pause.Entered.Task, commit).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.True(pause.Entered.Task.IsCompleted);
            await using var revoking = database.CreateContext();
            await new DeviceRegistry(revoking, database.DeliveryGate).RevokeAsync(prepared.Owner, prepared.Device, Ct);
            pause.Release.TrySetResult();
            var error = await Assert.ThrowsAsync<AccessDeniedException>(() => commit);
            Assert.Contains("initiating device", error.Message);
            Assert.Equal(0, await revoking.Projects.Where(project => project.Id == prepared.Project).Select(project => project.CurrentEpoch).SingleAsync(Ct));
            Assert.True(await revoking.Memberships.AnyAsync(member => member.ProjectId == prepared.Project && member.UserId == prepared.Member, Ct));
            Assert.False(await revoking.Updates.AnyAsync(update => update.ProjectId == prepared.Project, Ct));
        }
        finally { pause.Release.TrySetResult(); await Assert.ThrowsAsync<AccessDeniedException>(() => commit); }
    }

    [Fact]
    public async Task CommitKeepsTheDeliveryBarrierThroughPostCommitRevocation()
    {
        var prepared = await PrepareAsync();
        await using var db = database.CreateContext();
        var projects = new ProjectAccess(db, database.DeliveryGate);
        var service = new ProjectEpochAccess(db, projects);
        Task<IAsyncDisposable>? delivery = null;
        try
        {
            await service.CommitAsync(prepared.Owner, prepared.Device, new(prepared.Rotation, prepared.Member, [Grant(prepared.Device)]), Ct, async project =>
            {
                await using var verify = database.CreateContext();
                Assert.False(await verify.Memberships.AnyAsync(member => member.ProjectId == project && member.UserId == prepared.Member, Ct));
                delivery = projects.PauseDeliveryAsync(project, Ct).AsTask();
                Assert.False(delivery.IsCompleted);
            });
        }
        finally { if (delivery is not null) await (await delivery.WaitAsync(Ct)).DisposeAsync(); }
    }

    [Fact]
    public async Task UnregisteredMemberCanBeRemovedFromAProjectWithOtherRegisteredDevices()
    {
        var owner = await database.CreateUserAsync();
        var member = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (member.Id, MembershipRole.Contributor));
        await using var db = database.CreateContext();
        var projects = new ProjectAccess(db, database.DeliveryGate);
        await new ProjectEpochAccess(db, projects).RegisterAsync(owner.Id, device, project, 0, new byte[182], Ct);
        Assert.True(await projects.RemoveMemberAsync(owner.Id, project, member.Id, Ct));
        Assert.Single(await db.Memberships.Where(value => value.ProjectId == project).ToListAsync(Ct));
        Assert.Single(await db.ProjectDeviceAuthorizations.Where(value => value.ProjectId == project).ToListAsync(Ct));
    }

    private async Task<Prepared> PrepareAsync()
    {
        var owner = await database.CreateUserAsync();
        var member = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (member.Id, MembershipRole.Contributor));
        await using var db = database.CreateContext();
        var service = new ProjectEpochAccess(db, new(db, database.DeliveryGate));
        await service.RegisterAsync(owner.Id, device, project, 0, new byte[182], Ct);
        var rotation = Guid.NewGuid();
        await service.ReserveAsync(owner.Id, device, project, rotation, Ct);
        var envelope = TestData.Envelope(project, device, 1);
        envelope = envelope with { Header = (EnvelopeHeader.Parse(envelope.Header) with { Epoch = 1 }).ToBytes() };
        await service.StageAsync(owner.Id, device, rotation, envelope, Ct);
        return new(owner.Id, member.Id, device, project, rotation);
    }

    private static ProjectEpochGrant Grant(Guid device) => new(device, new byte[182], new byte[128]);
    private sealed record Prepared(Guid Owner, Guid Member, Guid Device, Guid Project, Guid Rotation);
}
