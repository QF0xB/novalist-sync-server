using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Novalist.Sync.Server.Tests.Infrastructure;

// Stands in for Keycloak/authentik: tokens signed with a key that only the test
// server is configured to trust (test-only configuration, security rule 8).
public static class TestTokens
{
    public const string Issuer = "https://idp.test/realms/novalist";
    public const string Audience = "novalist-sync";

    public static SymmetricSecurityKey SigningKey { get; } = new(RandomNumberGenerator.GetBytes(32));

    public static string Create(
        string subject,
        string? email = null,
        bool emailVerified = false,
        string? userName = null,
        string issuer = Issuer,
        SecurityKey? signingKey = null,
        DateTime? expires = null)
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject };
        if (email is not null)
        {
            claims["email"] = email;
            claims["email_verified"] = emailVerified;
        }

        if (userName is not null)
        {
            claims["preferred_username"] = userName;
        }

        var now = DateTime.UtcNow;
        var expiresAt = expires ?? now.AddMinutes(10);

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = Audience,
            Claims = claims,
            NotBefore = expiresAt < now ? expiresAt.AddMinutes(-10) : now.AddMinutes(-1),
            IssuedAt = expiresAt < now ? expiresAt.AddMinutes(-10) : now.AddMinutes(-1),
            Expires = expiresAt,
            SigningCredentials = new SigningCredentials(signingKey ?? SigningKey, SecurityAlgorithms.HmacSha256),
        });
    }

    // The principal the JwtBearer middleware builds from a valid token, for service tests.
    public static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "test"));
}
