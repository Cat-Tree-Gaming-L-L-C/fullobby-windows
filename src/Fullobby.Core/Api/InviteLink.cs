namespace Fullobby.Core.Api;

/// <summary>
/// Reads the token out of a network invite link. A link looks like
/// <c>https://&lt;api host&gt;/invite#&lt;64 hex&gt;</c>; the token lives in the fragment so browsers
/// never send it to the server. Players paste the whole link, or sometimes just the hex.
/// </summary>
public static class InviteLink
{
    /// <summary>Tokens are 32 random bytes, hex-encoded.</summary>
    public const int TokenLength = 64;

    /// <summary>
    /// Extract the token from a pasted link or bare token: the part after <c>#</c> when there is
    /// one, trimmed. Returns null when what's left isn't a 64-character hex token. The result is
    /// lowercased — the server issues lowercase hex and hashes the token as sent, so an
    /// uppercased paste would otherwise never match.
    /// </summary>
    public static string? ParseToken(string? input)
    {
        if (input is null)
        {
            return null;
        }
        var text = input.Trim();
        var hash = text.LastIndexOf('#');
        if (hash >= 0)
        {
            text = text[(hash + 1)..].Trim();
        }
        if (text.Length != TokenLength || !text.All(char.IsAsciiHexDigit))
        {
            return null;
        }
        return text.ToLowerInvariant();
    }
}
