using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Access;

public sealed class DeviceEnrollmentTokens(IOptionsMonitor<JwtBearerOptions> bearer, IConfiguration configuration,
    DeviceEnrollmentPolicy policy)
{
    public async Task ValidateAsync(ClaimsPrincipal account, DeviceEnrollmentChallenge challenge, string token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 16384) throw new AccessDeniedException("Invalid enrollment identity token.");
        var client = configuration["Authentication:NativeClient:ClientId"]?.Trim();
        if (string.IsNullOrEmpty(client)) throw new AccessDeniedException("Native client enrollment has not been configured.");
        var options = bearer.Get(JwtBearerDefaults.AuthenticationScheme);
        var provider = options.Configuration ?? (options.ConfigurationManager is { } manager
            ? await manager.GetConfigurationAsync(ct) : throw new AccessDeniedException("The identity provider is unavailable."));
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = provider.Issuer,
            ValidateAudience = true,
            AudienceValidator = (audiences, _, _) => audiences is not null && audiences.SequenceEqual([client]),
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = provider.SigningKeys
        });
        ct.ThrowIfCancellationRequested();
        if (!validation.IsValid) throw new AccessDeniedException("The enrollment identity token could not be verified.");
        var claims = validation.ClaimsIdentity;
        if (!Single(claims, "iss", account.FindFirstValue("iss")) || !Single(claims, "sub", account.FindFirstValue("sub")) ||
            !Single(claims, "nonce", challenge.Nonce) ||
            claims.HasClaim(value => value.Type == "azp") && !Single(claims, "azp", client))
            throw new AccessDeniedException("The enrollment sign-in does not match this account and challenge.");
        policy.AuthorizeMfa(new ClaimsPrincipal(claims));
    }

    private static bool Single(ClaimsIdentity claims, string type, string? expected) =>
        expected is not null && claims.FindAll(type).Select(claim => claim.Value).SequenceEqual([expected]);
}
