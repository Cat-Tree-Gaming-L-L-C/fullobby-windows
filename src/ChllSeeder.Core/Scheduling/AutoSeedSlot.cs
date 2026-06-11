namespace ChllSeeder.Core.Scheduling;

/// <summary>
/// Static configuration for one auto-seed region slot (NA / EU). Mirrors the Rust
/// <c>AUTOSEED_SLOTS</c> table. The store key holds the user's chosen UTC time; the task name is
/// the schtasks task; the CLI arg is what the scheduled task passes back to a fresh app instance.
/// Clean-break rename: no legacy task-name fallbacks (Esprit-Seeder-2 etc. are gone).
/// </summary>
public sealed record AutoSeedSlot(string Region, string StoreKey, string TaskName, string CliArg)
{
    /// <summary>NA: stored under "auto_seed_time", task "CHLL-Seeder", arg "--autoseed-na".</summary>
    public static readonly AutoSeedSlot Na = new("na", "auto_seed_time", Branding.ScheduledTaskNa, "--autoseed-na");

    /// <summary>EU: stored under "auto_seed_time_secondary", task "CHLL-Seeder-EU", arg "--autoseed-eu".</summary>
    public static readonly AutoSeedSlot Eu = new("eu", "auto_seed_time_secondary", Branding.ScheduledTaskEu, "--autoseed-eu");

    public static readonly IReadOnlyList<AutoSeedSlot> All = [Na, Eu];

    /// <summary>Resolve a slot by region ("na"/"eu"). Returns null for anything else.</summary>
    public static AutoSeedSlot? ByRegion(string region) => region switch
    {
        "na" => Na,
        "eu" => Eu,
        _ => null,
    };

    /// <summary>Resolve a slot by its CLI arg ("--autoseed-na"/"--autoseed-eu").</summary>
    public static AutoSeedSlot? ByCliArg(string arg) => arg switch
    {
        "--autoseed-na" or "--seed-na" => Na,
        "--autoseed-eu" or "--seed-eu" => Eu,
        _ => null,
    };
}

/// <summary>Snapshot of both auto-seed scheduled tasks for the Settings UI. Port of <c>AutoseedStatus</c>.</summary>
public sealed record AutoseedStatus(
    bool NaInstalled,
    string? NaNextRun,
    string? NaUtcTime,
    bool EuInstalled,
    string? EuNextRun,
    string? EuUtcTime);
