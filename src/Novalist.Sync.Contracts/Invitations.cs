namespace Novalist.Sync.Contracts;

public sealed record CreateInvitation(Guid InvitationId, DateTimeOffset ExpiresAt);
public sealed record RedeemInvitation(Guid InvitationId);
public sealed record RevokeInvitation(Guid InvitationId);
public sealed record InvitationGrant(Guid ProjectId);
public sealed record ActivateInvitation(Guid InvitationId, byte[] CertificateHash);
public sealed record InvitationActivation(Guid ProjectId, Guid DeviceId, byte[] CertificateHash);
