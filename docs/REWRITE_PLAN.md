# CHLL Seeder — Rebrand + WinUI 3 Rewrite Plan

## Status

- ✅ **Step 0** (2026-06, WSL): plan committed, `rust-final` tag, Rust moved to `/src-rust`, branch `rewrite/winui3`.
- ✅ **Phase 0 — Skeleton** (2026-06-07, Windows): solution + 3 projects build green
  (`dotnet build src/ChllSeeder.sln -c Release`), 16/16 Core tests pass, app verified
  running (frameless 5-tab shell, single-instance redirect, Serilog rolling logs,
  `chllseeder://` deep links end-to-end), Inno installer compiles + smoke-tested
  (per-user install, protocol keys), CI rewritten for dotnet.
  - Versions: .NET SDK 9.0.314, Windows App SDK **1.8.260508005**, CommunityToolkit.Mvvm 8.4.2,
    Microsoft.Extensions.Hosting 9.0.16, Serilog.Extensions.Hosting 9.0.0.
  - Learned: plain `"exe" "%1"` protocol registry keys surface as **Launch** (not Protocol)
    activation kind in unpackaged WAS — app self-registers via `ActivationRegistrationManager`
    on startup (installer keys remain as first-run bootstrap), plus a launch-args URI fallback.
  - Deferred: `[ObservableProperty]` partial-property form needs `LangVersion=preview`
    (MVVMTK0045 suppressed, field form used) — revisit on the .NET 10 bump.
