using Fullobby.Core.Games;

namespace Fullobby.Core.Seeding;

/// <summary>
/// When to nudge a player who has one game open ("Hell Let Loose: Vietnam needs seeding — swap?")
/// while the directive wants the other. Only a <see cref="GameSwapKind.Swap"/> nudges: someone
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

    /// <summary>The swap to offer now, or null. Offering starts the quiet period, so call this only
    /// when the nudge will actually be shown.</summary>
    public GameSwapPlan? Evaluate(GameDefinition? target, IReadOnlyList<GameDefinition> running, long nowMs)
    {
        if (target is null || running.Count == 0 || nowMs < _quietUntilMs)
        {
            return null;
        }
        var plan = GameSwap.Plan(target, running);
        if (plan.Kind != GameSwapKind.Swap || running.Any(g => g.Id == target.Id))
        {
            return null; // already in the game that needs seeding
        }
        _quietUntilMs = nowMs + (long)QuietAfterNudge.TotalMilliseconds;
        return plan;
    }

    /// <summary>The player said "Not now".</summary>
    public void Snooze(long nowMs) => _quietUntilMs = nowMs + (long)QuietAfterSnooze.TotalMilliseconds;
}
