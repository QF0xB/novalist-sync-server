using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Access;

namespace Novalist.Sync.Server.Hubs;

public sealed partial class SyncHub
{
    public async Task<IReadOnlyList<ProjectMember>> ListProjectMembers(Guid projectId)
    {
        await RequireVerifiedAccessAsync();
        return await projects.ListMembersAsync(UserId, projectId, Context.ConnectionAborted);
    }

    public async Task RemoveProjectMember(Guid projectId, Guid memberId)
    {
        await RequireVerifiedAccessAsync();
        await revocation.RemoveMemberAsync(UserId, projectId, memberId, Context.ConnectionAborted);
    }

    public async Task DeleteProject(Guid projectId)
    {
        await RequireVerifiedAccessAsync();
        await revocation.DeleteProjectAsync(UserId, projectId, Context.ConnectionAborted);
    }

    private async Task RequireVerifiedAccessAsync()
    {
        await EnsureDeviceActiveAsync(Context.ConnectionAborted);
        if (await devices.AuthenticationKeyAsync(UserId, DeviceId, Context.ConnectionAborted) is null)
            throw new AccessDeniedException("Managing project access requires a verified device.");
    }
}
