using System.Collections.ObjectModel;
using ChllSeeder.Core.Api;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace ChllSeeder.App.ViewModels;

/// <summary>
/// View model for the Ranks (Leaderboard) tab. Fetches the public seeding leaderboard
/// for the selected period and, when signed in, the current user's personal stats.
/// Port of <c>src-rust/src/components/leaderboard.rs</c>.
/// </summary>
public sealed partial class LeaderboardViewModel : ObservableObject
{
    private readonly ILogger<LeaderboardViewModel> _log;
    private readonly SeedingApiClient _api;
    private readonly AccountViewModel _account;
    private readonly DispatcherQueue _dispatcher;

    // Per-fetch cooldowns, matching the Rust 5s guards.
    private long _leaderboardCooldownUntil;
    private long _myStatsCooldownUntil;

    public LeaderboardViewModel(
        ILogger<LeaderboardViewModel> log,
        SeedingApiClient api,
        AccountViewModel account)
    {
        _log = log;
        _api = api;
        _account = account;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodLabel))]
    private long periodDays = 7;

    [ObservableProperty]
    private bool loading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool loaded;

    /// <summary>True when the signed-in user has a "My Stats" card to show.</summary>
    [ObservableProperty]
    private bool showMyStats;

    [ObservableProperty]
    private bool myStatsLoading;

    [ObservableProperty]
    private bool hasMyStats;

    [ObservableProperty]
    private string myStatsTotalTime = "0m";

    [ObservableProperty]
    private long myStatsSessions;

    [ObservableProperty]
    private string myStatsAvgSession = "0m";

    public ObservableCollection<LeaderboardRow> Entries { get; } = new();
    public ObservableCollection<ServerStatRow> MyServers { get; } = new();
    public ObservableCollection<RecentSessionRow> MyRecentSessions { get; } = new();

    public bool HasError => Error is not null;
    public bool IsEmpty => Loaded && Error is null && Entries.Count == 0;

    public string PeriodLabel => PeriodDays switch
    {
        1 => "Today",
        7 => "This Week",
        30 => "This Month",
        _ => "All Time",
    };

    /// <summary>Switch the period and reload. Bound by the Day/Week/Month/All buttons.</summary>
    [RelayCommand]
    public async Task SetPeriodAsync(string days)
    {
        if (long.TryParse(days, out var d))
        {
            PeriodDays = d;
        }
        await RefreshAsync().ConfigureAwait(false);
    }

    /// <summary>(Re)load the leaderboard and, when signed in, personal stats for the current period.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        await Task.WhenAll(FetchLeaderboardAsync(), FetchMyStatsAsync()).ConfigureAwait(false);
    }

    private async Task FetchLeaderboardAsync()
    {
        if (OnCooldown(ref _leaderboardCooldownUntil, 5))
        {
            return;
        }
        RunOnUi(() =>
        {
            Loading = true;
            Error = null;
        });
        try
        {
            var entries = await _api.GetLeaderboardAsync(PeriodDays, 25).ConfigureAwait(false);
            RunOnUi(() =>
            {
                Entries.Clear();
                foreach (var e in entries)
                {
                    Entries.Add(new LeaderboardRow(e));
                }
            });
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to fetch leaderboard");
            RunOnUi(() => Error = $"Failed to load leaderboard: {ApiValidation.FriendlyError(e.Message)}");
        }
        finally
        {
            RunOnUi(() =>
            {
                Loading = false;
                Loaded = true;
                OnPropertyChanged(nameof(IsEmpty));
            });
        }
    }

    private async Task FetchMyStatsAsync()
    {
        var user = _account.User;
        if (!_account.IsLoggedIn || user is null || user.UserId.Length == 0)
        {
            RunOnUi(() =>
            {
                ShowMyStats = false;
                HasMyStats = false;
            });
            return;
        }
        RunOnUi(() =>
        {
            ShowMyStats = true;
            MyStatsLoading = true;
        });
        if (OnCooldown(ref _myStatsCooldownUntil, 5))
        {
            RunOnUi(() => MyStatsLoading = false);
            return;
        }
        try
        {
            var stats = await _api.GetUserStatsAsync(user.UserId, PeriodDays).ConfigureAwait(false);
            RunOnUi(() =>
            {
                MyStatsTotalTime = FormatDuration(stats.TotalTimeSecs);
                MyStatsSessions = stats.SessionCount;
                MyStatsAvgSession = FormatDuration(stats.AvgSessionSecs);
                MyServers.Clear();
                foreach (var s in stats.Servers)
                {
                    MyServers.Add(new ServerStatRow(s.ServerName, FormatDuration(s.TotalTimeSecs), s.SessionCount));
                }
                MyRecentSessions.Clear();
                foreach (var s in stats.RecentSessions.Take(5))
                {
                    MyRecentSessions.Add(new RecentSessionRow(s.ServerName, s.Status, FormatDuration(s.DurationSecs)));
                }
                HasMyStats = true;
            });
        }
        catch (Exception e)
        {
            _log.LogError(e, "Failed to fetch my stats");
            RunOnUi(() => HasMyStats = false);
        }
        finally
        {
            RunOnUi(() => MyStatsLoading = false);
        }
    }

    /// <summary>Format seconds as "Xh Ym", or "Ym" under an hour. Port of fmt_duration.</summary>
    public static string FormatDuration(long secs)
    {
        var h = secs / 3600;
        var m = (secs % 3600) / 60;
        return h > 0 ? $"{h}h {m}m" : $"{m}m";
    }

    private static bool OnCooldown(ref long until, int seconds)
    {
        var now = Environment.TickCount64;
        if (now < until)
        {
            return true;
        }
        until = now + seconds * 1000L;
        return false;
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }
}

/// <summary>A single leaderboard ranking row, formatted for display.</summary>
public sealed class LeaderboardRow
{
    public LeaderboardRow(LeaderboardEntry e)
    {
        Rank = e.Rank;
        Username = e.Username;
        Time = LeaderboardViewModel.FormatDuration(e.TotalTimeSecs);
        Sessions = e.SessionCount;
    }

    public long Rank { get; }
    public string Username { get; }
    public string Time { get; }
    public long Sessions { get; }
}

/// <summary>Per-server breakdown row in the My Stats card.</summary>
public sealed class ServerStatRow(string serverName, string time, long sessions)
{
    public string ServerName { get; } = serverName;
    public string Time { get; } = time;
    public long Sessions { get; } = sessions;
}

/// <summary>Recent session row in the My Stats card.</summary>
public sealed class RecentSessionRow(string serverName, string status, string duration)
{
    public string ServerName { get; } = serverName;
    public string Status { get; } = status;
    public string Duration { get; } = duration;
}
