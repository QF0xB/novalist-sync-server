using Microsoft.EntityFrameworkCore;
using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Access;

public sealed partial class ProjectEpochAccess
{
    public async Task<ProjectEpochStatus> ReserveAsync(Guid user, Guid device, Guid projectId, Guid rotation, CancellationToken ct)
    {
        if (rotation == Guid.Empty) throw new FormatException("A rotation identifier is required.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var project = await LockProjectAsync(projectId, ct);
        await projects.EnsureIsOwnerAsync(user, projectId, ct);
        if (project.CurrentEpoch == uint.MaxValue) throw new ConflictException("The project cannot advance its encryption epoch.");
        var now = await DatabaseTimeAsync(ct);
        await db.ProjectEpochStages.Where(stage => stage.ProjectId == projectId && stage.CreatedAt <= now - ReservationLifetime).ExecuteDeleteAsync(ct);
        await EnsureReservationAvailableAsync(projectId, rotation, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO project_epoch_stages (id, project_id, device_id, previous_epoch, previous_server_seq, created_at, count)
            VALUES ({rotation}, {projectId}, {device}, {project.CurrentEpoch}, {project.LastServerSeq}, {now}, 0)
            ON CONFLICT (id) DO NOTHING
            """, ct);
        var reserved = await LockStageAsync(rotation, device, ct);
        if (reserved.ProjectId != projectId) throw new ConflictException("The rotation identifier is already in use.");
        CheckBoundary(project, reserved.PreviousEpoch, reserved.PreviousServerSeq);
        await transaction.CommitAsync(ct);
        return new((uint)reserved.PreviousEpoch, reserved.PreviousServerSeq);
    }

    public async Task CancelAsync(Guid user, Guid device, Guid rotation, CancellationToken ct)
    {
        var project = await db.ProjectEpochStages.AsNoTracking().Where(stage => stage.Id == rotation)
            .Select(stage => (Guid?)stage.ProjectId).SingleOrDefaultAsync(ct);
        if (project is null) return;
        await projects.EnsureIsOwnerAsync(user, project.Value, ct);
        await using var transaction = await AccountAccessLock.AcquireAsync(db, user, legacyAccount: false, ct, shared: true);
        await LockProjectAsync(project.Value, ct);
        await projects.EnsureIsOwnerAsync(user, project.Value, ct);
        if (!await DeviceRegistry.ActiveDevices(db).AnyAsync(value => value.Id == device && value.UserId == user, ct))
            throw new AccessDeniedException("The cancelling device is unavailable.");
        await db.ProjectEpochStages.Where(stage => stage.Id == rotation).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task EnsureReservationAvailableAsync(Guid projectId, Guid rotation, CancellationToken ct)
    {
        if (await db.ProjectEpochStages.AnyAsync(stage => stage.ProjectId == projectId && stage.Id != rotation, ct))
            throw new ConflictException("Another project key rotation is already in progress.");
    }
}
