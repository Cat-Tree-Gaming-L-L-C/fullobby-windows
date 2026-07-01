# CHLL Seeding

Windows desktop app for [Hell Let Loose](https://store.steampowered.com/app/686810/Hell_Let_Loose/) server seeding. One click to launch, seed, and keep your community servers populated.

Built by [Comp HLL](https://github.com/Cat-Tree-Gaming-L-L-C/chll-seeding-windows). Native C# + WinUI 3 — see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Download

Grab the latest installer (`CHLL-Seeding-Setup-<ver>.exe`) from [GitHub Releases](https://github.com/Cat-Tree-Gaming-L-L-C/chll-seeding-windows/releases).

**Requirements:** Windows 10 (19041) / Windows 11, Steam with Hell Let Loose installed.

## Features

- **One-click seeding** — launches HLL via Steam and joins the target server
- **Seed All** — cycles through NA/EU servers, skipping offline or full ones
- **Auto-seed scheduling** — set a time and the app seeds unattended via Windows Task Scheduler
- **Live server stats** — real-time player counts and map info
- **Efficiency mode** — automatically applies low-graphics settings to reduce resource usage while seeding
- **System tray** — runs in the background with close-to-tray support
- **In-app updates** — notifies you when a new version is available
- **Discord & Steam sign-in** — track seeding stats and appear on the leaderboard

## How the App Interacts with Steam and Hell Let Loose

This section documents exactly what the app does — and does not do — when interacting with Steam and game processes. The design is intentionally minimal and non-invasive.

### Game launching

The app launches HLL by calling `steam.exe -applaunch 686810 +connect <IP:PORT>` as a subprocess. The Steam executable path is read from the Windows registry (`HKLM\SOFTWARE\Wow6432Node\Valve\Steam`). This is the same mechanism Steam itself uses — no undocumented APIs, no DLL injection, no protocol hacks. The actual game process is started by Steam, not by this app.

### Process detection

The app checks whether Steam and HLL are running by taking a snapshot of the Windows process list via the standard [Toolhelp API](https://learn.microsoft.com/en-us/windows/win32/toolhelp/tool-help-functions) (`CreateToolhelp32Snapshot`). It reads process names only — no game memory is read, no handles are opened beyond `PROCESS_QUERY_LIMITED_INFORMATION` for PID validation.

### Process termination

When a seeding session ends (server switch, time limit, or user stop), the app terminates HLL via `TerminateProcess`. Before killing any process, the app verifies its full image path contains the expected Steam game installation folder (e.g., `\steamapps\common\Hell Let Loose\`). Processes outside the Steam game directory are never touched.

### Efficiency mode (opt-in)

When enabled, the app temporarily edits `GameUserSettings.ini` — the same INI file the in-game settings menu writes to. Changes include lowering resolution to 1024x768, setting all graphics to minimum, capping framerate at 30, and muting audio. The original file is backed up before any changes, and restored automatically when seeding ends. A persistent flag file ensures recovery even if the app crashes mid-session.

This file is located at `%LOCALAPPDATA%\HLL\Saved\Config\WindowsNoEditor\GameUserSettings.ini` and is a standard user-editable config — not a locked game binary. The app never modifies `Engine.ini` or any other protected file.

### Splash screen bypass

During HLL's startup screens, the app sends `Escape` (to skip intro videos) and `F13` (to dismiss "Press any button") via `PostMessageW` to HLL's window. F13 is a virtual key with no in-game binding — it acts as a neutral "any key" press. On Windows 11 where `PostMessageW` may be blocked for background windows, the app falls back to `SendInput` (hardware-level input injection). No DirectInput hooking, no memory writes.

### What the app does NOT do

- Does not read or write game process memory
- Does not inject DLLs or hook game functions
- Does not interact with or bypass Easy Anti-Cheat (EAC)
- Does not modify game binaries, Engine.ini, or any locked files
- Does not send mouse clicks or manipulate in-game UI
- Does not access other players' data or game state
- Does not create fake Steam accounts or multiple game instances

### Community context

Server seeding is a well-established practice in the HLL community. Multiple open-source seeding tools exist and operate publicly, including [hll_advanced_seeder](https://github.com/mattwright324/hll_advanced_seeder). Community server owners openly discuss and use seeding tools. The topic has been discussed in Steam community threads ([1](https://steamcommunity.com/app/686810/discussions/0/3872592051317031811/), [2](https://steamcommunity.com/app/686810/discussions/0/5291222404432540020/), [3](https://steamcommunity.com/app/686810/discussions/0/6497096716248264889/)) where community members report that Team17 has indicated seeding tools are not prohibited under the current user agreement. We cannot independently verify these secondhand reports, and Team17's position may change in the future.

## Security

### Desktop client

**Credential storage.** Auth tokens and API keys are encrypted at rest using [Windows DPAPI](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/), scoped to the current Windows user account. No other user on the machine can decrypt them. Credentials are stored in `%APPDATA%\org.comphll.chllseeding\config.json`.

**Config directory ACLs.** On every startup, the app restricts the config directory's permissions — inherited ACEs are removed and only the current user is granted access.

**Atomic file writes.** Config and game settings are written using a temp-file-then-rename pattern to prevent data loss or corruption on crash or power failure.

**Update security.** Before downloading an update, the URL is validated against a hardcoded domain whitelist and must use HTTPS. Downloaded installers are verified against a server-provided SHA-256 checksum before being written to disk. Only installer extensions are accepted, filenames are sanitized to prevent path traversal, and a 500 MB size limit prevents disk exhaustion.

**Input validation.** All URL path parameters (provider names, Steam IDs, user IDs) are validated against strict whitelists or format rules before being interpolated into API URLs. Config keys, session values, display names, server IPs, and deep link parameters all have length limits, character whitelists, and null-byte rejection. OAuth state parameters are URL-encoded.

**Process safety.** Before terminating any process, the app verifies the process path is inside the expected Steam game installation directory. User-provided file paths are canonicalized and checked for traversal attempts.

**HTTPS enforcement.** In release builds the API base URL is hardbaked to the production HTTPS host and cannot be overridden — the `CHLL_SEEDING_API_URL` environment override is compiled out of release builds entirely (it exists only in debug builds for local development). Update download URLs are explicitly checked for the `https` scheme.

**Data sent to the API.** The app sends only what is needed for seeding coordination: session IDs, game/region/server index, Steam ID (if linked), display name changes, and heartbeats. It never sends hardware fingerprints, machine names, installed software lists, file system contents, or any data beyond what is listed in the privacy policy.

**Local file access.** The app reads and writes only to its own config directory, its log directory, the HLL `GameUserSettings.ini` (only in efficiency mode), its backup directory, and a temp directory for updates. It reads the Steam registry key for the install path. No other files or registry keys are accessed.

### API server

The client communicates with a closed-source API server over HTTPS. All traffic is encrypted in transit. The API enforces authentication, rate limiting, and input validation server-side. Users can delete their account and all associated data at any time via the settings panel.

## Development

### Tech Stack

- **Framework:** C# / .NET 9 + [WinUI 3](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/) (Windows App SDK 1.8, unpackaged)
- **MVVM:** CommunityToolkit.Mvvm; Microsoft.Extensions.Hosting for DI + background services
- **Logging:** Serilog (rolling daily files)
- **Installer:** Inno Setup
- **API:** Closed-source backend (HTTPS)

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Windows 10 (19041) or later
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) (installer builds only)

### Build & Run

```powershell
# Clone
git clone https://github.com/Cat-Tree-Gaming-L-L-C/chll-seeding-windows.git
cd chll-seeding-windows

# Build
dotnet build src/ChllSeeding.sln -c Release

# Run tests
dotnet test src/ChllSeeding.Core.Tests

# Run the app
src\ChllSeeding.App\bin\x64\Release\net9.0-windows10.0.22621.0\win-x64\CHLLSeeding.exe

# Build the installer
dotnet publish src/ChllSeeding.App -c Release -r win-x64 --self-contained true -p:Platform=x64 -o artifacts/publish
iscc installer\ChllSeeding.iss
```

### Project Structure

```
src/
  ChllSeeding.App/         WinUI 3 app (views, view models, custom Main, tray)
  ChllSeeding.Core/        Non-UI logic (API, seeding engine, config, deep links)
  ChllSeeding.Core.Tests/  xUnit tests
tools/ChllSeeding.MockApi/ Local mock API for offline dev
installer/                Inno Setup script
docs/ARCHITECTURE.md      Architecture reference
```

## Contributing

Contributions are welcome! See [CONTRIBUTING.md](CONTRIBUTING.md) for setup,
conventions, and the PR checklist. In short:

1. Open an issue to discuss anything non-trivial before submitting a PR
2. Fork the repo and create a feature branch off `main`
3. Make your changes
4. Run `dotnet build src/ChllSeeding.sln -c Release` and `dotnet test src/ChllSeeding.Core.Tests`
5. Submit a pull request

Found a security issue? Please report it privately per [SECURITY.md](SECURITY.md) — don't open a public issue.

## Contact

For questions, feedback, or support: [GitHub Issues](https://github.com/Cat-Tree-Gaming-L-L-C/chll-seeding-windows/issues)

## License

Copyright (C) 2026 Cat Tree Gaming L.L.C. Licensed under [AGPL-3.0](LICENSE); see
[NOTICE](NOTICE) for third-party components.
