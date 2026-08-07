# Seeding Score — design (draft)

Player statistic tracking + reward score for seeding. Goal: reward *engagement*,
not just connected time — active in-game seeders earn the most, AFK-in-lobby
seeders earn a base rate, and showing up day after day compounds.

Status: **design draft** — scoring engine is backend work (`fullobby-api`);
client work is the manual-start signal + stats/score UI. Nothing here ships
until the backend side exists. Backend implementation scoping (grounded
against the API codebase) lives in `fullobby-api/docs/SCORING.md`.

## Principles

- **Server-authoritative.** The client never reports score. The backend already
  validates in-game presence per heartbeat via CRCON; scoring derives entirely
  from data the backend observes (session heartbeats + CRCON player score
  polls). Nothing the client sends can inflate score.
- **Only validated time counts.** Minutes accrue only while the session is
  `validated` (player confirmed on the server via CRCON), same gate the
  leaderboard time already uses.
- **Abuse-resistant by construction:** daily earn caps, per-day (not per-click)
  bonuses, streaks keyed to the existing `DailyResetHourUtc` day boundary.

## Score components

### 1. Base rate — validated seeding minutes

Points per validated minute of a seeding session (e.g. **1 pt/min**). This is
the floor every seeder earns, including AFK-in-lobby seeders — presence is
still the product.

### 2. Activity tier — Combat score earners (highest reward)

During a validated session the backend polls CRCON player stats on its existing
validation cadence and stores **per-window score deltas** per category
(combat / offense / defense / support). A window with a positive Combat delta
(kills/damage — the player is genuinely in the match fighting) classifies the
minute as **active**; active minutes earn a multiplied rate (e.g. **3×** base).

This also gives us the raw statistic the feature is named for: per-session
`active_minutes` vs `passive_minutes`, aggregable per user/day for the stats
page and leaderboard.

**Active is strictly Combat.** Offense/Defense are excluded: they tick
passively from cap-zone presence (an AFK player parked on the point earns
them), making them even more passive than Support. Combat score requires
actually fighting.

### 3. "Seed Now" click bonus

A flat bonus (e.g. **+25 pts**) when the player *manually* starts seeding, as
opposed to scheduled autoseed. Guards:

- Awarded **once per day** (per `DailyResetHourUtc` boundary), and only after
  the session reaches validated state and survives a minimum duration
  (e.g. 10 validated minutes) — no start/stop click farming.
- Wire: `POST /api/seeding/session` already receives `auto_seed` inside the
  analytics blob (`SessionStartAnalytics.AutoSeed`). Promote it to a
  first-class request field (e.g. `start_source: "manual" | "autoseed"`) —
  analytics fields shouldn't drive rewards. Client change is one line at the
  `StartSessionAsync` call site.

### 4. Consecutive-day streak multiplier

A slowly building multiplier on **all points earned that day**:

- A day *qualifies* when the user accrues ≥ N validated minutes (e.g. 15).
- Multiplier: `1.0 + 0.05 × streak_days`, **capped at 2.0** (~20 days to max).
- A missed day resets to 1.0 (option: one grace day per week — decide later;
  start strict, loosen if players ask).
- Day boundary = existing `DailyResetHourUtc` (10:00 UTC), so "a day" means
  the same thing as the seeding rotation's day.

### 5. Spawn-building reward — **wanted, blocked** ⚠

We want to reward players who build garrisons/outposts during seeds — spawns
are what actually make a seed stick. **Blocker:** HLL lumps spawn-building and
AFK resource-node income into the *same* Support score category, and RCON only
exposes category totals (no per-action events, and build events don't appear in
the game server log stream). Rewarding raw Support deltas would pay AFK node
farmers, which we explicitly do **not** want to reward at all.

**Status: iceboxed** (2026-08-06). CRCON's `get_detailed_players` does expose
per-player `role` and `world_position` alongside the score categories, but
snapshot gating on them doesn't survive real play:

- A player can build nodes as engineer, switch to SL, and build garrisons
  (or vice versa) — current role says nothing about where a Support delta
  came from.
- Worse, node income keeps ticking to the builder for as long as the nodes
  live, regardless of current role. Once a player has built any node, every
  subsequent Support delta that match is a mix of passive node noise and
  possibly-legit garrison credit.

The only honest approach would be per-window attribution — record (role,
category deltas, position) per 30s window, estimate the passive node-tick
baseline, and infer garrison bursts above it. That is a stateful classifier
with per-match noise-floor estimation, tuned against noisy seed-server data,
maintained forever against game patches — for a modest bonus. **Verdict: the
juice is not worth the squeeze now.** Mitigating factor: seed-server garrison
builders usually also fight between builds, so many still reach the active
tier through Combat — unlike node farmers, who by definition never do.

What we keep: cumulative Support deltas are recorded per session (free,
already part of the accrual pass) and worth 0. Revisit only if (a) HLL/CRCON
ever exposes build *events*, which would make attribution exact, or (b)
shadow-mode data shows a meaningful population of garrison-building seeders
stuck at base rate — then weigh the burst-inference classifier for real.

## Data model sketch (backend)

- `session_score_windows` (or accumulators on the session row): per-session
  `active_mins`, `passive_mins`, per-category score deltas.
- `user_daily_score` — `(user_id, day)`: base_pts, active_pts, seed_now_bonus,
  streak_multiplier, total. Daily rows make streaks, caps, and "this week"
  views trivial.
- `users`: `current_streak_days`, `best_streak_days`, `last_qualified_day`.

## API / client surface

- Extend `GET /api/seeding/leaderboard` + `GET /api/seeding/stats/{user}` with
  score/streak fields (client: `LeaderboardEntry`, `UserSeedingStats` in
  `Core/Api/Models.cs` — additive, wire-compatible).
- New client UI: score + streak + multiplier on the stats page; a small
  "streak day N — ×M" affordance (toast or badge) when a day qualifies.
- Client sends `start_source` on session start (promote from analytics).
- Existing `LeaderboardOptOut` should cover score visibility too.

## Open questions

- Exact numbers (rates, bonus size, streak step/cap, qualifying minutes) —
  tune after seeing real distributions; ship behind server config like the
  rest of `SeedingConfig` so they're adjustable without a client release.
- Does score decay for dormant players, or is the streak reset enough? (Lean:
  streak reset is enough; keep lifetime totals honest.)
