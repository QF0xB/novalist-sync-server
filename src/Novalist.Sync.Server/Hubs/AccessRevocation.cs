using Microsoft.AspNetCore.SignalR;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;

namespace Novalist.Sync.Server.Hubs;

// The single place that takes access away: first in database, so every later call
// is rejected, then on connections that are already open (security rule 4).
public sealed class AccessRevocation(
    DeviceRegistry devices,
    ProjectAccess projects,
    ConnectionRegistry connections,
    IHubContext<SyncHub, ISyncClient> hub)
{
    public async Task RevokeDeviceAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken)
    {
        await devices.RevokeAsync(userId, deviceId, cancellationToken);
        connections.AbortDevice(deviceId);
    }

    public async Task RemoveMemberAsync(Guid ownerId, Guid projectId, Guid memberId,
        CancellationToken cancellationToken)
    {
        await projects.RemoveMemberAsync(ownerId, projectId, memberId, cancellationToken);
        await NotifyRemovedAsync(projectId, memberId, cancellationToken);
    }

    public async Task NotifyRemovedAsync(Guid projectId, Guid memberId, CancellationToken cancellationToken)
    {
        connections.RevokeProject(memberId, projectId);
        foreach (var connectionId in connections.ConnectionIdsOf(memberId))
        {
            await hub.Groups.RemoveFromGroupAsync(connectionId, SyncHub.ProjectGroup(projectId), cancellationToken);
            await hub.Clients.Client(connectionId).ProjectAccessEnded(projectId);
        }
    }

    public async Task DeleteProjectAsync(Guid ownerId, Guid projectId, CancellationToken ct)
    {
        var users = await projects.DeleteAsync(ownerId, projectId, ct);
        foreach (var user in users)
            await NotifyRemovedAsync(projectId, user, ct);
    }
}
