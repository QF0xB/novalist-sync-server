using System.Net;
using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class DeviceEndpointsTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly SyncServer _server = new(database);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Put_NewDevice_RegistersItForTheTokensUser()
    {
        var subject = Guid.NewGuid().ToString();
        var deviceId = Guid.NewGuid();

        var response = await PutAsync(TestTokens.Create(subject), deviceId);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var db = database.CreateContext();
        var user = await db.Users.SingleAsync(u => u.Issuer == TestTokens.Issuer && u.Subject == subject, Ct);
        var device = await db.Devices.SingleAsync(d => d.Id == deviceId, Ct);
        Assert.Equal(user.Id, device.UserId);
        Assert.Null(device.RevokedAt);
    }

    [Fact]
    public async Task Put_SameDeviceTwice_IsIdempotent()
    {
        var token = TestTokens.Create(Guid.NewGuid().ToString());
        var deviceId = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(token, deviceId)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync(token, deviceId)).StatusCode);
    }

    [Fact]
    public async Task Put_DeviceIdOfAnotherUser_IsConflict()
    {
        var owner = await database.CreateUserAsync();
        var deviceId = await database.CreateDeviceAsync(owner.Id);

        var response = await PutAsync(TestTokens.Create(Guid.NewGuid().ToString()), deviceId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var db = database.CreateContext();
        Assert.Equal(owner.Id, (await db.Devices.SingleAsync(d => d.Id == deviceId, Ct)).UserId);
    }

    [Fact]
    public async Task Put_OwnRevokedDevice_IsForbiddenAndStaysRevoked()
    {
        var user = await database.CreateUserAsync();
        var deviceId = await database.CreateDeviceAsync(user.Id, revoked: true);

        var response = await PutAsync(TestTokens.Create(user.Subject), deviceId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var db = database.CreateContext();
        Assert.NotNull((await db.Devices.SingleAsync(d => d.Id == deviceId, Ct)).RevokedAt);
    }

    [Fact]
    public async Task Put_WithoutToken_IsUnauthorized()
    {
        var response = await PutAsync(token: null, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Put_WithInvalidDeviceId_IsNotFound()
    {
        using var client = _server.CreateClient(TestTokens.Create(Guid.NewGuid().ToString()));

        var response = await client.PutAsync($"{Protocol.DevicesPath}/not-a-guid", content: null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<HttpResponseMessage> PutAsync(string? token, Guid deviceId)
    {
        using var client = _server.CreateClient(token);
        return await client.PutAsync($"{Protocol.DevicesPath}/{deviceId}", content: null, Ct);
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();
}
