using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Hubs;
using Novalist.Sync.Server.Tests.Infrastructure;
using NSec.Cryptography;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class VerifiedDeviceTests(DatabaseFixture database) : IAsyncDisposable
{
    private readonly SyncServer server = new(database);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InvalidServerAddressIsNotReportedAsAnInvalidDeviceProof()
    {
        await using var misconfigured = server.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Collaboration:ServerUrl", "not-a-server-url");
        });
        using var client = misconfigured.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Create(Guid.NewGuid().ToString()));
        var keys = new DevicePublicKeys(Guid.NewGuid(), new byte[32], new byte[32]);
        using var response = await client.PutAsJsonAsync(Protocol.VerifiedDevicesPath,
            new VerifiedDeviceRegistration(keys, new byte[64]), Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task ALinkedDeviceWorksButRevokedKeysCannotEnrollAnotherDeviceOrClaimItsSibling()
    {
        var token = TestTokens.Create(Guid.NewGuid().ToString());
        using var http = server.CreateClient(token);
        var account = await http.GetFromJsonAsync<DeviceRegistrationContext>("/devices/registration", Ct);
        Assert.NotNull(account);
        using var firstSigning = Key.Create(SignatureAlgorithm.Ed25519);
        using var recoveryDevice = new TestRecoveryDevice();
        using var secondSigning = Key.Create(SignatureAlgorithm.Ed25519);
        using var receiving = Key.Create(KeyAgreementAlgorithm.X25519);
        var first = new DevicePublicKeys(Guid.NewGuid(), firstSigning.PublicKey.Export(KeyBlobFormat.RawPublicKey), receiving.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        var second = new DevicePublicKeys(Guid.NewGuid(), secondSigning.PublicKey.Export(KeyBlobFormat.RawPublicKey), receiving.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        TestEnrollment.Authorize(http, first.SigningPublicKey);
        using var firstResult = await http.PutAsJsonAsync("/devices/verified",
            TestEnrollment.First(account.UserId, first, firstSigning, recoveryDevice), Ct);
        Assert.Equal(HttpStatusCode.NoContent, firstResult.StatusCode);
        var unlinked = new VerifiedDeviceRegistration(second, SignatureAlgorithm.Ed25519.Sign(secondSigning, DeviceProof.EncodeRegistration(SyncServer.PublicUrl, account.UserId, second)));
        using var unlinkedResult = await http.PutAsJsonAsync("/devices/verified", unlinked, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, unlinkedResult.StatusCode);
        var linked = unlinked with
        {
            LinkedByDeviceId = first.DeviceId,
            LinkingSignature = SignatureAlgorithm.Ed25519.Sign(firstSigning, DeviceProof.EncodeLink(SyncServer.PublicUrl, account.UserId, first.DeviceId, second))
        };
        using var linkedResult = await http.PutAsJsonAsync("/devices/verified", linked, Ct);
        Assert.Equal(HttpStatusCode.NoContent, linkedResult.StatusCode);
        await using var sibling = server.CreateHubConnection(token, second.DeviceId);
        await sibling.StartAsync(Ct);
        var challenge = await sibling.InvokeAsync<DeviceConnectionChallenge>("GetDeviceChallenge", Ct);
        await sibling.InvokeAsync("AuthenticateDevice", SignatureAlgorithm.Ed25519.Sign(secondSigning, DeviceProof.EncodeConnection(challenge)), Ct);
        await sibling.InvokeAsync("CreateProject", Guid.NewGuid(), Ct);

        await using (var scope = server.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AccessRevocation>().RevokeDeviceAsync(account.UserId, first.DeviceId, Ct);
        var extra = second with { DeviceId = Guid.NewGuid() };
        using var reenroll = await http.PutAsJsonAsync("/devices/verified", new VerifiedDeviceRegistration(extra,
            SignatureAlgorithm.Ed25519.Sign(secondSigning, DeviceProof.EncodeRegistration(SyncServer.PublicUrl, account.UserId, extra)), first.DeviceId,
            SignatureAlgorithm.Ed25519.Sign(firstSigning, DeviceProof.EncodeLink(SyncServer.PublicUrl, account.UserId, first.DeviceId, extra))), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, reenroll.StatusCode);
        await using var impersonation = server.CreateHubConnection(token, second.DeviceId);
        await impersonation.StartAsync(Ct);
        var otherChallenge = await impersonation.InvokeAsync<DeviceConnectionChallenge>("GetDeviceChallenge", Ct);
        await Assert.ThrowsAsync<HubException>(() => impersonation.InvokeAsync("AuthenticateDevice",
            SignatureAlgorithm.Ed25519.Sign(firstSigning, DeviceProof.EncodeConnection(otherChallenge)), Ct));
    }

    [Fact]
    public async Task VerifiedAccountCannotFallBackToUnverifiedDevicesOrInvitationEndpoints()
    {
        var token = TestTokens.Create(Guid.NewGuid().ToString());
        using var http = server.CreateClient(token);
        var legacyId = Guid.NewGuid();
        using var legacyRegistration = await http.PutAsync($"/devices/{legacyId}", null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, legacyRegistration.StatusCode);
        await using var legacy = server.CreateHubConnection(token, legacyId);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        legacy.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await legacy.StartAsync(Ct);
        var project = Guid.NewGuid();
        await legacy.InvokeAsync("CreateProject", project, Ct);
        var registration = await http.GetFromJsonAsync<DeviceRegistrationContext>("/devices/registration", Ct);
        Assert.NotNull(registration);
        using var signing = Key.Create(SignatureAlgorithm.Ed25519);
        using var recoveryDevice = new TestRecoveryDevice();
        using var receiving = Key.Create(KeyAgreementAlgorithm.X25519);
        var keys = new DevicePublicKeys(Guid.NewGuid(), signing.PublicKey.Export(KeyBlobFormat.RawPublicKey), receiving.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        var request = TestEnrollment.First(registration.UserId, keys, signing, recoveryDevice);
        TestEnrollment.Authorize(http, keys.SigningPublicKey);
        using var response = await http.PutAsJsonAsync("/devices/verified", request, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var legacyAgain = await http.PutAsync($"/devices/{Guid.NewGuid()}", null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, legacyAgain.StatusCode);
        using var invitation = await http.PostAsJsonAsync($"/projects/{project}/invitations", new CreateInvitation(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1)), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, invitation.StatusCode);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task RegisteredKeysRequireFreshConnectionProofAndCannotBeReplaced()
    {
        var token = TestTokens.Create(Guid.NewGuid().ToString());
        using var http = server.CreateClient(token);
        var registration = await http.GetFromJsonAsync<DeviceRegistrationContext>("/devices/registration", Ct);
        Assert.NotNull(registration);
        using var signing = Key.Create(SignatureAlgorithm.Ed25519);
        using var recoveryDevice = new TestRecoveryDevice();
        using var receiving = Key.Create(KeyAgreementAlgorithm.X25519);
        var id = Guid.NewGuid();
        var keys = new DevicePublicKeys(id, signing.PublicKey.Export(KeyBlobFormat.RawPublicKey), receiving.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        var request = TestEnrollment.First(registration.UserId, keys, signing, recoveryDevice);
        TestEnrollment.Authorize(http, keys.SigningPublicKey);
        using var response = await http.PutAsJsonAsync("/devices/verified", request, Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var retry = await http.PutAsJsonAsync("/devices/verified", request with { RecoveryDevice = null }, Ct);
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);

        await using var first = server.CreateHubConnection(token, id);
        await first.StartAsync(Ct);
        await Assert.ThrowsAsync<HubException>(() => first.InvokeAsync("CreateProject", Guid.NewGuid(), Ct));
        var project = Guid.NewGuid();
        var invitation = new CreateInvitation(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1));
        await Assert.ThrowsAsync<HubException>(() => first.InvokeAsync("CreateInvitation", project, invitation, Ct));
        var challenge = await first.InvokeAsync<DeviceConnectionChallenge>("GetDeviceChallenge", Ct);
        Assert.Equal(registration.UserId, challenge.UserId);
        Assert.Equal(id, challenge.DeviceId);
        var signature = SignatureAlgorithm.Ed25519.Sign(signing, DeviceProof.EncodeConnection(challenge));
        await first.InvokeAsync("AuthenticateDevice", signature, Ct);
        await first.InvokeAsync("CreateProject", project, Ct);
        await first.InvokeAsync("CreateInvitation", project, invitation, Ct);
        await first.InvokeAsync("RevokeInvitation", project, invitation.InvitationId, Ct);

        await using var second = server.CreateHubConnection(token, id);
        await second.StartAsync(Ct);
        await Assert.ThrowsAsync<HubException>(() => second.InvokeAsync("AuthenticateDevice", signature, Ct));
        await Assert.ThrowsAsync<HubException>(() => second.InvokeAsync("CreateProject", Guid.NewGuid(), Ct));
        using var impostor = Key.Create(SignatureAlgorithm.Ed25519);
        var substituted = keys with { SigningPublicKey = impostor.PublicKey.Export(KeyBlobFormat.RawPublicKey) };
        using var replace = await http.PutAsJsonAsync("/devices/verified",
            new VerifiedDeviceRegistration(substituted, SignatureAlgorithm.Ed25519.Sign(impostor, DeviceProof.EncodeRegistration(SyncServer.PublicUrl, registration.UserId, substituted))), Ct);
        Assert.Equal(HttpStatusCode.Conflict, replace.StatusCode);
    }

    public async ValueTask DisposeAsync() => await server.DisposeAsync();
}
