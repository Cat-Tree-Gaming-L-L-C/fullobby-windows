# CHLL Seeding Mock API

A standalone, scriptable mock of the `seeding-api` backend for testing the desktop client
without the real backend (or the game). Serves the **full client contract** — every REST
endpoint plus the SSE stream — from controllable in-memory state, so you can drive flows
that are impossible to stage against the live backend (force a server switch, demo Seed All
rotation, inject auth/rate-limit/update errors, simulate offline/passworded servers).

It's dev-only tooling: not part of `ChllSeeding.sln`, not built in CI.

## Run

```powershell
./scripts/run-mock.ps1              # http://localhost:3000
./scripts/run-mock.ps1 -Port 5005
```

Then point the app at it:

```powershell
./scripts/run-local.ps1 -ApiUrl http://localhost:3000
# or: $env:CHLL_SEEDING_API_URL = 'http://localhost:3000'; <launch CHLLSeeding.exe>
```

## What it implements

**REST** (snake_case JSON, nulls omitted, `x-api-key`/`Bearer` accepted but not enforced):
`POST /api/auth/register`, `POST /api/auth/refresh`, `GET /api/auth/me`,
`GET /api/servers`, `GET /api/servers/stats`, `GET /api/seeding/status`,
`POST /api/seeding/next-server`, `POST /api/seeding/start-session`,
`POST /api/seeding/heartbeat`, `POST /api/seeding/stop`, `GET /api/seeding/leaderboard`.

**SSE** `GET /api/servers/stats/stream` — emits a `stats` + `seeding_status` snapshot on
connect, a `: keepalive` comment every 15s, and fresh `stats`/`seeding_status` frames whenever
state changes (so UI updates are live as you mutate state).

**Seeding candidate rule:** per region, the best candidate is the online, non-passworded
server still under its `seeding_threshold`, preferring the one closest to filling. `null` when
none qualify (drives Seed All exhaustion and the no-candidate path).

## Default state

Two NA + one EU HLL server, all seedable:

| region/index | name | threshold | players |
|---|---|---|---|
| na/0 | Comp HLL Main | 50 | 32 |
| na/1 | Pathfinders Chicago | 50 | 14 |
| eu/0 | EU Seed Server One | 50 | 8 |

## Control plane (`/__mock/...`)

> Control-request bodies are **snake_case** too (e.g. `player_count`, `retry_after_secs`).

| Method + path | Body | Effect |
|---|---|---|
| `GET /__mock/state` | — | Dump stats, seeding_status, SSE subscriber count, armed error |
| `POST /__mock/reset` | — | Reset to the default state above |
| `POST /__mock/servers/{region}/{index}/players` | `{player_count, max_player_count?}` | Set a server's population (broadcasts) |
| `POST /__mock/servers/{region}/{index}/flags` | `{offline?, password_protected?}` | Set offline / passworded flags (broadcasts) |
| `POST /__mock/autoadvance` | `{enabled, step?, interval_secs?}` | Simulate servers filling **while a seed is in progress** so it crosses threshold → switch/rotate on its own |
| `POST /__mock/tick` | — | Advance candidates one step manually |
| `POST /__mock/fill/{region}/{index}` | — | Push a server above threshold (advances rotation) |
| `POST /__mock/error` | `{status, count, retry_after_secs?, minimum_version?}` | Arm an error for the next `count` `/api/*` requests |
| `DELETE /__mock/error` | — | Clear the armed error |
| `POST /__mock/sse/push` | `{event, data}` | Push an arbitrary SSE frame |
| `POST /__mock/scenario/{name}` | — | Apply a preset (below) |

### Scenario presets

| name | what it sets up |
|---|---|
| `reset` | Default state |
| `seed-all-rotation` | Three seedable servers (na/0, na/1, eu/0) so Seed All has somewhere to rotate |
| `switch` | Current NA candidate fills past threshold → candidate moves to na/1 (fires the switch countdown over SSE while seeding) |
| `edge-offline` | na/1 marked offline |
| `edge-passworded` | na/0 marked password-protected (excluded from seeding) |
| `edge-empty` | All servers offline (list loads but empty → seed buttons stay hidden) |

## Examples (PowerShell)

```powershell
$b = 'http://localhost:3000'

# Demo Seed All rotation
Invoke-RestMethod -Method Post "$b/__mock/scenario/seed-all-rotation"

# While seeding na/0, force a switch (candidate moves to na/1)
Invoke-RestMethod -Method Post "$b/__mock/scenario/switch"

# Fill the current candidate so rotation advances
Invoke-RestMethod -Method Post "$b/__mock/fill/na/0"

# Inject a 429 with Retry-After: 30 on the next 2 requests
Invoke-RestMethod -Method Post "$b/__mock/error" -ContentType application/json `
  -Body (@{status=429; count=2; retry_after_secs=30} | ConvertTo-Json)

# Force the update-required (426) path
Invoke-RestMethod -Method Post "$b/__mock/error" -ContentType application/json `
  -Body (@{status=426; count=1; minimum_version='2.0.0'} | ConvertTo-Json)

# Set a server's population live (UI row updates over SSE)
Invoke-RestMethod -Method Post "$b/__mock/servers/na/0/players" -ContentType application/json `
  -Body (@{player_count=48; max_player_count=100} | ConvertTo-Json)
```

## Seeing a live switch / rotation

A switch fires from the engine's **monitor loop**, which only runs during a real seed (the game is
launched via Steam). To watch it transition end-to-end:

1. Enable auto-advance once: `Invoke-RestMethod -Method Post $b/__mock/autoadvance -ContentType application/json -Body (@{enabled=$true} | ConvertTo-Json)`
   (it's gated on an active seeding session, so the server list stays seedable until you click Seed).
2. Click **Seed** (or **Seed All**) in the app. Only the server **you are currently seeding** fills
   (the mock knows which from `start-session`): it holds at its starting population for a few beats,
   climbs each tick, and on reaching its threshold fills the rest of the way — the candidate moves to the
   next server and the client fires the switch countdown + toast. The next server stays put until you
   actually switch to it, so transitions are paced rather than back-to-back (key for Seed All).

`step` (default 4) and `interval_secs` (default 4) tune fill speed; `dwell_ticks` (default 3) is the
hold before a freshly-seeded server starts climbing.

## Caveats

- The **switch toast / countdown** and the on-screen Seed All rotation still need the engine's
  monitor loop running, which launches the game via Steam — the mock supplies the API state, not
  the game. It fully exercises the client's API/SSE/seeding-selection layer; the game-dependent
  engine bits need HLL.
- The guest `401 → refresh` path needs a JWT + refresh token (OAuth, Phase 3). A guest has only
  an `x-api-key`, so an injected `401` exercises the clear-and-fail path, not a token refresh.
