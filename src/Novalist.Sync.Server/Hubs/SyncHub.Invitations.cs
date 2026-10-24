using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Hubs;

public sealed partial class SyncHub
{
    public async Task CreateInvitation(Guid projectId, CreateInvitation request)
    {
        await EnsureDeviceActiveAsync(Context.ConnectionAborted);
        await invitations.CreateAsync(UserId, projectId, request, Context.ConnectionAborted);
    }

    public async Task<InvitationGrant> RedeemInvitation(Guid invitationId)
    {
        await EnsureDeviceActiveAsync(Context.ConnectionAborted);
        return await invitations.RedeemAsync(UserId, invitationId, Context.ConnectionAborted);
    }

    public async Task RevokeInvitation(Guid projectId, Guid invitationId)
    {
        await EnsureDeviceActiveAsync(Context.ConnectionAborted);
        await invitations.RevokeAsync(UserId, projectId, invitationId, Context.ConnectionAborted);
    }

    public async Task<InvitationActivation> ActivateInvitation(ActivateInvitation request)
    {
        await EnsureDeviceActiveAsync(Context.ConnectionAborted);
        return await invitations.ActivateAsync(UserId, DeviceId, request, Context.ConnectionAborted);
    }
}
