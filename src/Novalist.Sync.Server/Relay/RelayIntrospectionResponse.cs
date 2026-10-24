using System.Text.Json;

namespace Novalist.Sync.Server.Relay;

internal enum RelayTokenState { Active, Inactive, Unavailable }

internal static class RelayIntrospectionResponse
{
    public static RelayTokenState Read(ReadOnlyMemory<byte> bytes, RelayIdentity identity, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("active", out var active)) return RelayTokenState.Unavailable;
        if (active.ValueKind == JsonValueKind.False) return RelayTokenState.Inactive;
        if (active.ValueKind != JsonValueKind.True) return RelayTokenState.Unavailable;
        if (!Matches(value, "iss", identity.Issuer) || !Matches(value, "sub", identity.Subject)) return RelayTokenState.Inactive;
        if (value.TryGetProperty("exp", out var expiry) &&
            (expiry.ValueKind != JsonValueKind.Number || !expiry.TryGetInt64(out var seconds) || seconds <= now.ToUnixTimeSeconds()))
            return RelayTokenState.Inactive;
        return RelayTokenState.Active;
    }

    private static bool Matches(JsonElement value, string claim, string expected) =>
        !value.TryGetProperty(claim, out var actual) || actual.ValueKind == JsonValueKind.String && actual.GetString() == expected;
}
