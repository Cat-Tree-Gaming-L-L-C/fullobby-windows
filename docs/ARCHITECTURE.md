# CHLL Seeding — Architecture

C# + WinUI 3 (Windows App SDK) desktop app for Hell Let Loose server seeding.
This document is the durable architecture reference.

## Solution layout

```
/src/ChllSeeding.App         WinUI 3 app — net9.0-windows10.0.22621.0, min 10.0.19041.0.
                             Views, ViewModels, App.xaml, custom Main (DISABLE_XAML_GENERATED_MAIN).
/src/ChllSeeding.Core        Class library — all non-UI logic (API, SSE, seeding engine, config,
                             DPAPI, process/Win32, autoseed, updater). No XAML deps; testable.
/src/ChllSeeding.Core.Tests  xUnit coverage (crypto, deep-link parse, autoseed time, config atomic
                             writes, seeding state, schedule XML, etc.).
/installer                   Inno Setup script (.iss) — unpackaged setup.exe.
/tools/ChllSeeding.MockApi   Local mock of the seeding API for offline UI/dev testing.
```

## Key choices

- **.NET 9** + **Windows App SDK 1.8.x**; **CommunityToolkit.Mvvm** (source-gen MVVM);
  **Microsoft.Extensions.Hosting + DI** — background workers (SSE, heartbeat, missed-autoseed
  poller, bootstrapper) are `IHostedService`s registered by `AddChllSeedingCore`.
- **Deployment: UNPACKAGED, self-contained, Inno Setup `setup.exe`** (not MSIX). Preserves the
  self-update flow (download installer → SHA-256 verify → run), lets the installer write
  `chllseeding://` protocol keys directly, and keeps toasts/tray working unpackaged.
- **App stays non-elevated** — toasts break when elevated and the HKLM Steam path is read-only.
- NuGet: `H.NotifyIcon.WinUI` (tray), WAS `AppNotificationManager` (desktop toasts),
  `System.Security.Cryptography.ProtectedData` (DPAPI, `dpapi:<base64>` format),
  `Microsoft.Extensions.Http.Resilience`/Polly (timeout + backoff), hand-rolled SSE over
  HttpClient streaming (reconnect + 60s polling fallback), `System.Text.Json` source-gen,
  **Serilog** (rolling daily logs, 7-file retention), `schtasks.exe` shell-out for Task
  Scheduler, `Microsoft.Windows.CsWin32` for P/Invoke.

## Subsystem map

| Subsystem | Implementation |
|---|---|
| Seeding state machine | `Core.Seeding.SeedingEngine` (+ `SeedingState`, `SeedingEvent`) |
| API client | `Core.Api.SeedingApiClient`, `Models`/`ApiJson`, `RetryPolicy`/`ResilienceHandler`, `AuthHandler` + `AuthRefresher` + `AuthHeaders` |
| Live stats / SSE | `Core.Api.SseStreamClient : IHostedService` + `SseFrameParser`, `SseConnectionState`, `SeedingStatusCache`, `Core.Servers.LiveStats` |
| Config | `Core.Config.ConfigService` (STJ store, atomic temp+rename, 500ms throttle, DPAPI secrets, `icacls` hardening; no legacy-dir migration) + `Core.Security.DpapiProtector`, `Core.Config.AtomicFile` |
| Steam / process | `Core.Native.SteamLauncher`, `ProcessMonitor`, `SteamPaths` |
| Window focus / input | `Core.Native.WindowFocus` (HLL window find/cache, PostMessage Esc/F13 splash bypass, AttachThreadInput force-focus), `Win11Input` (SendInput + UIA fallback) |
| Auto-seed / scheduling | `Core.Scheduling.AutoSeedService`, `ScheduledTaskService` (schtasks `/create /xml`), `AutoSeedSlot`/`AutoSeedTime`/`AutoSeedState`, `MissedAutoseedMonitor : IHostedService` |
| Backup / restore | `Core.Tools.HllConfigBackupService` (efficiency-INI swap + crash-recovery flag), `ManualBackupService` (hardlink-dedup backups) |
| Game catalog | `Core.Games.GameDefinition` + `GameCatalog` (plain data record — no per-game interface) |
| Tray / notifications | `App` `H.NotifyIcon` `TaskbarIcon` (in `MainWindow.xaml`), `App.Services.ToastService` (AppNotificationManager + `MessageBeep`), `App.Services.InAppToastService` |
| Deep link / single instance | `Core.Activation.DeepLinkParser` + `AppInstance` redirection, `Core.Activation.OAuthStateStore` |
| Startup | `Core.Platform.StartupRegistry` (HKCU Run `CHLLSeeding`) |
| Updater | `Core.Update.UpdaterService` + `UpdateValidation` + `UpdateInfo` (HTTPS + trusted-domain + ext + ≤500MB + SHA-256; launches Inno setup.exe; stable/beta `update_channel`) |
| Power | `Core.Native.PowerStatus` (powercfg modern-standby/wake-timer warnings) |
| UI | `App.Views.*Page` + `App.ViewModels.*` (frameless 5-tab shell) |
| Keep-awake | `Core.Native.KeepAwake` (`SetThreadExecutionState` re-asserting thread; held while seeding) |

