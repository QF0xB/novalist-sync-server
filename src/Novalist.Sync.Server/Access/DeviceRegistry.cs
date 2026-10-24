using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Server.Data;
using Npgsql;

namespace Novalist.Sync.Server.Access;

public sealed partial class DeviceRegistry(SyncDbContext db, AccessDeliveryGate deliveryGate)
{
    private const string DevicePrimaryKey = "pk_devices";

    public async Task RegisterAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken)
    {
        await using var transaction = await AccountAccessLock.AcquireAsync(db, userId, legacyAccount: true, cancellationToken);
        if (await IsRegisteredAsync(userId, deviceId, cancellationToken))
        {
            return;
        }

        db.Devices.Add(new Device { Id = deviceId, UserId = userId, CreatedAt = DateTimeOffset.UtcNow });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: DevicePrimaryKey
        })
        {
            // A concurrent request registered the same ID first; check whose it is.
            db.ChangeTracker.Clear();
            await IsRegisteredAsync(userId, deviceId, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task EnsureActiveAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken)
    {
        var active = await ActiveDevices(db).AnyAsync(d => d.Id == deviceId && d.UserId == userId, cancellationToken);

        if (!active)
        {
            throw new AccessDeniedException($"Device {deviceId} is not an active device of this user.");
        }
    }

    public async Task RevokeAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken)
    {
        await using var delivery = await deliveryGate.AccountAsync(userId, cancellationToken);
        await using var transaction = await AccountAccessLock.AcquireAsync(db, userId, legacyAccount: false, cancellationToken);
        var revoked = await db.Devices
            .Where(d => d.Id == deviceId && d.UserId == userId && d.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(d => d.RevokedAt, DateTimeOffset.UtcNow), cancellationToken);

        if (revoked == 0)
        {
            throw new AccessDeniedException($"Device {deviceId} is not an active device of this user.");
        }
        await transaction.CommitAsync(cancellationToken);
    }

    // True if the device already belongs to the user; throws if the ID cannot be used.
    private async Task<bool> IsRegisteredAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken)
    {
        var existing = await db.Devices
            .AsNoTracking()
            .Where(d => d.Id == deviceId)
            .Select(d => new { d.UserId, d.RevokedAt })
            .SingleOrDefaultAsync(cancellationToken);

        if (existing is null)
        {
            return false;
        }

        if (existing.UserId != userId)
        {
            throw new ConflictException($"Device ID {deviceId} is already taken.");
        }

        if (existing.RevokedAt is not null)
        {
            throw new AccessDeniedException($"Device {deviceId} has been revoked.");
        }

        return true;
    }

}
