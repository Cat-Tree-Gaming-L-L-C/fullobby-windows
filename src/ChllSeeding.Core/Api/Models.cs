using System.Text.Json.Serialization;

namespace ChllSeeding.Core.Api;

// Wire format is snake_case (see ApiJson).

/// <summary>Server info from the API (no server_public_url — that stays server-side).</summary>
public sealed class ServerInfo
{
    public string Ip { get; set; } = "";
    /// <summary>BattleMetrics server id. Required field on the server's ServerInfo wire model;
    /// carried for wire-shape parity (no current C# consumer).</summary>
    public long BmId { get; set; }
    public string ShortName { get; set; } = "";
    public string Name { get; set; } = "";
    public int SeedingThreshold { get; set; }
    public string Game { get; set; } = "";
}

public sealed class BatchStatsResult
{
    public string Game { get; set; } = "";
    public int Index { get; set; }
    public string? MapName { get; set; }
    public int? PlayerCount { get; set; }
    public int? MaxPlayerCount { get; set; }
    public bool Offline { get; set; }
    /// <summary>Server requires a join password — unjoinable for seeding. The backend already
    /// excludes passworded servers from seeding candidates; this flag
    /// lets the UI badge them. Sourced from CRCON get_public_info → config.password_protected.</summary>
    public bool PasswordProtected { get; set; }
    public string? Error { get; set; }
}

/// <summary>Public server list for a game — one ordered rotation (region removed).</summary>
public sealed class ServersResponse
{
    public List<ServerInfo> Hll { get; set; } = [];
    public List<ServerInfo>? Hllv { get; set; }
    public long CachedAt { get; set; }
}

/// <summary>Auth provider used to sign in. Serializes lowercase; defaults to Steam.</summary>
public enum AuthProvider
{
    Steam,
    Discord,
    Epic,
    Xbox,
    Guest,
}

/// <summary>How the current session is authenticated.</summary>
public enum AuthMethod
{
    None,
    ApiKey,
    Jwt,
}

/// <summary>PC crossplay storefront a seeding session belongs to. Serializes lowercase
/// ("steam"/"epic"/"xbox") to match the API's JSON wire format. The API intentionally has no
/// default — the client must declare which storefront it is on. This desktop app launches
/// Hell Let Loose exclusively through the Steam client, so it always reports
/// <see cref="Steam"/>.</summary>
public enum Platform
{
    Steam,
    Epic,
    Xbox,
}

public static class PlatformExtensions
{
    /// <summary>Lowercase wire string matching the API's wire representation.</summary>
    public static string ToWireString(this Platform platform) => platform switch
    {
        Platform.Steam => "steam",
        Platform.Epic => "epic",
        Platform.Xbox => "xbox",
        _ => "steam",
    };
}

public static class AuthProviderExtensions
{
    /// <summary>Lowercase wire/display name, matching the server's lowercase display form.</summary>
    public static string ToWireString(this AuthProvider provider) => provider switch
    {
        AuthProvider.Steam => "steam",
        AuthProvider.Discord => "discord",
        AuthProvider.Epic => "epic",
        AuthProvider.Xbox => "xbox",
        AuthProvider.Guest => "guest",
        _ => "steam",
    };
}

public sealed class UserInfo
{
    public string UserId { get; set; } = "";
    public string Username { get; set; } = "";
    public AuthProvider AuthProvider { get; set; } = AuthProvider.Steam;
    public string? DisplayName { get; set; }
    public string? SteamId { get; set; }
    public string? DiscordId { get; set; }
    public string? Avatar { get; set; }
    public long CreatedAt { get; set; }
    public long LastSeenAt { get; set; }
    public bool LeaderboardOptOut { get; set; }
}

public sealed class AuthRefreshResponse
{
    public string Token { get; set; } = "";
    public string RefreshToken { get; set; } = "";
}

public sealed class LinkInitResponse
{
    public string RedirectUrl { get; set; } = "";
}

/// <summary>Response from POST /api/auth/rotate-api-key.</summary>
public sealed class RotateApiKeyResponse
{
    public string ApiKey { get; set; } = "";
}

public sealed class SteamIdEntry
{
    public string Id { get; set; } = "";
    public string SteamId { get; set; } = "";
    public string LinkedAt { get; set; } = "";
}

public sealed class HeartbeatResponse
{
    public string SessionId { get; set; } = "";
    public string Status { get; set; } = "";
    public int HeartbeatCount { get; set; }
    public long SessionDurationSecs { get; set; }
    public bool Validated { get; set; }
}

public sealed class StopSessionResponse
{
    public bool Ok { get; set; }
}

public sealed class LeaderboardEntry
{
    public long Rank { get; set; }
    public long UserId { get; set; }
    public string Username { get; set; } = "";
    public string? DiscordId { get; set; }
    public long TotalTimeSecs { get; set; }
    public long SessionCount { get; set; }
}

public sealed class UserSeedingStats
{
    public long TotalTimeSecs { get; set; }
    public long SessionCount { get; set; }
    public long AvgSessionSecs { get; set; }
    public List<ServerBreakdown> Servers { get; set; } = [];
    public List<RecentSession> RecentSessions { get; set; } = [];
}

