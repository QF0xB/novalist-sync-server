using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Data;
using Npgsql;
using NSec.Cryptography;

namespace Novalist.Sync.Server.Access;

public enum DeviceRegistrationChange { Unchanged, FirstVerified, Linked }

public sealed partial class DeviceRegistry
{
    public async Task<DeviceRegistrationChange> RegisterVerifiedAsync(Guid userId, VerifiedDeviceRegistration request, string serverUrl,
        Action<DevicePublicKeys> authorizeInitialEnrollment, CancellationToken ct)
    {
        if (request.Keys is null) throw new FormatException("Device keys are required.");
        var encoded = DeviceProof.EncodeRegistration(serverUrl, userId, request.Keys);
        Verify(request.Keys.SigningPublicKey, encoded, request.Signature);
        await using var delivery = await deliveryGate.AccountAsync(userId, ct);
        await using var transaction = await AccountAccessLock.AcquireAsync(db, userId, legacyAccount: false, ct);
        var existing = await db.Devices.FindAsync([request.Keys.DeviceId], ct);
        if (existing is not null)
        {
            if (existing.UserId != userId) throw new ConflictException("The device identity is already in use.");
            if (existing.RevokedAt is not null) throw new AccessDeniedException("This device has been revoked.");
            if (existing.SigningPublicKey is not null)
            {
                if (!existing.SigningPublicKey.AsSpan().SequenceEqual(request.Keys.SigningPublicKey) ||
                    !existing.ReceivingPublicKey.AsSpan().SequenceEqual(request.Keys.ReceivingPublicKey))
                    throw new ConflictException("A device identity cannot change its public keys.");
                return DeviceRegistrationChange.Unchanged;
            }
        }
        var first = !await db.Devices.AnyAsync(d => d.UserId == userId && d.SigningPublicKey != null, ct);
        if (!first) await VerifyLinkAsync(serverUrl, userId, request, ct);
        else if (request.LinkedByDeviceId is not null || request.LinkingSignature is not null)
            throw new AccessDeniedException("The linking device is not registered.");
        if (first)
        {
            authorizeInitialEnrollment(request.Keys);
            await RegisterRecoveryAsync(userId, request, serverUrl, ct);
        }
        else if (request.RecoveryDevice is not null)
            throw new ConflictException("A recovery device is enrolled only alongside the first verified device.");
        var device = existing ?? new Device { Id = request.Keys.DeviceId, UserId = userId, CreatedAt = DateTimeOffset.UtcNow };
        device.SigningPublicKey = request.Keys.SigningPublicKey.ToArray();
        device.ReceivingPublicKey = request.Keys.ReceivingPublicKey.ToArray();
        device.LinkedByDeviceId = request.LinkedByDeviceId;
        device.LinkingSignature = request.LinkingSignature?.ToArray();
        if (existing is null) db.Devices.Add(device);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: DevicePrimaryKey })
        {
            throw new ConflictException("The device identity is already in use.");
        }
        await transaction.CommitAsync(ct);
        return first ? DeviceRegistrationChange.FirstVerified : DeviceRegistrationChange.Linked;
    }

    private async Task VerifyLinkAsync(string serverUrl, Guid userId, VerifiedDeviceRegistration request, CancellationToken ct)
    {
        if (request.LinkedByDeviceId is not { } linkingDevice || request.LinkingSignature is not { } signature)
            throw new AccessDeniedException("Link this device from an authorized device or recovery key.");
        var publicKey = await AuthenticationKeyAsync(userId, linkingDevice, ct)
            ?? throw new AccessDeniedException("The linking device has no verified identity.");
        Verify(publicKey, DeviceProof.EncodeLink(serverUrl, userId, linkingDevice, request.Keys), signature);
    }

    public async Task<byte[]?> AuthenticationKeyAsync(Guid userId, Guid deviceId, CancellationToken ct)
    {
        await EnsureActiveAsync(userId, deviceId, ct);
        return await db.Devices.Where(d => d.Id == deviceId).Select(d => d.SigningPublicKey).SingleAsync(ct);
    }

    public async Task VerifyConnectionAsync(DeviceConnectionChallenge challenge, byte[] signature, CancellationToken ct)
    {
        var key = await AuthenticationKeyAsync(challenge.UserId, challenge.DeviceId, ct)
            ?? throw new AccessDeniedException("Register verified device keys before proving possession.");
        Verify(key, DeviceProof.EncodeConnection(challenge), signature);
    }

    private static void Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        if (signature is not { Length: 64 } ||
            !SignatureAlgorithm.Ed25519.Verify(PublicKey.Import(SignatureAlgorithm.Ed25519, publicKey, KeyBlobFormat.RawPublicKey), message, signature))
            throw new AccessDeniedException("Device possession proof failed.");
    }
}
