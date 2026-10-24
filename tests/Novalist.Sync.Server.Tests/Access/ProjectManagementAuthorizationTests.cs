using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class ProjectManagementAuthorizationTests(DatabaseFixture database)
{
    [Theory]
    [InlineData("delete")]
    [InlineData("commit")]
    public async Task UnauthorizedManagementIsRejectedWithoutWaitingForLiveDelivery(string operation)
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var other = await database.CreateUserAsync();
        var device = await database.CreateDeviceAsync(owner.Id);
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (other.Id, MembershipRole.Contributor));
        var rotation = Guid.NewGuid();
        await using var db = database.CreateContext();
        var projects = new ProjectAccess(db, database.DeliveryGate);
        var epochs = new ProjectEpochAccess(db, projects);
        await epochs.ReserveAsync(owner.Id, device, project, rotation, ct);
        await using var delivery = await database.DeliveryGate.ProjectAsync(project, ct);
        await Assert.ThrowsAsync<AccessDeniedException>(() => operation == "delete"
            ? projects.DeleteAsync(other.Id, project, ct)
            : epochs.CommitAsync(other.Id, device, new(rotation, owner.Id, []), ct))
            .WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.True(await db.ProjectEpochStages.AnyAsync(stage => stage.Id == rotation, ct));
        Assert.Equal(2, await db.Memberships.CountAsync(member => member.ProjectId == project, ct));
    }
}
