namespace ChllSeeder.Core.Seeding;

/// <summary>
/// Events raised by the <see cref="SeedingEngine"/> as it drives a seeding session.
/// These are the subset of the Rust <c>AppEvent</c> enum (see <c>src-rust/src/events.rs</c>)
/// that the seeding state machine emits. The UI layer subscribes via
/// <see cref="SeedingEngine.Event"/> to drive countdowns, banners, toasts, and switch prompts.
/// </summary>
public abstract record SeedingEvent
{
    /// <summary>Splash-bypass key-spamming has begun; UI may show a countdown.</summary>
    public sealed record SplashBypassStarted(long DurationSecs) : SeedingEvent;

    /// <summary>Splash bypass finished (game should be in-menu / connecting).</summary>
    public sealed record SplashBypassComplete : SeedingEvent;

    /// <summary>The game never launched / windowed in time; splash bypass aborted.</summary>
    public sealed record SplashBypassTimeout(string Reason) : SeedingEvent;

    /// <summary>The game process closed (user-closed or crashed); seeding is stopping.</summary>
    public sealed record HllClosed : SeedingEvent;

    /// <summary>A server switch is pending; UI shows a countdown with snooze/switch-now controls.</summary>
    public sealed record ServerSwitchPending(long CountdownSecs, string ServerName, string Reason) : SeedingEvent;

    /// <summary>The pending switch was snoozed for the given duration.</summary>
    public sealed record ServerSwitchSnoozed(long SnoozeSecs) : SeedingEvent;

    /// <summary>The switch is now executing (game is being killed).</summary>
    public sealed record ServerSwitchExecuting : SeedingEvent;

    /// <summary>A pending switch was cancelled (stop requested, or conditions cleared after snooze).</summary>
    public sealed record ServerSwitchCancelled : SeedingEvent;

    /// <summary>The launch watcher is waiting for a phantom/updating launch to settle before retrying.</summary>
    public sealed record SeedingUpdateWaiting(int ServerIndex, string Region) : SeedingEvent;

    /// <summary>The launch watcher successfully restarted seeding after an update/phantom launch.</summary>
    public sealed record SeedingUpdateStarted(int ServerIndex, string Region) : SeedingEvent;

    /// <summary>The launch watcher gave up after its timeout.</summary>
    public sealed record SeedingUpdateTimeout : SeedingEvent;
}
