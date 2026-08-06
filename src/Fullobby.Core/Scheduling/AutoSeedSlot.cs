namespace Fullobby.Core.Scheduling;

/// <summary>
/// Static configuration for the single auto-seed slot. The store key holds the user's chosen UTC
/// wake time (derived from the server's active window); the task name is the schtasks task; the CLI
/// arg is what the scheduled task passes back to a fresh app instance. Region is removed — the
/// server now decides the rotation, so there is only one daily seed task.
/// </summary>
public sealed record AutoSeedSlot(string StoreKey, string TaskName, string CliArg)
{
    /// <summary>The single auto-seed slot: stored under "auto_seed_time", task "Fullobby",
    /// arg "--autoseed".</summary>
    public static readonly AutoSeedSlot Default = new("auto_seed_time", Branding.ScheduledTaskNa, "--autoseed");

    /// <summary>Whether a CLI arg requests an auto-seed launch. Accepts --autoseed (+ legacy --seed*).</summary>
    public static bool IsAutoseedArg(string arg) => arg switch
    {
        "--autoseed" or "--autoseed-na" or "--autoseed-eu" or "--seed-na" or "--seed-eu" => true,
        _ => false,
    };
}

/// <summary>Snapshot of the auto-seed scheduled task for the Settings UI.</summary>
public sealed record AutoseedStatus(
    bool Installed,
    string? NextRun,
    string? UtcTime);
