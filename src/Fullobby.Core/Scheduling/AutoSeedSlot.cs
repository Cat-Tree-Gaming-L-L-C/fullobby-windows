using System.Globalization;

namespace Fullobby.Core.Scheduling;

/// <summary>
/// One auto-seed wake's external identity: the Task Scheduler task name and the CLI arg the task
/// passes back to a fresh app instance. No longer a single static slot — there is one per wake
/// time, keyed by the time itself (<c>Fullobby-0600</c> / <c>--autoseed-0600</c>) so a task's
/// identity survives its neighbours changing; ordinal names ("Fullobby-1") would churn whenever a
/// wake is removed (docs/PER-TENANT-SCHEDULING.md).
/// </summary>
public sealed record AutoSeedSlot(string TaskName, string CliArg)
{
    /// <summary>The retired single-slot identity (task "Fullobby", arg "--autoseed") from builds
    /// before the wake set. Kept so an existing install's task is adopted, its launches still
    /// honoured, and its name still swept on uninstall/orphan cleanup.</summary>
    public static readonly AutoSeedSlot Legacy = new(Branding.ScheduledTaskName, "--autoseed");

    /// <summary>The slot for a wake at the given UTC time: task "Fullobby-HHMM", arg
    /// "--autoseed-HHMM". The time is UTC even though the task triggers at the equivalent local
    /// time — the identity must not shift with DST or a timezone move.</summary>
    public static AutoSeedSlot ForTime(TimeOnly timeUtc) =>
        new($"{Branding.ScheduledTaskName}-{Stamp(timeUtc)}", $"--autoseed-{Stamp(timeUtc)}");

    private static string Stamp(TimeOnly t) => $"{t.Hour:D2}{t.Minute:D2}";

    /// <summary>Whether a CLI arg requests an auto-seed launch: per-wake --autoseed-HHMM, plus
    /// --autoseed and the legacy region forms (--autoseed-na/eu, --seed-na/eu) that older tasks
    /// still pass.</summary>
    public static bool IsAutoseedArg(string arg) => arg switch
    {
        "--autoseed" or "--autoseed-na" or "--autoseed-eu" or "--seed-na" or "--seed-eu" => true,
        _ => TryParseWakeArg(arg, out _),
    };

    /// <summary>
    /// Parse a per-wake CLI arg ("--autoseed-HHMM") back into its UTC wake time, identifying which
    /// wake fired. False for the wake-less legacy forms — the caller treats those as "the machine's
    /// only wake" from a pre-set builds' task.
    /// </summary>
    public static bool TryParseWakeArg(string? arg, out TimeOnly timeUtc)
    {
        timeUtc = default;
        const string prefix = "--autoseed-";
        if (arg is null || !arg.StartsWith(prefix, StringComparison.Ordinal) || arg.Length != prefix.Length + 4)
        {
            return false;
        }
        var digits = arg.AsSpan(prefix.Length);
        if (!int.TryParse(digits[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(digits[2..], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
            || h > 23 || m > 59)
        {
            return false;
        }
        timeUtc = new TimeOnly(h, m);
        return true;
    }
}

/// <summary>Status of one wake's scheduled task, for the Settings UI.</summary>
public sealed record AutoseedWakeStatus(
    string TimeUtc,
    IReadOnlyList<long> NetworkIds,
    bool Installed,
    string? NextRun);

/// <summary>Snapshot of the auto-seed wake set for the Settings UI. <see cref="Installed"/> is
/// true while any wake's task is registered.</summary>
public sealed record AutoseedStatus(bool Installed, IReadOnlyList<AutoseedWakeStatus> Wakes);
