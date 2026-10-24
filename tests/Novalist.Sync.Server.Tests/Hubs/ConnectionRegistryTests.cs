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

    [Fact]
    public void RevocationIsScopedToTheUserAndProjectAndCanBeRegranted()
    {
        var user = Guid.NewGuid();
        var first = Add(user, Guid.NewGuid());
        var second = Add(user, Guid.NewGuid());
        var other = Add(Guid.NewGuid(), Guid.NewGuid());
        var project = Guid.NewGuid();
        _registry.RevokeProject(user, project);
        Assert.True(_registry.ProjectRevoked(first.ConnectionId, project));
        Assert.True(_registry.ProjectRevoked(second.ConnectionId, project));
        Assert.False(_registry.ProjectRevoked(other.ConnectionId, project));
        Assert.False(_registry.ProjectRevoked(first.ConnectionId, Guid.NewGuid()));
        Assert.True(_registry.ProjectRevoked("unknown", project));
        _registry.JoinProject(first.ConnectionId, project);
        Assert.False(_registry.ProjectRevoked(first.ConnectionId, project));
        Assert.True(_registry.ProjectRevoked(second.ConnectionId, project));
        _registry.JoinProject("unknown", project);
        Assert.True(_registry.ProjectRevoked("unknown", project));
    }

    [Fact]
    public void AbortUserAbortsAllTheirDevicesAndLeavesOtherUsersConnected()
    {
        var user = Guid.NewGuid();
        var first = Add(user, Guid.NewGuid());
        var second = Add(user, Guid.NewGuid());
        var other = Add(Guid.NewGuid(), Guid.NewGuid());
        _registry.AbortUser(user);
        Assert.True(first.Aborted);
        Assert.True(second.Aborted);
        Assert.False(other.Aborted);
    }

    [Fact]
    public void PresenceRecipientsRequireAnActiveDeviceAndAJoinedUnrevokedProject()
    {
        var user = Guid.NewGuid();
        var device = Guid.NewGuid();
        var sender = Add(user, device);
        var joined = Add(user, device);
        var inactive = Add(user, Guid.NewGuid());
        Add(user, device);
        var project = Guid.NewGuid();
        foreach (var connection in new[] { sender, joined, inactive }) _registry.JoinProject(connection.ConnectionId, project);
        Assert.Equal([joined.ConnectionId], _registry.PresenceRecipients(project, new HashSet<Guid> { device }, _registry.PresenceTargets(project, sender.ConnectionId, 256)));
        _registry.LeaveProject(joined.ConnectionId, project);
        Assert.Empty(_registry.PresenceRecipients(project, new HashSet<Guid> { device }, _registry.PresenceTargets(project, sender.ConnectionId, 256)));
        _registry.JoinProject(joined.ConnectionId, project);
        _registry.RevokeProject(user, project);
        Assert.Empty(_registry.PresenceRecipients(project, new HashSet<Guid> { device }, _registry.PresenceTargets(project, sender.ConnectionId, 256)));
    }

    [Fact]
    public void PresenceSnapshotDoesNotGrowAndRechecksDepartedConnections()
    {
        var user = Guid.NewGuid();
        var device = Guid.NewGuid();
        var project = Guid.NewGuid();
        var first = Add(user, device);
        _registry.JoinProject(first.ConnectionId, project);
        var snapshot = _registry.PresenceTargets(project, "sender", 256);
        var later = Add(user, device);
        _registry.JoinProject(later.ConnectionId, project);
        Assert.Equal([first.ConnectionId], _registry.PresenceRecipients(project, new HashSet<Guid> { device }, snapshot));
        _registry.LeaveProject(first.ConnectionId, project);
        Assert.Empty(_registry.PresenceRecipients(project, new HashSet<Guid> { device }, snapshot));
    }

    [Fact]
    public void RevocationBlocksJoinedRecipientsUntilAccessIsRegranted()
    {
        var user = Guid.NewGuid();
        var device = Guid.NewGuid();
        var joined = Add(user, device);
        var otherDevice = Guid.NewGuid();
        var other = Add(Guid.NewGuid(), otherDevice);
        var project = Guid.NewGuid();
        var otherProject = Guid.NewGuid();
        var active = new HashSet<Guid> { device, otherDevice };
        foreach (var connection in new[] { joined, other })
        {
            _registry.JoinProject(connection.ConnectionId, project);
            _registry.JoinProject(connection.ConnectionId, otherProject);
        }
        _registry.RevokeProject(user, project);
        Assert.Equal([other.ConnectionId], _registry.PresenceRecipients(project, active, _registry.PresenceTargets(project, "sender", 256)));
        Assert.Equivalent(new[] { joined.ConnectionId, other.ConnectionId }, _registry.PresenceRecipients(otherProject, active, _registry.PresenceTargets(otherProject, "sender", 256)));
        _registry.LeaveProject(joined.ConnectionId, project);
        Assert.True(_registry.ProjectRevoked(joined.ConnectionId, project));
        Assert.Equal([other.ConnectionId], _registry.PresenceRecipients(project, active, _registry.PresenceTargets(project, "sender", 256)));
        _registry.JoinProject(joined.ConnectionId, project);
        Assert.False(_registry.ProjectRevoked(joined.ConnectionId, project));
        Assert.Equivalent(new[] { joined.ConnectionId, other.ConnectionId }, _registry.PresenceRecipients(project, active, _registry.PresenceTargets(project, "sender", 256)));
    }

    private FakeHubCallerContext Add(Guid userId, Guid deviceId)
    {
        var context = new FakeHubCallerContext();
        _registry.Add(context, userId, deviceId);
        return context;
    }
}