## WinUI 3 gotchas (load-bearing)

- **Frameless window:** `AppWindowTitleBar.ExtendsContentIntoTitleBar` + `SetTitleBar` + explicit
  drag regions (`InputNonClientPointerSource`); `OverlappedPresenter` for non-resizable.
- **Single instance:** `AppInstance.FindOrRegisterForKey("chll-seeding-main")` +
  `RedirectActivationToAsync`; the main instance handles redirected `chllseeding://` activations via
  the `Activated` event.
- **Unpackaged protocol activation:** plain `"exe" "%1"` registry keys surface as **Launch** (not
  Protocol) activation kind — the app self-registers via `ActivationRegistrationManager` on startup,
  and there is a launch-args URI fallback. Cast activation `Data` via the **interface**
  (`IProtocolActivatedEventArgs` / `ILaunchActivatedEventArgs`), not the concrete class.
- **Close-to-tray:** cancel `AppWindow.Closing` and `Hide()`; a real quit sets a flag and
  **disposes the tray icon** (ghost-icon pitfall). `ForceCreate()` the icon on startup.
- **Threading:** hidden windows don't suspend, so background services keep running; marshal UI
  updates via `DispatcherQueue.TryEnqueue`. The seeding engine runs entirely off the UI thread; the
  Win11 `SendInput` fallback needs foreground-focus handling first.
- **MVVM source-gen:** `[ObservableProperty]` partial-property form needs `LangVersion=preview`
  (MVVMTK0045 suppressed, field form used) — revisit on the .NET 10 bump.

## ⚠ Cross-repo coordination (outside this repo)

These depend on the `seeding-api` backend / release infra and can't be verified from this repo:

- OAuth redirect target must be `chllseeding://` (auth + provider-link callbacks).
- API base host `https://seeding.comp-hll.org` (configurable via the `api_host` config key).
- `releases/latest` must point at the new installer names (`CHLL-Seeding-Setup-<ver>.exe`) with
  matching SHA-256 for the self-updater.
- Provider linking/sign-in for **Epic Games** and **Xbox** is stubbed (disabled) in Settings until
  the backend supports those providers and they're added to `ApiValidation.ValidProviders`.

## Build & test

- `dotnet build src/ChllSeeding.sln -c Release` — build
- `dotnet test src/ChllSeeding.Core.Tests` — run tests
- Installer: build with Inno Setup against `installer/`.
- Manual end-to-end (needs real API + Steam + HLL): click Seed on a live server → HLL launches,
  joins, splash bypassed, heartbeat in logs, Stop kills the process.
