using System.Text.Json.Serialization;

namespace Fullobby.Core.Api;

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
    /// <summary>Whether the user holds a <b>global</b> Admin grant (server-computed from
    /// the grants system, independent of which provider they signed in with).
    /// <para>This is NOT the gate for the Manage tab — see <see cref="CanOperateServers"/>.
    /// Gating on it locked out every org admin, who is precisely who the server
    /// management surface exists for.</para></summary>
    public bool IsAdmin { get; set; }

    /// <summary>Whether this user may reach the server-management surface at all: a global
    /// grant, or Admin over at least one org. The server computes it with the same
    /// rule its own endpoints enforce, so this flag and a 403 cannot disagree.</summary>
    public bool CanManageServers { get; set; }

    /// <summary>Whether this user may reach the server list at all, and the two things an
    /// <b>Operator</b> may do there: enable/disable a server, and set its seed window.
    /// A superset of <see cref="CanManageServers"/> — every admin operates too.
    ///
    /// <para>This is the gate for the Manage tab. Operators run an org's servers day
    /// to day and answer its ready checks; gating on the <c>CanManage*</c> flags hid the
    /// tab from them, and their only remaining route to those jobs was Discord.</para>
    /// </summary>
    public bool CanOperateServers { get; set; }

    /// <summary>Whether this user may reach network management: a global grant, or Admin
    /// over at least one network.</summary>
    public bool CanManageNetworks { get; set; }
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

/// <summary>Response from POST /api/auth/panel-code — a single-use web-panel sign-in
/// code (120 s TTL) plus the panel callback URL that redeems it.</summary>
public sealed class PanelCodeResponse
{
    public string Code { get; set; } = "";
    public string Url { get; set; } = "";
    public long ExpiresIn { get; set; }
}

/// <summary>Response from POST /api/auth/link-confirm (committing a staged link).</summary>
public sealed class LinkConfirmResponse
{
    public string Provider { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string? DisplayName { get; set; }
    public bool Linked { get; set; }
}

/// <summary>Response from POST /api/auth/rotate-api-key.</summary>
public sealed class RotateApiKeyResponse
{
    public string ApiKey { get; set; } = "";
}

public sealed class HeartbeatResponse
{
    public string SessionId { get; set; } = "";
    public string Status { get; set; } = "";
    public int HeartbeatCount { get; set; }
    public long SessionDurationSecs { get; set; }
    public bool Validated { get; set; }

    /// <summary>Present when the server has decided this client's join stalled and it should restart
    /// Hell Let Loose and rejoin. Absent/null on a healthy heartbeat.</summary>
    public RejoinSignal? Rejoin { get; set; }
}

/// <summary>A server→client instruction to recover a stalled join (see the API's join-health pass).
/// Delivered on the heartbeat response; the client dedupes by <see cref="Attempt"/> since the same
/// attempt may arrive on more than one heartbeat.</summary>
public sealed class RejoinSignal
{
    /// <summary>What to do. Currently always "restart": close HLL, wait for exit, then relaunch and reconnect.</summary>
    public string Action { get; set; } = "";
    /// <summary>1-based cycle number.</summary>
    public int Attempt { get; set; }
    /// <summary>Ceiling after which the server gives up and closes the session.</summary>
    public int MaxAttempts { get; set; }
    /// <summary>Machine-readable cause, e.g. "join_stalled".</summary>
    public string Reason { get; set; } = "";
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

/// <summary>A server's standing in today's seeding rotation, for the per-server board.
/// Computed server-side alongside the candidate.</summary>
public enum DayStatus
{
    /// <summary>Crossed its threshold this cycle.</summary>
    Done,
    /// <summary>The current seeding candidate.</summary>
    Current,
    /// <summary>Not yet seeded; eligible when its turn comes.</summary>
    Pending,
    /// <summary>Offline or password-protected — skipped by the sequential gate.</summary>
    Skipped,
    /// <summary>Ready check pending/unanswered — cannot become candidate yet.</summary>
    [JsonStringEnumMemberName("not_ready")]
    NotReady,
    /// <summary>Ready check missed for the day — excluded from candidacy.</summary>
    [JsonStringEnumMemberName("missed_ready")]
    MissedReady,
    /// <summary>Responded ready late — deferred to the back of the queue.</summary>
    Deferred,
}

/// <summary>Per-server status line for one game's rotation today.</summary>
public sealed class ServerDayStatus
{
    public long DbId { get; set; }
    public string Name { get; set; } = "";
    public string ShortName { get; set; } = "";
    /// <summary>Tag of the org that operates the server (<c>org_tag</c>; the API still sends
    /// the pre-rename <c>community_tag</c> too, for clients older than 0.4.4).</summary>
    public string? OrgTag { get; set; }
    public DayStatus Status { get; set; }
    public int? PlayerCount { get; set; }
    public int Threshold { get; set; }
    /// <summary>Unix ts of today's scheduled window start (null = no window).</summary>
    public long? WindowStartTs { get; set; }
}

/// <summary>Pre-computed seeding status, one entry per seeding network (multi-tenant).
/// The old top-level per-game candidate/day fields moved into
/// <see cref="NetworkSeedingStatus"/>.</summary>
public sealed class SeedingStatusResponse
{
    public List<NetworkSeedingStatus> Networks { get; set; } = [];
    public long UpdatedAt { get; set; }
}

/// <summary>Where a network's per-game rotation stands today. Serializes lowercase/snake_case
/// ("cycling"/"all_seeded"); null when the network has no servers for the game.</summary>
public enum NetworkPhase
{
    /// <summary>Still working through the rotation.</summary>
    Cycling,
    /// <summary>Every server crossed its threshold (reseed mode may keep a candidate).</summary>
    [JsonStringEnumMemberName("all_seeded")]
    AllSeeded,
}

/// <summary>One network's seeding status: candidate + day board per game, schedule state.</summary>
public sealed class NetworkSeedingStatus
{
    public long NetworkId { get; set; }
    public string NetworkTag { get; set; } = "";
    public string? DisplayName { get; set; }
    /// <summary>Whether the network's schedule window is currently open.</summary>
    public bool Active { get; set; }
    /// <summary>Seconds until the network's next active window opens (when inactive), else null.</summary>
    public uint? NextActiveInSecs { get; set; }
    public int DefaultSeedingThreshold { get; set; }
    public SeedingCandidate? Hll { get; set; }
    public SeedingCandidate? Hllv { get; set; }
    public NetworkPhase? HllPhase { get; set; }
    public NetworkPhase? HllvPhase { get; set; }
    public List<ServerDayStatus> HllDay { get; set; } = [];
    public List<ServerDayStatus> HllvDay { get; set; } = [];

