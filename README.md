# Esprit Seeder

Windows desktop app for [Hell Let Loose](https://store.steampowered.com/app/686810/Hell_Let_Loose/) server seeding. One click to launch, seed, and keep your community servers populated.

Built by [Esprit De Corps Gaming](https://github.com/Esprit-De-Corps-Gaming).

## Download

Grab the latest installer from [GitHub Releases](https://github.com/Esprit-De-Corps-Gaming/esprit-seeder-windows/releases).

**Requirements:** Windows 10/11, Steam with Hell Let Loose installed.

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

When enabled, the app temporarily edits `GameUserSettings.ini` — the same INI file the in-game settings menu writes to. Changes include lowering resolution to 1024x768, setting all graphics to minimum, capping framerate at 30, and muting audio. The original file is backed up before any changes, and restored automatically when seeding ends. A persistent flag file (`~\hllseeder-backup\.efficiency_mode_active`) ensures recovery even if the app crashes mid-session.

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

**Credential storage.** Auth tokens and API keys are encrypted at rest using [Windows DPAPI](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/) (`CryptProtectData`), scoped to the current Windows user account. No other user on the machine can decrypt them. Credentials are stored in `%APPDATA%\org.espritdecorpsgaming.hllseeder\config.json`.

**Config directory ACLs.** On every startup, the app restricts the config directory's permissions via `icacls` — inherited ACEs are removed and only the current user is granted access. The `USERNAME` environment variable is validated against a strict character whitelist before being used in the command.

**In-memory token handling.** Tokens and API keys are zeroized in memory (via the `zeroize` crate) before being overwritten or cleared, reducing the window for memory disclosure.

**Atomic file writes.** Config and game settings are written using a temp-file-then-rename pattern with `sync_all()` to prevent data loss or corruption on crash or power failure.

**Update security.** Before downloading an update, the URL is validated against a hardcoded domain whitelist (`seeding-api.espritdecorpsgaming.org`, `github.com`, `objects.githubusercontent.com`) and must use HTTPS. Downloaded installers are verified against a server-provided SHA-256 checksum before being written to disk. Only `.exe` and `.msi` extensions are accepted. Installer filenames are sanitized to prevent path traversal. A 500 MB size limit prevents disk exhaustion.

**Input validation.** All URL path parameters (provider names, Steam IDs, user IDs) are validated against strict whitelists or format rules before being interpolated into API URLs. Config keys, session values, display names, server IPs, and deep link parameters all have length limits, character whitelists, and null-byte rejection. OAuth state parameters are URL-encoded.

**Process safety.** Before terminating any process, the app verifies the process path is inside the expected Steam game installation directory. User-provided file paths are canonicalized and checked for traversal attempts.

**HTTPS enforcement.** In release builds, the API base URL is hardcoded to HTTPS and cannot be overridden. Update download URLs are explicitly checked for the `https` scheme.

**Data sent to the API.** The app sends only what is needed for seeding coordination: session IDs, game/region/server index, Steam ID (if linked), display name changes, and heartbeats. It never sends hardware fingerprints, machine names, installed software lists, file system contents, or any data beyond what is listed in the API endpoints below.

**Local file access.** The app reads and writes only to its own config directory, its log directory, the HLL `GameUserSettings.ini` (only in efficiency mode), its backup directory, and a temp directory for updates. It reads the Steam registry key for the install path. No other files or registry keys are accessed.

### API server

The client communicates with a closed-source API server over HTTPS. All traffic is encrypted in transit. The API enforces authentication, rate limiting, and input validation server-side. Users can delete their account and all associated data at any time via the settings panel.

## Development

### Tech Stack

- **Framework:** [Dioxus](https://dioxuslabs.com/) 0.7 (Rust desktop)
- **Styling:** TailwindCSS v4 + DaisyUI v5
- **HTTP:** reqwest with JWT auto-refresh, reqwest-eventsource for SSE
- **Platform:** Win32 API (window control, system tray, DPAPI encryption)
- **API:** Closed-source backend (HTTPS)

### Prerequisites

- [Rust](https://rustup.rs/) 1.75+
- [Dioxus CLI](https://dioxuslabs.com/learn/0.6/getting_started): `cargo install dioxus-cli`

### Build & Run

```sh
# Clone
git clone git@github.com:Esprit-De-Corps-Gaming/esprit-seeder-windows.git
cd esprit-seeder-windows

# Dev build with hot reload
dx serve

# Release build (exe only)
dx build --release

# Bundle installer (NSIS + MSI)
dx bundle --release
```

### Project Structure

```
src/
  main.rs               Entry point
  app.rs                Root component
  api/                  HTTP client with JWT auto-refresh and SSE streaming
  backend/              Core logic (seeding, server, process, steam, tools, etc.)
  components/           UI components (seed, launch, settings, tools, modal, toast, etc.)
  state/                GlobalSignal state modules (servers, seeding, auth, toast, etc.)
  platform/             Platform integration (notification, tray, deep_link, updater)
  config.rs             JSON file I/O for app settings
  events.rs             Typed event bus (tokio broadcast)
  macros.rs             Utility macros (lock!, read_lock!, write_lock!)
```

### Running Tests

```sh
cargo test
```

## Contributing

Contributions are welcome! Please open an issue to discuss your idea before submitting a PR.

1. Fork the repo and create a feature branch
2. Make your changes
3. Run `cargo clippy` and `cargo test`
4. Submit a pull request against `main`

## Contact

For questions, feedback, or support: dev@espritdecorpsgaming.org

## License

[AGPL-3.0](LICENSE)
