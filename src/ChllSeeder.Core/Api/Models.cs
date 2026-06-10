using System.Text.Json.Serialization;

namespace ChllSeeder.Core.Api;

// Port of src-rust/src/api/types.rs and the ServerInfo struct from
// src-rust/src/backend/server.rs. Wire format is snake_case (see ApiJson).

/// <summary>Server info from the API (no server_public_url — that stays server-side).</summary>
public sealed class ServerInfo
{
    public string Ip { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string Name { get; set; } = "";
    public int SeedingThreshold { get; set; }
    public string Game { get; set; } = "";
}

public sealed class BatchStatsResult
{
    public string Game { get; set; } = "";
    public string Region { get; set; } = "";
    public int Index { get; set; }
    public string? MapName { get; set; }
    public int? PlayerCount { get; set; }
    public int? MaxPlayerCount { get; set; }
    public bool Offline { get; set; }
    /// <summary>Server requires a join password — unjoinable for seeding. The backend already
    /// excludes passworded servers from seeding candidates (see seeding_status.rs); this flag
    /// lets the UI badge them. Sourced from CRCON get_public_info → config.password_protected.</summary>
    public bool PasswordProtected { get; set; }
    public string? Error { get; set; }
}

public sealed class RegionServers
{
    public List<ServerInfo> Na { get; set; } = [];
    public List<ServerInfo> Eu { get; set; } = [];
}

public sealed class ServersResponse
{
    public RegionServers Hll { get; set; } = new();
    public RegionServers? Hllv { get; set; }
    public long CachedAt { get; set; }
}

public sealed class NextServerResponse
{
    public string Game { get; set; } = "";
    public string Region { get; set; } = "";
    public int Index { get; set; }
    public ServerInfo Server { get; set; } = new();
    public bool AllExhausted { get; set; }
    public string? SessionId { get; set; }
}

/// <summary>Auth provider used to sign in. Serializes lowercase; defaults to Steam.</summary>
public enum AuthProvider
{
    Steam,
    Discord,
    Guest,
}

public static class AuthProviderExtensions
{
    /// <summary>Lowercase wire/display name, matching the Rust Display impl.</summary>
    public static string ToWireString(this AuthProvider provider) => provider switch
    {
        AuthProvider.Steam => "steam",
        AuthProvider.Discord => "discord",
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

public sealed class GameSeedingStatus
{
    public RegionCandidate? Na { get; set; }
    public RegionCandidate? Eu { get; set; }
}

/// <summary>Pre-computed seeding status (best server per region).</summary>
public sealed class SeedingStatusResponse
{
    public GameSeedingStatus Hll { get; set; } = new();
    public GameSeedingStatus? Hllv { get; set; }
    public long UpdatedAt { get; set; }
}

/// <summary>A single region's best seeding candidate.</summary>
public sealed class RegionCandidate
{
    public string Game { get; set; } = "";
    public int Index { get; set; }
    public ServerInfo Server { get; set; } = new();
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
    bool EuEnabled,
    bool AutoSeed);
