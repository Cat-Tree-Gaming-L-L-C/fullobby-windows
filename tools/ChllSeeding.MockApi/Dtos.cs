namespace ChllSeeding.MockApi;

// Response/request DTOs mirroring the C# client's wire contract (ChllSeeding.Core/Api/Models.cs).
// PascalCase here → snake_case on the wire via the configured JsonNamingPolicy.SnakeCaseLower.
// Kept as a standalone copy so the mock doesn't take a dependency on the net9.0-windows Core lib.

public record ServerInfo(string Ip, string ShortName, string Name, int SeedingThreshold, string Game);

public record BatchStatsResult(
    string Game, string Region, int Index, string? MapName,
    int? PlayerCount, int? MaxPlayerCount, bool Offline, bool PasswordProtected, string? Error);

public record RegionServers(List<ServerInfo> Na, List<ServerInfo> Eu);
public record ServersResponse(RegionServers Hll, RegionServers? Hllv, long CachedAt);

public record RegionCandidate(string Game, int Index, ServerInfo Server);
public record GameSeedingStatus(RegionCandidate? Na, RegionCandidate? Eu);
public record SeedingStatusResponse(GameSeedingStatus Hll, GameSeedingStatus? Hllv, long UpdatedAt);

public record RegisterResponse(string UserId, string ApiKey, string Username, string DisplayName);
public record AuthRefreshResponse(string Token, string RefreshToken);

public record NextServerRequest(
    string Game, string CurrentRegion, int CurrentIndex, bool EuEnabled, string Reason, string? SteamId,
    string? Platform);
public record NextServerResponse(
    string Game, string Region, int Index, ServerInfo Server, bool AllExhausted, string? SessionId);

public record StartSessionRequest(
    string Game, string Region, int Index, string? SteamId, string? Platform,
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
