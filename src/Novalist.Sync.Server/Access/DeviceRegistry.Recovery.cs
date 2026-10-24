using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Data;

namespace Novalist.Sync.Server.Access;

public sealed partial class DeviceRegistry
{
    private async Task RegisterRecoveryAsync(Guid userId, VerifiedDeviceRegistration request, string serverUrl, CancellationToken ct)
    {
        var recovery = request.RecoveryDevice ?? throw new AccessDeniedException("Register a recovery device with your first verified device.");
        if (recovery.Keys is null) throw new FormatException("Recovery device keys are required.");
        var encoded = DeviceProof.EncodeRegistration(serverUrl, userId, recovery.Keys);
        if (recovery.Keys.DeviceId == request.Keys.DeviceId ||
            recovery.Keys.SigningPublicKey.AsSpan().SequenceEqual(request.Keys.SigningPublicKey) ||
            recovery.Keys.ReceivingPublicKey.AsSpan().SequenceEqual(request.Keys.ReceivingPublicKey))
            throw new ConflictException("The recovery device must have a separate identity and keys.");
        Verify(recovery.Keys.SigningPublicKey, encoded, recovery.Signature);
        Verify(request.Keys.SigningPublicKey, DeviceProof.EncodeLink(serverUrl, userId, request.Keys.DeviceId, recovery.Keys), recovery.LinkingSignature);
        if (await db.Devices.AnyAsync(device => device.Id == recovery.Keys.DeviceId, ct))
            throw new ConflictException("The recovery device identity is already in use.");
        db.Devices.Add(new Device
        {
            Id = recovery.Keys.DeviceId,
            UserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
            SigningPublicKey = recovery.Keys.SigningPublicKey.ToArray(),
            ReceivingPublicKey = recovery.Keys.ReceivingPublicKey.ToArray(),
            LinkedByDeviceId = request.Keys.DeviceId,
            LinkingSignature = recovery.LinkingSignature.ToArray()
        });
    }
}
