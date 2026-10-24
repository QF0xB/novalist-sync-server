using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Novalist.Sync.Contracts;

namespace Novalist.Sync.Server.Access;

public sealed class DeviceEnrollmentPolicy(TimeProvider clock)
{
    public void Authorize(ClaimsPrincipal principal, DevicePublicKeys keys)
    {
        AuthorizeMfa(principal);
        var confirmations = principal.FindAll("cnf").ToArray();
        if (confirmations.Length != 1 || !MatchesKey(confirmations[0].Value, keys.SigningPublicKey))
            throw new AccessDeniedException("The enrollment token must be bound to this device's signing key.");
    }

    public void AuthorizeMfa(ClaimsPrincipal principal)
    {
        var times = principal.FindAll("auth_time").ToArray();
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        if (principal.Identity?.IsAuthenticated != true || !principal.FindAll("amr").Any(IsMfa) || times.Length != 1 ||
            !long.TryParse(times[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var authenticated) ||
            authenticated < now - 300 || authenticated > now + 30)
            throw new AccessDeniedException("First device enrollment requires multi-factor authentication within the last five minutes.");
    }

    public static string SigningKeyThumbprint(byte[] publicKey)
    {
        if (publicKey is not { Length: 32 }) throw new FormatException("An Ed25519 public key must contain 32 bytes.");
        var canonical = $"{{\"crv\":\"Ed25519\",\"kty\":\"OKP\",\"x\":\"{Base64UrlEncoder.Encode(publicKey)}\"}}";
        return Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool MatchesKey(string confirmation, byte[] publicKey)
    {
        try
        {
            using var json = JsonDocument.Parse(confirmation);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return false;
            var thumbprints = json.RootElement.EnumerateObject().Where(value => value.NameEquals("jkt")).ToArray();
            return thumbprints.Length == 1 && thumbprints[0].Value.ValueKind == JsonValueKind.String &&
                thumbprints[0].Value.GetString() == SigningKeyThumbprint(publicKey);
        }
        catch (JsonException) { return false; }
    }

    private static bool IsMfa(Claim claim)
    {
        if (claim.Value == "mfa") return true;
        try
        {
            using var json = JsonDocument.Parse(claim.Value);
            return json.RootElement.ValueKind == JsonValueKind.Array && json.RootElement.EnumerateArray()
                .Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "mfa");
        }
        catch (JsonException) { return false; }
    }
}
