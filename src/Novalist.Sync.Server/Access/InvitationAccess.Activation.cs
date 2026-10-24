using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Access;

public sealed partial class InvitationAccess
{
    public async Task<InvitationActivation> ActivateAsync(Guid userId, Guid deviceId, ActivateInvitation request, CancellationToken ct)
    {
        if (request.CertificateHash is not { Length: 32 }) throw new FormatException("An activation requires a 32-byte certificate fingerprint.");
        await using var transaction = await AccountAccessLock.AcquireAsync(db, userId, legacyAccount: false, ct);
        if (!await db.Devices.AnyAsync(d => d.Id == deviceId && d.UserId == userId && d.RevokedAt == null && d.SigningPublicKey != null, ct))
            throw new AccessDeniedException("Invitation activation requires a verified device.");
        var projectId = await db.Invitations.AsNoTracking().Where(value => value.Id == request.InvitationId).Select(value => (Guid?)value.ProjectId).SingleOrDefaultAsync(ct)
            ?? throw new AccessDeniedException("This invitation is unavailable.");
        await LockProjectAsync(projectId, ct);
        var rows = await db.Invitations.FromSqlInterpolated(
            $"SELECT * FROM invitations WHERE id = {request.InvitationId} FOR UPDATE").ToListAsync(ct);
        var invitation = rows.SingleOrDefault();
        if (invitation is null || (invitation.RedeemedBy is { } claimant && claimant != userId))
            throw new AccessDeniedException("This invitation is unavailable.");
        if (invitation.RedeemedBy is null)
        {
            if (invitation.ExpiresAt <= clock.GetUtcNow()) throw new AccessDeniedException("This invitation is unavailable.");
            await AdmitAsync(invitation, userId, ct);
            invitation.ActivatedDeviceId = deviceId;
            invitation.CertificateHash = request.CertificateHash.ToArray();
            await db.SaveChangesAsync(ct);
        }
        else
        {
            if (!await db.Memberships.AnyAsync(m => m.ProjectId == invitation.ProjectId && m.UserId == userId, ct))
                throw new AccessDeniedException("This invitation is unavailable.");
            if (invitation.ActivatedDeviceId != deviceId || invitation.CertificateHash is null ||
                !invitation.CertificateHash.AsSpan().SequenceEqual(request.CertificateHash))
                throw new ConflictException("The invitation was activated by a different device or certificate.");
        }
        await transaction.CommitAsync(ct);
        return new(invitation.ProjectId, deviceId, request.CertificateHash.ToArray());
    }
}
