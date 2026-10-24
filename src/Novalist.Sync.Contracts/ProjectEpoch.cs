namespace Novalist.Sync.Contracts;

public sealed record ProjectDevice(Guid UserId, Guid DeviceId, uint Epoch, byte[] Certificate);
public sealed record ProjectEpochStatus(uint Epoch, long LastServerSeq);
public sealed record BeginProjectEpoch(Guid ProjectId, Guid RotationId, uint PreviousEpoch, long PreviousServerSeq);
public sealed record ProjectEpochGrant(Guid DeviceId, byte[] Certificate, byte[] Payload);
public sealed record CommitProjectEpoch(Guid RotationId, Guid RemovedMemberId, IReadOnlyList<ProjectEpochGrant> Grants);
public sealed record StoredProjectEpochGrant(uint Epoch, long FirstServerSeq, Guid GrantorDeviceId, byte[] Payload);
