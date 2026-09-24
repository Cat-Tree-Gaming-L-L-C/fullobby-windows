using System.Net.Http.Json;
using System.Text.Json;

namespace Fullobby.Core.Api;

/// <summary>
/// Typed client for the Fullobby API.
/// Auth headers and 401-refresh are applied by <see cref="AuthHandler"/>; transient
/// retries by <see cref="ResilienceHandler"/>. Uses the named HttpClient configured
/// in <c>ServiceCollectionExtensions.AddFullobbyCore</c>.
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
    /// client identifies its current server by index in <paramref name="game"/>'s rotation; null =
    /// "not seeding". <paramref name="games"/> lists every game this machine can launch — the server
    /// then picks across them by network and rotation priority, and the target's <c>Game</c> says
    /// which it chose (possibly not <paramref name="game"/>). Null asks about <paramref name="game"/>
    /// alone.</summary>
    public Task<SeedingDirective> GetDirectiveAsync(
        string game, int? currentIndex, string? sessionId = null, long? networkId = null,
        IReadOnlyList<string>? games = null, CancellationToken ct = default)
    {
        var path = $"/api/seeding/directive?game={Uri.EscapeDataString(game)}";
        if (games is { Count: > 0 })
        {
            path += $"&games={Uri.EscapeDataString(string.Join(',', games))}";
        }
        if (currentIndex is not null)
        {
            path += $"&current_index={currentIndex.Value}";
        }
        if (sessionId is not null)
        {
            path += $"&session_id={Uri.EscapeDataString(sessionId)}";
        }
        // Omitted = current behaviour exactly: the server picks across all memberships by the
        // user's priority order. Present = restrict target selection (and scheduled_pause) to
        // that network. Must stay omitted for a wake justified by more than one network — see
        // "one wake, many tenants" in docs/PER-TENANT-SCHEDULING.md.
        if (networkId is not null)
        {
            path += $"&network_id={networkId.Value}";
        }
        return SendAsync<SeedingDirective>(HttpMethod.Get, path, null, ct);
    }

    /// <summary>Fetch the current seeding timing config (public, no session). Used to prime the
    /// peripheral services with server values at startup and on the poll loop.</summary>
    public Task<SeedingConfig> GetSeedingConfigAsync(CancellationToken ct = default) =>
        SendAsync<SeedingConfig>(HttpMethod.Get, "/api/seeding/config", null, ct);

    /// <summary>Register a guest account (unauthenticated). <paramref name="turnstileToken"/> is
    /// attached only when set — required when the server has Turnstile enabled for registration,
    /// obtained via the <c>/register-challenge</c> browser flow (see
    /// <c>DeepLinkAction.RegisterCallback</c>).</summary>
    public Task<RegisterResponse> RegisterGuestAsync(string? turnstileToken = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>();
        if (turnstileToken is not null)
        {
            body["turnstile_token"] = turnstileToken;
        }
        return SendAsync<RegisterResponse>(HttpMethod.Post, "/api/auth/register", body, ct);
    }

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

    /// <summary>Mint a single-use web-panel sign-in code from this session (auth required).
    /// Drives the embedded Admin tab: navigate a WebView to the returned URL and the panel
    /// callback page signs the same user in as an independent panel session.</summary>
    public Task<PanelCodeResponse> GetPanelCodeAsync(CancellationToken ct = default) =>
        SendAsync<PanelCodeResponse>(HttpMethod.Post, "/api/auth/panel-code", null, ct);

    /// <summary>Commit a staged provider link (auth required). The OAuth link callback stages
    /// the verified identity under a single-use code instead of committing it; the API refuses
    /// the commit unless the authenticated user is the one who initiated the link.</summary>
    public Task<LinkConfirmResponse> ConfirmLinkAsync(string code, CancellationToken ct = default) =>
        SendAsync<LinkConfirmResponse>(HttpMethod.Post, "/api/auth/link-confirm",
            new Dictionary<string, object?> { ["code"] = code }, ct);

    /// <summary>Unlink a provider from the current account (auth required).</summary>
    public Task UnlinkProviderAsync(string provider, CancellationToken ct = default)
    {
        if (!ApiValidation.IsValidProvider(provider))
        {
            throw new ApiException($"Invalid provider: '{provider}'");
        }
        return SendNoContentAsync(HttpMethod.Delete, $"/api/auth/providers/{provider}", null, ct);
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

    /// <summary>Ready checks waiting on this user, soonest window first (auth required).
    ///
    /// <para>A ready check gates its server out of the rotation until an operator confirms. Discord
    /// DMs are the only push the platform has otherwise, and they need an install, a bot in the
    /// right guild, and open DMs — this is the surface that does not. Only checks this caller may
    /// answer are returned, so an empty list means nothing is waiting on them.</para></summary>
    public Task<MyReadyChecksResponse> GetMyReadyChecksAsync(CancellationToken ct = default) =>
        SendAsync<MyReadyChecksResponse>(HttpMethod.Get, "/api/seeding/ready-checks", null, ct);

    // ── Seeding networks (auth required) ──────────────────────────────────

    /// <summary>Join a seeding network by name + join code. The API returns a uniform 400 for any
    /// failure (unknown network or wrong code — indistinguishable by design) and 429 when
    /// rate-limited. The code is sent and forgotten — never stored client-side.</summary>
    public Task<NetworkMembership> JoinNetworkAsync(string name, string code, CancellationToken ct = default) =>
        SendAsync<NetworkMembership>(HttpMethod.Post, "/api/networks/join",
            new Dictionary<string, object?> { ["name"] = name, ["code"] = code }, ct);

    /// <summary>Fetch the user's network memberships, ordered by priority.</summary>
    public Task<List<NetworkMembership>> GetMyNetworksAsync(CancellationToken ct = default) =>
        SendAsync<List<NetworkMembership>>(HttpMethod.Get, "/api/networks/mine", null, ct);

    /// <summary>Leave a network (idempotent).</summary>
    public Task LeaveNetworkAsync(long networkId, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"/api/networks/{networkId}/membership", null, ct);

    /// <summary>Reorder network priorities. Must contain exactly the user's network ids
    /// (<see cref="NetworkMembership.NetworkId"/>); returns the memberships in the new order.</summary>
    public Task<List<NetworkMembership>> SetNetworkPrioritiesAsync(
        IReadOnlyList<long> orderedNetworkIds, CancellationToken ct = default) =>
        SendAsync<List<NetworkMembership>>(HttpMethod.Put, "/api/networks/priorities",
            new Dictionary<string, object?> { ["ordered_ids"] = orderedNetworkIds }, ct);

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

        // Redirects are not followed on the authenticated clients (custom headers like x-api-key
        // survive a cross-origin hop, unlike Authorization). If the API ever legitimately redirects,
        // it surfaces here as an explicit, named failure rather than a confusing parse error.
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new ApiException(
                $"API returned an unfollowed redirect ({(int)response.StatusCode}) to " +
                $"'{response.Headers.Location}'. Redirects are disabled on authenticated requests; " +
                "if this endpoint is meant to redirect, that has to be handled explicitly.",
                response.StatusCode);
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
        // Cap the echoed body: it flows into ApiException.Message, which is both logged to disk
        // and shown to the user. An unbounded body (e.g. a proxy/Cloudflare HTML error page on a
        // transient 5xx) would otherwise dump verbatim into the log file on every failure.
        if (errorText.Length > 512)
        {
            errorText = errorText[..512] + "…";
        }
        throw new ApiException($"API error: {errorText}", response.StatusCode);
    }
}
