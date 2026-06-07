using System.Net.Http.Json;
using System.Text.Json;

namespace ChllSeeder.Core.Api;

/// <summary>
/// Typed client for the CHLL seeding API. Port of <c>src-rust/src/api/client.rs</c>.
/// Auth headers and 401-refresh are applied by <see cref="AuthHandler"/>; transient
/// retries by <see cref="ResilienceHandler"/>. Uses the named HttpClient configured
/// in <c>ServiceCollectionExtensions.AddChllSeederCore</c>.
/// </summary>
public sealed class SeedingApiClient(HttpClient http)
{
    /// <summary>Fetch the server list (guest OK).</summary>
    public Task<ServersResponse> GetServersAsync(CancellationToken ct = default) =>
        SendAsync<ServersResponse>(HttpMethod.Get, "/api/servers", null, ct);

    /// <summary>Fetch batch server stats (guest OK).</summary>
    public Task<List<BatchStatsResult>> GetStatsAsync(CancellationToken ct = default) =>
        SendAsync<List<BatchStatsResult>>(HttpMethod.Get, "/api/servers/stats", null, ct);

    /// <summary>Fetch pre-computed seeding status (best candidate per region).</summary>
    public Task<SeedingStatusResponse> GetSeedingStatusAsync(CancellationToken ct = default) =>
        SendAsync<SeedingStatusResponse>(HttpMethod.Get, "/api/seeding/status", null, ct);

    /// <summary>Register a guest account (unauthenticated).</summary>
    public Task<RegisterResponse> RegisterGuestAsync(CancellationToken ct = default) =>
        SendAsync<RegisterResponse>(HttpMethod.Post, "/api/auth/register", new Dictionary<string, object?>(), ct);

    /// <summary>Get the next seeding candidate (auth required).</summary>
    public Task<NextServerResponse> GetNextServerAsync(
        string game, string currentRegion, int currentIndex, bool euEnabled, string reason,
        string? steamId = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["game"] = game,
            ["current_region"] = currentRegion,
            ["current_index"] = currentIndex,
            ["eu_enabled"] = euEnabled,
            ["reason"] = reason,
        };
        if (steamId is not null)
        {
            body["steam_id"] = steamId;
        }
        return SendAsync<NextServerResponse>(HttpMethod.Post, "/api/seeding/next-server", body, ct);
    }

    /// <summary>Create a seeding session after a successful game launch (auth required).</summary>
    public Task<StartSessionResponse> StartSessionAsync(
        string game, string region, int index,
        string? steamId = null, SessionStartAnalytics? analytics = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["game"] = game,
            ["region"] = region,
            ["index"] = index,
        };
        if (steamId is not null)
        {
            body["steam_id"] = steamId;
        }
        if (analytics is not null)
        {
            body["os_version"] = analytics.OsVersion;
            body["os_arch"] = analytics.OsArch;
            body["efficiency_mode"] = analytics.EfficiencyMode;
            body["eu_enabled"] = analytics.EuEnabled;
            body["auto_seed"] = analytics.AutoSeed;
        }
        return SendAsync<StartSessionResponse>(HttpMethod.Post, "/api/seeding/start-session", body, ct);
    }

    /// <summary>Send a seeding heartbeat (auth required).</summary>
    public Task<HeartbeatResponse> SendHeartbeatAsync(string sessionId, CancellationToken ct = default) =>
        SendAsync<HeartbeatResponse>(HttpMethod.Post, "/api/seeding/heartbeat",
            new Dictionary<string, object?> { ["session_id"] = sessionId }, ct);

    /// <summary>Stop a seeding session (auth required).</summary>
    public Task<StopSessionResponse> StopSessionAsync(string sessionId, string? reason = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["session_id"] = sessionId };
        if (reason is not null)
        {
            body["reason"] = reason;
        }
        return SendAsync<StopSessionResponse>(HttpMethod.Post, "/api/seeding/stop", body, ct);
    }

    /// <summary>Get the current user (auth required).</summary>
    public Task<UserInfo> GetMeAsync(CancellationToken ct = default) =>
        SendAsync<UserInfo>(HttpMethod.Get, "/api/auth/me", null, ct);

    /// <summary>Fetch the seeding leaderboard (no auth required).</summary>
    public Task<List<LeaderboardEntry>> GetLeaderboardAsync(long days, long limit, CancellationToken ct = default) =>
        SendAsync<List<LeaderboardEntry>>(HttpMethod.Get,
            $"/api/seeding/leaderboard?days={days}&limit={limit}", null, ct);

    // ── Internals ─────────────────────────────────────────────────────────

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, ApiConfig.BaseUrl + path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: ApiJson.Options);
        }

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        var result = await response.Content.ReadFromJsonAsync<T>(ApiJson.Options, ct).ConfigureAwait(false);
        return result ?? throw new ApiException("API error: empty response body");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if ((int)response.StatusCode == 426)
        {
            var min = "unknown";
            try
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("minimum_version", out var v) && v.ValueKind == JsonValueKind.String)
                {
                    min = v.GetString()!;
                }
            }
            catch (JsonException) { /* keep "unknown" */ }

            throw new ApiException(
                $"Update required: minimum version is {min}. Please download the latest release.");
        }

        var errorText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new ApiException($"API error: {errorText}");
    }
}
