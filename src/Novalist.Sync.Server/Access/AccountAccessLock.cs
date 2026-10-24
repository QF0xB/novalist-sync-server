using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Novalist.Sync.Server.Data;

namespace Novalist.Sync.Server.Access;

internal static class AccountAccessLock
{
    public static async Task<IDbContextTransaction> AcquireAsync(SyncDbContext db, Guid userId, bool legacyAccount, CancellationToken ct, bool shared = false)
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var query = shared
                ? db.Users.FromSqlInterpolated($"SELECT * FROM users WHERE id = {userId} FOR SHARE")
                : db.Users.FromSqlInterpolated($"SELECT * FROM users WHERE id = {userId} FOR UPDATE");
            var users = await query.ToListAsync(ct);
            if (users.Count != 1) throw new AccessDeniedException("The authenticated account is unavailable.");
            if (legacyAccount && await db.Devices.AnyAsync(d => d.UserId == userId && d.SigningPublicKey != null, ct))
                throw new AccessDeniedException("This account requires a verified device connection.");
            return transaction;
        }
        catch { await transaction.DisposeAsync(); throw; }
    }
}
