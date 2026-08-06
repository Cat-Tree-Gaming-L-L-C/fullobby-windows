using Fullobby.Core.Api;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Fullobby.App.ViewModels;

/// <summary>
/// One row in the server stats / launch lists. Static identity (name, index,
/// threshold) is set at construction; <see cref="StatsLine"/> and <see cref="IsOffline"/>
/// update in place on each stats poll so the launch buttons don't rebuild/flicker.
/// </summary>
public sealed partial class ServerRow : ObservableObject
{
    public int Index { get; }
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

    public ServerRow(int index, ServerInfo info)
    {
        Index = index;
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

    // Network group header rendered above this row on the seed board (first row of each
    // network's block; only set when more than one network has servers).
    [ObservableProperty]
    private string networkHeader = "";

    [ObservableProperty]
    private bool showNetworkHeader;

    /// <summary>Set (or clear, with null) the network group header shown above this row.</summary>
    public void SetNetworkGroup(string? header)
    {
        NetworkHeader = header ?? "";
        ShowNetworkHeader = header is not null;
    }

    private DayStatus? _readyStatus;
    private long? _windowStartTs;

    /// <summary>This server's standing in today's rotation (ready-check / seeded state), or null
    /// before the first seeding-status update. Drives <see cref="ReadyBadge"/>.</summary>
    public DayStatus? ReadyStatus => _readyStatus;

    /// <summary>Apply a per-server day-status entry (null → clear the badge).</summary>
    public void ApplyDayStatus(ServerDayStatus? day)
    {
        _readyStatus = day?.Status;
        _windowStartTs = day?.WindowStartTs;
        OnPropertyChanged(nameof(ReadyBadge));
        OnPropertyChanged(nameof(ShowReadyBadge));
    }

    /// <summary>Short badge describing this server's ready standing, or "" when there's nothing
    /// noteworthy (pending its turn, or offline/passworded which the stats line already conveys).</summary>
    public string ReadyBadge => _readyStatus switch
    {
        DayStatus.NotReady => _windowStartTs is { } ts
            ? $"⧗ Not ready — window {LocalHm(ts)}"
            : "⧗ Not ready",
        DayStatus.MissedReady => "✗ Missed ready",
        DayStatus.Deferred => "⟳ Deferred (late)",
        DayStatus.Current => "● Seeding now",
        DayStatus.Done => "✓ Seeded",
        _ => "",
    };

    public bool ShowReadyBadge => ReadyBadge.Length > 0;

    private static string LocalHm(long unixTs) =>
        DateTimeOffset.FromUnixTimeSeconds(unixTs).ToLocalTime().ToString("HH:mm");
}
