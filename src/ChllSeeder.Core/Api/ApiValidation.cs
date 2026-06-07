using System.Text;
using System.Text.Json;

namespace ChllSeeder.Core.Api;

/// <summary>
/// Pure client-side validators and the user-friendly error mapper.
/// Port of the free functions in <c>src-rust/src/api/client.rs</c>.
/// </summary>
public static class ApiValidation
{
    public static readonly string[] ValidProviders = ["steam", "discord"];

    /// <summary>Profanity blocklist mirroring the server's name_validation.rs.</summary>
    private static readonly string[] BlockedWords =
    [
        "nigger", "nigga", "faggot", "retard", "chink", "spic", "kike",
        "tranny", "coon", "gook", "wetback", "beaner", "raghead",
    ];

    public static bool IsValidProvider(string provider) => Array.IndexOf(ValidProviders, provider) >= 0;

    public static bool IsValidSteamId(string steamId) =>
        steamId.Length is > 0 and <= 20 && steamId.All(char.IsAsciiDigit);

    public static bool IsValidUserId(string userId) =>
        userId.Length is > 0 and <= 64 && userId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>OAuth URL for a provider. Returns null when the provider is invalid.</summary>
    public static string? GetOAuthUrl(string provider, string state)
    {
        if (!IsValidProvider(provider))
        {
            return null;
        }
        return $"{ApiConfig.BaseUrl}/api/auth/{provider}?state={Uri.EscapeDataString(state)}";
    }

    /// <summary>
    /// Validate a display name client-side. Returns null when valid, otherwise a
    /// user-friendly error message. Mirrors the Rust validate_display_name.
    /// </summary>
    public static string? ValidateDisplayName(string name)
    {
        var trimmed = name.Trim();

        if (trimmed.Length < 2)
        {
            return "Name must be at least 2 characters";
        }
        if (trimmed.Length > 32)
        {
            return "Name must be 32 characters or fewer";
        }

        foreach (var ch in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not (' ' or '-' or '_' or '.'))
            {
                return $"Name contains invalid character '{ch}' — only letters, numbers, " +
                       "spaces, hyphens, underscores, and dots are allowed";
            }
        }

        var lower = trimmed.ToLowerInvariant();
        if (BlockedWords.Any(lower.Contains))
        {
            return "Name contains inappropriate language";
        }

        return null;
    }

    /// <summary>Map a raw API/network error message to a short, user-facing string.</summary>
    public static string FriendlyError(string msg)
    {
        // Network-level errors
        if (msg.Contains("dns error") || msg.Contains("No such host"))
        {
            return "Could not reach server — check your internet connection";
        }
        if (msg.Contains("timed out") || msg.Contains("Timeout"))
        {
            return "Request timed out — the server may be slow, try again";
        }
        if (msg.Contains("connection refused") || msg.Contains("Connection refused"))
        {
            return "Server is not responding — try again later";
        }
        if (msg.Contains("connect error") || msg.Contains("ConnectError"))
        {
            return "Network error — check your internet connection";
        }

        // API-level errors
        if (msg.Contains("Update required"))
        {
            return msg; // already user-friendly
        }
        if (msg.Contains("API error"))
        {
            var brace = msg.IndexOf('{');
            if (brace >= 0)
            {
                try
                {
                    using var doc = JsonDocument.Parse(msg[brace..]);
                    if (doc.RootElement.TryGetProperty("error", out var err)
                        && err.ValueKind == JsonValueKind.String)
                    {
                        return err.GetString()!;
                    }
                }
                catch (JsonException)
                {
                    // fall through to truncation
                }
            }
        }

        // Fallback — truncate long errors (rune-aware to avoid splitting UTF-8).
        if (Encoding.UTF8.GetByteCount(msg) > 80)
        {
            var runes = msg.EnumerateRunes().Take(77);
            return string.Concat(runes.Select(r => r.ToString())) + "...";
        }
        return msg;
    }
}
