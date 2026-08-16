using Fullobby.Core.Api;

namespace Fullobby.Core.Scheduling;

/// <summary>
/// One planned daily wake: a time the machine must be pulled out of sleep, plus the networks whose
/// windows justify it. A wake is not owned by a tenant — it is "be awake at 06:00", justified by one
/// or more networks; what gets seeded once awake is settled at seed time by priority order
/// (docs/PER-TENANT-SCHEDULING.md, "one wake, many tenants").
/// </summary>
/// <param name="TimeUtc">The daily wake time (UTC).</param>
/// <param name="NetworkIds">Networks whose windows cover this wake (sorted, distinct). Empty when
/// the wake is derived from the fleet-wide config with no membership attribution.</param>
/// <param name="MissedWindowHours">How long past the wake the missed-seed watchdog may still fire.
/// The maximum across the justifying networks: the watchdog guards the machine having slept
/// through a wake, so the most patient tenant's window governs.</param>
public sealed record PlannedWake(TimeOnly TimeUtc, IReadOnlyList<long> NetworkIds, int MissedWindowHours)
{
    /// <summary>Canonical "HH:MM" (UTC) — the identity used for store entries and dedup, and the
    /// stem of the task name / CLI arg (see <see cref="AutoSeedSlot.ForTime"/>).</summary>
    public string Key => $"{TimeUtc.Hour:D2}:{TimeUtc.Minute:D2}";
}

/// <summary>The derived wake set, plus what the cap dropped (so callers can log it — silent
/// truncation reads as "covered everything").</summary>
public sealed record WakePlan(IReadOnlyList<PlannedWake> Wakes, IReadOnlyList<PlannedWake> Dropped);

/// <summary>
/// Pure derivation of the daily wake set from per-network schedule boundaries, with the fleet-wide
/// <see cref="SeedingConfig"/> as fallback. Until the backend exposes per-network windows
/// (<see cref="NetworkSeedingStatus.ActiveWindows"/> stays null everywhere) this collapses to
/// exactly the single server-derived wake the client has always kept.
/// </summary>
public static class WakePlanner
{
    /// <summary>
    /// Cap on concurrent wakes. Each wake is a Task Scheduler entry that pulls the machine out of
    /// sleep; a user in six networks with disjoint windows would otherwise get six nightly wakes.
    /// Client policy for now — revisit if the server starts advertising a cap.
    /// </summary>
    public const int MaxWakes = 4;

    /// <summary>
    /// Derive the wake set.
    ///
    /// Per network: its own windows when sent; the fleet windows when it omits them (permanent
    /// fallback, not transitional). An empty window list means "always active", which needs the
    /// machine up from the daily reset hour. Wakes are deduplicated by time — two networks opening
    /// at 06:00 produce one wake justified by both. When no network reports boundaries at all (or
    /// there are no memberships) the result is the single unattributed fleet wake.
    ///
    /// Wakes beyond <paramref name="maxWakes"/> are dropped earliest-time-first-kept — deterministic
    /// and inspectable via <see cref="WakePlan.Dropped"/>.
    /// </summary>
    public static WakePlan ComputePlan(
        IReadOnlyList<NetworkSeedingStatus>? networks, SeedingConfig fleet, int maxWakes = MaxWakes)
    {
        networks ??= [];
        if (networks.Count == 0 || networks.All(n => n.ActiveWindows is null))
        {
            // Dormant mode: no per-network boundaries anywhere — the single fleet wake,
            // unattributed, exactly as before per-tenant scheduling.
            var (h, m) = FleetWakeTimeUtc(fleet);
            return new WakePlan(
                [new PlannedWake(new TimeOnly(h, m), [], Math.Max(1, fleet.MissedAutoseedWindowHours))],
                []);
        }

        // time → (ids, missed-hours max)
        var byTime = new SortedDictionary<TimeOnly, (SortedSet<long> Ids, int MissedHours)>();
        foreach (var network in networks)
        {
            var windows = network.ActiveWindows ?? fleet.ActiveWindows;
            var missedHours = Math.Max(1, network.MissedAutoseedWindowHours ?? fleet.MissedAutoseedWindowHours);
            foreach (var time in NetworkWakeTimes(windows, network.DailyResetHourUtc ?? fleet.DailyResetHourUtc))
            {
                if (byTime.TryGetValue(time, out var existing))
                {
                    existing.Ids.Add(network.NetworkId);
                    byTime[time] = (existing.Ids, Math.Max(existing.MissedHours, missedHours));
                }
                else
                {
                    byTime[time] = (new SortedSet<long> { network.NetworkId }, missedHours);
                }
            }
        }

        var all = byTime
            .Select(kv => new PlannedWake(kv.Key, kv.Value.Ids.ToList(), kv.Value.MissedHours))
            .ToList();
        return new WakePlan(all.Take(maxWakes).ToList(), all.Skip(maxWakes).ToList());
    }

    /// <summary>One network's wake times: each window's start, or the daily reset hour when the
    /// window list is empty (always active — the machine still has to be up for the day's cycle).</summary>
    private static IEnumerable<TimeOnly> NetworkWakeTimes(List<TimeWindow> windows, int dailyResetHourUtc)
    {
        if (windows.Count == 0)
        {
            yield return new TimeOnly(Math.Clamp(dailyResetHourUtc, 0, 23), 0);
            yield break;
        }
        foreach (var w in windows)
        {
            var start = Math.Clamp(w.StartMin, 0, 1439);
            yield return new TimeOnly(start / 60, start % 60);
        }
    }

    /// <summary>
    /// The fleet-wide daily wake time (UTC): the earliest active-window start, or
    /// <c>DailyResetHourUtc:00</c> when no windows are configured. This is the pre-per-tenant
    /// behaviour, kept as the membership-less / dormant fallback.
    /// </summary>
    public static (int Hours, int Minutes) FleetWakeTimeUtc(SeedingConfig config)
    {
        if (config.ActiveWindows is { Count: > 0 } windows)
        {
            var minStart = windows.Min(w => w.StartMin);
            minStart = Math.Clamp(minStart, 0, 1439);
            return (minStart / 60, minStart % 60);
        }
        return (Math.Clamp(config.DailyResetHourUtc, 0, 23), 0);
    }
}
