using ChllSeeding.Core.Config;
using ChllSeeding.Core.Native;
using Microsoft.Extensions.Logging;

namespace ChllSeeding.Core.Scheduling;

/// <summary>
/// High-level auto-seed orchestration: create/remove the daily scheduled tasks and report their
/// status. The user picks a daily time in <b>UTC</b>; we persist that UTC string (for the missed-task
/// monitor) and register the task at the equivalent local time. Port of the public surface of
/// <c>backend/autoseed.rs</c> (<c>setup_auto_seed</c>, <c>uninstall_auto_seed*</c>, <c>get_autoseed_status</c>).
/// </summary>
public sealed class AutoSeedService
{
    private readonly ILogger<AutoSeedService> _log;
    private readonly ScheduledTaskService _tasks;
    private readonly ConfigService _config;
    private readonly PowerStatus _power;

    public AutoSeedService(
        ILogger<AutoSeedService> log,
        ScheduledTaskService tasks,
        ConfigService config,
        PowerStatus power)
    {
        _log = log;
        _tasks = tasks;
        _config = config;
        _power = power;
    }

    /// <summary>
    /// Set up the daily auto-seed for a region. <paramref name="utcTime"/> is the user-entered UTC
    /// "HH:MM". Persists it, creates the scheduled task at the local-equivalent time, and returns a
    /// status message (plus any power-config warnings). Throws <see cref="FormatException"/> on a bad time.
    /// </summary>
    public async Task<string> SetupAsync(string region, string utcTime, CancellationToken ct = default)
    {
        var slot = AutoSeedSlot.ByRegion(region)
            ?? throw new ArgumentException($"Unknown region '{region}'", nameof(region));

        if (!AutoSeedTime.TryParseUtc(utcTime, out var h, out var m))
        {
            throw new FormatException($"Invalid UTC time '{utcTime}' (expected HH:MM).");
        }
        var normalizedUtc = $"{h:D2}:{m:D2}";
        var localHms = AutoSeedTime.UtcToLocalHms(h, m);

        // Persist the UTC time first so the missed-task monitor can act even if task creation is slow.
        _config.SetString(slot.StoreKey, normalizedUtc);

        var ok = await _tasks.CreateDailyTaskAsync(slot.TaskName, localHms, slot.CliArg, ct).ConfigureAwait(false);
        if (!ok)
        {
            throw new InvalidOperationException($"Failed to create the {region.ToUpperInvariant()} scheduled task.");
        }

        // Re-query to confirm the task actually registered — schtasks can report success on /create
        // yet leave nothing queryable. Port of the post-create verification in autoseed.rs:40-54.
        if (!await _tasks.IsInstalledAsync(slot.TaskName, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"The {region.ToUpperInvariant()} scheduled task could not be verified after creation. Please try again.");
        }

        var localDisplay = AutoSeedTime.UtcToLocalDisplay(h, m);
        var message = $"Daily {region.ToUpperInvariant()} auto-seed is set up for {normalizedUtc} UTC " +
                      $"({localDisplay} your time). Your computer will wake from sleep to seed.";

        var warnings = _power.CheckPowerWarnings();
        if (!string.IsNullOrEmpty(warnings))
        {
            message += "\n\n" + warnings;
        }
        return message;
    }

    /// <summary>Remove a region's scheduled task and forget its stored time. Returns true if a task was deleted.</summary>
    public async Task<bool> UninstallAsync(string region, CancellationToken ct = default)
    {
        var slot = AutoSeedSlot.ByRegion(region)
            ?? throw new ArgumentException($"Unknown region '{region}'", nameof(region));

        var deleted = await _tasks.DeleteTaskAsync(slot.TaskName, ct).ConfigureAwait(false);
        _config.Remove(slot.StoreKey);
        return deleted;
    }

    /// <summary>Whether the EU auto-seed task is installed (gates the EU "Set up/Remove" UI).</summary>
    public Task<bool> IsEuInstalledAsync(CancellationToken ct = default) =>
        _tasks.IsInstalledAsync(AutoSeedSlot.Eu.TaskName, ct);

    /// <summary>Snapshot both tasks (installed + next-run + stored UTC time) for the Settings UI.</summary>
    public async Task<AutoseedStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var naInstalled = await _tasks.IsInstalledAsync(AutoSeedSlot.Na.TaskName, ct).ConfigureAwait(false);
        var naNext = naInstalled ? await _tasks.GetNextRunTimeAsync(AutoSeedSlot.Na.TaskName, ct).ConfigureAwait(false) : null;
        var euInstalled = await _tasks.IsInstalledAsync(AutoSeedSlot.Eu.TaskName, ct).ConfigureAwait(false);
        var euNext = euInstalled ? await _tasks.GetNextRunTimeAsync(AutoSeedSlot.Eu.TaskName, ct).ConfigureAwait(false) : null;

        return new AutoseedStatus(
            naInstalled, naNext, _config.GetString(AutoSeedSlot.Na.StoreKey),
            euInstalled, euNext, _config.GetString(AutoSeedSlot.Eu.StoreKey));
    }
}
