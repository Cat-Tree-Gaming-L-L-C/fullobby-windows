using System.Text;
using Fullobby.Core.Api;
using Fullobby.Core.Config;
using Fullobby.Core.Games;
using Fullobby.Core.Native;
using Microsoft.Extensions.Logging;

namespace Fullobby.Core.Scheduling;

/// <summary>
/// High-level auto-seed orchestration over the wake <em>set</em>: one Task Scheduler task per
/// planned wake time (see <see cref="WakePlanner"/>), reconciled against the per-network schedule
/// boundaries when the backend sends them and the fleet-wide config otherwise. Until the backend's
/// per-tenant fields land, the plan is always the single server-derived wake the client has always
/// kept — the multi-wake machinery ships dormant (docs/PER-TENANT-SCHEDULING.md).
/// </summary>
public sealed class AutoSeedService
{
    private readonly ILogger<AutoSeedService> _log;
    private readonly ScheduledTaskService _tasks;
    private readonly ConfigService _config;
    private readonly PowerStatus _power;
    private readonly SeedingConfigProvider _configProvider;
    private readonly SeedingStatusCache _statusCache;

    /// <summary>Serializes reconciles: the pause path, the bootstrapper's periodic refresh, and a
    /// status push can all ask at once, and two concurrent plan-diffs would race schtasks.</summary>
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);

    public AutoSeedService(
        ILogger<AutoSeedService> log,
        ScheduledTaskService tasks,
        ConfigService config,
        PowerStatus power,
        SeedingConfigProvider configProvider,
        SeedingStatusCache statusCache)
    {
        _log = log;
        _tasks = tasks;
        _config = config;
        _power = power;
        _configProvider = configProvider;
        _statusCache = statusCache;
    }

    /// <summary>
    /// Set up (or refresh) the daily auto-seed: derive the wake plan, register a task per wake, and
    /// return a status message (plus any power-config warnings). Enables auto-seed — from here on
    /// the reconciler keeps the set in step with the schedule.
    /// </summary>
    public async Task<string> SetupAsync(CancellationToken ct = default)
    {
        var plan = ComputeCurrentPlan();
        await ApplyPlanAsync(plan, WakeStore.Load(_config), force: true, ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.Append(plan.Wakes.Count == 1
            ? "Daily auto-seed is set up. Your computer will wake from sleep to seed at "
            : "Daily auto-seed is set up. Your computer will wake from sleep to seed at each of ");
        sb.Append(string.Join(", ", plan.Wakes.Select(w =>
            $"{w.Key} UTC ({AutoSeedTime.UtcToLocalDisplay(w.TimeUtc.Hour, w.TimeUtc.Minute)} your time)")));
        sb.Append('.');

        var warnings = _power.CheckPowerWarnings();
        if (!string.IsNullOrEmpty(warnings))
        {
            sb.Append("\n\n").Append(warnings);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Re-arm the wake set from a freshly fetched config — used when an auto-seed wake lands in a
    /// scheduled pause and the schedule has moved. Publishes the fresh config, then reconciles.
    /// </summary>
    public async Task RescheduleFromConfigAsync(SeedingConfig freshConfig, CancellationToken ct = default)
    {
        _configProvider.Update(freshConfig);
        await ReconcileAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Bring the scheduled tasks in line with the current plan. No-op unless auto-seed is enabled;
    /// pure in-memory comparison (no process spawns) when nothing changed, so it is safe to call on
    /// every status push and config refresh.
    ///
    /// The one rule that must hold (it is why per-tenant scheduling exists): a wake is removed only
    /// on <em>positive evidence</em> — a fresh per-network status whose windows no longer cover it.
    /// When per-network data is unavailable (SSE down, cache stale) a plan derived from the fleet
    /// fallback must not delete wakes that networks justified; we skip instead. The unattributed
    /// single fleet wake has no such protection to need — it just follows the fleet config, which
    /// is the pre-per-tenant behaviour.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        if (!WakeStore.IsEnabled(_config))
        {
            return; // auto-seed not set up — nothing to keep in step
        }

        var stored = WakeStore.Load(_config);
        var statuses = _statusCache.GetCached()?.Networks;
        if (statuses is null && stored.Any(w => w.NetworkIds.Count > 0))
        {
            return; // no fresh data — never drop a tenant's wake on silence
        }

        await ApplyPlanAsync(ComputeCurrentPlan(), stored, force: false, ct).ConfigureAwait(false);
    }

    /// <summary>The desired plan, from cached per-network statuses when fresh (else the fleet
    /// fallback plan). Server windows count only for games this machine can launch.</summary>
    private WakePlan ComputeCurrentPlan() =>
        WakePlanner.ComputePlan(
            _statusCache.GetCached()?.Networks, _configProvider.Current, gameIds: InstalledGames.Ids());

    /// <summary>
    /// Diff the desired plan against the stored set and make Task Scheduler match: register tasks
    /// for new wake times, delete tasks whose time no wake wants anymore (including a legacy
    /// single-slot "Fullobby" task, which this adopts into its time-keyed name), and leave matching
    /// times alone — a task's identity is its time, so neighbours changing never churn it.
    /// <paramref name="force"/> (setup) registers every task even when the stored set already
    /// matches, healing externally deleted tasks.
    /// </summary>
    private async Task ApplyPlanAsync(
        WakePlan plan, IReadOnlyList<StoredWake> stored, bool force, CancellationToken ct)
    {
        if (plan.Dropped.Count > 0)
        {
            _log.LogWarning(
                "Wake plan capped at {Max}: dropped {Dropped}",
                WakePlanner.MaxWakes, string.Join(", ", plan.Dropped.Select(w => w.Key)));
        }

        await _reconcileGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var desiredKeys = plan.Wakes.Select(w => w.Key).ToHashSet(StringComparer.Ordinal);
            var keptKeys = stored
                .Where(w => !w.Legacy && desiredKeys.Contains(w.TimeUtc))
                .Select(w => w.TimeUtc)
                .ToHashSet(StringComparer.Ordinal);

            var toRemove = stored.Where(w => w.Legacy || !desiredKeys.Contains(w.TimeUtc)).ToList();
            var toCreate = force
                ? plan.Wakes
                : plan.Wakes.Where(w => !keptKeys.Contains(w.Key)).ToList();

            if (toCreate.Count == 0 && toRemove.Count == 0)
            {
                // Times unchanged; persist only if the metadata (justifying networks, missed
                // window) moved, so the store stays truthful without touching schtasks.
                if (!MetadataMatches(plan.Wakes, stored))
                {
                    WakeStore.Save(_config, plan.Wakes);
                }
                return;
            }

            // Persist first, matching the old single-slot behaviour: the missed-seed watchdog can
            // act on the stored times even if task creation is slow (it independently verifies a
            // task exists before firing).
            WakeStore.Save(_config, plan.Wakes);

            // Register the new (or, under force, all) wakes. A failed registration is dropped from
            // the persisted set so the next reconcile sees the wake as missing and retries, instead
            // of no-op'ing forever against a store that claims a task that isn't there.
            var failed = new List<string>();
            InvalidOperationException? firstFailure = null;
            foreach (var wake in toCreate)
            {
                try
                {
                    await RegisterAsync(wake, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException e)
                {
                    _log.LogError(e, "Failed to register auto-seed wake {Time} UTC", wake.Key);
                    failed.Add(wake.Key);
                    firstFailure ??= e;
                }
            }
            if (failed.Count > 0)
            {
                WakeStore.Save(_config, plan.Wakes.Where(w => !failed.Contains(w.Key)));
            }

            foreach (var wake in toRemove)
            {
                await _tasks.DeleteTaskAsync(wake.Slot.TaskName, ct).ConfigureAwait(false);
                _log.LogInformation(
                    "Removed auto-seed wake {Time} UTC ({Reason})",
                    wake.TimeUtc, wake.Legacy ? "adopted into the wake set" : "no network's window covers it anymore");
            }

            // Backstop sweep, only on ticks that actually changed something: delete any owned task
            // the plan doesn't want — catches a stray a crashed reconcile forgot, and a delete that
            // failed on an earlier pass (the store no longer lists it, so toRemove can't retry it).
            var desiredTaskNames = plan.Wakes
                .Select(w => AutoSeedSlot.ForTime(w.TimeUtc).TaskName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in await _tasks.ListOwnedTasksAsync(Branding.ScheduledTaskName, ct).ConfigureAwait(false))
            {
                if (!desiredTaskNames.Contains(name))
                {
                    await _tasks.DeleteTaskAsync(name, ct).ConfigureAwait(false);
                }
            }

            _log.LogInformation(
                "Auto-seed wake set reconciled: {Wakes}",
                string.Join(", ", plan.Wakes.Select(w => w.NetworkIds.Count == 0
                    ? $"{w.Key} (fleet)"
                    : $"{w.Key} (networks {string.Join('/', w.NetworkIds)})")));

            // Setup must still surface the failure to the user after the cleanup above.
            if (firstFailure is not null)
            {
                throw firstFailure;
            }
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private static bool MetadataMatches(IReadOnlyList<PlannedWake> desired, IReadOnlyList<StoredWake> stored)
    {
        // Callers have already established the key sets match; compare per-key metadata.
        var byKey = stored.ToDictionary(w => w.TimeUtc, StringComparer.Ordinal);
        foreach (var wake in desired)
        {
            if (!byKey.TryGetValue(wake.Key, out var s)
                || s.Legacy
                || !s.NetworkIds.SequenceEqual(wake.NetworkIds)
                || s.MissedWindowHours != (wake.NetworkIds.Count == 0 ? null : wake.MissedWindowHours))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Create (or replace) one wake's daily task at the local-equivalent time and verify
    /// it registered. Throws on failure.</summary>
    private async Task RegisterAsync(PlannedWake wake, CancellationToken ct)
    {
        var slot = AutoSeedSlot.ForTime(wake.TimeUtc);
        var localHms = AutoSeedTime.UtcToLocalHms(wake.TimeUtc.Hour, wake.TimeUtc.Minute);

        var ok = await _tasks.CreateDailyTaskAsync(slot.TaskName, localHms, slot.CliArg, ct).ConfigureAwait(false);
        if (!ok)
        {
            throw new InvalidOperationException("Failed to create the auto-seed scheduled task.");
        }

        // Re-query to confirm the task actually registered — schtasks can report success on /create
        // yet leave nothing queryable.
        if (!await _tasks.IsInstalledAsync(slot.TaskName, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "The auto-seed scheduled task could not be verified after creation. Please try again.");
        }
    }

    /// <summary>
    /// Remove auto-seed tasks left behind by a previous install. The tasks belong to Windows Task
    /// Scheduler, which outlives our config: a wiped config, or an uninstall by a build whose
    /// uninstaller didn't clean them up, leaves the machine waking daily to run a seed the app will
    /// refuse. An install that has never completed onboarding cannot legitimately own one —
    /// auto-seed is set up from Settings, which is only reachable after the wizard — so any task
    /// found here is a leftover. Sweeps by name prefix, because a wiped config can no longer say
    /// which time-keyed names exist. Returns true if any was removed.
    ///
    /// Keyed on the persisted onboarding flag rather than the shell's <c>ShowOnboarding</c>: that is
    /// also true while the join-a-network gate is up, for a fully onboarded user whose tasks must
    /// survive. Call only once session restore has settled, since that can heal a completion flag
    /// lost to a crash.
    /// </summary>
    public async Task<bool> RemoveOrphanedTasksAsync(CancellationToken ct = default)
    {
        if (_config.GetBool(ConfigKeys.OnboardingComplete))
        {
            return false; // a real user's tasks — leave them alone
        }

        var names = await _tasks.ListOwnedTasksAsync(Branding.ScheduledTaskName, ct).ConfigureAwait(false);
        if (names.Count == 0)
        {
            return false;
        }

        _log.LogWarning(
            "Removing {Count} auto-seed scheduled task(s) left behind by a previous install: {Names}",
            names.Count, string.Join(", ", names));
        var removed = false;
        foreach (var name in names)
        {
            removed |= await _tasks.DeleteTaskAsync(name, ct).ConfigureAwait(false);
        }
        WakeStore.Clear(_config);
        return removed;
    }

    /// <summary>Remove every auto-seed task (ours by name prefix, so strays from older builds go
    /// too) and forget the stored wake set. Returns true if any task was deleted.</summary>
    public async Task<bool> UninstallAsync(CancellationToken ct = default)
    {
        // Prefix sweep rather than just the stored names: it also catches the legacy "Fullobby" /
        // "Fullobby-EU" tasks and any time-keyed task a crashed reconcile forgot.
        var names = await _tasks.ListOwnedTasksAsync(Branding.ScheduledTaskName, ct).ConfigureAwait(false);
        var deleted = false;
        foreach (var name in names)
        {
            deleted |= await _tasks.DeleteTaskAsync(name, ct).ConfigureAwait(false);
        }
        WakeStore.Clear(_config);
        return deleted;
    }

    /// <summary>Snapshot the wake set (per-wake installed + next-run) for the Settings UI.</summary>
    public async Task<AutoseedStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var stored = WakeStore.Load(_config);
        var wakes = new List<AutoseedWakeStatus>(stored.Count);
        foreach (var wake in stored)
        {
            var installed = await _tasks.IsInstalledAsync(wake.Slot.TaskName, ct).ConfigureAwait(false);
            var next = installed
                ? await _tasks.GetNextRunTimeAsync(wake.Slot.TaskName, ct).ConfigureAwait(false)
                : null;
            wakes.Add(new AutoseedWakeStatus(wake.TimeUtc, wake.NetworkIds, installed, next));
        }
        return new AutoseedStatus(wakes.Any(w => w.Installed), wakes);
    }

    /// <summary>The fleet-wide daily wake time (UTC). Kept as a convenience alias for the resleep
    /// messaging path; the real derivation lives in <see cref="WakePlanner.FleetWakeTimeUtc"/>.</summary>
    public static (int Hours, int Minutes) WakeTimeUtc(SeedingConfig config) =>
        WakePlanner.FleetWakeTimeUtc(config);
}
