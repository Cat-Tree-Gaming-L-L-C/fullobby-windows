using System.Text.Json;

namespace Fullobby.Core.Api;

/// <summary>
/// Minimal JWT helpers — just enough to decide whether a stored token is worth a
/// network round-trip on startup. No signature verification (the server does that);
/// this only reads the <c>exp</c> claim.
/// </summary>
public static class JwtUtil
{
    /// <summary>
    /// Returns <c>true</c> when the token is expired, malformed, or missing an <c>exp</c>
    /// claim (fail-safe: a token we can't read is treated as expired).
    /// </summary>
    public static bool IsExpired(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return true;
        }

        byte[] payload;
        try
        {
            payload = Base64UrlDecode(parts[1]);
        }
        catch (FormatException)
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("exp", out var expEl)
                || !expEl.TryGetInt64(out var exp))
            {
                return true;
            }
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return exp <= now;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>Decode a base64url segment (no padding, '-'/'_' alphabet) to bytes.</summary>
    private static byte[] Base64UrlDecode(string segment)
    {
        var s = segment.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("Invalid base64url length");
        }
        return Convert.FromBase64String(s);
    }
}
