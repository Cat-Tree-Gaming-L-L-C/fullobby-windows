using System.Text.Json;
using ChllSeeding.MockApi;

var builder = WebApplication.CreateBuilder(args);

// snake_case + omit-nulls on every minimal-API JSON result, matching the client's ApiJson.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DefaultIgnoreCondition =
        System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddSingleton<MockState>();
builder.Services.AddHostedService<AutoAdvanceService>();

var app = builder.Build();
var state = app.Services.GetRequiredService<MockState>();

// ── Error injection: any armed error fires on the next matching /api request ───────
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") && state.Armed is { Count: > 0 } e)
    {
        e.Count--;
        if (e.Count <= 0) state.Armed = null;
        ctx.Response.StatusCode = e.Status;
        switch (e.Status)
        {
            case 426:
                await ctx.Response.WriteAsJsonAsync(new { minimum_version = e.MinimumVersion ?? "9.9.9" });
                break;
            case 429:
                ctx.Response.Headers.RetryAfter = (e.RetryAfterSecs ?? 5).ToString();
                await ctx.Response.WriteAsJsonAsync(new { error = "rate limited (mock)" });
                break;
            default:
                await ctx.Response.WriteAsJsonAsync(new { error = $"mock injected {e.Status}" });
                break;
        }
        app.Logger.LogInformation("Injected {Status} on {Path}", e.Status, ctx.Request.Path);
        return;
    }
    await next();
});

// ── Auth ───────────────────────────────────────────────────────────────────────
app.MapPost("/api/auth/register", () =>
    Results.Json(new RegisterResponse(
        UserId: "guest-1", ApiKey: "mock-api-key", Username: "Wandering Fox 330", DisplayName: "Wandering Fox")));

app.MapPost("/api/auth/refresh", (AuthRefreshResponse? _) =>
    Results.Json(new AuthRefreshResponse(Token: "mock-jwt", RefreshToken: "mock-refresh")));

app.MapGet("/api/auth/me", () => Results.Json(new
{
    user_id = "guest-1", username = "Wandering Fox 330", auth_provider = "guest",
    display_name = "Wandering Fox", created_at = 0L, last_seen_at = 0L, leaderboard_opt_out = false,
}));

// ── Servers + stats ──────────────────────────────────────────────────────────────
app.MapGet("/api/servers", () => Results.Json(state.ServersResponse()));
app.MapGet("/api/servers/stats", () => Results.Json(state.Stats()));

// ── Seeding ──────────────────────────────────────────────────────────────────────
app.MapGet("/api/seeding/status", () => Results.Json(state.SeedingStatus()));

app.MapPost("/api/seeding/next-server", (NextServerRequest req) =>
{
    // Pick the best candidate in the requested region, then fall back to the other (if eu_enabled).
    var status = state.SeedingStatus();
    RegionCandidate? c = req.CurrentRegion == "eu" ? status.Hll.Eu : status.Hll.Na;
    var region = req.CurrentRegion;
    if (c is null && req.EuEnabled)
    {
        c = req.CurrentRegion == "eu" ? status.Hll.Na : status.Hll.Eu;
        region = req.CurrentRegion == "eu" ? "na" : "eu";
    }
    if (c is null)
    {
        // Nothing left to seed.
        return Results.Json(new NextServerResponse(req.Game, req.CurrentRegion, req.CurrentIndex,
            new ServerInfo("", "", "", 0, req.Game), AllExhausted: true, SessionId: null));
    }
    return Results.Json(new NextServerResponse(c.Game, region, c.Index, c.Server, AllExhausted: false, SessionId: null));
});

app.MapPost("/api/seeding/start-session", (StartSessionRequest req) =>
    Results.Json(new StartSessionResponse(state.StartSession(req.Region, req.Index))));

app.MapPost("/api/seeding/heartbeat", (HeartbeatRequest req) =>
    Results.Json(state.Heartbeat(req.SessionId)));

app.MapPost("/api/seeding/stop", (StopSessionRequest req) =>
{
    state.StopSession(req.SessionId);
    return Results.Json(new StopSessionResponse(true));
});

app.MapGet("/api/seeding/leaderboard", (long? days, long? limit) => Results.Json(new[]
{
    new LeaderboardEntry(1, 1, "Wandering Fox 330", null, 7200, 5),
    new LeaderboardEntry(2, 2, "Sneaky Badger 12", null, 3600, 3),
}));

