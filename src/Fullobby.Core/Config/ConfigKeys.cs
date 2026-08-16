namespace Fullobby.Core.Config;

/// <summary>
/// Config keys shared across assemblies. Most keys are private to a single call site and stay as
/// literals there; these are the ones read from more than one place, where a typo in one copy is a
/// silent behaviour change rather than a compile error.
/// </summary>
public static class ConfigKeys
{
    /// <summary>Set once the user finishes (or skips) first-run onboarding. Written by the shell's
    /// account view model; read by the auto-seed watchdog, which must never fire before it is set.</summary>
    public const string OnboardingComplete = "onboarding_complete";

    /// <summary>The auto-seed wake set (JSON list of stored wakes — see
    /// <c>Core.Scheduling.WakeStore</c>). Presence of this key is what "auto-seed is enabled"
    /// means. Written by <c>AutoSeedService</c>; read by the missed-seed watchdog.</summary>
    public const string AutoSeedWakes = "auto_seed_wakes";

    /// <summary>Retired single-wake key ("HH:MM" UTC) from builds before the wake set. On disk in
    /// every pre-0.3.0 install that set up auto-seed — read for adoption into
    /// <see cref="AutoSeedWakes"/>, removed once adopted, never written again.</summary>
    public const string LegacyAutoSeedTime = "auto_seed_time";
}
