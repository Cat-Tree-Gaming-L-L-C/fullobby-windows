namespace Fullobby.MockApi;

// Response/request DTOs mirroring the C# client's wire contract (Fullobby.Core/Api/Models.cs).
// PascalCase here → snake_case on the wire via the configured JsonNamingPolicy.SnakeCaseLower.
// Kept as a standalone copy so the mock doesn't take a dependency on the net9.0-windows Core lib.

public record ServerInfo(string Ip, long BmId, string ShortName, string Name, int SeedingThreshold, string Game);

public record BatchStatsResult(
    string Game, int Index, string? MapName,
    int? PlayerCount, int? MaxPlayerCount, bool Offline, bool PasswordProtected, string? Error);

// One ordered rotation per game (region removed).
public record ServersResponse(List<ServerInfo> Hll, List<ServerInfo>? Hllv, long CachedAt);

public record SeedingCandidate(string Game, int Index, ServerInfo Server, long DbId);

// status: "done" | "current" | "pending" | "skipped" | "not_ready" | "missed_ready" | "deferred".
public record ServerDayStatus(
    long DbId, string Name, string ShortName, string? OrgTag, string Status,
    int? PlayerCount, int Threshold, long? WindowStartTs);

// Per-network seeding status (multi-tenant). phase: "cycling" | "all_seeded" | null.
// The trailing three are the per-tenant schedule boundaries (docs/PER-TENANT-SCHEDULING.md):
// null = omitted from the wire (WhenWritingNull), the pre-per-tenant shape; ActiveWindows may be
// an empty list, which means "always active" — distinct from null.
public record NetworkSeedingStatus(
    long NetworkId, string NetworkTag, string? DisplayName, bool Active, long? NextActiveInSecs,
    int DefaultSeedingThreshold, SeedingCandidate? Hll, SeedingCandidate? Hllv,
    string? HllPhase, string? HllvPhase, List<ServerDayStatus> HllDay, List<ServerDayStatus> HllvDay,
    List<TimeWindow>? ActiveWindows = null, int? DailyResetHourUtc = null,
    int? MissedAutoseedWindowHours = null);

public record SeedingStatusResponse(List<NetworkSeedingStatus> Networks, long UpdatedAt);

// ── Seeding networks (membership) ───────────────────────────────────────────────

public record NetworkMembership(
    long Id, long NetworkId, string Name, string? DisplayName, string? DiscordInviteUrl,
    long Priority, long JoinedAt);

public record JoinNetworkRequest(string Name, string Code);
public record InviteTokenRequest(string Token);

/// <summary>An invite link as its recipient sees it (preview and accept both return this).</summary>
public record InviteView(
    long Id, string Role, long? NetworkId, string? NetworkName, string? OrgTag, string? Label,
    long? MaxUses, long Uses, long? ExpiresAt, string? CreatedByName, bool Already);
public record SetPrioritiesRequest(List<long> OrderedIds);

public record RegisterResponse(string UserId, string ApiKey, string Username, string DisplayName);
public record AuthRefreshResponse(string Token, string RefreshToken);

// ── Seeding config + directive (server-decided rotation/timing) ────────────────

public record TimeWindow(int StartMin, int EndMin);

public record SeedingConfig(
    int StaggerMaxSecs, int StaggerJitterSecs, int SwitchCountdownSecs, int SnoozeMinSecs, int SnoozeMaxSecs,
    int MaxSessionSecs, int MonitorMinSecs, int MonitorMaxSecs, int MonitorBackoffStepSecs, int HeartbeatSecs,
    int PollFallbackSecs, int PollIdleSecs, int DefaultSeedingThreshold, int SseKeepaliveSecs, int SseBackoffCapSecs,
    int CacheStaleSecs, List<int> GameOpenRetrySecs, int GameOpenTimeoutSecs, int SplashBypassSecs,
    int MissedAutoseedWindowHours, List<TimeWindow> ActiveWindows, int DailyResetHourUtc,
    int CandidateMinDwellSecs = 45);

// network_id: the owning network echoed back (per-tenant directive scoping); null = omitted,
// matching a backend that hasn't shipped the scoping change.
public record DirectiveTarget(string Game, int Index, ServerInfo Server, long DbId, long? NetworkId = null);

// action: "seed" | "switch" | "stay" | "stop" (lowercase on the wire).
// switchReason (switch only): "seeded" | "unavailable" | "higher_priority_ready" | "rotation_advanced".
// join_a_network: present + true only when the user holds no network membership (always with
// action "stop"); null otherwise so the field is omitted (WhenWritingNull).
public record SeedingDirective(
    string Action, DirectiveTarget? Target, bool AllExhausted, bool ScheduledPause, int? NextActiveInSecs,
    int StaggerSecs, int CountdownSecs, int SnoozeMinSecs, int SnoozeMaxSecs, int PollAgainInSecs,
    int MaxSessionSecs, SeedingConfig Config, string? SwitchReason = null, bool? JoinANetwork = null);

public record StartSessionRequest(
    string Game, int Index, string? SteamId,
    string? OsVersion, string? OsArch, bool? EfficiencyMode, bool? AutoSeed);
public record StartSessionResponse(string SessionId);

public record HeartbeatRequest(string SessionId);
public record HeartbeatResponse(
    string SessionId, string Status, int HeartbeatCount, long SessionDurationSecs, bool Validated);

public record StopSessionRequest(string SessionId, string? Reason);
public record StopSessionResponse(bool Ok);

public record LeaderboardEntry(
    long Rank, long UserId, string Username, string? DiscordId, long TotalTimeSecs, long SessionCount);

// ── Control-plane DTOs (/__mock/...) ───────────────────────────────────────────

public record AutoAdvanceRequest(bool Enabled, int? Step, int? IntervalSecs, int? DwellTicks);
public record SetScheduleRequest(
    List<TimeWindow>? ActiveWindows, int? DailyResetHourUtc, int? MissedAutoseedWindowHours);
public record SetPlayersRequest(int? PlayerCount, int? MaxPlayerCount);
public record SetFlagsRequest(bool? Offline, bool? PasswordProtected);
public record ArmErrorRequest(int Status, int Count, int? RetryAfterSecs, string? MinimumVersion);
public record PushSseRequest(string Event, object Data);
