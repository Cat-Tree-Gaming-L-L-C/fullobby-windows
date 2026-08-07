using Fullobby.Core.Api;
using Fullobby.Core.Config;
using Fullobby.Core.Native;
using Microsoft.Extensions.Logging;

namespace Fullobby.Core.Scheduling;

/// <summary>
/// High-level auto-seed orchestration: create/remove the single daily scheduled task and report its
/// status. The wake time is no longer user-picked per region — it's derived from the server's first
/// active window (UTC), so the PC wakes when seeding actually starts. We persist that UTC string (for
/// the missed-task monitor) and register the task at the equivalent local time. Collapsed to one slot.
/// </summary>
public sealed class AutoSeedService
{
    private readonly ILogger<AutoSeedService> _log;
    private readonly ScheduledTaskService _tasks;
    private readonly ConfigService _config;
    private readonly PowerStatus _power;
    private readonly SeedingConfigProvider _configProvider;

    public AutoSeedService(
        ILogger<AutoSeedService> log,
        ScheduledTaskService tasks,
        ConfigService config,
        PowerStatus power,
        SeedingConfigProvider configProvider)
    {
        _log = log;
        _tasks = tasks;
        _config = config;
        _power = power;
        _configProvider = configProvider;
    }

    /// <summary>
    /// Set up the daily auto-seed. The wake time is the server's first active-window start (UTC),
    /// or <c>DailyResetHourUtc:00</c> when no windows are configured. Persists it, creates the
    /// scheduled task at the local-equivalent time, and returns a status message (plus any
    /// power-config warnings).
    /// </summary>
    public async Task<string> SetupAsync(CancellationToken ct = default)
    {
        var (h, m) = WakeTimeUtc(_configProvider.Current);
        await RegisterAsync(h, m, ct).ConfigureAwait(false);

        var localDisplay = AutoSeedTime.UtcToLocalDisplay(h, m);
        var message = $"Daily auto-seed is set up for {h:D2}:{m:D2} UTC " +
                      $"({localDisplay} your time). Your computer will wake from sleep to seed.";

        var warnings = _power.CheckPowerWarnings();
        if (!string.IsNullOrEmpty(warnings))
        {
            message += "\n\n" + warnings;
        }
        return message;
    }

    /// <summary>
    /// Silently re-arm the daily wake to the window in a freshly fetched config — used when an
    /// auto-seed wake lands in a scheduled pause and the fleet has moved its window. The client
    /// learns the new time and can go back to sleep. Publishes the fresh config, then rewrites the
    /// task only when the target time actually changed (avoids needless churn). No-op when the task
    /// isn't installed (auto-seed disabled).
    /// </summary>
    public async Task RescheduleFromConfigAsync(SeedingConfig freshConfig, CancellationToken ct = default)
    {
        _configProvider.Update(freshConfig);

        var slot = AutoSeedSlot.Default;
        if (!await _tasks.IsInstalledAsync(slot.TaskName, ct).ConfigureAwait(false))
        {
            return; // auto-seed not set up — nothing to re-arm
        }

        var (h, m) = WakeTimeUtc(freshConfig);
        var normalizedUtc = $"{h:D2}:{m:D2}";
        if (_config.GetString(slot.StoreKey) == normalizedUtc)
        {
            return; // already armed for this window
        }

        await RegisterAsync(h, m, ct).ConfigureAwait(false);
        _log.LogInformation("Auto-seed wake re-armed to {Utc} UTC from updated fleet config", normalizedUtc);
    }

    /// <summary>Create (or replace) the daily wake task at the given UTC time and verify it
    /// registered. Persists the UTC string first so the missed-task monitor can act even if task
    /// creation is slow. Throws on failure.</summary>
    private async Task RegisterAsync(int hoursUtc, int minutesUtc, CancellationToken ct)
    {
        var normalizedUtc = $"{hoursUtc:D2}:{minutesUtc:D2}";
        var localHms = AutoSeedTime.UtcToLocalHms(hoursUtc, minutesUtc);
        var slot = AutoSeedSlot.Default;

        _config.SetString(slot.StoreKey, normalizedUtc);

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

    /// <summary>Remove the scheduled task and forget its stored time. Returns true if a task was deleted.</summary>
    public async Task<bool> UninstallAsync(CancellationToken ct = default)
    {
        var slot = AutoSeedSlot.Default;
        var deleted = await _tasks.DeleteTaskAsync(slot.TaskName, ct).ConfigureAwait(false);
        _config.Remove(slot.StoreKey);
        return deleted;
    }

    /// <summary>Snapshot the task (installed + next-run + stored UTC time) for the Settings UI.</summary>
    public async Task<AutoseedStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var slot = AutoSeedSlot.Default;
        var installed = await _tasks.IsInstalledAsync(slot.TaskName, ct).ConfigureAwait(false);
        var next = installed ? await _tasks.GetNextRunTimeAsync(slot.TaskName, ct).ConfigureAwait(false) : null;
        return new AutoseedStatus(installed, next, _config.GetString(slot.StoreKey));
    }

    /// <summary>
    /// The daily wake time (UTC) derived from the seeding config: the earliest active-window start,
    /// or <c>DailyResetHourUtc:00</c> when no windows are configured. Pure; unit-testable.
    /// </summary>
    public static (int Hours, int Minutes) WakeTimeUtc(SeedingConfig config)
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
