namespace Novalist.Sync.Contracts;

public sealed record DeviceEnrollmentRequest(DevicePublicKeys Keys, DevicePublicKeys RecoveryKeys);
public sealed record DeviceEnrollmentChallenge(Guid Id, string Nonce, DateTimeOffset ExpiresAt);
public sealed record DeviceEnrollmentProof(Guid ChallengeId, string IdentityToken);