    // ── Per-tenant schedule boundaries (docs/PER-TENANT-SCHEDULING.md) ──
    // Additive backend change: authoritative per network when present; null = the backend doesn't
    // send them yet (or this network omits them), in which case the identically named SeedingConfig
    // fields act as fleet defaults. Null and empty are distinct: an empty ActiveWindows list means
    // "always active", null means "no per-network data".

    /// <summary>This network's daily UTC active windows. Empty = always active; null = not sent —
    /// fall back to <see cref="SeedingConfig.ActiveWindows"/>.</summary>
    public List<TimeWindow>? ActiveWindows { get; set; }
    /// <summary>This network's daily reset hour (UTC); null = fall back to the fleet default.</summary>
    public int? DailyResetHourUtc { get; set; }
    /// <summary>This network's missed-autoseed window; null = fall back to the fleet default.</summary>
    public int? MissedAutoseedWindowHours { get; set; }
}

/// <summary>The user's membership in a seeding network (an alliance running one rotation over
/// its member orgs' servers — networks are their own entity server-side, distinct from
/// the orgs that administer servers). Ordered by <see cref="Priority"/> (lower =
/// preferred).</summary>
/// <summary>A ready check waiting on this user, from <c>GET /api/seeding/ready-checks</c>.
/// Only checks the caller may answer and that are still open are listed.</summary>
public sealed class ReadyCheckNotice
{
    public long ReadyCheckId { get; set; }
    public string ServerName { get; set; } = "";
    public string ShortName { get; set; } = "";
    /// <summary>Tag of the org that operates the server — the ready check is theirs.</summary>
    public string? OrgTag { get; set; }
    public long NetworkId { get; set; }
    /// <summary>"pending" (created, nobody told yet) or "notified" (a DM went out).</summary>
    public string State { get; set; } = "";
    public long WindowStartTs { get; set; }
    /// <summary>Seconds until the seed window opens. Treat a non-positive value as "now".</summary>
    public long SecondsUntilWindow { get; set; }
}

public sealed class MyReadyChecksResponse
{
    public List<ReadyCheckNotice> Checks { get; set; } = new();
}

public sealed class NetworkMembership
{
    public long Id { get; set; }
    public long NetworkId { get; set; }
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }
    /// <summary>The network's lead Discord invite (public). Null only while the network is
    /// paused with its lead cleared — hide the Discord affordance then.</summary>
    public string? DiscordInviteUrl { get; set; }
    public long Priority { get; set; }
    public long JoinedAt { get; set; }
}

/// <summary>An invite link as its recipient sees it, from <c>POST /api/invites/preview</c> and
/// <c>POST /api/invites/accept</c>. The app only acts on player links
/// (<see cref="Role"/> == <see cref="MemberRole"/>); org and staff links are accepted on the web
/// page, where an org Admin picks the org. The org picker (<c>eligible_orgs</c>) is not modelled
/// for that reason.</summary>
public sealed class InvitePreview
{
    /// <summary>The role of a player link: accepting it joins the network.</summary>
    public const string MemberRole = "member";

