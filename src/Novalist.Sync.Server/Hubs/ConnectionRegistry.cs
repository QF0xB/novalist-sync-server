using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Novalist.Sync.Server.Hubs;

// Which user and device each open hub connection belongs to, so access can be
// revoked immediately. Only in memory currently.
public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<string, Connection> _connections = new();

    public void Add(HubCallerContext context, Guid userId, Guid deviceId) => _connections[context.ConnectionId] = new Connection(context, userId, deviceId);

    public void Remove(string connectionId) => _connections.TryRemove(connectionId, out _);

    public void AbortDevice(Guid deviceId)
    {
        foreach (var connection in _connections.Values.Where(c => c.DeviceId == deviceId))
        {
            connection.Context.Abort();
        }
    }

    public void AbortUser(Guid userId)
    {
        foreach (var connection in _connections.Values.Where(c => c.UserId == userId)) connection.Context.Abort();
    }

    public IReadOnlyList<string> ConnectionIdsOf(Guid userId) =>
        _connections.Values
            .Where(c => c.UserId == userId)
            .Select(c => c.Context.ConnectionId)
            .ToList();

    public void RevokeProject(Guid userId, Guid projectId)
    {
        foreach (var connection in _connections.Values.Where(value => value.UserId == userId))
        {
            connection.Projects[projectId] = ProjectConnectionState.Revoked;
        }
    }

    public void JoinProject(string connectionId, Guid projectId)
    {
        if (!_connections.TryGetValue(connectionId, out var connection)) return;
        connection.Projects[projectId] = ProjectConnectionState.Joined;
    }

    public void LeaveProject(string connectionId, Guid projectId)
    {
        if (_connections.TryGetValue(connectionId, out var connection))
            connection.Projects.TryRemove(new(projectId, ProjectConnectionState.Joined));
    }

    public bool ProjectRevoked(string connectionId, Guid projectId) =>
        !_connections.TryGetValue(connectionId, out var connection) ||
        connection.Projects.TryGetValue(projectId, out var state) && state == ProjectConnectionState.Revoked;

    private enum ProjectConnectionState { Joined, Revoked }

    private sealed record Connection(HubCallerContext Context, Guid UserId, Guid DeviceId)
    {
        internal ConcurrentDictionary<Guid, ProjectConnectionState> Projects { get; } = new();
    }
}


