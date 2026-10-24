using System.Security.Claims;
using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Access;

public static class InvitationEndpoints
{
    public static IEndpointRouteBuilder MapInvitationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/projects/{projectId:guid}/invitations", CreateAsync).RequireAuthorization();
        app.MapPost("/invitations/redeem", RedeemAsync).RequireAuthorization();
        app.MapPost("/projects/{projectId:guid}/invitations/revoke", RevokeAsync).RequireAuthorization();
        return app;
    }

    private static Task<IResult> CreateAsync(Guid projectId, CreateInvitation request, ClaimsPrincipal principal,
        UserDirectory users, InvitationAccess invitations, CancellationToken ct) =>
        RespondAsync(async () =>
        {
            var user = await users.GetOrCreateAsync(principal, ct);
            await invitations.CreateAsync(user.Id, projectId, request, ct, legacyAccount: true);
            return Results.NoContent();
        });

    private static Task<IResult> RedeemAsync(RedeemInvitation request, ClaimsPrincipal principal,
        UserDirectory users, InvitationAccess invitations, CancellationToken ct) =>
        RespondAsync(async () =>
        {
            var user = await users.GetOrCreateAsync(principal, ct);
            return Results.Ok(await invitations.RedeemAsync(user.Id, request.InvitationId, ct, legacyAccount: true));
        });

    private static async Task<IResult> RespondAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (AccessDeniedException ex) { return Results.Problem(detail: ex.Message, statusCode: 403); }
        catch (ConflictException ex) { return Results.Problem(detail: ex.Message, statusCode: 409); }
        catch (ArgumentException ex) { return Results.Problem(detail: ex.Message, statusCode: 400); }
    }

    private static Task<IResult> RevokeAsync(Guid projectId, RevokeInvitation request, ClaimsPrincipal principal,
        UserDirectory users, InvitationAccess invitations, CancellationToken ct) =>
        RespondAsync(async () =>
        {
            var user = await users.GetOrCreateAsync(principal, ct);
            await invitations.RevokeAsync(user.Id, projectId, request.InvitationId, ct, legacyAccount: true);
            return Results.NoContent();
        });
}
