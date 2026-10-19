using Novalist.Sync.Server.Hubs;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Hubs;

public class ConnectionRegistryTests
{
    private readonly ConnectionRegistry _registry = new();

    [Fact]
    public void AbortDevice_AbortsEveryConnectionOfThatDeviceOnly()
    {
        var userId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var first = Add(userId, deviceId);
        var second = Add(userId, deviceId);
        var otherDevice = Add(userId, Guid.NewGuid());

        _registry.AbortDevice(deviceId);

        Assert.True(first.Aborted);
        Assert.True(second.Aborted);
        Assert.True(first.ConnectionAborted.IsCancellationRequested);
        Assert.False(otherDevice.Aborted);
    }

    [Fact]
    public void AbortDevice_IgnoresRemovedConnections()
    {
        var deviceId = Guid.NewGuid();
        var closed = Add(Guid.NewGuid(), deviceId);
        _registry.Remove(closed.ConnectionId);

        _registry.AbortDevice(deviceId);

        Assert.False(closed.Aborted);
    }

    [Fact]
    public void ConnectionIdsOf_ReturnsAllConnectionsOfTheUserAcrossDevices()
    {
        var userId = Guid.NewGuid();
        var laptop = Add(userId, Guid.NewGuid());
        var desktop = Add(userId, Guid.NewGuid());
        Add(Guid.NewGuid(), Guid.NewGuid());

        var ids = _registry.ConnectionIdsOf(userId);

        Assert.Equivalent(new[] { laptop.ConnectionId, desktop.ConnectionId }, ids, strict: true);
    }

    [Fact]
    public void Remove_UnknownConnection_LeavesOthersUntouched()
    {
        var userId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var existing = Add(userId, deviceId);

        _registry.Remove("never-added");

        Assert.Equivalent(new[] { existing.ConnectionId }, _registry.ConnectionIdsOf(userId), strict: true);
        _registry.AbortDevice(deviceId);
        Assert.True(existing.Aborted);
    }

    [Fact]
    public async Task ConcurrentAddAndRemove_KeepsTheRegistryConsistent()
    {
        var userId = Guid.NewGuid();

        await Task.WhenAll(Enumerable.Range(0, 1000).Select(i => Task.Run(() =>
        {
            var context = Add(userId, Guid.NewGuid());
            if (i % 2 == 0)
            {
                _registry.Remove(context.ConnectionId);
            }
        }, TestContext.Current.CancellationToken)));

        Assert.Equal(500, _registry.ConnectionIdsOf(userId).Count);
    }

    private FakeHubCallerContext Add(Guid userId, Guid deviceId)
    {
        var context = new FakeHubCallerContext();
        _registry.Add(context, userId, deviceId);
        return context;
    }
}
