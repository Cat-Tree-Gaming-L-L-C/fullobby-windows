using ChllSeeding.Core.Api;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChllSeeding.App.ViewModels;

/// <summary>
/// One row in the server stats / launch lists. Static identity (name, index, region,
/// threshold) is set at construction; <see cref="StatsLine"/> and <see cref="IsOffline"/>
/// update in place on each stats poll so the launch buttons don't rebuild/flicker.
/// </summary>
public sealed partial class ServerRow : ObservableObject
{
    public int Index { get; }
    public string Region { get; }
    public string Name { get; }
    public string ShortName { get; }
    public int Threshold { get; }

    /// <summary>Label for the Launch-tab button ("Launch &lt;short name&gt;").</summary>
    public string LaunchLabel => $"Launch {ShortName}";

    [ObservableProperty]
    private string statsLine;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLaunch))]
    [NotifyPropertyChangedFor(nameof(LaunchBlockedReason))]
    private bool isOffline;

    /// <summary>Server is password-protected. The backend excludes these from seeding
    /// candidates (HLL can't join a passworded server via the launcher), so the Seed list
    /// badges them to explain why they're never picked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanLaunch))]
    [NotifyPropertyChangedFor(nameof(LaunchBlockedReason))]
    private bool isPassworded;

    /// <summary>Whether a direct launch to this server is allowed. The Launch tab lets the user
    /// pick a server by hand (no backend candidate filtering), so it must apply the same
    /// exclusions the seeding path gets for free: an offline server has nothing to join, and HLL
    /// can't join a passworded server via the launcher (confirmed live — all +connect syntaxes
    /// failed). Buttons bind to this and the launch command re-checks it.</summary>
    public bool CanLaunch => !IsOffline && !IsPassworded;

    /// <summary>Caption shown under a disabled launch button explaining why it's blocked. Offline
    /// takes priority — a down server is the more immediate blocker. Empty when launchable.</summary>
    public string LaunchBlockedReason =>
        IsOffline ? "Offline — can't launch"
        : IsPassworded ? "Password-protected — can't join via launcher"
        : "";

    public ServerRow(int index, string region, ServerInfo info)
    {
        Index = index;
        Region = region;
        Name = info.Name;
        ShortName = info.ShortName;
        Threshold = info.SeedingThreshold;
        statsLine = $"{info.Name} — loading…";
    }

    /// <summary>Refresh the display line from a stats batch entry (null → still loading).</summary>
    public void Apply(BatchStatsResult? stat)
    {
        if (stat is null)
        {
            StatsLine = $"{Name} — loading…";
            return;
        }

        IsOffline = stat.Offline;
        IsPassworded = stat.PasswordProtected;
        var map = stat.Offline
            ? "Offline"
            : string.IsNullOrEmpty(stat.MapName) ? "—" : stat.MapName;
        var players = stat.PlayerCount?.ToString() ?? "?";
        StatsLine = $"{Name} — {map} — {players}/{Threshold}";
    }
}
