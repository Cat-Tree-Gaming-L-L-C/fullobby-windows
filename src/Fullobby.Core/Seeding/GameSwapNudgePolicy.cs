using Fullobby.Core.Games;

namespace Fullobby.Core.Seeding;

/// <summary>
/// When to nudge a player who has one game open ("Hell Let Loose: Vietnam needs seeding — swap?")
/// while the directive wants the other — or has another Unreal Engine game open, which would likely
/// stop the seed launching. Only a <see cref="GameSwapKind.Swap"/> nudges: someone
/// playing the very game that needs seeding is left to their own server. One nudge, then quiet for
/// <see cref="QuietAfterNudge"/>; "Not now" buys <see cref="QuietAfterSnooze"/>. The nudge only
/// ever offers — the swap happens when the player picks Swap.
///
/// <para>Times are caller-supplied monotonic milliseconds (<c>Environment.TickCount64</c>), so the
/// policy is pure and testable.</para>
/// </summary>
public sealed class GameSwapNudgePolicy
{
    public static readonly TimeSpan QuietAfterNudge = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan QuietAfterSnooze = TimeSpan.FromHours(2);

    private long _quietUntilMs = long.MinValue;

    /// <summary>The swap to offer now, or null. <paramref name="foreign"/> are other Unreal games
    /// open (they'd likely stop the target launching). Offering starts the quiet period, so call
    /// this only when the nudge will actually be shown.</summary>
    public GameSwapPlan? Evaluate(
        GameDefinition? target, IReadOnlyList<GameDefinition> running, long nowMs,
        IReadOnlyList<ForeignGame>? foreign = null)
    {
        if (target is null || nowMs < _quietUntilMs || !Wanted(target, running, foreign))
        {
            return null;
        }
        _quietUntilMs = nowMs + (long)QuietAfterNudge.TotalMilliseconds;
        return GameSwap.Plan(target, running, foreign);
    }

    /// <summary>Whether a nudge toward <paramref name="target"/> applies at all (ignoring quiet
    /// periods): something is in the way, and the player isn't already in the game that needs
    /// seeding. Also how a showing nudge knows it has gone stale.</summary>
    public static bool Wanted(
        GameDefinition target, IReadOnlyList<GameDefinition> running, IReadOnlyList<ForeignGame>? foreign = null) =>
        GameSwap.Plan(target, running, foreign).Kind == GameSwapKind.Swap
        && running.All(g => g.Id != target.Id);

    /// <summary>The player said "Not now".</summary>
    public void Snooze(long nowMs) => _quietUntilMs = nowMs + (long)QuietAfterSnooze.TotalMilliseconds;
}