- ✅ **Phase 1 — Core seeding** complete (2026-06-07, Windows): all subsystems below plus the
  Seed + Launch tab UI are wired end-to-end; solution builds clean (0 warnings), 227/227 tests pass,
  app smoke-tested (DI graph resolves, bootstrapper guest-auth + server fetch run and degrade
  gracefully when the TBD API host doesn't resolve, clean shutdown). Live seeding is a manual test
  (needs the real API + Steam + HLL).
  - ✅ **Config foundation:** `Core.Security.DpapiProtector` (port of `crypto.rs`, `dpapi:<base64>` format),
    `Core.Config.AtomicFile` (port of `write_file_safe`), `Core.Config.ConfigService`
    (STJ store, 500ms save throttle, `icacls` ACL hardening, transparent DPAPI for sensitive keys,
    **old-dir migration dropped** per clean break).
  - ✅ **API layer:** `Core.Api.Models` (full port of `types.rs` + `ServerInfo`, snake_case/lowercase-enum
    wire format via `ApiJson`), `ApiValidation` (friendly-error + validators + OAuth URL),
    `RetryPolicy` + `ResilienceHandler` (port of `retry.rs`), `AuthSession` + `AuthHandler`
    (JWT/x-api-key, dedup'd 401 refresh, DPAPI-persisted tokens), `SeedingStatusCache`
    (SSE cache from `backend/api_client.rs`), `Core.Servers.ServerStore` (port of `server.rs`),
    `SeedingApiClient` (typed endpoints), `AddChllSeederCore` DI extension.
  - ✅ **Native + tools layer (via CsWin32 0.3.275):** `Core.Games.GameDefinition`+`GameCatalog`
    (port of `game.rs`), `Core.Native.ProcessMonitor` (`process.rs` — Toolhelp32 scan, 15s PID cache,
    verified kill-by-path), `Core.Native.SteamPaths` (registry cache, shared to break the steam↔process
    mutual dep), `Core.Native.WindowFocus` (`window_focus.rs` — HLL window find/cache, PostMessage
    Esc/F13 splash bypass, AttachThreadInput force-focus, minimize), `Core.Native.Win11Input`
    (`win11_input.rs` — SendInput + UIA fallback; Win11 detect via `Environment.OSVersion`),
    `Core.Native.SteamLauncher` (`steam.rs` launch half — cold-start + `-applaunch +connect`, IP
    validation), `Core.Native.PowerStatus` (`power.rs` — powercfg modern-standby/wake-timer warnings),
    `Core.Tools.HllConfigBackupService` (`backup_restore_hll_config.rs` — efficiency INI rewrite,
    atomic backup/restore, write-ahead crash-recovery flag). All registered in `AddChllSeederCore`.
  - ✅ **SeedingEngine + keep-awake (2026-06-07):** `Core.Seeding.SeedingEngine` (port of the
    1183-LOC `backend/seeding.rs` state machine — open/launch retries, 3-phase splash bypass,
    monitor loop w/ linear backoff + dynamic fill-based stagger + jitter, server-switch countdown
    w/ snooze/switch-now, launch watcher for phantom/update relaunch, stop/stop-only/efficiency-
    cleanup). Process-global atomics → DI-singleton instance state; tokio tasks → `Task.Run` on a
    lifetime `CancellationTokenSource`; `tokio::select` SSE-wake → edge-triggered `SemaphoreSlim`
    (`NotifyMonitor`, dormant until Phase 2 SSE). Testable `Core.Seeding.SeedingState`
    (stop/snooze/switch-now atomics) + pure static `ComputeStaggerSecs` mirror the Rust
    `#[cfg(test)]` block. Engine events → `Core.Seeding.SeedingEvent` record hierarchy raised via
    `SeedingEngine.Event` (the AppEvent subset the engine emits). **Keep-awake** (new feature, no
    Rust source): `Core.Native.KeepAwake` holds `SetThreadExecutionState` (ES_CONTINUOUS|SYSTEM|
    DISPLAY) on a dedicated re-asserting thread; engine `Acquire`s on seed start, `Release`s on
    stop. Added `SetCursorPos` (enigo mouse-nudge replacement) + `SetThreadExecutionState` to
    `NativeMethods.txt`; `WindowFocus.HasHllWindow()` added to avoid leaking Win32 HWND to the engine.
    All registered in `AddChllSeederCore`.
  - Deviations: OS switch-notification toast + switch sound (Rust `platform::notification`) are
    deferred to Phase 2 — the engine emits `ServerSwitchPending` (UI subscribes) and focuses the
    seeder window, but does not yet play sound / show a toast. Autoseed flags (`AUTOSEED_*`,
    `cancel_autoseed`) left out — they belong to the Phase 4 autoseed feature, not the engine core.
  - Verification: `dotnet test` → **227/227 pass** (216 prior + 11 new: stop/snooze/switch-now/
    stagger/constants); full solution builds clean (0 warnings).
  - Native-layer deviations from the Rust monolith (deliberate): `game.rs` is a plain data record
    (no `IGameProfile` interface — Rust has no per-game behavior); `SteamLauncher.OpenGameAsync` does
    launch mechanics only — efficiency-apply / config-restore / the enigo mouse-nudge are left to the
    SeedingEngine orchestration; backup dir rebranded `espritseeder-backup` → `chllseeder-backup`
    (`Branding.BackupDirName`).
  - ✅ **Seed + Launch tab UI (2026-06-07):** `App.ViewModels.SeedingViewModel` (shared DI singleton)
    drives the engine and mirrors `SeedingEngine.Event` into observable UI state — status banner,
    splash-bypass banner w/ countdown, server-switch overlay (snooze +5/+15/+30, switch-now), stop
    controls. `Core.Bootstrap.AppBootstrapper : IHostedService` does guest-register → server-list load
    → 10s stats poll (Phase 2 → SSE), raising `ServersLoaded`/`ServersLoadFailed`/`StatsUpdated`.
    `SeedPage` = banner + NA/EU(or single) seed buttons + stats lists + switch overlay; `LaunchPage` =
    per-server launch buttons (one-click `StartAsync`). `App.Converters.BoolToVisibilityConverter`
    (`invert` param) for visibility. Added small engine UI surface (`IsGameRunning`,
    `KillGameAndWaitAsync`, post-monitor restore+keep-awake cleanup in `MonitorSeedImplAsync`).
    Added `Microsoft.Extensions.Hosting.Abstractions` to Core for `IHostedService`.
  - ✅ **`AddChllSeederCore` wired:** `App.BuildHost` now calls it + registers `MainWindow` and
    `SeedingViewModel`; window-close path runs `SeedingEngine.CleanupEfficiencyOnExitAsync` before
    `AppHost.StopAsync` (which flushes config via the bootstrapper's `StopAsync`).
  - Phase 1 deviations / deferred: **session + heartbeat** (`/api/seeding/start-session` + `heartbeat.rs`)
    are NOT ported — they're analytics, non-fatal in Rust too; deferred with SSE to Phase 2. "Seed All"
    rotation across servers/games is Phase 2 (the single-server monitor + in-place switch countdown work
    now). Switch toast/sound stay Phase 2. The switch-overlay countdown is visual only (the engine drives
    the real timing server-side).
- ✅ **Phase 2 — Live data, rotation, tray** complete. Part 1 (SSE + session/heartbeat) done 2026-06-09;
  **part 2 (Seed-All rotation, tray, toasts) done 2026-06-10**, Windows. Solution builds clean (0 warnings),
  273/273 tests pass. Live LAN/desktop smoke test (tray hide/restore, toast, rotation) still pending (manual).
  - ✅ **SSE stream:** `Core.Api.SseFrameParser` (pure SSE frame parser replacing the Rust
    `reqwest_eventsource` dep — `event:`/`data:`/multiline/comment/blank-line dispatch) + `Core.Api.SseStreamClient`
    (`IHostedService`, port of `api/sse.rs`): own infinite-timeout named `"sse"` HttpClient (no delegating
    handlers; per-connection bearer/x-api-key via `Core.Api.AuthHeaders`), 30s connect bound, 60s keepalive via
    reset `CancelAfter` (>120s ⇒ wake-from-sleep 3s settle), exponential backoff + jitter (`ComputeBackoffSecs`),
    429 `Retry-After` (≤120s), 401 ⇒ refresh. `stats` ⇒ `LiveStats.Apply`, `seeding_status` ⇒
    `SeedingStatusCache.Update`; on (re)connect invalidates the cache + `SeedingEngine.NotifyMonitor()`.
  - ✅ **Coordination:** `Core.Servers.LiveStats` (shared stats sink — `Apply` + `StatsUpdated`, moved out of
    `AppBootstrapper`; both SSE and the poll fallback feed it) and `Core.Api.SseConnectionState`
    (`Connected`/`FailureCount` + `RequestPoll`/`WaitForPollOrInterval` = Rust `SSE_CONNECTED`/`POLL_NOTIFY`,
    + `RequestReconnect`). `AppBootstrapper` poll loop is now the SSE-aware **fallback** (idles 60s while
    connected, polls 10s + wakes on disconnect; nudges `RequestReconnect` after guest auth).
  - ✅ **Auth extraction:** `Core.Api.AuthRefresher` (single-flight JWT refresh, extracted from `AuthHandler`,
    uses a dedicated `"auth"` named client = resilience only, no auth, no recursion) + `Core.Api.AuthHeaders`,
    both now shared by `AuthHandler` and the SSE client. (Phase 2 auth is the guest `x-api-key`; the 401-refresh
    path isn't exercised until Phase 3 OAuth.)
  - ✅ **Session + heartbeat:** `Core.Seeding.HeartbeatService` (port of `heartbeat.rs` — single active loop,
    30s + capped exponential backoff `BackoffIntervalSecs`, wake-from-sleep at `interval*3`, `StartAsync`/
    `StopAsync(reason)`/`StopFireAndForget`), `Core.Native.OsInfo` + `Core.Api.Analytics.Gather` (reads config
    `efficiency_mode`/`eu_enabled`). Wired in `SeedingViewModel.RunSeedAsync` (start session+heartbeat after a
    successful launch, auth-gated/non-fatal; stop reasons `monitor_complete`/`user_stopped`/
    `user_stopped_keep_game`) and `App.xaml.cs` window-close (`app_exit`, fire-and-forget).
  - ✅ **Phase 2 part 2 (2026-06-10):** UI-platform integration, all in the **App** project (Core stays
    UI-free/testable — deviation from the subsystem table, which had put `ToastService` in Core; `AppNotificationManager`
    and `H.NotifyIcon.WinUI` need WindowsAppSDK).
    - **Tray + close-to-tray:** `H.NotifyIcon.WinUI` 2.3.0 `TaskbarIcon` declared in `MainWindow.xaml` (Show /
      Restart / Quit menu, tooltip, `icon.ico`, left-click → restore; `ForceCreate()` + dispose-on-close to avoid
      ghost icons). `MainWindow` now DI-resolves `ConfigService`+`InAppToastService`; `AppWindow.Closing` hides to
      tray when `close_to_tray` (default **true**) and the user didn't pick Quit/Restart (`_forceQuit` flag). Restart
      ports `platform::restart` (delayed `cmd /c timeout 2 && exe` re-launch, then close → full App shutdown path).
    - **Desktop toasts + switch sound:** `App.Services.ToastService` over WAS `AppNotificationManager`
      (Register on startup / Unregister on exit; `NotificationInvoked` → bring window forward). Port of
      `platform::notification` — `MessageBeep(MB_ICONEXCLAMATION)` (DllImport) + "Switching servers in Ns…" toast.
      Fired from the VM's existing `ServerSwitchPending` handler (engine already gates that event behind
      `switch_notification`, so no extra gating).
    - **In-app toasts:** `App.Services.InAppToast`/`InAppToastService` (ObservableCollection of InfoBars,
      auto-dismiss timer + manual close) hosted bottom-of-shell in `MainWindow.xaml`. Port of `state::toast`.
      Used for "Switching to the next server…" and "Seed All complete".
    - **Seed All rotation:** `SeedingViewModel.SeedAllCommand` + `_isSeedAll` flag + "Seed All" button on `SeedPage`.
      Client-driven rotation (matches Rust `do_seed_next_server` + the `IS_SEED_ALL` branch of `do_monitor_seed`):
      `RunSeedAsync` refactored to extract `LaunchAndMonitorAsync`; the rotation loop awaits a monitor, then
      `GetSeedingStatusAsync` → `PickNextHop` (current game/region-preferred → EU when `eu_enabled` → other enabled
      games, killing + 20s + switching `CurrentGame`) and re-enters; ends with an in-app success toast when exhausted.
      Cross-game hop is dormant (`GameCatalog.Released` = [hll] only). Stop commands clear `_isSeedAll`.
    - **Settings toggles:** minimal `SettingsPage` `ToggleSwitch`es for `close_to_tray` + `switch_notification`
      (read/write via `ConfigService`); the full Settings tab stays Phase 3.
    - **Deviations / deferred:** the Rust 30s Seed-All cooldown (anti-spam after "all full"/error) is NOT ported —
      the `IsBusy` guard already blocks re-entry while seeding; cooldown only gates rapid retry. The Seed-All cascade
      helpers live in the App VM (matching Rust's component placement) and aren't covered by Core.Tests (no App test
      project). Tray icon/toast attribution + behavior need a live desktop smoke test (build-verified only).

## Context

The app "Esprit Seeder" (Hell Let Loose server-seeding desktop tool, Rust + Dioxus 0.7, ~18.5K LOC, 65 files) is being:

1. **Rebranded** to **CHLL Seeder** (Comp HLL Seeder) — new identifiers, new git remote `git@github.com:catalloc/chll-seeder-windows.git`, owned domain `comp-hll.org`.
2. **Rewritten** as a native **C# + WinUI 3** (Windows App SDK) app, replacing the Rust UI entirely, with **full feature parity delivered in shippable phases**.

Decisions locked in with the user:
- Rewrite **in this repo** (keep history), on branch `rewrite/winui3`.
- Deep-link scheme `espritseeder://` → **`chllseeder://`** (⚠ requires a matching change in the seeding-api backend OAuth redirects — outside this repo).
- All app IDs/task names/registry keys/mutex → CHLL-branded. **Clean break, NO migration** from old Esprit config paths (drop the old-dir migration logic in `config.rs`).
- API base URL → `https://seeding-api.comp-hll.org` (configurable; exact host TBD).
- Development continues in a **Windows PowerShell session** (dotnet CLI builds). This WSL session only produces this plan + repo prep.

## Step 0 — Make this plan portable (do first, from WSL)

The next work session is on Windows; this WSL plan file won't be reachable. So:
1. Copy this plan into the repo as `docs/REWRITE_PLAN.md`.
2. Tag the final Rust commit: `git tag rust-final`.
3. Update remote: `git remote set-url origin git@github.com:catalloc/chll-seeder-windows.git` (keep old URL noted in plan; user creates the GitHub repo if not present).
4. Create branch `rewrite/winui3`, commit the plan, push branch + tag.
5. Tell the user: clone/open the repo on the Windows side and resume from `docs/REWRITE_PLAN.md`.

## Repo layout transition

- Move all Rust to `/src-rust/` (`src/`, `Cargo.toml`, `Cargo.lock`, `Dioxus.toml`, `build.rs`, `tailwind.css`, any `installer_hooks.nsh`) — kept as porting reference until Phase 5 parity sign-off, then deleted in one commit.
- New C# code under `/src/`. Keep `/icons/` and `/assets/` (rebrand images later).
- `.gitignore`: add `bin/`, `obj/`, `*.user`, `.vs/`, `artifacts/`, `installer/Output/`, `msbuild.binlog`.
- Update `CLAUDE.md` for the new stack (dotnet build commands, project layout).
- Update `README.md` + `PRIVACY.md` branding and repo URLs.

## Architecture (verified against mid-2026 ecosystem)

**Solution `ChllSeeder.sln`:**
```
/src/ChllSeeder.App         WinUI 3 app — net9.0-windows10.0.22621.0, min 10.0.19041.0.
                            Views, ViewModels, App.xaml, custom Main (DISABLE_XAML_GENERATED_MAIN).
/src/ChllSeeder.Core        Class library — all non-UI logic (API, SSE, seeding engine, config,
                            DPAPI, process/Win32, autoseed, updater). No XAML deps; testable.
/src/ChllSeeder.Core.Tests  xUnit — port existing Rust #[test] coverage (crypto, deep-link parsing,
                            autoseed time parsing, config atomic writes).
/installer                  Inno Setup script (.iss).
```

**Key choices:**
- **.NET 9** + **Windows App SDK 1.8.6** (stable line; .NET 10 bump later is trivial).
- **CommunityToolkit.Mvvm** (source-gen MVVM; maps 1:1 onto Dioxus GlobalSignals).
- **Microsoft.Extensions.Hosting + DI**; background workers (SSE loop, heartbeat, missed-task poller) as `IHostedService`.
- **Deployment: UNPACKAGED, self-contained, Inno Setup `setup.exe`** — not MSIX. Reasons: preserves the existing self-update flow (download installer → SHA-256 verify → run), users expect a setup.exe, installer writes `chllseeder://` protocol registry keys directly, toasts/tray confirmed working unpackaged.
- NuGet: `H.NotifyIcon.WinUI` (tray), WAS `AppNotificationManager` (desktop toasts), `System.Security.Cryptography.ProtectedData` (DPAPI, keep `dpapi:<base64>` format), `Microsoft.Extensions.Http.Resilience`/Polly (30s timeout + exponential backoff), **hand-rolled SSE** over HttpClient streaming (reconnect + 60s polling fallback, like `api/sse.rs`), `System.Text.Json` source-gen, **Serilog** rolling daily file logs w/ 7-file retention, **keep `schtasks.exe`** shell-out for Task Scheduler (port `task_scheduler.rs` verbatim), `Microsoft.Windows.CsWin32` for P/Invoke (PostMessageW/SendInput/window enum).

**WinUI 3 gotchas to handle:**
- Frameless window: `AppWindowTitleBar.ExtendsContentIntoTitleBar` + `SetTitleBar` + explicit drag regions (`InputNonClientPointerSource`); `OverlappedPresenter` for non-resizable.
- Single instance: `AppInstance.FindOrRegisterForKey("chll-seeder-main")` + `RedirectActivationToAsync`; main instance handles redirected `chllseeder://` activations via `Activated` event (replaces named mutex/pipe).
- Close-to-tray: cancel `AppWindow.Closing` and `Hide()`; real quit sets a flag and **disposes the tray icon** (ghost-icon pitfall).
- Hidden desktop windows don't suspend — background services keep running; marshal UI updates via `DispatcherQueue.TryEnqueue`.
- Seeding engine runs entirely off the UI thread; Win11 SendInput fallback needs foreground focus handling first.
- Unpackaged protocol activation args need careful casting (known WAS quirk).
- App must stay **non-elevated** (toasts break elevated; HKLM Steam path is read-only).

## Subsystem mapping (Rust → C#)

| Rust | C# |
|---|---|
| `backend/seeding.rs` (1183 LOC, core) | `Core.Seeding.SeedingEngine` state machine |
| `api/client.rs` + `types.rs` + `retry.rs` | `Core.Api.SeedingApiClient` (typed HttpClient + Polly) + `AuthHandler : DelegatingHandler` (x-api-key / JWT / refresh-on-401) |
| `api/sse.rs` | `Core.Api.ServerStatsSseClient : IHostedService` |
| `config.rs` | `Core.Config.ConfigService` (STJ, temp+rename atomic writes, 500ms debounce, DPAPI secrets, icacls hardening; **drop old-dir migration**) |
| `backend/steam.rs`, `process.rs`, `window_focus.rs`, `win11_input.rs` | `Core.Native.SteamLauncher`, `GameProcessMonitor`, `WindowFocus`, `SplashBypass` (CsWin32) |
| `backend/autoseed.rs`, `task_scheduler.rs` | `Core.Scheduling.AutoSeedService` + `MissedTaskMonitor`; CLI `--autoseed-na/--autoseed-eu` kept |
| `backend/backup*.rs` (efficiency mode INI) | `Core.Tools.EfficiencyModeService` + `HllConfigBackupService` (crash-recovery flag file) |
| `backend/crypto.rs` | `Core.Security.DpapiProtector` |
| `platform/tray.rs` | `App.Tray.TrayIconService` (H.NotifyIcon) |
| `platform/deep_link.rs`, `single_instance.rs` | `Core.Activation.DeepLinkParser` + AppInstance redirection |
| `platform/notification.rs` | `Core.Notifications.ToastService` (AppNotificationManager + system sound) |
| `platform/updater.rs` | `Core.Update.UpdaterService` (HTTPS + trusted-domain + ext + ≤500MB + SHA-256 validation; launches Inno setup.exe) |
| `platform/startup.rs` | `Core.Platform.StartupRegistry` (HKCU Run `CHLLSeeder`) |
| `components/*` + `state/*` | `App.Views.*Page` + `App.ViewModels.*` (NavigationView shell, 5 pages); `IMessenger` for events |
| `backend/game.rs` | `Core.Games.IGameProfile` + `HllGameProfile` (HLLV placeholder) |

## Rebrand checklist

| Item | Old | New |
|---|---|---|
| Protocol | `espritseeder://` | `chllseeder://` ("URL:CHLL Seeder Protocol") |
| Single-instance key | `Global\EspritSeeder` mutex | `AppInstance` key `chll-seeder-main` |
| HKCU Run value | `EspritSeeder` | `CHLLSeeder` |
| Config dir | `%APPDATA%\org.espritdecorpsgaming.hllseeder` | `%APPDATA%\org.comphll.chllseeder` |
| Logs dir | Esprit dir in `%LOCALAPPDATA%` | `%LOCALAPPDATA%\CHLLSeeder\logs` |
| Scheduled tasks | `Esprit-Seeder`, `Esprit-Seeder-Secondary` | `CHLL-Seeder`, `CHLL-Seeder-EU` |
| Exe / product | `esprit-seeder.exe` / "Esprit Seeder" | `CHLLSeeder.exe` / "CHLL Seeder" |
| Publisher | Esprit De Corps Gaming | Comp HLL |
| Installer | NSIS `EspritSeeder_x.y.z_x64-setup.exe` | Inno `CHLL-Seeder-Setup-<ver>.exe` |
| API base | `seeding-api.espritdecorpsgaming.org` | `https://seeding-api.comp-hll.org` (configurable, TBD) |
| Git remote | `Esprit-De-Corps-Gaming/esprit-seeder-windows` | `catalloc/chll-seeder-windows` |
| Window title / README / PRIVACY / CLAUDE.md | Esprit Seeder | CHLL Seeder |

⚠ **Backend coordination needed (outside this repo):** OAuth redirect to `chllseeder://`, new API domain, `releases/latest` pointing at new installer names.

## Phases (each shippable)

- **Phase 0 — Skeleton:** solution + 3 projects, custom Main with single-instancing, DI/host, Serilog, frameless 5-tab MainWindow, rebranded metadata/icons, Inno script producing a working setup.exe, GitHub Actions CI (`windows-latest`: setup-dotnet 9.x → build → test → artifact; drop Rust/clippy/dx steps).
- **Phase 1 — Core seeding (first real ship):** ConfigService (+DPAPI/atomic/ACL), API client + guest auth + JWT refresh, **SeedingEngine end-to-end** (Steam launch → 60s window wait → splash bypass Esc/F13 → 5s monitor → 30s heartbeat → kill/20s cooldown → 5h max), Seed tab w/ polled server list + one-click seed + status banner, Launch tab, keep-awake.
- **Phase 2 — Live data, rotation, tray:** SSE + polling fallback, Seed All rotation (next-server API, jitter, countdown, snooze), tray icon + close-to-tray, desktop toasts + switch sound, in-app toasts.
- **Phase 3 — Accounts & settings:** `chllseeder://` OAuth deep links, provider linking, display name, API key rotation, account deletion, Leaderboard tab, full Settings tab, onboarding.
- **Phase 4 — Automation & tools:** auto-seed schtasks setup + CLI args + missed-task detection (startup + 60s, UTC↔local), efficiency mode INI swap + crash recovery, Tools tab (backup/restore, open logs).
- **Phase 5 — Updater & parity sign-off:** self-updater wired to Inno setup.exe, stable/beta channels, theming polish, parity audit vs `/src-rust`, **delete `/src-rust`**, release workflow on tags (build, sign, attach `CHLL-Seeder-Setup-<ver>.exe` + SHA-256).

## Key reference files for porting (in `/src-rust` after move)

- `src/backend/seeding.rs` — highest-risk port, do first in Phase 1
- `src/config.rs` — config schema + the migration logic to drop
- `src/api/client.rs`, `src/api/types.rs` — full API surface to mirror in STJ models
- `src/platform/deep_link.rs`, `single_instance.rs` — rebrand-sensitive
- `src/backend/autoseed.rs`, `src/platform/updater.rs` — task names + updater validation flow

## Verification

- **WSL (this session):** `git remote -v` shows new origin; branch `rewrite/winui3` pushed with `docs/REWRITE_PLAN.md`; `rust-final` tag pushed; repo builds nothing yet (no code moved unless Step 0 includes the `/src-rust` move — it does, verify `cargo` files are under `/src-rust/`).
- **Windows (later phases):** `dotnet build -c Release` + `dotnet test` green per phase; Phase 1 manual test = click Seed on a live server → HLL launches, joins, splash bypassed, heartbeat visible in logs, Stop kills process; installer smoke test via Inno output; protocol test via `start chllseeder://auth/callback?...`.
