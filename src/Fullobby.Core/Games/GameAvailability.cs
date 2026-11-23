namespace Fullobby.Core.Games;

/// <summary>
/// Which catalog games this client offers, and which of them the player seeds.
///
/// <para><b>Offered</b> = the deployment's games registry (<c>GET /api/games</c>, public, read
/// before sign-in) ∩ <see cref="GameCatalog.All"/>, keeping only games the API seeds. A game the
/// API enables that this client version doesn't know isn't offered (it can't launch it); a game
/// this client knows that the API hasn't enabled isn't either. Until the registry has been read
/// (first start, offline) the answer is HLL and HLL: Vietnam, what clients offered before the
/// registry existed.</para>
///
/// <para><b>Opted in</b> = the player's "My games" (<c>GET /api/games/mine</c>). Until that has
/// been read, the API's own default applies: the games Fullobby ran before the registry are in,
/// newer games out — so a new game is never seeded without the player choosing it. The API applies
/// the same rule to directives; the client applies it too so it neither asks about nor wakes for a
/// game the player left out.</para>
///
/// Static, like <see cref="InstalledGames"/>, because the seeding engine and the schedulers read it
/// from wherever they run.
/// </summary>
public static class GameAvailability
{
    /// <summary>The games every client offered before the registry: the fallback.</summary>
    public static readonly IReadOnlyList<string> LegacyIds = ["hll", "hllv"];

    /// <summary>Opted in by default (the API's catalogue <c>default_enabled</c>).</summary>
    private static readonly HashSet<string> DefaultOptedIn = ["hll", "hllv", "wardogs"];

    private static readonly object Gate = new();
    private static IReadOnlyList<GameDefinition> _offered = Resolve(LegacyIds);
    private static IReadOnlyDictionary<string, bool>? _choices;
    private static IReadOnlySet<string> _untested = new HashSet<string>();

    /// <summary>Raised (on the caller's thread) when either set changes.</summary>
    public static event Action? Changed;

    /// <summary>The offered games, in catalog order. Never empty.</summary>
    public static IReadOnlyList<GameDefinition> Offered
    {
        get { lock (Gate) { return _offered; } }
    }

    /// <summary>The offered games the player seeds, in catalog order (may be empty).</summary>
    public static IReadOnlyList<GameDefinition> OptedIn
    {
        get
        {
            lock (Gate)
            {
                return _offered.Where(g => IsOptedInLocked(g.Id)).ToList();
            }
        }
    }

    /// <summary>Whether the player seeds <paramref name="gameId"/>.</summary>
    public static bool IsOptedIn(string gameId)
    {
        lock (Gate) { return IsOptedInLocked(gameId); }
    }

    /// <summary>Whether the API says the game hasn't been proven on a real server yet.</summary>
    public static bool IsUntested(string gameId)
    {
        lock (Gate)
        {
            return _untested.Contains(gameId) || GameCatalog.ById(gameId)?.Untested == true;
        }
    }

    private static bool IsOptedInLocked(string gameId) =>
        _choices is not null && _choices.TryGetValue(gameId, out var on) ? on : DefaultOptedIn.Contains(gameId);

    /// <summary>Apply the registry: the API's seeding games, as (id, tested) pairs.</summary>
    public static void SetRegistry(IEnumerable<(string Id, bool Seeding, bool Tested)> games)
    {
        var list = games.ToList();
        var offered = Resolve(list.Where(g => g.Seeding).Select(g => g.Id));
        lock (Gate)
        {
            // An empty answer (no seeding game this client knows) keeps HLL, so the client never
            // ends up offering nothing.
            _offered = offered.Count > 0 ? offered : Resolve(["hll"]);
            _untested = list.Where(g => !g.Tested).Select(g => g.Id).ToHashSet();
        }
        Changed?.Invoke();
    }

    /// <summary>Apply the player's choices (<c>GET|PUT /api/games/mine</c>); null forgets them
    /// (signed out), returning to the defaults.</summary>
    public static void SetChoices(IReadOnlyDictionary<string, bool>? choices)
    {
        lock (Gate)
        {
            _choices = choices is null ? null : new Dictionary<string, bool>(choices);
        }
        Changed?.Invoke();
    }

    /// <summary>The catalog games with these ids, in catalog order. Pure; public for tests.</summary>
    public static IReadOnlyList<GameDefinition> Resolve(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(StringComparer.Ordinal);
        return GameCatalog.All.Where(g => set.Contains(g.Id)).ToList();
    }

    /// <summary>Back to the state before anything was read. For tests.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _offered = Resolve(LegacyIds);
            _choices = null;
            _untested = new HashSet<string>();
        }
    }
}