// ── SSE: live stats + seeding_status ─────────────────────────────────────────────
app.MapGet("/api/servers/stats/stream", async (HttpContext ctx, MockState st, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    async Task Write(string ev, string data)
    {
        await ctx.Response.WriteAsync($"event: {ev}\ndata: {data}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    var ch = st.Subscribe();
    app.Logger.LogInformation("SSE client connected ({Count} total)", st.SubscriberCount);
    try
    {
        // Initial snapshot.
        await Write("stats", JsonSerializer.Serialize(st.Stats(), Json.Options));
        await Write("seeding_status", JsonSerializer.Serialize(st.SeedingStatus(), Json.Options));

        while (!ct.IsCancellationRequested)
        {
            using var keep = CancellationTokenSource.CreateLinkedTokenSource(ct);
            keep.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var msg = await ch.Reader.ReadAsync(keep.Token);
                await Write(msg.Event, msg.Data);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await ctx.Response.WriteAsync(": keepalive\n\n", ct); // comment frame
                await ctx.Response.Body.FlushAsync(ct);
            }
        }
    }
    catch (OperationCanceledException) { /* client disconnected */ }
    finally
    {
        st.Unsubscribe(ch);
        app.Logger.LogInformation("SSE client disconnected ({Count} remain)", st.SubscriberCount);
    }
});

// ══ Control plane (/__mock/...) ══════════════════════════════════════════════════
var mock = app.MapGroup("/__mock");

mock.MapGet("/state", () => Results.Json(new
{
    servers = state.Stats(),
    seeding_status = state.SeedingStatus(),
    sse_subscribers = state.SubscriberCount,
    armed_error = state.Armed,
    auto_advance = new { state.AutoAdvanceEnabled, state.AutoAdvanceStep, state.AutoAdvanceIntervalSecs, state.DwellTicks },
}));

mock.MapPost("/reset", () => { state.ResetToDefaults(); return Results.Ok(new { reset = true }); });

// Auto-advance: simulate servers filling over time so a live seed transitions on its own.
mock.MapPost("/autoadvance", (AutoAdvanceRequest req) =>
{
    state.AutoAdvanceEnabled = req.Enabled;
    if (req.Step is { } s) state.AutoAdvanceStep = s;
    if (req.IntervalSecs is { } i) state.AutoAdvanceIntervalSecs = i;
    if (req.DwellTicks is { } d) state.DwellTicks = d;
    return Results.Ok(new { state.AutoAdvanceEnabled, state.AutoAdvanceStep, state.AutoAdvanceIntervalSecs, state.DwellTicks });
});

// One manual tick (advance candidates one step).
mock.MapPost("/tick", () => { state.Tick(); return Results.Ok(new { seeding_status = state.SeedingStatus() }); });

mock.MapPost("/servers/{region}/{index:int}/players", (string region, int index, SetPlayersRequest req) =>
    state.SetPlayers(region, index, req.PlayerCount, req.MaxPlayerCount)
        ? Results.Ok(new { ok = true, seeding_status = state.SeedingStatus() })
        : Results.NotFound(new { error = $"no server {region}/{index}" }));

mock.MapPost("/servers/{region}/{index:int}/flags", (string region, int index, SetFlagsRequest req) =>
    state.SetFlags(region, index, req.Offline, req.PasswordProtected)
        ? Results.Ok(new { ok = true })
        : Results.NotFound(new { error = $"no server {region}/{index}" }));

// Convenience: push a server above its threshold (it stops being a seeding candidate → rotation advances).
mock.MapPost("/fill/{region}/{index:int}", (string region, int index) =>
{
    var s = state.Find(region, index);
    if (s is null) return Results.NotFound(new { error = $"no server {region}/{index}" });
    state.SetPlayers(region, index, s.Info.SeedingThreshold + 20, s.MaxPlayerCount);
    return Results.Ok(new { ok = true, seeding_status = state.SeedingStatus() });
});

mock.MapPost("/error", (ArmErrorRequest req) =>
{
    state.Armed = new ArmedError
    {
        Status = req.Status, Count = req.Count <= 0 ? 1 : req.Count,
        RetryAfterSecs = req.RetryAfterSecs, MinimumVersion = req.MinimumVersion,
    };
    return Results.Ok(new { armed = state.Armed });
});
mock.MapDelete("/error", () => { state.Armed = null; return Results.Ok(new { cleared = true }); });

mock.MapPost("/sse/push", (PushSseRequest req) =>
{
    state.Push(new SseMessage(req.Event, JsonSerializer.Serialize(req.Data, Json.Options)));
    return Results.Ok(new { pushed = req.Event });
});

// Scenario presets for the four flow groups.
mock.MapPost("/scenario/{name}", (string name) =>
{
    switch (name)
    {
        case "reset":
            state.ResetToDefaults();
            break;
        case "seed-all-rotation":
            // Three seedable servers across NA + EU, auto-filling so the rotation advances hands-free.
            state.ResetToDefaults();
            state.SetPlayers("na", 0, 40, 100);
            state.SetPlayers("na", 1, 25, 100);
            state.SetPlayers("eu", 0, 12, 100);
            state.AutoAdvanceEnabled = true;
            break;
        case "switch":
            // Current best NA candidate (index 0) fills past threshold → candidate moves to index 1,
            // which the client's monitor sees over SSE and starts the switch countdown.
            state.SetPlayers("na", 1, 30, 100);   // make index 1 a valid fallback candidate
            state.SetPlayers("na", 0, 70, 100);   // index 0 now over threshold → no longer a candidate
            break;
        case "edge-offline":
            state.SetFlags("na", 1, offline: true, passworded: null);
            break;
        case "edge-passworded":
            state.SetFlags("na", 0, offline: null, passworded: true);
            break;
        case "edge-empty":
            // Clear every server: list loads but is empty (seed buttons stay hidden).
            foreach (var s in state.Servers) state.SetFlags(s.Region, s.Index, offline: true, passworded: null);
            break;
        default:
            return Results.NotFound(new { error = $"unknown scenario '{name}'" });
    }
    return Results.Ok(new { scenario = name, seeding_status = state.SeedingStatus() });
});

app.Logger.LogInformation("CHLL Seeding mock API — point the app at it via CHLL_SEEDING_API_URL");
app.Run();
