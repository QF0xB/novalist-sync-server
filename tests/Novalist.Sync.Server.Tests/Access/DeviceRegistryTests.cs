using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Access;

public class DeviceRegistryTests(DatabaseFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Register_NewDevice_MakesItActive()
    {
        var user = await database.CreateUserAsync();
        var deviceId = Guid.NewGuid();

        await RegisterAsync(user.Id, deviceId);

        await EnsureActiveAsync(user.Id, deviceId);
    }

    [Fact]
    public async Task Register_SameDeviceAgain_IsIdempotent()
    {
        var user = await database.CreateUserAsync();
        var deviceId = Guid.NewGuid();

        await RegisterAsync(user.Id, deviceId);
        await RegisterAsync(user.Id, deviceId);

        await using var db = database.CreateContext();
        Assert.Equal(1, await db.Devices.CountAsync(d => d.Id == deviceId, Ct));
    }

    [Fact]
    public async Task Register_DeviceIdOfAnotherUser_IsConflictAndNeverTakesItOver()
    {
        var owner = await database.CreateUserAsync();
        var deviceId = await database.CreateDeviceAsync(owner.Id);
        var other = await database.CreateUserAsync();

        await Assert.ThrowsAsync<ConflictException>(() => RegisterAsync(other.Id, deviceId));

        await using var db = database.CreateContext();
        Assert.Equal(owner.Id, (await db.Devices.SingleAsync(d => d.Id == deviceId, Ct)).UserId);
    }

    [Fact]
    public async Task Register_OwnRevokedDevice_IsDeniedAndDoesNotUndoTheRevocation()
    {
        var user = await database.CreateUserAsync();
        var deviceId = await database.CreateDeviceAsync(user.Id, revoked: true);

        await Assert.ThrowsAsync<AccessDeniedException>(() => RegisterAsync(user.Id, deviceId));

        await Assert.ThrowsAsync<AccessDeniedException>(() => EnsureActiveAsync(user.Id, deviceId));
    }

    [Fact]
    public async Task Register_ConcurrentlyBySameUser_AllSucceedWithOneRow()
    {
        var user = await database.CreateUserAsync();
        var deviceId = Guid.NewGuid();

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => RegisterAsync(user.Id, deviceId)));

        await using var db = database.CreateContext();
        Assert.Equal(1, await db.Devices.CountAsync(d => d.Id == deviceId, Ct));
    }

    [Fact]
    public async Task Register_ConcurrentlyByTwoUsers_ExactlyOneGetsTheId()
    {
        var alice = await database.CreateUserAsync();
        var bob = await database.CreateUserAsync();
        var deviceId = Guid.NewGuid();
        var attempts = Enumerable.Range(0, 10).Select(i => i % 2 == 0 ? alice.Id : bob.Id).ToList();

        var results = await Task.WhenAll(attempts.Select(async userId =>
        {
            try
            {
                await RegisterAsync(userId, deviceId);
                return (UserId: userId, Won: true);
            }
            catch (ConflictException)
            {
                return (UserId: userId, Won: false);
            }
        }));

        await using var db = database.CreateContext();
        var winner = (await db.Devices.SingleAsync(d => d.Id == deviceId, Ct)).UserId;
        Assert.All(results, r => Assert.Equal(r.UserId == winner, r.Won));
    }

    [Fact]
    public async Task EnsureActive_UnknownDevice_IsDenied()
    {
        var user = await database.CreateUserAsync();

        await Assert.ThrowsAsync<AccessDeniedException>(() => EnsureActiveAsync(user.Id, Guid.NewGuid()));
    }

    [Fact]
    public async Task EnsureActive_DeviceOfAnotherUser_IsDenied()
    {
        var owner = await database.CreateUserAsync();
        var deviceId = await database.CreateDeviceAsync(owner.Id);
        var other = await database.CreateUserAsync();

        await Assert.ThrowsAsync<AccessDeniedException>(() => EnsureActiveAsync(other.Id, deviceId));
    }

    [Fact]
    public async Task Revoke_ActiveDevice_RevokesIt()
    {
        var user = await database.CreateUserAsync();
        var deviceId = await database.CreateDeviceAsync(user.Id);

        await RevokeAsync(user.Id, deviceId);

        await Assert.ThrowsAsync<AccessDeniedException>(() => EnsureActiveAsync(user.Id, deviceId));
    }

    [Fact]
    public async Task Revoke_Twice_IsDeniedAndKeepsTheOriginalRevocationTime()
    {
        var user = await database.CreateUserAsync();
        var deviceId = await database.CreateDeviceAsync(user.Id);
        await RevokeAsync(user.Id, deviceId);
        var revokedAt = await RevokedAtAsync(deviceId);

        await Assert.ThrowsAsync<AccessDeniedException>(() => RevokeAsync(user.Id, deviceId));

        Assert.Equal(revokedAt, await RevokedAtAsync(deviceId));
    }

    [Fact]
    public async Task Revoke_DeviceOfAnotherUser_IsDeniedAndLeavesItActive()
    {
        var owner = await database.CreateUserAsync();
        var deviceId = await database.CreateDeviceAsync(owner.Id);
        var other = await database.CreateUserAsync();

        await Assert.ThrowsAsync<AccessDeniedException>(() => RevokeAsync(other.Id, deviceId));

        await EnsureActiveAsync(owner.Id, deviceId);
    }

    private async Task RegisterAsync(Guid userId, Guid deviceId)
    {
        await using var db = database.CreateContext();
        await new DeviceRegistry(db).RegisterAsync(userId, deviceId, Ct);
    }

    private async Task EnsureActiveAsync(Guid userId, Guid deviceId)
    {
        await using var db = database.CreateContext();
        await new DeviceRegistry(db).EnsureActiveAsync(userId, deviceId, Ct);
    }

    private async Task RevokeAsync(Guid userId, Guid deviceId)
    {
        await using var db = database.CreateContext();
        await new DeviceRegistry(db).RevokeAsync(userId, deviceId, Ct);
    }

    private async Task<DateTimeOffset?> RevokedAtAsync(Guid deviceId)
    {
        await using var db = database.CreateContext();
        return await db.Devices.Where(d => d.Id == deviceId).Select(d => d.RevokedAt).SingleAsync(Ct);
    }
}
