using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Access;

public sealed class DeviceEnrollmentChallenges(TimeProvider clock)
{
    private sealed record Pending(Guid User, byte[] KeysHash, DeviceEnrollmentChallenge Challenge);
    private readonly object gate = new();
    private readonly Dictionary<Guid, Pending> pending = new();

    public DeviceEnrollmentChallenge Create(Guid user, string server, DeviceEnrollmentRequest request)
    {
        var hash = Hash(server, user, request.Keys, request.RecoveryKeys);
        lock (gate)
        {
            foreach (var id in pending.Where(pair => pair.Value.Challenge.ExpiresAt <= clock.GetUtcNow()).Select(pair => pair.Key).ToArray())
                pending.Remove(id);
            if (pending.Count >= 1024 || pending.Values.Count(value => value.User == user) >= 8)
                throw new ConflictException("Too many pending enrollments. Wait for an earlier challenge to expire.");
            var challenge = new DeviceEnrollmentChallenge(Guid.NewGuid(), Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)),
                clock.GetUtcNow().AddMinutes(5));
            pending.Add(challenge.Id, new(user, hash, challenge));
            return challenge;
        }
    }

    public DeviceEnrollmentChallenge Find(Guid user, string server, VerifiedDeviceRegistration request)
    {
        var proof = request.EnrollmentProof ?? throw new AccessDeniedException("An enrollment proof is required.");
        var recovery = request.RecoveryDevice ?? throw new AccessDeniedException("A recovery device is required.");
        var hash = Hash(server, user, request.Keys, recovery.Keys);
        lock (gate)
        {
            if (!pending.TryGetValue(proof.ChallengeId, out var value) || value.User != user ||
                value.Challenge.ExpiresAt <= clock.GetUtcNow() || !CryptographicOperations.FixedTimeEquals(hash, value.KeysHash))
                throw new AccessDeniedException("The enrollment challenge expired or does not match these device keys.");
            return value.Challenge;
        }
    }

    public void Consume(Guid user, string server, VerifiedDeviceRegistration request)
    {
        lock (gate) pending.Remove(Find(user, server, request).Id);
    }

    private static byte[] Hash(string server, Guid user, DevicePublicKeys keys, DevicePublicKeys recovery)
    {
        if (keys is null || recovery is null) throw new FormatException("Ordinary and recovery device keys are required.");
        return SHA256.HashData([.. DeviceProof.EncodeRegistration(server, user, keys), .. DeviceProof.EncodeRegistration(server, user, recovery)]);
    }
}
