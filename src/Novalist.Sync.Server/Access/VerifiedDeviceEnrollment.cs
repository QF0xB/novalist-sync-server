using System.Security.Claims;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Hubs;

namespace Novalist.Sync.Server.Access;

public sealed class VerifiedDeviceEnrollment(UserDirectory users, DeviceRegistry devices,
    ConnectionRegistry connections, DeviceEnrollmentPolicy policy, DeviceEnrollmentChallenges challenges, DeviceEnrollmentTokens tokens)
{
    public async Task RegisterAsync(ClaimsPrincipal principal, VerifiedDeviceRegistration request, string serverUrl, CancellationToken ct)
    {
        var user = await users.GetOrCreateAsync(principal, ct);
        if (request.EnrollmentProof is { } proof)
            await tokens.ValidateAsync(principal, challenges.Find(user.Id, serverUrl, request), proof.IdentityToken, ct);
        var change = await devices.RegisterVerifiedAsync(user.Id, request, serverUrl, keys =>
        {
            if (request.EnrollmentProof is null) policy.Authorize(principal, keys);
            else challenges.Consume(user.Id, serverUrl, request);
        }, ct);
        if (change == DeviceRegistrationChange.FirstVerified) connections.AbortUser(user.Id);
        else if (change == DeviceRegistrationChange.Linked) connections.AbortDevice(request.Keys.DeviceId);
    }
}
