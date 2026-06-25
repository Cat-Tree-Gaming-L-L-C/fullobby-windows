namespace ChllSeeding.MockApi;

// Response/request DTOs mirroring the C# client's wire contract (ChllSeeding.Core/Api/Models.cs).
// PascalCase here → snake_case on the wire via the configured JsonNamingPolicy.SnakeCaseLower.
// Kept as a standalone copy so the mock doesn't take a dependency on the net9.0-windows Core lib.

public record ServerInfo(string Ip, long BmId, string ShortName, string Name, int SeedingThreshold, string Game);

public record BatchStatsResult(
    string Game, int Index, string? MapName,
    int? PlayerCount, int? MaxPlayerCount, bool Offline, bool PasswordProtected, string? Error);

// One ordered rotation per game (region removed).
public record ServersResponse(List<ServerInfo> Hll, List<ServerInfo>? Hllv, long CachedAt);

public record SeedingCandidate(string Game, int Index, ServerInfo Server, long DbId);
public record SeedingStatusResponse(SeedingCandidate? Hll, SeedingCandidate? Hllv, long UpdatedAt);

public record RegisterResponse(string UserId, string ApiKey, string Username, string DisplayName);
public record AuthRefreshResponse(string Token, string RefreshToken);

// ── Seeding config + directive (server-decided rotation/timing) ────────────────

public record TimeWindow(int StartMin, int EndMin);

public record SeedingConfig(
    int StaggerMaxSecs, int StaggerJitterSecs, int SwitchCountdownSecs, int SnoozeMinSecs, int SnoozeMaxSecs,
    int MaxSessionSecs, int MonitorMinSecs, int MonitorMaxSecs, int MonitorBackoffStepSecs, int HeartbeatSecs,
    int PollFallbackSecs, int PollIdleSecs, int DefaultSeedingThreshold, int SseKeepaliveSecs, int SseBackoffCapSecs,
    int CacheStaleSecs, List<int> GameOpenRetrySecs, int GameOpenTimeoutSecs, int SplashBypassSecs,
    int MissedAutoseedWindowHours, List<TimeWindow> ActiveWindows, int DailyResetHourUtc);

public record DirectiveTarget(string Game, int Index, ServerInfo Server, long DbId);

// action: "seed" | "switch" | "stay" | "stop" (lowercase on the wire).
public record SeedingDirective(
    string Action, DirectiveTarget? Target, bool AllExhausted, bool ScheduledPause, int? NextActiveInSecs,
    int StaggerSecs, int CountdownSecs, int SnoozeMinSecs, int SnoozeMaxSecs, int PollAgainInSecs,
    int MaxSessionSecs, SeedingConfig Config);

public record StartSessionRequest(
    string Game, int Index, string? SteamId,
    string? OsVersion, string? OsArch, bool? EfficiencyMode, bool? EuEnabled, bool? AutoSeed);
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
public record SetPlayersRequest(int? PlayerCount, int? MaxPlayerCount);
public record SetFlagsRequest(bool? Offline, bool? PasswordProtected);
public record ArmErrorRequest(int Status, int Count, int? RetryAfterSecs, string? MinimumVersion);
public record PushSseRequest(string Event, object Data);
