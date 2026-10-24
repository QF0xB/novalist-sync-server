using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Tests.Infrastructure;
using Novalist.Sync.Server.Hubs;
using NSec.Cryptography;

namespace Novalist.Sync.Server.Tests.Access;

public sealed class InvitationActivationTests(DatabaseFixture database)
{
    [Theory]
    [InlineData("membership")]
    [InlineData("device")]
    public async Task TheClaimingDeviceCanRecoverTheSameActivationAfterExpiryButCannotSubstituteItsCertificate(string revoke)
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new InvitationClock();
        await using var server = new SyncServer(database, clock);
        using var ownerKey = Key.Create(SignatureAlgorithm.Ed25519);
        using var guestKey = Key.Create(SignatureAlgorithm.Ed25519);
        using var ownerRecovery = new TestRecoveryDevice();
        using var guestRecovery = new TestRecoveryDevice();
        var ownerToken = TestTokens.Create(Guid.NewGuid().ToString());
        var guestToken = TestTokens.Create(Guid.NewGuid().ToString());
        var guestId = Guid.NewGuid();
        await using var owner = await ConnectAsync(server, ownerToken, Guid.NewGuid(), ownerKey, ownerRecovery, ct);
        await using var guest = await ConnectAsync(server, guestToken, guestId, guestKey, guestRecovery, ct);
        var project = Guid.NewGuid();
        var invitation = Guid.NewGuid();
        await owner.InvokeAsync("CreateProject", project, ct);
        await owner.InvokeAsync("CreateInvitation", project, new CreateInvitation(invitation, clock.Now.AddMinutes(1)), ct);
        await owner.StopAsync(ct);
        var certificateHash = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var request = new ActivateInvitation(invitation, certificateHash);
        var activated = await guest.InvokeAsync<InvitationActivation>("ActivateInvitation", request, ct);
        Assert.Equal(project, activated.ProjectId);
        Assert.Equal(guestId, activated.DeviceId);
        Assert.Equal(certificateHash, activated.CertificateHash);
        await guest.StopAsync(ct);
        clock.Now = clock.Now.AddMinutes(2);
        await using var reopened = server.CreateHubConnection(guestToken, guestId);
        await AuthenticateAsync(reopened, guestKey, ct);
        var recovered = await reopened.InvokeAsync<InvitationActivation>("ActivateInvitation", request, ct);
        Assert.Equal(activated.ProjectId, recovered.ProjectId);
        Assert.Equal(activated.DeviceId, recovered.DeviceId);
        Assert.Equal(activated.CertificateHash, recovered.CertificateHash);
        await reopened.InvokeAsync("JoinProject", project, ct);
        await Assert.ThrowsAsync<HubException>(() => reopened.InvokeAsync<InvitationActivation>("ActivateInvitation",
            request with { CertificateHash = new byte[32] }, ct));
        await Assert.ThrowsAsync<HubException>(() => reopened.InvokeAsync<InvitationActivation>("ActivateInvitation",
            request with { CertificateHash = new byte[31] }, ct));
        using var guestHttp = server.CreateClient(guestToken);
        using var ownerHttp = server.CreateClient(ownerToken);
        var guestAccount = await guestHttp.GetFromJsonAsync<DeviceRegistrationContext>("/devices/registration", ct);
        var ownerAccount = await ownerHttp.GetFromJsonAsync<DeviceRegistrationContext>("/devices/registration", ct);
        Assert.NotNull(guestAccount);
        Assert.NotNull(ownerAccount);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var revocation = scope.ServiceProvider.GetRequiredService<AccessRevocation>();
            if (revoke == "membership") await revocation.RemoveMemberAsync(ownerAccount.UserId, project, guestAccount.UserId, ct);
            else await revocation.RevokeDeviceAsync(guestAccount.UserId, guestId, ct);
        }
        if (revoke == "membership")
            await Assert.ThrowsAsync<HubException>(() => reopened.InvokeAsync<InvitationActivation>("ActivateInvitation", request, ct));
        else
        {
            await using var scope = server.Services.CreateAsyncScope();
            await Assert.ThrowsAsync<Novalist.Sync.Server.Access.AccessDeniedException>(() =>
                scope.ServiceProvider.GetRequiredService<Novalist.Sync.Server.Access.InvitationAccess>().ActivateAsync(guestAccount.UserId, guestId, request, ct));
        }
    }

    private static async Task<HubConnection> ConnectAsync(SyncServer server, string token, Guid deviceId, Key signing,
        TestRecoveryDevice recoveryDevice, CancellationToken ct)
    {
        using var http = server.CreateClient(token);
        var context = await http.GetFromJsonAsync<DeviceRegistrationContext>("/devices/registration", ct)
            ?? throw new InvalidDataException("Missing device registration context.");
        using var receiving = Key.Create(KeyAgreementAlgorithm.X25519);
        var keys = new DevicePublicKeys(deviceId, signing.PublicKey.Export(KeyBlobFormat.RawPublicKey), receiving.PublicKey.Export(KeyBlobFormat.RawPublicKey));
        TestEnrollment.Authorize(http, keys.SigningPublicKey);
        using var response = await http.PutAsJsonAsync("/devices/verified", TestEnrollment.First(context.UserId, keys, signing, recoveryDevice), ct);
        response.EnsureSuccessStatusCode();
        var hub = server.CreateHubConnection(token, deviceId);
        try { await AuthenticateAsync(hub, signing, ct); return hub; }
        catch { await hub.DisposeAsync(); throw; }
    }

    private static async Task AuthenticateAsync(HubConnection hub, Key signing, CancellationToken ct)
    {
        await hub.StartAsync(ct);
        var challenge = await hub.InvokeAsync<DeviceConnectionChallenge>("GetDeviceChallenge", ct);
        await hub.InvokeAsync("AuthenticateDevice", SignatureAlgorithm.Ed25519.Sign(signing, DeviceProof.EncodeConnection(challenge)), ct);
    }

    private sealed class InvitationClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
