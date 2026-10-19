using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Access;

public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut($"{Protocol.DevicesPath}/{{deviceId:guid}}", RegisterAsync)
            .RequireAuthorization();

        return app;
    }

    private static async Task<IResult> RegisterAsync(
        Guid deviceId,
        ClaimsPrincipal principal,
        UserDirectory users,
        DeviceRegistry devices,
        CancellationToken cancellationToken)
    {
        try
        {
            var user = await users.GetOrCreateAsync(principal, cancellationToken);
            await devices.RegisterAsync(user.Id, deviceId, cancellationToken);
            return Results.NoContent();
        }
        catch (ConflictException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (AccessDeniedException ex)
        {
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status403Forbidden);
        }
    }
}