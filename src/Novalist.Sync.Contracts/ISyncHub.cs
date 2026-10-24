namespace Novalist.Sync.Contracts;

public interface ISyncHub
{
    Task<DeviceConnectionChallenge> GetDeviceChallenge();
    Task AuthenticateDevice(byte[] signature);
    Task CreateInvitation(Guid projectId, CreateInvitation request);
    Task<InvitationGrant> RedeemInvitation(Guid invitationId);
    Task<InvitationActivation> ActivateInvitation(ActivateInvitation request);
    Task RevokeInvitation(Guid projectId, Guid invitationId);

    Task JoinProject(Guid projectId);
    Task LeaveProject(Guid projectId);

    Task<long> Push(Envelope envelope);

    Task CreateProject(Guid projectId);

    IAsyncEnumerable<SequencedEnvelope> CatchUp(
        Guid projectId,
        long afterServerSeq,
        CancellationToken cancellationToken);
}
