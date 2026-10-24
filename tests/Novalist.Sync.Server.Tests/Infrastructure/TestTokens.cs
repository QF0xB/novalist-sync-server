using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Novalist.Sync.Server.Access;

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
        DateTime? expires = null,
        IReadOnlyDictionary<string, object>? additionalClaims = null)
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
        if (additionalClaims is not null)
            foreach (var claim in additionalClaims) claims[claim.Key] = claim.Value;

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

    public static string Enrollment(string subject, byte[] signingPublicKey) => Create(subject, additionalClaims: EnrollmentClaims(signingPublicKey));

    public static Dictionary<string, object> EnrollmentClaims(byte[] signingPublicKey) => new()
    {
        ["amr"] = new[] { "mfa" },
        ["auth_time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["cnf"] = new Dictionary<string, object> { ["jkt"] = DeviceEnrollmentPolicy.SigningKeyThumbprint(signingPublicKey) }
    };

    // The principal the JwtBearer middleware builds from a valid token, for service tests.
    public static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "test"));
}