public sealed class ServerBreakdown
{
    public string ServerName { get; set; } = "";
    public long TotalTimeSecs { get; set; }
    public long SessionCount { get; set; }
}

public sealed class RecentSession
{
    public string SessionId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string Status { get; set; } = "";
    public long StartedAt { get; set; }
    public long? EndedAt { get; set; }
    public long DurationSecs { get; set; }
}

/// <summary>The current seeding candidate for a game (the gated server in the rotation).</summary>
public sealed class SeedingCandidate
{
    public string Game { get; set; } = "";
    public int Index { get; set; }
    public ServerInfo Server { get; set; } = new();
    public long DbId { get; set; }
}

/// <summary>Pre-computed seeding status: the current candidate per game (one ordered
/// rotation per game; region removed). Null means nothing to seed right now.</summary>
public sealed class SeedingStatusResponse
{
    public SeedingCandidate? Hll { get; set; }
    public SeedingCandidate? Hllv { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class StartSessionResponse
{
    public string SessionId { get; set; } = "";
}

/// <summary>Response from POST /api/auth/register (guest registration).</summary>
public sealed class RegisterResponse
{
    public string UserId { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public sealed class LinkedProvider
{
    public AuthProvider Provider { get; set; } = AuthProvider.Steam;
    public string ProviderId { get; set; } = "";
    public string? DisplayName { get; set; }
    public long LinkedAt { get; set; }
}

/// <summary>Non-PII analytics collected at session start.</summary>
public sealed record SessionStartAnalytics(
    string OsVersion,
    string OsArch,
    bool EfficiencyMode,
    bool AutoSeed);

/// <summary>Admin-editable seeding timing config served by the API (embedded in each directive
/// and at <c>GET /api/seeding/config</c>). The defaults here mirror the server's so the client
/// still works before the first successful fetch and when the API is unreachable.</summary>
public sealed class SeedingConfig
{
    // Rotation
    public int StaggerMaxSecs { get; set; } = 90;
    public int StaggerJitterSecs { get; set; } = 15;
    public int SwitchCountdownSecs { get; set; } = 30;
    public int SnoozeMinSecs { get; set; } = 60;
    public int SnoozeMaxSecs { get; set; } = 1800;
    // Session & poll cadence
    public int MaxSessionSecs { get; set; } = 18000;
    public int MonitorMinSecs { get; set; } = 15;
    public int MonitorMaxSecs { get; set; } = 60;
    public int MonitorBackoffStepSecs { get; set; } = 15;
    public int HeartbeatSecs { get; set; } = 30;
    public int PollFallbackSecs { get; set; } = 10;
    public int PollIdleSecs { get; set; } = 60;
    // Threshold
    public int DefaultSeedingThreshold { get; set; } = 75;
    // Client resilience
    public int SseKeepaliveSecs { get; set; } = 60;
    public int SseBackoffCapSecs { get; set; } = 300;
    public int CacheStaleSecs { get; set; } = 90;
    public List<int> GameOpenRetrySecs { get; set; } = [60, 120];
    public int GameOpenTimeoutSecs { get; set; } = 180;
    public int SplashBypassSecs { get; set; } = 20;
    public int MissedAutoseedWindowHours { get; set; } = 4;
    // UTC schedule
    /// <summary>Daily UTC active windows during which seeding runs. Empty = always active.</summary>
    public List<TimeWindow> ActiveWindows { get; set; } = [];
    public int DailyResetHourUtc { get; set; } = 10;
}

/// <summary>A daily UTC window in minutes-of-day (0–1439). start &gt; end wraps midnight.</summary>
public sealed class TimeWindow
{
    public int StartMin { get; set; }
    public int EndMin { get; set; }
}

/// <summary>What the server decided the client should do next. Serializes lowercase
/// ("seed"/"switch"/"stay"/"stop").</summary>
public enum DirectiveAction
{
    Seed,
    Switch,
    Stay,
    Stop,
}

/// <summary>The seeding target a directive points at: a position in the game's ordered rotation.</summary>
public sealed class DirectiveTarget
{
    public string Game { get; set; } = "";
    public int Index { get; set; }
    public ServerInfo Server { get; set; } = new();
    public long DbId { get; set; }
}

/// <summary>Full server-decided directive from <c>GET /api/seeding/directive</c>. The client polls
/// this and obeys — all timing/decision logic lives server-side.</summary>
public sealed class SeedingDirective
{
    public DirectiveAction Action { get; set; }
    public DirectiveTarget? Target { get; set; }
    public bool AllExhausted { get; set; }
    /// <summary>True when there's no target because of a scheduled gap (timezone dead-hours),
    /// not exhaustion — the client should idle and resume, not hard-stop.</summary>
    public bool ScheduledPause { get; set; }
    /// <summary>Seconds until the next active window opens (when paused), else null.</summary>
    public int? NextActiveInSecs { get; set; }
    public int StaggerSecs { get; set; }
    public int CountdownSecs { get; set; }
    public int SnoozeMinSecs { get; set; }
    public int SnoozeMaxSecs { get; set; }
    public int PollAgainInSecs { get; set; }
    public int MaxSessionSecs { get; set; }
    public SeedingConfig Config { get; set; } = new();
}
