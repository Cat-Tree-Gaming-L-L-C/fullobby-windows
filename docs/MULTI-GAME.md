# Multi-game Fullobby — product design (draft)

Generalize Fullobby from a Hell Let Loose seeding tool into a seeding +
direct-connect + statistics service for **any game with Steam direct connect
and/or an RCON-style admin surface**. First new game: **Palworld**. Users
opt in per game, from the set of games Fullobby has enabled that they have
installed.

Status: **phase 5 built (2026-11-23), untested against real game servers.**
The plan grew to Squad, Rust, Arma Reforger, Palworld and ARK; the client
seeds the first three (Palworld and ARK don't seed). Backend gap analysis
(grounded in the API codebase): `fullobby/docs/seeding/MULTI-GAME.md`.

**As built (phase 5):**

- **Registry before sign-in.** `AppBootstrapper.RefreshGamesAsync` reads the
  public `GET /api/games` at start and with each config refresh;
  `GameAvailability.Offered` = the API's seeding games ∩ `GameCatalog.All`
  (`Released` is now this). Last-known registry persists
  (`GameAvailabilityStore`, `games_registry`); without one the client offers
  HLL + Vietnam as before.
- **Catalog.** `GameCatalog` gains Squad (393380), Rust (252490) and Arma
  Reforger (1874880), each **`Untested`**: install folders and exe names are
  from public sources until each pilot. `GameDefinition.Join`: Rust launches
  with `+connect`; Squad and Arma Reforger are **manual** (as the API's
  catalogue says) — the client launches the game, copies the server address to
  the clipboard, toasts, and shows it in a banner with a Copy button
  (`SeedingEvent.ManualJoinNeeded`). A server the API marks `launch: "manual"`
  is joined by hand whatever the catalog says. `HasSplashBypass` keeps the
  EAC wait and Esc/F13 splash bypass to the HLL titles; other games are never
  "loading", only running or not.
- **Opt-in.** Settings → **My Games** (`AccountViewModel.MyGames`): every
  offered game with install state, a toggle and up/down priority, synced to
  `GET|PUT /api/games/mine` (the API's `user_games`). Not installed → "Get it
  on Steam", can't be opted into. Defaults match the API: HLL, Vietnam and
  WARDOGS in, newer games out — a new game is never seeded without the player
  choosing it. The directive's `games=` = installed ∩ opted in; the API
  filters by opt-in too and orders by the player's priority.
- **Wire.** `NetworkSeedingStatus.Games` / `ServersResponse.Games` (the
  game-keyed maps) are read first through `Candidate(id)`, `Day(id)`,
  `Phase(id)`, `ByGame()`, falling back to the legacy `hll*`/`hllv*` fields.
  The API keeps sending those until the version gate rises past this release.

Not yet: an onboarding step for "My games" (Settings only), the release
itself (signed update feed), and each game's pilot on a real server.

## Why this is closer than it looks

The architecture was already shaped for it:

- **Client**: `GameDefinition`/`GameCatalog` (`Core/Games/`) is a plain data
  record — app id, exe names, install folder, feature flags — explicitly so
  "the rest of the codebase can be game-agnostic". The launcher is already
  parameterized (`steam.exe -applaunch <appid> +connect <ip>`), process
  monitoring is by exe name, and `hllv` exists as a scaffolded second game.
- **API**: `game` is a first-class dimension on servers, sessions,
  rotations, and directives. The catch: it's a closed Rust enum
  (`hll`/`hllv`) threaded through ~254 call sites, with per-game *struct
  fields* (`hll`, `hllv`, `hll_day`, …) instead of a map — see the backend
  doc for the unwind plan.
- **Scoring**: the new accrual engine is per-session and only its *activity
  signal* (Combat delta) is HLL-specific — a per-game adapter slot.

## The opt-in model

- **Fullobby enables games** (server-side registry: slug, display name,
  Steam app id, enabled flag, adapter type). The client learns the enabled
  set from the API; its compiled-in `GameCatalog` carries the launch/install
  mechanics for games it knows how to drive. A game is offered only when
  both sides know it (API-enabled ∩ client catalog).
- **Users opt in per game** they have installed. Client detects installs
  from Steam library manifests (`SteamPaths` already parses the library);
  Settings (and onboarding) shows enabled games with install state; toggling
  opt-in syncs to the API (`user_games`). Default: current HLL users are
  opted into HLL; new games are opt-in, never opt-out.
- **Recheck mechanics:** install detection is a local manifest scan
  (milliseconds), so the games UI re-scans on every load — no stale cache —
  plus an explicit Refresh button for install-just-finished moments. The
  *enabled* set refetches from the API on app start and page load, so newly
  enabled games appear without a client update. Enabled-but-not-installed
  games still render (with a store link) but can't be opted into until the
  manifest appears. (No filesystem watcher in v1 — refresh covers it.)
- Ownership check is **client-attested installed-ness**, not Steam Web API
  ownership proof. There is nothing to gain by lying (you'd be told to seed
  a game you can't launch), and it avoids requiring public Steam profiles.
- **Directive across games**: today the client asks per game. Generalized:
  the directive considers every opted-in game, ordered by a per-user game
  priority list (same pattern as network priorities). Seeding one game at a
  time per machine stays the rule.
  - *Implemented (interim):* every directive request carries
    `games=<installed ids>` (`InstalledGames` — Steam app manifests across
    library folders; HLL alone if nothing can be detected). The server picks
    by network priority, then rotation position, then that list's order, and
    the target's `game` says which; the client puts that game in focus
    (`SeedingViewModel.SelectGame`) before launching, and a switch may cross
    games. Wakes come only from installed games' server windows. The seed
    buttons name the game in focus underneath ("Seed" / "(Hell Let Loose)").
    Opt-in and a per-user game order (`user_games`) are still future.

## Per-game capability matrix

Not every game supports every feature — capabilities are flags on the
server-side registry + client catalog, and each surface degrades cleanly:

| Capability | HLL | Palworld | Notes |
|---|---|---|---|
| Server stats (count/map) | CRCON `get_public_info` | REST `/v1/api/info` + `/players` | drives thresholds + seed board |
| Presence validation | CRCON player ids | REST `/players` (`userId` = Steam id) | drives verified time |
| Direct connect | `-applaunch +connect <ip>` | **verify** — may need in-game join automation | per-game join strategy in `GameDefinition` |
| Score categories | Combat/Off/Def/Support | none | HLL-only active tier |
| Activity signal (scoring) | Combat delta | position delta (`location_x/y` in REST players) — movement = active | per-game adapter |
| Efficiency mode | INI swap | TBD | already a feature flag |
| Splash bypass | Esc/F13 automation | TBD | per-game launch quirks |

Palworld's REST API (default port 8212, basic auth) is a first-class
citizen for everything the seeding loop needs: info, player list with Steam
ids, even per-player positions. Source-RCON exists as a fallback but the
REST surface is strictly better. A Palworld server is already running on
`gamesvm` (the 8212 LocalForward) — the natural pilot.

**Open verification item (client):** whether Palworld accepts a
direct-connect launch arg. If not, the join strategy is UI automation
(the client already owns window-focus + input machinery for HLL's splash
bypass) or the in-game community-server browser.

## Client work (this repo)

1. `GameDefinition` grows: join strategy (launch-arg template vs automation
   script id), optional stats hints, capability flags. `GameCatalog` gains
   Palworld with real values; `Released` becomes API-driven.
2. Opt-in UI: Settings "My games" section + onboarding step; installed
   detection via Steam manifests; sync to `user_games`.
3. Wire models: the per-game `Hll`/`Hllv` properties on
   `SeedingStatusResponse`/`NetworkSeedingStatus` become a game-keyed
   collection when the API's wire shape generalizes (coordinated breaking
   change — acceptable pre-launch).
4. Seed board/stats UI: game becomes a visible dimension (filter/sections).

## Business model

Set 2026-08-06: **players never pay; communities do.**

- The desktop client and all individual player features (seeding, stats,
  leaderboards, score) stay free — it's the supply side of the marketplace
  and the trust surface (open-source AGPL client).
- Revenue: gaming communities / server operators pay for **server
  integration** — their servers in rotations, verified seeding, analytics —
  via **free and premium subscription tiers** (exact tier contents and
  pricing TBD; premium candidates: multiple servers, priority in shared
  rotations, advanced analytics, custom seed windows, partner webhooks).
- Legal/marketing wording updated 2026-08-06 to platform framing (API
  `routes/legal.rs` + fullobby.com, kept in sync): Terms §2 now permits paid
  community plans under their own terms; game lineup published as HLL soft
  launched / Palworld WIP / CS2 + V Rising under evaluation.
- **Before charging real money**: billing terms (fees, renewal, refunds,
  taxes), a partner agreement template, and a qualified legal review of the
  whole set. The current Terms language deliberately defers to "additional
  terms presented at purchase" so nothing is promised prematurely.

## Explicitly deferred

- Non-Steam storefronts for new games (Palworld Game Pass etc.) — the
  platform enum exists, but launch mechanics are Steam-first.
- Per-game scoring tuning beyond the activity adapter (rates stay global
  until shadow data says otherwise).
- Games with neither RCON nor a query API: not servable — stats and
  validation are the product, not just launching.
