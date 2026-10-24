using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Hubs;
using Novalist.Sync.Server.Tests.Infrastructure;
using NSec.Cryptography;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class DeviceRecoveryTests(DatabaseFixture database)
{
    [Fact]
    public async Task RecoveryDeviceLinksAReplacementAfterTheWorkingDeviceIsRevokedWithoutReopeningBootstrap()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database);
        var subject = Guid.NewGuid().ToString();
        var token = TestTokens.Create(subject);
        using var http = server.CreateClient(token);
        var account = await http.GetFromJsonAsync<DeviceRegistrationContext>(Protocol.DeviceRegistrationPath, ct);
        Assert.NotNull(account);
        using var signing = Key.Create(SignatureAlgorithm.Ed25519);
        using var recoveryDevice = new TestRecoveryDevice();
        using var receiving = Key.Create(KeyAgreementAlgorithm.X25519);
        var root = Keys(signing, receiving);
        var recovery = recoveryDevice.Keys;
        var request = TestEnrollment.First(account.UserId, root, signing, recoveryDevice);
        TestEnrollment.Authorize(http, root.SigningPublicKey);
        using var enrolled = await http.PutAsJsonAsync(Protocol.VerifiedDevicesPath, request, ct);
        Assert.Equal(HttpStatusCode.NoContent, enrolled.StatusCode);
        await RevokeAsync(server, account.UserId, root.DeviceId, ct);
        using var replacementSigning = Key.Create(SignatureAlgorithm.Ed25519);
        var replacement = Keys(replacementSigning, receiving);
        var unlinked = new VerifiedDeviceRegistration(replacement,
            SignatureAlgorithm.Ed25519.Sign(replacementSigning, DeviceProof.EncodeRegistration(SyncServer.PublicUrl, account.UserId, replacement)));
        TestEnrollment.Authorize(http, replacement.SigningPublicKey);
        using var reset = await http.PutAsJsonAsync(Protocol.VerifiedDevicesPath, unlinked, ct);
        Assert.Equal(HttpStatusCode.Forbidden, reset.StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var linked = unlinked with
        {
            LinkedByDeviceId = recovery.DeviceId,
            LinkingSignature = SignatureAlgorithm.Ed25519.Sign(recoveryDevice.Signing,
                DeviceProof.EncodeLink(SyncServer.PublicUrl, account.UserId, recovery.DeviceId, replacement))
        };
        using var recovered = await http.PutAsJsonAsync(Protocol.VerifiedDevicesPath, linked, ct);
        Assert.Equal(HttpStatusCode.NoContent, recovered.StatusCode);
        await using var hub = server.CreateHubConnection(token, replacement.DeviceId);
        await hub.StartAsync(ct);
        var challenge = await hub.InvokeAsync<DeviceConnectionChallenge>("GetDeviceChallenge", ct);
        await hub.InvokeAsync("AuthenticateDevice", SignatureAlgorithm.Ed25519.Sign(replacementSigning, DeviceProof.EncodeConnection(challenge)), ct);
        await hub.InvokeAsync("CreateProject", Guid.NewGuid(), ct);
        await using var db = database.CreateContext();
        Assert.Equal(3, await db.Devices.CountAsync(device => device.UserId == account.UserId, ct));
        Assert.Equal(recovery.DeviceId, (await db.Devices.SingleAsync(device => device.Id == replacement.DeviceId, ct)).LinkedByDeviceId);
        await RevokeAsync(server, account.UserId, recovery.DeviceId, ct);
        await RevokeAsync(server, account.UserId, replacement.DeviceId, ct);
        var next = replacement with { DeviceId = Guid.NewGuid() };
        TestEnrollment.Authorize(http, next.SigningPublicKey);
        using var resetAll = await http.PutAsJsonAsync(Protocol.VerifiedDevicesPath, new VerifiedDeviceRegistration(next,
            SignatureAlgorithm.Ed25519.Sign(replacementSigning, DeviceProof.EncodeRegistration(SyncServer.PublicUrl, account.UserId, next))), ct);
        Assert.Equal(HttpStatusCode.Forbidden, resetAll.StatusCode);
        var revokedRecovery = new VerifiedDeviceRegistration(next,
            SignatureAlgorithm.Ed25519.Sign(replacementSigning, DeviceProof.EncodeRegistration(SyncServer.PublicUrl, account.UserId, next)),
            recovery.DeviceId, SignatureAlgorithm.Ed25519.Sign(recoveryDevice.Signing, DeviceProof.EncodeLink(SyncServer.PublicUrl, account.UserId, recovery.DeviceId, next)));
        using var denied = await http.PutAsJsonAsync(Protocol.VerifiedDevicesPath, revokedRecovery, ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    private static DevicePublicKeys Keys(Key signing, Key receiving) =>
        new(Guid.NewGuid(), signing.PublicKey.Export(KeyBlobFormat.RawPublicKey), receiving.PublicKey.Export(KeyBlobFormat.RawPublicKey));

    private static async Task RevokeAsync(SyncServer server, Guid user, Guid device, CancellationToken ct)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AccessRevocation>().RevokeDeviceAsync(user, device, ct);
    }
}
