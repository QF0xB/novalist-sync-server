using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;
using Novalist.Sync.Server.Data;

namespace Novalist.Sync.Server.Updates;

public sealed class PresenceAccess(SyncDbContext db, DeviceRegistry devices, ProjectAccess projects, AccessDeliveryGate deliveryGate)
{
    public const int MaximumRecipients = 256;

    public async Task<EnvelopeHeader> PreflightAsync(Guid userId, Guid deviceId, Envelope envelope, CancellationToken ct)
    {
        var header = ValidateEnvelope(deviceId, envelope);
        await devices.EnsureActiveAsync(userId, deviceId, ct);
        await projects.EnsureCanReadAsync(userId, header.ProjectId, ct);
        return header;
    }

    public async Task DeliverAsync(Guid userId, Guid deviceId, Envelope envelope, IReadOnlySet<Guid> joinedDevices,
        Func<Guid, IReadOnlySet<Guid>, CancellationToken, Task> deliver, CancellationToken ct)
    {
        var header = await PreflightAsync(userId, deviceId, envelope, ct);
        if (joinedDevices.Count > MaximumRecipients) throw new PushRejectedException("Presence has too many receiving devices.");

        await using var projectDelivery = await deliveryGate.ProjectAsync(header.ProjectId, ct);
        var candidates = joinedDevices.Append(deviceId).Distinct().ToArray();
        var accounts = await db.Devices.Where(device => candidates.Contains(device.Id))
            .Join(db.Memberships.Where(member => member.ProjectId == header.ProjectId), device => device.UserId, member => member.UserId,
                (device, member) => member.UserId).Distinct().ToListAsync(ct);
        await using var accountDelivery = await deliveryGate.AccountsAsync(accounts.Append(userId), ct);
        await devices.EnsureActiveAsync(userId, deviceId, ct);
        await projects.EnsureCanReadAsync(userId, header.ProjectId, ct);
        var epoch = await db.Projects.Where(project => project.Id == header.ProjectId)
            .Select(project => (long?)project.CurrentEpoch).SingleOrDefaultAsync(ct);
        if (epoch != header.Epoch)
            throw new PushRejectedException("The presence epoch is no longer current.");
        var recipients = await DeviceRegistry.ActiveDevices(db).Where(device => candidates.Contains(device.Id) && accounts.Contains(device.UserId))
            .Select(device => device.Id).ToListAsync(ct);
        if (!recipients.Contains(deviceId))
            throw new AccessDeniedException("The sending device is no longer active.");
        using var delivery = CancellationTokenSource.CreateLinkedTokenSource(ct);
        delivery.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await deliver(header.ProjectId, recipients.ToHashSet(), delivery.Token);
            delivery.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (delivery.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new PushRejectedException("Presence delivery timed out.");
        }
    }

    private static EnvelopeHeader ValidateEnvelope(Guid deviceId, Envelope envelope)
    {
        var header = EnvelopeHeader.Parse(envelope.Header);
        if (header.MsgType != MsgType.Presence || header.SenderDeviceId != deviceId ||
            header.SenderSeq is 0 or > long.MaxValue || envelope.Nonce is not { Length: 24 } ||
            envelope.Signature is not { Length: 64 } || envelope.Ciphertext is not { Length: >= 16 and <= 4096 })
            throw new PushRejectedException("Invalid presence envelope.");
        return header;
    }
}
