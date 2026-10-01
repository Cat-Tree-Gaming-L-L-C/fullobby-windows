using Fullobby.Core.Games;

namespace Fullobby.Core.Seeding;

/// <summary>
/// When to nudge a player that a game needs seeding ("Hell Let Loose: Vietnam needs seeding —
/// seed now?"). Only while they're free: on the desktop, not in a game of their own (ours or
/// another), not in something fullscreen, and not already seeding or playing through Fullobby. A
/// player who is gaming is left alone entirely — the app should be as intrusive as it needs to be
/// and no more. One nudge, then quiet for <see cref="QuietAfterNudge"/>; "Not now" buys
/// <see cref="QuietAfterSnooze"/>. The nudge only offers — seeding starts when they pick Seed.
///
/// <para>Times are caller-supplied monotonic milliseconds (<c>Environment.TickCount64</c>), so the
/// policy is pure and testable.</para>
/// </summary>
public sealed class SeedNudgePolicy
{
    public static readonly TimeSpan QuietAfterNudge = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan QuietAfterSnooze = TimeSpan.FromHours(2);

    private long _quietUntilMs = long.MinValue;

    /// <summary>Whether to nudge about <paramref name="target"/> (null: nothing needs seeding) now.
    /// <paramref name="playerBusy"/>: in a game, fullscreen, or otherwise not to be disturbed.
    /// A yes starts the quiet period, so ask only when the nudge will actually be shown.</summary>
    public bool ShouldNudge(GameDefinition? target, bool playerBusy, long nowMs)
    {
        if (target is null || playerBusy || nowMs < _quietUntilMs)
        {
            return false;
        }
        _quietUntilMs = nowMs + (long)QuietAfterNudge.TotalMilliseconds;
        return true;
    }

    /// <summary>The player said "Not now".</summary>
    public void Snooze(long nowMs) => _quietUntilMs = nowMs + (long)QuietAfterSnooze.TotalMilliseconds;
}
