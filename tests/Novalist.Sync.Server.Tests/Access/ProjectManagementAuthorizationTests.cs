using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class ProjectManagementAuthorizationTests(DatabaseFixture database)
{
    [Fact]
    public async Task UnauthorizedManagementIsRejectedWithoutWaitingForLiveDelivery()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await database.CreateUserAsync();
        var other = await database.CreateUserAsync();
        var project = await database.CreateProjectAsync((owner.Id, MembershipRole.Owner), (other.Id, MembershipRole.Contributor));
        await using var db = database.CreateContext();
        var projects = new ProjectAccess(db, database.DeliveryGate);
        await using var delivery = await database.DeliveryGate.ProjectAsync(project, ct);
        await Assert.ThrowsAsync<AccessDeniedException>(() => projects.DeleteAsync(other.Id, project, ct))
            .WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(2, await db.Memberships.CountAsync(member => member.ProjectId == project, ct));
    }
}
