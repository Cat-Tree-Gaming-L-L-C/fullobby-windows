using System.Text.RegularExpressions;
using Fullobby.Core.Native;

namespace Fullobby.Core.Games;

/// <summary>
/// Which released games this machine can launch: those Steam has an app manifest for in any of its
/// library folders. The directive is asked across exactly these (the server picks the game by
/// network and rotation priority), and auto-seed only wakes for their server windows — a client
/// that can't launch a game must neither be sent to it nor woken for it.
///
/// <para>A local manifest scan is milliseconds, but it runs on every directive poll, so the result
/// is cached briefly. When nothing can be detected (Steam missing from the registry, unreadable
/// library file) the answer falls back to HLL alone — exactly what the client did before games
/// were detected at all, rather than a client that seeds nothing.</para>
/// </summary>
public static partial class InstalledGames
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly object Gate = new();
    private static IReadOnlyList<GameDefinition>? _cached;
    private static long _cachedAt;

    /// <summary>The launchable released games, in catalog order. Never empty.</summary>
    public static IReadOnlyList<GameDefinition> Get()
    {
        var now = Environment.TickCount64;
        lock (Gate)
        {
            if (_cached is not null && now - _cachedAt < CacheTtl.TotalMilliseconds)
            {
                return _cached;
            }
        }

        var found = Detect();
        lock (Gate)
        {
            _cached = found;
            _cachedAt = now;
        }
        return found;
    }

    /// <summary>The launchable games' ids, in catalog order (the directive's <c>games=</c>).</summary>
    public static IReadOnlyList<string> Ids() => Get().Select(g => g.Id).ToList();

    /// <summary>Drop the cached answer (e.g. after the user installs a game).</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _cached = null;
        }
    }

    private static IReadOnlyList<GameDefinition> Detect()
    {
        try
        {
            var steam = SteamPaths.GetInstallPath();
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            var libraries = File.Exists(vdf)
                ? ParseLibraryPaths(File.ReadAllText(vdf))
                : [];
            if (!libraries.Contains(steam, StringComparer.OrdinalIgnoreCase))
            {
                libraries.Insert(0, steam);
            }
            var installed = Filter(GameCatalog.Released, appId =>
                libraries.Any(lib => File.Exists(Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf"))));
            return installed.Count > 0 ? installed : [GameCatalog.Hll];
        }
        catch (Exception)
        {
            return [GameCatalog.Hll];
        }
    }

    /// <summary>The released games whose Steam app id <paramref name="hasManifest"/> reports, in
    /// order. Pure; public for unit coverage.</summary>
    public static List<GameDefinition> Filter(
        IReadOnlyList<GameDefinition> games, Func<string, bool> hasManifest) =>
        games.Where(g => hasManifest(g.SteamAppId)).ToList();

    /// <summary>The library folder paths in a <c>libraryfolders.vdf</c> (its <c>"path"</c> values,
    /// with the VDF escaping of backslashes undone). Pure; public for unit coverage.</summary>
    public static List<string> ParseLibraryPaths(string vdf) =>
        PathEntry().Matches(vdf)
            .Select(m => m.Groups[1].Value.Replace(@"\\", @"\"))
            .Where(p => p.Length > 0)
            .ToList();

    [GeneratedRegex("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathEntry();
}
