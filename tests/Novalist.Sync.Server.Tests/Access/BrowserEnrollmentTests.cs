using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Tests.Infrastructure;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class BrowserEnrollmentTests(DatabaseFixture database)
{
    [Theory]
    [InlineData(null)]
    [InlineData("nonce")]
    [InlineData("subject")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("signature")]
    [InlineData("missing-mfa")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("keys")]
    [InlineData("recovery")]
    public async Task BrowserMfaMustMatchTheAccountAndBothDeviceKeys(string? invalid)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = new SyncServer(database).WithWebHostBuilder(builder =>
            builder.UseSetting("Authentication:NativeClient:ClientId", "novalist"));
        using var http = server.CreateClient();
        var subject = Guid.NewGuid().ToString();
        http.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(subject));
        var account = await http.GetFromJsonAsync<DeviceRegistrationContext>(Protocol.DeviceRegistrationPath, ct);
        Assert.NotNull(account);
        using var device = new TestRecoveryDevice();
        using var recovery = new TestRecoveryDevice();
        using var response = await http.PostAsJsonAsync("/devices/enrollment", new DeviceEnrollmentRequest(device.Keys, recovery.Keys), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var challenge = await response.Content.ReadFromJsonAsync<DeviceEnrollmentChallenge>(ct);
        Assert.NotNull(challenge);
        var claims = new Dictionary<string, object>
        {
            ["sub"] = invalid == "subject" ? "another-account" : subject,
            ["nonce"] = invalid == "nonce" ? "another-challenge" : challenge.Nonce,
            ["amr"] = new[] { invalid == "missing-mfa" ? "pwd" : "mfa" },
            ["auth_time"] = DateTimeOffset.UtcNow.AddMinutes(invalid == "stale" ? -6 : invalid == "future" ? 2 : 0).ToUnixTimeSeconds()
        };
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = invalid == "issuer" ? "https://other.example" : TestTokens.Issuer,
            Audience = invalid == "audience" ? TestTokens.Audience : "novalist",
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = claims,
            SigningCredentials = new(invalid == "signature" ? new SymmetricSecurityKey(new byte[32]) : TestTokens.SigningKey, SecurityAlgorithms.HmacSha256)
        });
        using var substituted = new TestRecoveryDevice();
        var ordinary = invalid == "keys" ? substituted : device;
        var backup = invalid == "recovery" ? substituted : recovery;
        var request = TestEnrollment.First(account.UserId, ordinary.Keys, ordinary.Signing, backup) with
        { EnrollmentProof = new(challenge.Id, token) };
        using var enrolled = await http.PutAsJsonAsync(Protocol.VerifiedDevicesPath, request, ct);
        Assert.Equal(invalid is null ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden, enrolled.StatusCode);
        await using var db = database.CreateContext();
        Assert.Equal(invalid is null ? 2 : 0, await db.Devices.CountAsync(value => value.UserId == account.UserId && value.SigningPublicKey != null, ct));
        if (invalid is not null) return;
        using var replayed = await http.PutAsJsonAsync(Protocol.VerifiedDevicesPath, request, ct);
        Assert.Equal(HttpStatusCode.Forbidden, replayed.StatusCode);
        using var retry = await http.PutAsJsonAsync(Protocol.VerifiedDevicesPath, request with { EnrollmentProof = null }, ct);
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
    }
}