    public long Id { get; set; }
    /// <summary>"member", "org", "operator", "admin" or "owner".</summary>
    public string Role { get; set; } = "";
    public long? NetworkId { get; set; }
    /// <summary>The network's display name, falling back to its name.</summary>
    public string? NetworkName { get; set; }
    public string? OrgTag { get; set; }
    /// <summary>A note from the link's maker (e.g. which event or community it was for).</summary>
    public string? Label { get; set; }
    /// <summary>Null: no use limit.</summary>
    public long? MaxUses { get; set; }
    public long Uses { get; set; }
    /// <summary>Unix seconds; null: never expires.</summary>
    public long? ExpiresAt { get; set; }
    public string? CreatedByName { get; set; }
    /// <summary>Accepting would change nothing — the account is already a member. Such an accept
    /// doesn't use the link up.</summary>
    public bool Already { get; set; }

    public bool IsPlayerLink => Role == MemberRole;
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
    /// <summary>Minimum secs the server holds a candidate before a different one may replace it
    /// (anti-flap). Informational on the client; the server enforces it.</summary>
    public int CandidateMinDwellSecs { get; set; } = 45;
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

/// <summary>Why the client is being switched off its current server, surfaced on a
/// <see cref="DirectiveAction.Switch"/> directive so the app can explain the switch.
/// Serializes snake_case; null on any non-switch action.</summary>
public enum SwitchReason
{
    /// <summary>The server the client is on crossed its seeding threshold.</summary>
    Seeded,
    /// <summary>The current server went offline or is password-protected.</summary>
    Unavailable,
    /// <summary>A server earlier in the rotation became the candidate (ready check
    /// confirmed / window opened).</summary>
    [JsonStringEnumMemberName("higher_priority_ready")]
    HigherPriorityReady,
    /// <summary>The rotation advanced to the next server for another reason.</summary>
    [JsonStringEnumMemberName("rotation_advanced")]
    RotationAdvanced,
    /// <summary>The current network finished (all seeded), paused, or timed out for the day —
    /// the client is moving to the next network in its priority list.</summary>
    [JsonStringEnumMemberName("network_transition")]
    NetworkTransition,
}

/// <summary>The seeding target a directive points at: a position in the game's ordered rotation.</summary>
public sealed class DirectiveTarget
{
    public string Game { get; set; } = "";
    public int Index { get; set; }
    public ServerInfo Server { get; set; } = new();
    public long DbId { get; set; }
    /// <summary>The network whose server this is — the server's resolution echoed back so the
    /// client can name who it is seeding for. Null until the backend's directive-scoping change
    /// lands (docs/PER-TENANT-SCHEDULING.md); the client then labels by time instead.</summary>
    public long? NetworkId { get; set; }
}

/// <summary>Full server-decided directive from <c>GET /api/seeding/directive</c>. The client polls
/// this and obeys — all timing/decision logic lives server-side.</summary>
public sealed class SeedingDirective
{
    public DirectiveAction Action { get; set; }
    public DirectiveTarget? Target { get; set; }
    /// <summary>Why the client is being switched off its current server (Switch only; else null).</summary>
    public SwitchReason? SwitchReason { get; set; }
    public bool AllExhausted { get; set; }
    /// <summary>True when there's no target because of a scheduled gap (timezone dead-hours),
    /// not exhaustion — the client should idle and resume, not hard-stop.</summary>
    public bool ScheduledPause { get; set; }
    /// <summary>True (with <see cref="ScheduledPause"/>) when every network the user is in is stopped —
    /// paused by its own staff or restricted by Fullobby — so there is no known resume time.</summary>
    public bool NetworksStopped { get; set; }
    /// <summary>Seconds until the next active window opens (when paused), else null.</summary>
    public int? NextActiveInSecs { get; set; }
    /// <summary>True (with action "stop") only when the user has no network membership — the
    /// limited-beta hard gate. The client stops seeding and surfaces the join-a-network portal.</summary>
    public bool JoinANetwork { get; set; }
    public int StaggerSecs { get; set; }
    public int CountdownSecs { get; set; }
    public int SnoozeMinSecs { get; set; }
    public int SnoozeMaxSecs { get; set; }
    public int PollAgainInSecs { get; set; }
    public int MaxSessionSecs { get; set; }
    public SeedingConfig Config { get; set; } = new();
}
