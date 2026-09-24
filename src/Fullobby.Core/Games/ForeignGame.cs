namespace Fullobby.Core.Games;

/// <summary>
/// An Unreal Engine game running that isn't one of ours (Wardogs, another shooter, …). Two Unreal
/// games side by side usually means the one launched second fails to start — shared engine and
/// anti-cheat state, GPU/shader caches, exclusive fullscreen — so a seed launch checks for these
/// first. Identified by process, never closed without the player's say-so.
/// </summary>
/// <param name="Pid">The process to close if the player agrees.</param>
/// <param name="ExeName">Its image name, re-checked before closing so a recycled pid is never hit.</param>
/// <param name="DisplayName">What to call it: the exe's product name, else the Unreal project name.</param>
public sealed record ForeignGame(uint Pid, string ExeName, string DisplayName)
{
    private static readonly string[] ShippingSuffixes = ["-Win64-Shipping.exe", "-WinGDK-Shipping.exe"];

    /// <summary>Whether <paramref name="exeName"/> is an Unreal shipping build's exe name
    /// (<c>&lt;Project&gt;-Win64-Shipping.exe</c>, or <c>-WinGDK-</c> for Game Pass builds).</summary>
    public static bool IsUnrealShippingExe(string exeName) =>
        ShippingSuffixes.Any(s => exeName.EndsWith(s, StringComparison.OrdinalIgnoreCase)
            && exeName.Length > s.Length);

    /// <summary>A readable name when the exe carries no product name: the Unreal project name in
    /// front of the shipping suffix ("Wardogs-Win64-Shipping.exe" → "Wardogs"), else the exe name
    /// without its extension.</summary>
    public static string NameFromExe(string exeName)
    {
        foreach (var suffix in ShippingSuffixes)
        {
            if (exeName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && exeName.Length > suffix.Length)
            {
                return exeName[..^suffix.Length];
            }
        }
        return Path.GetFileNameWithoutExtension(exeName);
    }
}
