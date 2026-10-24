using Novalist.Sync.Server.Data;

namespace Novalist.Sync.Server.Access;

public sealed partial class DeviceRegistry
{
    internal static IQueryable<Device> ActiveDevices(SyncDbContext db) =>
        db.Devices.Where(device => device.RevokedAt == null &&
            (device.SigningPublicKey != null || !db.Devices.Any(other => other.UserId == device.UserId && other.SigningPublicKey != null)));
}
