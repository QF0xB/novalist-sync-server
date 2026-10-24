using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Tests.Infrastructure;
using NSec.Cryptography;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class DeviceEnrollmentTests(DatabaseFixture database)
{
    [Fact]
    public void SigningKeyThumbprintMatchesRfc8037AppendixA3()
    {
        var key = Base64UrlEncoder.DecodeBytes("11qYAYKxCrfVS_7TyWQHOg7hcvPapiMlrwIaaPcHURo");
        Assert.Equal("kPrK_qmxVWaYVA9wwBF6Iuo3vVzz7TxHCTwXBygrS4k", DeviceEnrollmentPolicy.SigningKeyThumbprint(key));
    }

    [Theory]
    [InlineData("missing-mfa")]
    [InlineData("missing-time")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("missing-binding")]
    [InlineData("wrong-binding")]
    [InlineData("malformed-binding")]
    public async Task FirstDeviceRejectsUnboundOrStaleAuthenticationWithoutRegisteringEitherDevice(string invalid)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database);
        var subject = Guid.NewGuid().ToString();
        using var http = server.CreateClient(TestTokens.Create(subject));
        var account = await http.GetFromJsonAsync<DeviceRegistrationContext>(Protocol.DeviceRegistrationPath, ct);
        Assert.NotNull(account);
        using var signing = Key.Create(SignatureAlgorithm.Ed25519);
        using var recoveryDevice = new TestRecoveryDevice();
        using var receiving = Key.Create(KeyAgreementAlgorithm.X25519);
        var keys = new DevicePublicKeys(Guid.NewGuid(), signing.PublicKey.Export(KeyBlobFormat.RawPublicKey), receiving.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        var claims = TestTokens.EnrollmentClaims(keys.SigningPublicKey);
        switch (invalid)
        {
            case "missing-mfa": claims.Remove("amr"); break;
            case "missing-time": claims.Remove("auth_time"); break;
            case "stale": claims["auth_time"] = DateTimeOffset.UtcNow.AddMinutes(-6).ToUnixTimeSeconds(); break;
            case "future": claims["auth_time"] = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds(); break;
            case "missing-binding": claims.Remove("cnf"); break;
            case "wrong-binding": claims["cnf"] = new Dictionary<string, object> { ["jkt"] = DeviceEnrollmentPolicy.SigningKeyThumbprint(new byte[32]) }; break;
            case "malformed-binding": claims["cnf"] = "not-json"; break;
        }
        http.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(subject, additionalClaims: claims));
        using var response = await http.PutAsJsonAsync(Protocol.VerifiedDevicesPath, TestEnrollment.First(account.UserId, keys, signing, recoveryDevice), ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var db = database.CreateContext();
        Assert.False(await db.Devices.AnyAsync(device => device.UserId == account.UserId, ct));
    }

    [Theory]
    [InlineData("missing", HttpStatusCode.Forbidden)]
    [InlineData("self-proof", HttpStatusCode.Forbidden)]
    [InlineData("link-proof", HttpStatusCode.Forbidden)]
    [InlineData("same-id", HttpStatusCode.Conflict)]
    [InlineData("same-key", HttpStatusCode.Conflict)]
    [InlineData("same-receiving-key", HttpStatusCode.Conflict)]
    [InlineData("collision", HttpStatusCode.Conflict)]
    public async Task InvalidRecoveryEnrollmentRollsBackBothDevices(string invalid, HttpStatusCode expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database);
        var subject = Guid.NewGuid().ToString();
        using var http = server.CreateClient(TestTokens.Create(subject));
        var account = await http.GetFromJsonAsync<DeviceRegistrationContext>(Protocol.DeviceRegistrationPath, ct);
        Assert.NotNull(account);
        using var signing = Key.Create(SignatureAlgorithm.Ed25519);
        using var recoveryDevice = new TestRecoveryDevice();
        using var receiving = Key.Create(KeyAgreementAlgorithm.X25519);
        var keys = new DevicePublicKeys(Guid.NewGuid(), signing.PublicKey.Export(KeyBlobFormat.RawPublicKey), receiving.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        var request = TestEnrollment.First(account.UserId, keys, signing, recoveryDevice);
        var recovery = request.RecoveryDevice ?? throw new InvalidOperationException("Missing test recovery device.");
        if (invalid == "collision")
        {
            var other = await database.CreateUserAsync();
            await using var collision = database.CreateContext();
            collision.Devices.Add(new() { Id = recovery.Keys.DeviceId, UserId = other.Id, CreatedAt = DateTimeOffset.UtcNow });
            await collision.SaveChangesAsync(ct);
        }
        request = request with
        {
            RecoveryDevice = invalid switch
            {
                "missing" => null,
                "self-proof" => recovery with { Signature = new byte[64] },
                "link-proof" => recovery with { LinkingSignature = new byte[64] },
                "same-id" => recovery with { Keys = recovery.Keys with { DeviceId = keys.DeviceId } },
                "same-key" => recovery with { Keys = recovery.Keys with { SigningPublicKey = keys.SigningPublicKey } },
                "same-receiving-key" => recovery with { Keys = recovery.Keys with { ReceivingPublicKey = keys.ReceivingPublicKey } },
                _ => recovery
            }
        };
        TestEnrollment.Authorize(http, keys.SigningPublicKey);
        using var response = await http.PutAsJsonAsync(Protocol.VerifiedDevicesPath, request, ct);
        Assert.Equal(expected, response.StatusCode);
        await using var db = database.CreateContext();
        Assert.False(await db.Devices.AnyAsync(device => device.UserId == account.UserId, ct));
    }
}
