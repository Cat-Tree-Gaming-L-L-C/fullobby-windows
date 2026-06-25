using System.Net.Http.Json;
using System.Text.Json;

namespace ChllSeeding.Core.Api;

/// <summary>
/// Typed client for the CHLL seeding API. Port of <c>src-rust/src/api/client.rs</c>.
/// Auth headers and 401-refresh are applied by <see cref="AuthHandler"/>; transient
/// retries by <see cref="ResilienceHandler"/>. Uses the named HttpClient configured
/// in <c>ServiceCollectionExtensions.AddChllSeedingCore</c>.
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

    /// <summary>Fetch the server-decided directive: what to do next (seed/switch/stay/stop), the
    /// target server, stagger/countdown timings, when to poll again, and the current config. The
    /// client identifies its current server by index in the game's rotation; null = "not seeding".</summary>
    public Task<SeedingDirective> GetDirectiveAsync(
        string game, int? currentIndex, string? sessionId = null, CancellationToken ct = default)
    {
        var path = $"/api/seeding/directive?game={Uri.EscapeDataString(game)}";
        if (currentIndex is not null)
        {
            path += $"&current_index={currentIndex.Value}";
        }
        if (sessionId is not null)
        {
            path += $"&session_id={Uri.EscapeDataString(sessionId)}";
        }
        return SendAsync<SeedingDirective>(HttpMethod.Get, path, null, ct);
    }

    /// <summary>Fetch the current seeding timing config (public, no session). Used to prime the
    /// peripheral services with server values at startup and on the poll loop.</summary>
    public Task<SeedingConfig> GetSeedingConfigAsync(CancellationToken ct = default) =>
        SendAsync<SeedingConfig>(HttpMethod.Get, "/api/seeding/config", null, ct);

    /// <summary>Register a guest account (unauthenticated).</summary>
    public Task<RegisterResponse> RegisterGuestAsync(CancellationToken ct = default) =>
        SendAsync<RegisterResponse>(HttpMethod.Post, "/api/auth/register", new Dictionary<string, object?>(), ct);

    /// <summary>Create a seeding session after a successful game launch (auth required).</summary>
    public Task<StartSessionResponse> StartSessionAsync(
        string game, int index,
        string? steamId = null, SessionStartAnalytics? analytics = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["game"] = game,
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

    /// <summary>Update the current user's display name (auth required).</summary>
    public Task<UserInfo> UpdateDisplayNameAsync(string name, CancellationToken ct = default) =>
        SendAsync<UserInfo>(HttpMethod.Patch, "/api/auth/me",
            new Dictionary<string, object?> { ["display_name"] = name }, ct);

    /// <summary>Generate a random anonymous nickname via the API (auth required).</summary>
    public Task<UserInfo> RandomizeDisplayNameAsync(CancellationToken ct = default) =>
        SendAsync<UserInfo>(HttpMethod.Patch, "/api/auth/me",
            new Dictionary<string, object?> { ["randomize"] = true }, ct);

    /// <summary>Update the leaderboard opt-out preference (auth required).</summary>
    public Task<UserInfo> UpdateLeaderboardOptOutAsync(bool optOut, CancellationToken ct = default) =>
        SendAsync<UserInfo>(HttpMethod.Patch, "/api/auth/me",
            new Dictionary<string, object?> { ["leaderboard_opt_out"] = optOut }, ct);

    /// <summary>Get all linked providers for the current user (auth required).</summary>
    public Task<List<LinkedProvider>> GetLinkedProvidersAsync(CancellationToken ct = default) =>
        SendAsync<List<LinkedProvider>>(HttpMethod.Get, "/api/auth/providers", null, ct);

    /// <summary>Get the HMAC-signed OAuth URL to link an additional provider (auth required).</summary>
    public Task<LinkInitResponse> GetLinkRedirectUrlAsync(string provider, CancellationToken ct = default)
    {
        if (!ApiValidation.IsValidProvider(provider))
        {
            throw new ApiException($"Invalid provider: '{provider}'");
        }
        return SendAsync<LinkInitResponse>(HttpMethod.Post, $"/api/auth/{provider}/link-init", null, ct);
    }

    /// <summary>Unlink a provider from the current account (auth required).</summary>
    public Task UnlinkProviderAsync(string provider, CancellationToken ct = default)
    {
        if (!ApiValidation.IsValidProvider(provider))
        {
            throw new ApiException($"Invalid provider: '{provider}'");
        }
        return SendNoContentAsync(HttpMethod.Delete, $"/api/auth/providers/{provider}", null, ct);
    }

    /// <summary>List linked Steam IDs (auth required).</summary>
    public Task<List<SteamIdEntry>> GetSteamIdsAsync(CancellationToken ct = default) =>
        SendAsync<List<SteamIdEntry>>(HttpMethod.Get, "/api/auth/steam-ids", null, ct);

    /// <summary>Remove a linked Steam ID (auth required).</summary>
    public Task RemoveSteamIdAsync(string steamId, CancellationToken ct = default)
    {
        if (!ApiValidation.IsValidSteamId(steamId))
        {
            throw new ApiException("Invalid Steam ID");
        }
        return SendNoContentAsync(HttpMethod.Delete, $"/api/auth/steam-ids/{steamId}", null, ct);
    }

    /// <summary>Rotate the API key for guest accounts. Returns the new key (auth required).</summary>
    public async Task<string> RotateApiKeyAsync(CancellationToken ct = default)
    {
        var resp = await SendAsync<RotateApiKeyResponse>(
            HttpMethod.Post, "/api/auth/rotate-api-key", null, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(resp.ApiKey))
        {
            throw new ApiException("Missing api_key in response");
        }
        return resp.ApiKey;
    }

    /// <summary>Delete the current user's account (auth required).</summary>
    public Task DeleteAccountAsync(CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, "/api/auth/account", null, ct);

    /// <summary>Fetch the seeding leaderboard (no auth required).</summary>
    public Task<List<LeaderboardEntry>> GetLeaderboardAsync(long days, long limit, CancellationToken ct = default) =>
        SendAsync<List<LeaderboardEntry>>(HttpMethod.Get,
            $"/api/seeding/leaderboard?days={days}&limit={limit}", null, ct);

    /// <summary>Fetch per-user seeding stats (auth required).</summary>
    public Task<UserSeedingStats> GetUserStatsAsync(string userId, long days, CancellationToken ct = default)
    {
        if (!ApiValidation.IsValidUserId(userId))
        {
            throw new ApiException("Invalid user ID");
        }
        return SendAsync<UserSeedingStats>(HttpMethod.Get,
            $"/api/seeding/stats/{userId}?days={days}", null, ct);
    }

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

    /// <summary>Send a request whose response body we don't care about (e.g. DELETE endpoints
    /// that return <c>{ "ok": true }</c> or an empty body). Only success/failure matters.</summary>
    private async Task SendNoContentAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, ApiConfig.BaseUrl + path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: ApiJson.Options);
        }

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
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
