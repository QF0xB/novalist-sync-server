namespace Novalist.Sync.Server.Data;

public sealed class User
{
    public Guid Id { get; set; }
    public required string Issuer { get; set; }
    public required string Subject { get; set; }
    public string? Email { get; set; }
    public string? UserName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Device
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public byte[]? SigningPublicKey { get; set; }
    public byte[]? ReceivingPublicKey { get; set; }
    public Guid? LinkedByDeviceId { get; set; }
    public byte[]? LinkingSignature { get; set; }
}

public sealed class Project
{
    public Guid Id { get; set; }
    public long CurrentEpoch { get; set; }
    public DateTimeOffset EpochStartedAt { get; set; }
    public long LastServerSeq { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum MembershipRole
{
    Owner = 1,
    Contributor = 2,
    Viewer = 3
}

public sealed class Membership
{
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }
    public MembershipRole Role { get; set; }
}

public sealed class StoredUpdate
{
    public Guid ProjectId { get; set; }
    public long ServerSeq { get; set; }
    public Guid ItemId { get; set; }
    public long Epoch { get; set; }
    public Guid SenderDeviceId { get; set; }
    public long SenderSeq { get; set; }
    public byte[] Header { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Signature { get; set; } = [];
    public DateTimeOffset ReceivedAt { get; set; }
}








