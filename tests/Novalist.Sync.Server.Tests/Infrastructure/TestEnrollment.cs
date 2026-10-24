using Microsoft.IdentityModel.JsonWebTokens;
using Novalist.Sync.Contracts;
using NSec.Cryptography;

namespace Novalist.Sync.Server.Tests.Infrastructure;

internal static class TestEnrollment
{
    public static void Authorize(HttpClient http, byte[] signingPublicKey)
    {
        var token = http.DefaultRequestHeaders.Authorization?.Parameter ?? throw new InvalidOperationException("A test account token is required.");
        var subject = new JsonWebTokenHandler().ReadJsonWebToken(token).Subject;
        http.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.Enrollment(subject, signingPublicKey));
    }

    public static VerifiedDeviceRegistration First(Guid user, DevicePublicKeys keys, Key signing, TestRecoveryDevice recovery) =>
        new(keys, SignatureAlgorithm.Ed25519.Sign(signing, DeviceProof.EncodeRegistration(SyncServer.PublicUrl, user, keys)),
            RecoveryDevice: Recovery(user, keys.DeviceId, signing, recovery.Keys, recovery.Signing));

    public static DeviceRecoveryRegistration Recovery(Guid user, Guid rootDevice, Key rootSigning, DevicePublicKeys recovery, Key recoverySigning) =>
        new(recovery, SignatureAlgorithm.Ed25519.Sign(recoverySigning, DeviceProof.EncodeRegistration(SyncServer.PublicUrl, user, recovery)),
            SignatureAlgorithm.Ed25519.Sign(rootSigning, DeviceProof.EncodeLink(SyncServer.PublicUrl, user, rootDevice, recovery)));
}

internal sealed class TestRecoveryDevice : IDisposable
{
    private readonly Guid id = Guid.NewGuid();
    private readonly Key receiving = Key.Create(KeyAgreementAlgorithm.X25519);
    public Key Signing { get; } = Key.Create(SignatureAlgorithm.Ed25519);
    public DevicePublicKeys Keys => new(id, Signing.PublicKey.Export(KeyBlobFormat.RawPublicKey), receiving.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    public void Dispose() { Signing.Dispose(); receiving.Dispose(); }
}
