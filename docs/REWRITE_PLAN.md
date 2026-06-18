# CHLL Seeding — Rebrand + WinUI 3 Rewrite Plan

## Status

- ✅ **Step 0** (2026-06, WSL): plan committed, `rust-final` tag, Rust moved to `/src-rust`, branch `rewrite/winui3`.
- ✅ **Phase 0 — Skeleton** (2026-06-07, Windows): solution + 3 projects build green
  (`dotnet build src/ChllSeeding.sln -c Release`), 16/16 Core tests pass, app verified
  running (frameless 5-tab shell, single-instance redirect, Serilog rolling logs,
  `chllseeding://` deep links end-to-end), Inno installer compiles + smoke-tested
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
    `SeedingApiClient` (typed endpoints), `AddChllSeedingCore` DI extension.
  - ✅ **Native + tools layer (via CsWin32 0.3.275):** `Core.Games.GameDefinition`+`GameCatalog`
    (port of `game.rs`), `Core.Native.ProcessMonitor` (`process.rs` — Toolhelp32 scan, 15s PID cache,
    verified kill-by-path), `Core.Native.SteamPaths` (registry cache, shared to break the steam↔process
    mutual dep), `Core.Native.WindowFocus` (`window_focus.rs` — HLL window find/cache, PostMessage
    Esc/F13 splash bypass, AttachThreadInput force-focus, minimize), `Core.Native.Win11Input`
    (`win11_input.rs` — SendInput + UIA fallback; Win11 detect via `Environment.OSVersion`),
    `Core.Native.SteamLauncher` (`steam.rs` launch half — cold-start + `-applaunch +connect`, IP
    validation), `Core.Native.PowerStatus` (`power.rs` — powercfg modern-standby/wake-timer warnings),
    `Core.Tools.HllConfigBackupService` (`backup_restore_hll_config.rs` — efficiency INI rewrite,
    atomic backup/restore, write-ahead crash-recovery flag). All registered in `AddChllSeedingCore`.
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
    All registered in `AddChllSeedingCore`.
  - Deviations: OS switch-notification toast + switch sound (Rust `platform::notification`) are
    deferred to Phase 2 — the engine emits `ServerSwitchPending` (UI subscribes) and focuses the
    seeder window, but does not yet play sound / show a toast. Autoseed flags (`AUTOSEED_*`,
    `cancel_autoseed`) left out — they belong to the Phase 4 autoseed feature, not the engine core.
  - Verification: `dotnet test` → **227/227 pass** (216 prior + 11 new: stop/snooze/switch-now/
    stagger/constants); full solution builds clean (0 warnings).
  - Native-layer deviations from the Rust monolith (deliberate): `game.rs` is a plain data record
    (no `IGameProfile` interface — Rust has no per-game behavior); `SteamLauncher.OpenGameAsync` does
    launch mechanics only — efficiency-apply / config-restore / the enigo mouse-nudge are left to the
    SeedingEngine orchestration; backup dir rebranded `espritseeder-backup` → `chllseeding-backup`
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
  - ✅ **`AddChllSeedingCore` wired:** `App.BuildHost` now calls it + registers `MainWindow` and
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
      (read/write via `ConfigService`); the full Settings tab stays Phase 3. **`switch_notification` defaults to
      `true`** (deviation from Rust's `false`) — otherwise the switch countdown/toast/sound is invisible out of the
      box and every server switch is an abrupt game-kill. Engine + Settings both default it on when the key is unset.
    - **Deviations / deferred:** the Rust 30s Seed-All cooldown (anti-spam after "all full"/error) is NOT ported —
      the `IsBusy` guard already blocks re-entry while seeding; cooldown only gates rapid retry. The Seed-All cascade
      helpers live in the App VM (matching Rust's component placement) and aren't covered by Core.Tests (no App test
      project). Tray icon/toast attribution + behavior need a live desktop smoke test (build-verified only).

- ✅ **Phase 3 — Accounts & settings** complete (2026-06-10, Windows). Solution builds clean (0 warnings),
  289/289 tests pass (273 + 16 new: OAuth state lifecycle, JWT expiry). App smoke-tested (DI graph resolves,
  session-restore runs and degrades gracefully against the unresolved TBD API host, window up, clean shutdown).
  Live OAuth round-trip is a manual test (needs the real/staging backend + browser).
  - ✅ **API surface:** `SeedingApiClient` gained PATCH/DELETE (`SendNoContentAsync`) + the account endpoints —
    `UpdateDisplayName`/`RandomizeDisplayName`/`UpdateLeaderboardOptOut` (PATCH `/api/auth/me`),
    `GetLinkedProviders`, `GetLinkRedirectUrl` (link-init), `UnlinkProvider`, `GetSteamIds`, `RemoveSteamId`,
    `RotateApiKey`, `DeleteAccount`, `GetUserStats`. New model `RotateApiKeyResponse`; added `AuthMethod` enum.
  - ✅ **Core auth helpers (testable):** `Core.Activation.OAuthStateStore` (single-use CSRF `state` set/validate +
    `GenerateState` 128-bit hex; port of `state/auth.rs OAUTH_STATE`, now a DI singleton instead of a global) and
    `Core.Api.JwtUtil.IsExpired` (base64url `exp` decode, fail-safe; port of `app.rs is_jwt_expired`). xUnit:
    `OAuthStateStoreTests` (mirrors `test_oauth_state_lifecycle`) + `JwtUtilTests`.
  - ✅ **`App.ViewModels.AccountViewModel`** (shared DI singleton, dispatcher captured at ctor): owns the auth
    lifecycle — `RestoreSessionAsync` (port of `init_auth`: JWT-first w/ expiry short-circuit → API-key fallback →
    full reset + re-arm onboarding on rejection), `Login(provider)` (OAuth state + open browser),
    `RegisterGuestAsync` (idempotent — adopts the bootstrapper's silent guest if present), deep-link handlers
    `HandleAuthCallbackAsync`/`HandleLinkCallbackAsync`, name update/randomize, leaderboard opt-out toggle, API-key
    rotation, account deletion, provider link/unlink, Steam-ID remove, sign-out, onboarding step/complete. Per-action
    cooldowns mirror `state::cooldown`. Observable state (`User`/`IsLoggedIn`/`IsGuest`/`OnboardingComplete`/…) +
    derived (`ShowOnboarding`/`DisplayName`/`NameButtonText`/`CanRotateApiKey`/`ShowOnLeaderboard`/`HasDiscordProvider`).
    `LinkedProviderRow` display type for the providers list.
  - ✅ **Deep-link wiring:** `App.HandleActivation` now routes `auth/callback` → `HandleAuthCallbackAsync` and
    `auth/link-callback` → `HandleLinkCallbackAsync` (Phase 0's log-only stubs replaced). `App.OnLaunched` kicks
    `RestoreSessionAsync` in the background. Tokens never logged.
  - ✅ **Leaderboard tab:** `App.ViewModels.LeaderboardViewModel` (period Day/Week/Month/All, rankings + signed-in
    My Stats: totals, per-server breakdown, recent 5 sessions; 5s per-fetch cooldowns) + rebuilt `LeaderboardPage`
    (period buttons, My Stats card, rankings table). Port of `components/leaderboard.rs`.
  - ✅ **Full Settings tab:** account section (sign-in / display name / linked providers + unlink / link Steam+Discord /
    linked Steam IDs + remove / rotate key / sign out / delete), leaderboard opt-out toggle, EU servers, Dark Mode
    (applied via `MainWindow.SetTheme` → root `RequestedTheme`, persisted to `theme`), close-to-tray, switch
    notification, Power Savings (efficiency, w/ confirm), splash-bypass duration. Deferred rows shown **disabled** with
    a "later phase" note: Start-with-Windows (P4), Auto-Seed setup (P4), Check-Updates + Beta channel (P5).
  - ✅ **Onboarding wizard:** `Views/OnboardingView` (4-step overlay sign-in → link → nickname → done, code-behind
    step switching since `x:Bind` isn't available on a `Window` root) hosted in `MainWindow`, gated on
    `AccountViewModel.ShowOnboarding`. Guest path completes immediately; OAuth advances to the link step. Port of
    `components/onboarding.rs` (rebranded copy).
  - **Deviations / deferred:** EU config key is `eu_enabled` (clean-break rename of Rust's `secondary_servers_enabled`).
    A **"Skip for now"** link on onboarding step 0 (not in Rust) prevents a dead backend from trapping first-run, per
    the silent-guest-fallback decision. The bootstrapper still auto-registers a guest silently; onboarding's guest
    button is idempotent against that. Settings rows whose Core services land later (autoseed/startup/updater) are
    visible-but-disabled stubs, not wired. App VM code isn't covered by Core.Tests (no App test project) — only the
    new Core helpers are. Live OAuth/link callbacks + leaderboard data need a reachable backend to verify end-to-end.
  - ✅ **Epic Games + Xbox account providers (2026-06-14):** the API supports `steam`/`discord`/`epic`/`xbox`/`guest`
    (Epic/Xbox fully implemented server-side, 404 unless their creds are configured), but the client only exposed
    steam+discord. Added `AuthProvider.Epic`/`Xbox` (+ `ToWireString`; `JsonStringEnumConverter` handles the wire form),
    widened `ApiValidation.ValidProviders` + `DeepLinkParser.ValidProviders` to all four OAuth providers, gave
    `LinkedProviderRow` Epic/Xbox labels, and added `HasEpicProvider`/`HasXboxProvider` hide-if-linked gates
    (alongside the existing Discord one). Sign-in + link buttons for Epic/Xbox added to **both** the onboarding wizard
    (steps 0 + 1) and the Settings account section — **always shown** (unconfigured providers surface a 404/error toast).
    Tests: +10 (wire strings, new-provider deserialize, epic/xbox link-callback parse, epic/xbox OAuth URL + IsValidProvider).
    358/358 pass, build clean. **Account-provider linking is distinct from the seeding `platform` field** (the client still
    always launches via Steam and reports `platform=steam`). Live Epic/Xbox round-trip needs the backend creds set + a browser.

- ✅ **Phase 4 — Automation & tools** complete (2026-06-10, Windows). Solution builds clean (0 warnings),
  348/348 tests pass (289 + 59 new: auto-seed time parse/normalize, task XML + next-run parse, missed-seed
  window, auto-seed coordination state, manual-backup timestamp/unchanged helpers). App smoke-tested (fresh
  instance: DI graph + new hosted `MissedAutoseedMonitor` resolve, window up, degrades gracefully against the
  unresolved TBD API host, clean). Live task creation / a real missed-seed fire need a reachable backend + Steam/HLL.
  - ✅ **Startup registry:** `Core.Platform.StartupRegistry` (HKCU `…\Run` value `CHLLSeeding`; Enable/Disable/
    IsEnabled/UpdatePathIfNeeded — port of `startup.rs`). Wired to a working "Start with Windows" toggle in
    Settings; `UpdatePathIfNeeded()` runs once at launch (stale-path refresh after an installer move).
  - ✅ **Auto-seed scheduling (Core.Scheduling):** `AutoSeedTime` (UTC HH:MM parse/validate, `NormalizeHms`,
    UTC→local for the task + display — port of `validate_start_time` + `components/settings.rs` time helpers),
    `ScheduledTaskService` (**schtasks `/create /xml`** with a generated Task v1.2 XML so WakeToRun +
    StartWhenAvailable survive — replaces the Rust `planif` COM path; delete/query/next-run via schtasks; XML
    builder + `ParseNextRunTime` are pure + tested), `AutoSeedSlot`/`AutoseedStatus` (NA→`CHLL-Seeding`/`--autoseed-na`/
    `auto_seed_time`, EU→`CHLL-Seeding-EU`/`--autoseed-eu`/`auto_seed_time_secondary`; **clean break — no legacy
    `Esprit-Seeder-2` fallbacks**), `AutoSeedService` (setup/uninstall/status; persists the UTC time, registers the
    task at the local-equivalent, appends `PowerStatus` warnings), `AutoSeedState` (DI-singleton replacing the Rust
    `AUTOSEED_IN_PROGRESS`/`AUTOSEED_CANCELLED`/`LAST_AUTOSEED_TRIGGER` atomics — exclusive begin/cancel + per-region
    per-UTC-day triggered log).
  - ✅ **Missed-task detection:** `Core.Scheduling.MissedAutoseedMonitor : IHostedService` (60s poll, wake heuristic
    >180s, **4h** UTC catch-up window, guards on triggered-today / in-progress / HLL-running; pure `IsWithinMissedWindow`
    tested). Raises `AutoseedDue(region)`; `App` marshals it onto the UI and runs the countdown. Port of
    `check_missed_autoseed` + the `app.rs` 60s loop. **Time model (UTC↔local):** user enters the daily time in **UTC**,
    we store the UTC string + create the task at the local-equivalent (schtasks triggers are local), and the monitor
    compares the stored UTC against the UTC clock.
  - ✅ **Auto-seed run + countdown + CLI:** `SeedingViewModel.RunAutoseedAsync(region)` (60s cancellable countdown
    overlay on `SeedPage`, then launch the best candidate — requested region first, the other region when EU is
    enabled — with `autoSeed:true` analytics; port of `run_autoseed`). `App` parses `--autoseed-na`/`--autoseed-eu`
    (+ legacy `--seed-*`) from both a fresh scheduled-task launch (`Environment.GetCommandLineArgs`) and a redirected
    second-instance activation (`ILaunchActivatedEventArgs.Arguments`), and subscribes to `MissedAutoseedMonitor`.
  - ✅ **Efficiency crash-recovery wiring (Phase 1 gap closed):** `App.OnLaunched` now calls
    `HllConfigBackupService.CheckAndRestoreOnStartup()` early and surfaces `TakeStartupRestoreNotice()` as an in-app
    toast once the window is up — a run killed mid-seed with degraded settings is restored on next launch.
  - ✅ **Manual backup + Tools tab:** `Core.Tools.ManualBackupService` (port of `backend/backup.rs` —
    timestamped incremental backups under `chllseeding-backup\HLL\manual\`, `CreateHardLinkW` P/Invoke dedup of
    unchanged files with copy fallback, restore with symlink-skip + containment guards, `RestoreFromAutoBackup`,
    `OpenLogs`; pure `IsTimestampFolder` + `IsFileUnchanged` tested). `ToolsPage` rebuilt (handler-driven, matching
    the Rust component): Backup Settings / Restore Manual / Restore Auto / View Logs / Links & Resources, with a
    WinUI `FolderPicker` (HWND-initialized).
  - **Deviations / deferred:** task creation uses generated schtasks XML rather than COM (the plan's
    "port task_scheduler.rs verbatim" via schtasks — XML is the only schtasks path that sets WakeToRun). The WinUI
    `FolderPicker` can't pre-seed a start directory (no rfd `set_directory` equivalent) — picker opens at "This PC".
    Web-resource URLs rebranded to `comp-hll.org/{faq,terms,privacy}` (finalized 2026-06-14; `/faq` 404s for now). App
    VM/page code isn't covered by Core.Tests (no App test project) — only the new Core helpers are. Updater +
    beta-channel rows in Settings stay disabled stubs (Phase 5).

- 🟡 **Phase 5 — Updater & parity sign-off** in progress (2026-06-18, Windows). Solution builds clean (0 warnings),
  393/393 tests pass (348 + 45: 35 updater-validation + the existing suite). App smoke-tested (DI graph resolves
  with the new injections, window up, live API host now reachable — `/api/servers` + SSE return 200, session-restore
  401-resets cleanly). Live update round-trip needs a populated `/api/releases/latest` + a real installer.
  - ✅ **Self-updater (`Core.Update`):** `UpdaterService` (port of `platform/updater.rs`) — `CheckForUpdatesAsync`
    (reads the `update_channel` config key for stable/beta, GETs `/api/releases/latest`, parses version/url/notes/
    sha256/signature, string-inequality version compare) + `DownloadAndInstallAsync` (HTTPS + trusted-host + SHA-256
    + ≤500 MB + exe/msi-extension validation, temp-dir write, launch). Pure helpers split into `UpdateValidation`
    (validate-url / extension / sanitize-filename / verify-sha256 / size / is-update-available) so the Rust
    `#[cfg(test)]` block carries over 1:1 (`UpdateValidationTests`, +35). Trusted domains derive from the configured
    API host (`ApiConfig.BaseUrl`) + GitHub CDNs, so the env override is honored; default installer name rebranded
    `esprit-seeder-update.exe` → `chll-seeding-update.exe`. New named HttpClient `"updater"` (300s timeout, resilience,
    **no** auth handler — release endpoints are public). `CurrentVersion` reads the entry-assembly version (`0.1.0`
    from `src/Directory.Build.props`).
  - ✅ **Settings wiring:** the disabled Updates stubs are now live — "Check for Updates" → `CheckForUpdatesAsync`
    → "no updates" / "update available" dialog; **Download & Install** (an enhancement over Rust, whose UI only
    showed the URL — `download_and_install` was never wired) downloads + verifies + launches the installer, then
    `MainWindow.ForceQuit()` exits through the normal shutdown path so the installer can replace the exe. "Beta
    Updates" toggle persists `update_channel` (stable/beta). Current-version line shown under the button.
  - ✅ **Live-data connection indicator (`reconnect_button.rs` gap):** `LiveStats.LastUpdateUtc` (port of
    `LAST_STATS_UPDATE`) + `SeedingViewModel` connection props (`ConnectionIndicatorVisible`/`ConnectionPolling`/
    `ConnectionStatusText`/`ConnectionAgeText`) refreshed off the existing 1s tick from `SseConnectionState`
    (`Connected`/`FailureCount`, threshold 3 → "Polling" vs "Disconnected") + a `ReconnectCommand`
    (`RequestReconnect`). Shown on `SeedPage` only while SSE is disconnected (dot + status + "Updated Ns ago" +
    Reconnect link). Port of the stale-timer (`stale_timer.rs`) display + the reconnect button component.
  - ✅ **Parity audit vs `/src-rust` (2026-06-18):** 6-way subagent sweep over all 65 Rust files, every High finding
    verified against the actual code. **API layer, seeding-engine timing constants, scheduling math, and config/crypto
    core all confirmed faithful — no functional gaps.** Two verified correctness bugs **fixed**: (1) `DpapiProtector.MaybeEncrypt`
    now falls back to plaintext on a DPAPI failure instead of throwing out of `ConfigService.Set` (was silently dropping
    tokens; mirrors Rust `maybe_encrypt`); (2) `AccountViewModel.HandleLinkCallbackAsync` now re-derives `IsGuest`/`guest_mode`
    from `/me` so a guest→permanent link upgrade applies without a restart (+ "your account is now permanent!" toast; port of
    `state/events.rs` LinkCallback). 393/393 tests pass, build clean.
    - **Verified-remaining gaps (deferred — user chose to stop, 2026-06-18):**
      - *Behavioral:* **game-running status watcher absent** — Rust `app.rs:874 check_game_running` polls every 5s to flip
        status Running/Stopped when HLL is launched/closed **outside** the app; C# `SeedingViewModel.OnTimerTick` doesn't,
        so a hand-launched game shows no "Game Running" banner and seed buttons don't auto-hide.
      - *UX:* seeding banners lack server-name/region context (Rust shows "Seeding {server} (EU)" etc.); auto-seed "starting
        in 60s" desktop toast missing (in-window overlay only); switch-overlay `server_full`→"Server is Full" + snoozed
        "Server Switch Snoozed" titles collapse to generic; in-app toast dedup missing + durations differ (C# 6s/12s vs Rust
        3s/10s); auto-seed has no uninstall-confirm dialog and no "View schedule" button.
      - *Robustness:* backup/restore dropped Rust's `validate_user_path` (symlink canonicalization + traversal guard — low
        attack surface since folders are user-picked); `MissedAutoseed` 4h window uses `<=` vs Rust strict `<`; `schtasks`
        stdout forced to UTF-8 can mangle next-run time on non-English Windows (display-only).
    - **Dismissed false-positives:** switch sound *is* played (`ToastService.ShowServerSwitch` → `MessageBeep`); the Seed-All
      30s cooldown is an intentional documented drop; `os_version` is fine (.NET 5+ `Environment.OSVersion` uses
      `RtlGetVersion`); token-string zeroization isn't reliably achievable on .NET.
  - **Remaining for Phase 5:** stable/beta release-channel **server** support is backend-side; theming polish, deleting
    `/src-rust`, the tag-driven release workflow (build + sign + attach `CHLL-Seeding-Setup-<ver>.exe` + SHA-256), and the
    deferred parity gaps listed above are still open.

## Planned enhancements (beyond Rust parity)

- 📝 **Notification-sound volume control (requested 2026-06-18, not yet implemented).** The switch-notification chime is
  currently `MessageBeep(MB_ICONEXCLAMATION)` in `App.Services.ToastService.PlayAttentionSound` — a 1:1 port of the Rust
  `play_notification_sound`, which plays at the **system** sound volume with no app-side control (the Rust app had no
  volume control either, so this is a net-new feature). `MessageBeep` takes no volume parameter, so adding a volume slider
  requires switching the playback mechanism. **Planned approach:**
  - Replace `MessageBeep` with `Windows.Media.Playback.MediaPlayer` (has a `Volume` property 0.0–1.0; works in unpackaged
    WinUI 3). Bundle a short attention chime as an app asset (e.g. `Assets/switch-alert.wav` — none exists today; either ship
    a WAV or point the `MediaPlayer` at a `ms-winsoundevent`/system-sound source). Cache one `MediaPlayer` instance in
    `ToastService` and re-`Play()` it per alert.
  - New config key `notification_volume` (0–100, default 100). `PlayAttentionSound` reads it; **0 = mute** (skip playback),
    otherwise `player.Volume = value / 100.0`. Persist via `ConfigService.SetString` like the other Settings toggles.
  - Add a `Slider` (0–100) to `SettingsPage.xaml` next to the existing "Switch Notification" toggle, seeded in `SeedControls`
    and written in a `ValueChanged` handler (guard with the `_loading` flag like the other controls). Optionally a small
    "Test" button that calls `PlayAttentionSound` so the user can preview the level.
  - Tradeoff to note: moving off `MessageBeep` drops integration with the user's Windows *sound scheme* (the chime becomes a
    fixed bundled asset) in exchange for app-controlled volume. Acceptable for a volume slider; revisit if scheme respect matters.

## Context

The app "Esprit Seeder" (Hell Let Loose server-seeding desktop tool, Rust + Dioxus 0.7, ~18.5K LOC, 65 files) is being:

1. **Rebranded** to **CHLL Seeding** (Comp HLL Seeder) — new identifiers, new git remote `git@github.com:catalloc/chll-seeding-windows.git`, owned domain `comp-hll.org`.
2. **Rewritten** as a native **C# + WinUI 3** (Windows App SDK) app, replacing the Rust UI entirely, with **full feature parity delivered in shippable phases**.

Decisions locked in with the user:
- Rewrite **in this repo** (keep history), on branch `rewrite/winui3`.
- Deep-link scheme `espritseeder://` → **`chllseeding://`** (⚠ requires a matching change in the seeding-api backend OAuth redirects — outside this repo).
- All app IDs/task names/registry keys/mutex → CHLL-branded. **Clean break, NO migration** from old Esprit config paths (drop the old-dir migration logic in `config.rs`).
- API base URL → `https://seeding-api.comp-hll.org` (configurable; exact host TBD).
- Development continues in a **Windows PowerShell session** (dotnet CLI builds). This WSL session only produces this plan + repo prep.

## Step 0 — Make this plan portable (do first, from WSL)

The next work session is on Windows; this WSL plan file won't be reachable. So:
1. Copy this plan into the repo as `docs/REWRITE_PLAN.md`.
2. Tag the final Rust commit: `git tag rust-final`.
3. Update remote: `git remote set-url origin git@github.com:catalloc/chll-seeding-windows.git` (keep old URL noted in plan; user creates the GitHub repo if not present).
4. Create branch `rewrite/winui3`, commit the plan, push branch + tag.
5. Tell the user: clone/open the repo on the Windows side and resume from `docs/REWRITE_PLAN.md`.

## Repo layout transition

- Move all Rust to `/src-rust/` (`src/`, `Cargo.toml`, `Cargo.lock`, `Dioxus.toml`, `build.rs`, `tailwind.css`, any `installer_hooks.nsh`) — kept as porting reference until Phase 5 parity sign-off, then deleted in one commit.
- New C# code under `/src/`. Keep `/icons/` and `/assets/` (rebrand images later).
- `.gitignore`: add `bin/`, `obj/`, `*.user`, `.vs/`, `artifacts/`, `installer/Output/`, `msbuild.binlog`.
- Update `CLAUDE.md` for the new stack (dotnet build commands, project layout).
- Update `README.md` + `PRIVACY.md` branding and repo URLs.

## Architecture (verified against mid-2026 ecosystem)

**Solution `ChllSeeding.sln`:**
```
/src/ChllSeeding.App         WinUI 3 app — net9.0-windows10.0.22621.0, min 10.0.19041.0.
                            Views, ViewModels, App.xaml, custom Main (DISABLE_XAML_GENERATED_MAIN).
/src/ChllSeeding.Core        Class library — all non-UI logic (API, SSE, seeding engine, config,
                            DPAPI, process/Win32, autoseed, updater). No XAML deps; testable.
/src/ChllSeeding.Core.Tests  xUnit — port existing Rust #[test] coverage (crypto, deep-link parsing,
                            autoseed time parsing, config atomic writes).
/installer                  Inno Setup script (.iss).
```

**Key choices:**
- **.NET 9** + **Windows App SDK 1.8.6** (stable line; .NET 10 bump later is trivial).
- **CommunityToolkit.Mvvm** (source-gen MVVM; maps 1:1 onto Dioxus GlobalSignals).
- **Microsoft.Extensions.Hosting + DI**; background workers (SSE loop, heartbeat, missed-task poller) as `IHostedService`.
- **Deployment: UNPACKAGED, self-contained, Inno Setup `setup.exe`** — not MSIX. Reasons: preserves the existing self-update flow (download installer → SHA-256 verify → run), users expect a setup.exe, installer writes `chllseeding://` protocol registry keys directly, toasts/tray confirmed working unpackaged.
- NuGet: `H.NotifyIcon.WinUI` (tray), WAS `AppNotificationManager` (desktop toasts), `System.Security.Cryptography.ProtectedData` (DPAPI, keep `dpapi:<base64>` format), `Microsoft.Extensions.Http.Resilience`/Polly (30s timeout + exponential backoff), **hand-rolled SSE** over HttpClient streaming (reconnect + 60s polling fallback, like `api/sse.rs`), `System.Text.Json` source-gen, **Serilog** rolling daily file logs w/ 7-file retention, **keep `schtasks.exe`** shell-out for Task Scheduler (port `task_scheduler.rs` verbatim), `Microsoft.Windows.CsWin32` for P/Invoke (PostMessageW/SendInput/window enum).

**WinUI 3 gotchas to handle:**
- Frameless window: `AppWindowTitleBar.ExtendsContentIntoTitleBar` + `SetTitleBar` + explicit drag regions (`InputNonClientPointerSource`); `OverlappedPresenter` for non-resizable.
- Single instance: `AppInstance.FindOrRegisterForKey("chll-seeding-main")` + `RedirectActivationToAsync`; main instance handles redirected `chllseeding://` activations via `Activated` event (replaces named mutex/pipe).
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
| `platform/startup.rs` | `Core.Platform.StartupRegistry` (HKCU Run `CHLLSeeding`) |
| `components/*` + `state/*` | `App.Views.*Page` + `App.ViewModels.*` (NavigationView shell, 5 pages); `IMessenger` for events |
| `backend/game.rs` | `Core.Games.IGameProfile` + `HllGameProfile` (HLLV placeholder) |

## Rebrand checklist

| Item | Old | New |
|---|---|---|
| Protocol | `espritseeder://` | `chllseeding://` ("URL:CHLL Seeding Protocol") |
| Single-instance key | `Global\EspritSeeder` mutex | `AppInstance` key `chll-seeding-main` |
| HKCU Run value | `EspritSeeder` | `CHLLSeeding` |
| Config dir | `%APPDATA%\org.espritdecorpsgaming.hllseeder` | `%APPDATA%\org.comphll.chllseeding` |
| Logs dir | Esprit dir in `%LOCALAPPDATA%` | `%LOCALAPPDATA%\CHLLSeeding\logs` |
| Scheduled tasks | `Esprit-Seeder`, `Esprit-Seeder-Secondary` | `CHLL-Seeding`, `CHLL-Seeding-EU` |
| Exe / product | `esprit-seeder.exe` / "Esprit Seeder" | `CHLLSeeding.exe` / "CHLL Seeding" |
| Publisher | Esprit De Corps Gaming | Comp HLL |
| Installer | NSIS `EspritSeeder_x.y.z_x64-setup.exe` | Inno `CHLL-Seeding-Setup-<ver>.exe` |
| API base | `seeding-api.espritdecorpsgaming.org` | `https://seeding-api.comp-hll.org` (configurable, TBD) |
| Git remote | `Esprit-De-Corps-Gaming/esprit-seeder-windows` | `catalloc/chll-seeding-windows` |
| Window title / README / PRIVACY / CLAUDE.md | Esprit Seeder | CHLL Seeding |

⚠ **Backend coordination needed (outside this repo):** OAuth redirect to `chllseeding://`, new API domain, `releases/latest` pointing at new installer names.

## Phases (each shippable)

- **Phase 0 — Skeleton:** solution + 3 projects, custom Main with single-instancing, DI/host, Serilog, frameless 5-tab MainWindow, rebranded metadata/icons, Inno script producing a working setup.exe, GitHub Actions CI (`windows-latest`: setup-dotnet 9.x → build → test → artifact; drop Rust/clippy/dx steps).
- **Phase 1 — Core seeding (first real ship):** ConfigService (+DPAPI/atomic/ACL), API client + guest auth + JWT refresh, **SeedingEngine end-to-end** (Steam launch → 60s window wait → splash bypass Esc/F13 → 5s monitor → 30s heartbeat → kill/20s cooldown → 5h max), Seed tab w/ polled server list + one-click seed + status banner, Launch tab, keep-awake.
- **Phase 2 — Live data, rotation, tray:** SSE + polling fallback, Seed All rotation (next-server API, jitter, countdown, snooze), tray icon + close-to-tray, desktop toasts + switch sound, in-app toasts.
- **Phase 3 — Accounts & settings:** `chllseeding://` OAuth deep links, provider linking, display name, API key rotation, account deletion, Leaderboard tab, full Settings tab, onboarding.
- **Phase 4 — Automation & tools:** auto-seed schtasks setup + CLI args + missed-task detection (startup + 60s, UTC↔local), efficiency mode INI swap + crash recovery, Tools tab (backup/restore, open logs).
- **Phase 5 — Updater & parity sign-off:** self-updater wired to Inno setup.exe, stable/beta channels, theming polish, parity audit vs `/src-rust`, **delete `/src-rust`**, release workflow on tags (build, sign, attach `CHLL-Seeding-Setup-<ver>.exe` + SHA-256).

## Key reference files for porting (in `/src-rust` after move)

- `src/backend/seeding.rs` — highest-risk port, do first in Phase 1
- `src/config.rs` — config schema + the migration logic to drop
- `src/api/client.rs`, `src/api/types.rs` — full API surface to mirror in STJ models
- `src/platform/deep_link.rs`, `single_instance.rs` — rebrand-sensitive
- `src/backend/autoseed.rs`, `src/platform/updater.rs` — task names + updater validation flow

## Verification

- **WSL (this session):** `git remote -v` shows new origin; branch `rewrite/winui3` pushed with `docs/REWRITE_PLAN.md`; `rust-final` tag pushed; repo builds nothing yet (no code moved unless Step 0 includes the `/src-rust` move — it does, verify `cargo` files are under `/src-rust/`).
- **Windows (later phases):** `dotnet build -c Release` + `dotnet test` green per phase; Phase 1 manual test = click Seed on a live server → HLL launches, joins, splash bypassed, heartbeat visible in logs, Stop kills process; installer smoke test via Inno output; protocol test via `start chllseeding://auth/callback?...`.
