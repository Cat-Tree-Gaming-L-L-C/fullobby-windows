using Fullobby.Core.Config;

namespace Fullobby.Core.Scheduling;

/// <summary>
/// One persisted wake, as stored under <see cref="ConfigKeys.AutoSeedWakes"/>.
/// </summary>
/// <param name="TimeUtc">Canonical "HH:MM" UTC — the wake's identity.</param>
/// <param name="NetworkIds">Networks that justified the wake when it was last reconciled. Empty =
/// the unattributed fleet wake (no per-network boundaries were available).</param>
/// <param name="MissedWindowHours">Missed-seed window resolved at plan time (max across justifying
/// networks); null = follow the live fleet config, which is what the unattributed wake does.</param>
/// <param name="Legacy">True only for the synthetic entry representing a not-yet-adopted
/// pre-wake-set install: its task is still named plain "Fullobby". Never persisted — it only
/// appears on the read path.</param>
public sealed record StoredWake(
    string TimeUtc, List<long> NetworkIds, int? MissedWindowHours, bool Legacy = false)
{
    /// <summary>The wake's task/CLI identity — the legacy single slot for a not-yet-adopted
    /// install, else the time-keyed slot.</summary>
    public AutoSeedSlot Slot =>
        Legacy || !AutoSeedTime.TryParseStoredUtc(TimeUtc, out var t)
            ? AutoSeedSlot.Legacy
            : AutoSeedSlot.ForTime(t);
}

/// <summary>
/// Load/save for the persisted wake set. Shared by <see cref="AutoSeedService"/> (reconciliation)
/// and <see cref="MissedAutoseedMonitor"/> (the watchdog), which must agree on what is scheduled.
/// The whole set lives under one config key so a reconcile swaps it atomically — no partially
/// written plans.
/// </summary>
public static class WakeStore
{
    /// <summary>
    /// The stored wake set. When the set key is absent but the retired single-wake key parses,
    /// returns that as one legacy-flagged entry — a pre-wake-set install whose auto-seed keeps
    /// working (watchdog included) until the first reconcile adopts it. Empty = auto-seed not set
    /// up.
    /// </summary>
    public static IReadOnlyList<StoredWake> Load(ConfigService config)
    {
        var stored = config.Get<List<StoredWake>>(ConfigKeys.AutoSeedWakes);
        if (stored is not null)
        {
            return stored
                .Where(w => AutoSeedTime.TryParseStoredUtc(w.TimeUtc, out _))
                .Select(w => w with { Legacy = false })
                .ToList();
        }

        if (AutoSeedTime.TryParseStoredUtc(config.GetString(ConfigKeys.LegacyAutoSeedTime), out var t))
        {
            return [new StoredWake($"{t.Hour:D2}:{t.Minute:D2}", [], null, Legacy: true)];
        }
        return [];
    }

    /// <summary>True when auto-seed has been set up (new set key or the legacy key).</summary>
    public static bool IsEnabled(ConfigService config) =>
        config.Get<List<StoredWake>>(ConfigKeys.AutoSeedWakes) is not null
        || config.GetString(ConfigKeys.LegacyAutoSeedTime) is not null;

    /// <summary>Persist a reconciled plan and retire the legacy key (adoption is complete once the
    /// set exists). The unattributed fleet wake stores a null missed window so it keeps following
    /// the live fleet value, as the single-slot build did.</summary>
    public static void Save(ConfigService config, IEnumerable<PlannedWake> wakes)
    {
        var stored = wakes
            .Select(w => new StoredWake(
                w.Key,
                w.NetworkIds.ToList(),
                w.NetworkIds.Count == 0 ? null : w.MissedWindowHours))
            .ToList();
        config.Set(ConfigKeys.AutoSeedWakes, stored);
        config.Remove(ConfigKeys.LegacyAutoSeedTime);
    }

    /// <summary>Forget the whole set (auto-seed disabled), legacy key included.</summary>
    public static void Clear(ConfigService config)
    {
        config.Remove(ConfigKeys.AutoSeedWakes);
        config.Remove(ConfigKeys.LegacyAutoSeedTime);
    }
}
