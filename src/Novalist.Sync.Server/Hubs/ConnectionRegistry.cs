using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;

namespace Novalist.Sync.Server.Hubs;

// Which user and device each open hub connection belongs to, so access can be
// revoked immediately. Only in memory currently.
// TODO: Make multi server compatible 
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

    public IReadOnlyList<string> ConnectionIdsOf(Guid userId) =>
        _connections.Values
            .Where(c => c.UserId == userId)
            .Select(c => c.Context.ConnectionId)
            .ToList();

    private sealed record Connection(HubCallerContext Context, Guid UserId, Guid DeviceId);
}