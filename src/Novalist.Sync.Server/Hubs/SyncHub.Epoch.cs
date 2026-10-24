using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Hubs;

public sealed partial class SyncHub
{
    public async Task<ProjectEpochStatus> GetProjectEpoch(Guid projectId)
    {
        await RequireVerifiedAccessAsync();
        return await epochs.StatusAsync(UserId, projectId, Context.ConnectionAborted);
    }

    public async Task RegisterProjectDevice(Guid projectId, uint epoch, byte[] certificate)
    {
        await RequireVerifiedAccessAsync();
        await epochs.RegisterAsync(UserId, DeviceId, projectId, epoch, certificate, Context.ConnectionAborted);
    }

    public async Task<IReadOnlyList<ProjectDevice>> ListProjectDevices(Guid projectId)
    {
        await RequireVerifiedAccessAsync();
        return await epochs.DevicesAsync(UserId, projectId, Context.ConnectionAborted);
    }

    public async Task BeginProjectEpoch(BeginProjectEpoch request)
    {
        await RequireVerifiedAccessAsync();
        await epochs.BeginAsync(UserId, DeviceId, request, Context.ConnectionAborted);
    }

    public async Task<ProjectEpochStatus> ReserveProjectEpoch(Guid projectId, Guid rotationId)
    {
        await RequireVerifiedAccessAsync();
        return await epochs.ReserveAsync(UserId, DeviceId, projectId, rotationId, Context.ConnectionAborted);
    }

    public async Task CancelProjectEpoch(Guid rotationId)
    {
        await RequireVerifiedAccessAsync();
        await epochs.CancelAsync(UserId, DeviceId, rotationId, Context.ConnectionAborted);
    }

    public async Task<long> StageProjectEpoch(Guid rotationId, Envelope envelope)
    {
        await RequireVerifiedAccessAsync();
        return await epochs.StageAsync(UserId, DeviceId, rotationId, envelope, Context.ConnectionAborted);
    }

    public async Task CommitProjectEpoch(CommitProjectEpoch request)
    {
        await RequireVerifiedAccessAsync();
        var project = await epochs.CommitAsync(UserId, DeviceId, request, Context.ConnectionAborted);
        await revocation.NotifyRemovedAsync(project, request.RemovedMemberId, Context.ConnectionAborted);
        var state = await epochs.StatusAsync(UserId, project, Context.ConnectionAborted);
        await Clients.Group(ProjectGroup(project)).ProjectEpochChanged(project, state.Epoch);
    }

    public async Task StageProjectEpochGrant(Guid rotationId, ProjectEpochGrant grant)
    {
        await RequireVerifiedAccessAsync();
        await epochs.StageGrantAsync(UserId, DeviceId, rotationId, grant, Context.ConnectionAborted);
    }

    public async Task<StoredProjectEpochGrant?> GetProjectEpochGrant(Guid projectId, uint afterEpoch)
    {
        await RequireVerifiedAccessAsync();
        return await epochs.ReadGrantAsync(UserId, DeviceId, projectId, afterEpoch, Context.ConnectionAborted);
    }
}
