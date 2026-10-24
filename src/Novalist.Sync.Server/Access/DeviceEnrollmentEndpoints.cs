using System.Security.Claims;
using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Access;

public static class DeviceEnrollmentEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEnrollmentEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(Protocol.DeviceEnrollmentPath, CreateAsync).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> CreateAsync(DeviceEnrollmentRequest request, ClaimsPrincipal principal,
        UserDirectory users, DeviceProofServer proofs, DeviceEnrollmentChallenges challenges, CancellationToken ct)
    {
        try { return Results.Ok(challenges.Create((await users.GetOrCreateAsync(principal, ct)).Id, proofs.ServerUrl, request)); }
        catch (FormatException error) { return Results.Problem(detail: error.Message, statusCode: 400); }
        catch (ConflictException error) { return Results.Problem(detail: error.Message, statusCode: 429); }
        catch (AccessDeniedException error) { return Results.Problem(detail: error.Message, statusCode: 403); }
    }
}
