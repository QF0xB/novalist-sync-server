using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Novalist.Sync.Contracts;
using Novalist.Sync.Server.Relay;

namespace Novalist.Sync.Server.Access;

public static class NativeClientEndpoint
{
    public static IEndpointRouteBuilder MapNativeClientEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(Protocol.ClientConfigurationPath, GetConfiguration).AllowAnonymous();
        return app;
    }

    private static IResult GetConfiguration(IConfiguration configuration, IOptionsMonitor<JwtBearerOptions> bearer)
    {
        if (!RelayAuthentication.Required(configuration))
            return Results.Ok(new NativeClientConfiguration(Protocol.Version, null, null, null, false));
        var authority = bearer.Get(JwtBearerDefaults.AuthenticationScheme).Authority?.Trim();
        var clientId = configuration["Authentication:NativeClient:ClientId"]?.Trim();
        var scope = configuration["Authentication:NativeClient:Scope"]?.Trim();
        if (!Uri.TryCreate(authority, UriKind.Absolute, out var url) ||
            (url.Scheme != "https" && !(url.Scheme == "http" && url.IsLoopback)) ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 ||
            string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(scope) || !scope.Split(' ').Contains("openid"))
        {
            return Results.Problem(detail: "Native sign-in has not been configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new NativeClientConfiguration(Protocol.Version, authority, clientId, scope));
    }
}
