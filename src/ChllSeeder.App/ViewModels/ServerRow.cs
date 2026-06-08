using ChllSeeder.Core.Api;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChllSeeder.App.ViewModels;

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
    private bool isOffline;

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
        var map = stat.Offline
            ? "Offline"
            : string.IsNullOrEmpty(stat.MapName) ? "—" : stat.MapName;
        var players = stat.PlayerCount?.ToString() ?? "?";
        StatsLine = $"{Name} — {map} — {players}/{Threshold}";
    }
}
