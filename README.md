# Fullobby

Windows desktop app for multi-game community server seeding — one click to
launch, connect, and keep your community's servers populated. Free for players.

Current lineup: [Hell Let Loose](https://store.steampowered.com/app/686810/Hell_Let_Loose/)
(soft launched), Palworld (work in progress), and more games with Steam direct
connect / server query support on the way.

Built by [Cat Tree Gaming L.L.C.](https://github.com/Cat-Tree-Gaming-L-L-C). Native C# + WinUI 3 — see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Guides

- **[User guide](docs/USER-GUIDE.md)** — install, join a network, seed. Start here.
- **[Operator guide](docs/OPERATOR-GUIDE.md)** — answering ready checks and keeping your community's servers in the rotation.
- **[Admin guide](docs/ADMIN-GUIDE.md)** — running a community: servers, staff, your Discord, schedules.

## Download

Grab the latest installer (`Fullobby-Setup-<ver>.exe`) from [GitHub Releases](https://github.com/Cat-Tree-Gaming-L-L-C/fullobby-windows/releases).

**Requirements:** Windows 11, Steam with a supported game
installed (currently Hell Let Loose).

### Windows SmartScreen warning

Releases are not yet Authenticode-signed (an organization code-signing certificate is in
progress), so the first launch shows **"Windows protected your PC"**. Click **More info →
Run anyway** to proceed. Please **don't** disable SmartScreen or Defender for this — if you
want assurance beyond "the download page looked right", verify the installer below; it's
stronger than a publisher signature anyway.

### Verify your download (optional)

Every release can be verified two independent ways:

1. **Checksum against the signed update feed.** The update manifest at
   [`https://updates.fullobby.com/latest.json`](https://updates.fullobby.com/latest.json)
   carries the installer's SHA-256, signed with an offline release key that every client
   pins (see [Update security](#desktop-client)). Compare it to your download:

   ```powershell
   (Get-FileHash .\Fullobby-Setup-<ver>.exe).Hash
   # must equal the "sha256" field in https://updates.fullobby.com/latest.json
   ```

   The `.sha256` file attached to each GitHub release is a convenience copy of the same hash.

2. **Build provenance.** Each installer is attested at build time with a
   [Sigstore-signed provenance record](https://docs.github.com/en/actions/security-for-github-actions/using-artifact-attestations),
   proving it was built by this repository's public CI from a specific commit — nobody
   (including a compromised release account) can attach a valid attestation to a binary
   that didn't come from this repo's workflow. With the [GitHub CLI](https://cli.github.com/):

   ```powershell
   gh attestation verify .\Fullobby-Setup-<ver>.exe -R Cat-Tree-Gaming-L-L-C/fullobby-windows
   ```

## Features

- **One-click seeding** — launches HLL via Steam and joins the target server
- **Seed All** — cycles through NA/EU servers, skipping offline or full ones
- **Auto-seed scheduling** — set a time and the app seeds via Windows Task Scheduler; if you're at the machine it asks "Seed now?" first, and proceeds on its own if you're away
- **Live server stats** — real-time player counts and map info
- **Power savings** — optional low-graphics settings while seeding, chosen per game and remembered for next time
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

### Power savings / efficiency mode (opt-in)

Power savings is chosen per game — from the Seed button's flyout or the "Seed now?"
prompt — and your last choice is remembered for that game. It is off by default, and
games without efficiency-mode support never offer it.

When enabled, the app temporarily edits `GameUserSettings.ini` — the same INI file the in-game settings menu writes to. Changes are: switching to windowed 1024x768, setting all graphics quality to minimum, capping the framerate at 30, and muting audio, plus disabling a few non-essential HUD/gameplay display options (gore, hints, and incoming kick-vote/command/chat notifications) to further reduce rendering load. The original file is backed up before any changes, and restored automatically when seeding ends. A persistent flag file ensures recovery even if the app crashes mid-session.

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

**Credential storage.** Auth tokens and API keys are encrypted at rest using [Windows DPAPI](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/), scoped to the current Windows user account. No other user on the machine can decrypt them. Credentials are stored in `%APPDATA%\com.fullobby.app\config.json`.

**Config directory ACLs.** On every startup, the app restricts the config directory's permissions — inherited ACEs are removed and only the current user is granted access.

**Atomic file writes.** Config and game settings are written using a temp-file-then-rename pattern to prevent data loss or corruption on crash or power failure.

**Update security.** Every update manifest must carry a valid **ECDSA P-256 signature over its `(version, sha256)`**, verified against a public key hardbaked into the client — the private key is held offline and never touches CI or any server, so a compromised update origin cannot push a release it can't sign; a missing or invalid signature is refused. On top of that, update URLs are validated against a hardcoded domain whitelist and must use HTTPS, downloaded installers are verified against the signed SHA-256 before being written to disk (and re-verified immediately before launch), only installer extensions are accepted, filenames are sanitized to prevent path traversal, and a 500 MB size limit prevents disk exhaustion.

**Input validation.** All URL path parameters (provider names, Steam IDs, user IDs) are validated against strict whitelists or format rules before being interpolated into API URLs. Config keys, session values, display names, server IPs, and deep link parameters all have length limits, character whitelists, and null-byte rejection. OAuth state parameters are URL-encoded.

**Process safety.** Before terminating any process, the app verifies the process path is inside the expected Steam game installation directory. User-provided file paths are canonicalized and checked for traversal attempts.

**HTTPS enforcement.** In release builds the API base URL is hardbaked to the production HTTPS host and cannot be overridden — the `FULLOBBY_API_URL` environment override is compiled out of release builds entirely (it exists only in debug builds for local development). Update download URLs are explicitly checked for the `https` scheme.

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
- Windows 11
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) (installer builds only)

### Build & Run

```powershell
# Clone
git clone https://github.com/Cat-Tree-Gaming-L-L-C/fullobby-windows.git
cd fullobby-windows

# Build
dotnet build src/Fullobby.sln -c Release

# Run tests
dotnet test src/Fullobby.Core.Tests

# Run the app
src\Fullobby.App\bin\x64\Release\net9.0-windows10.0.22621.0\win-x64\Fullobby.exe

# Build the installer
dotnet publish src/Fullobby.App -c Release -r win-x64 --self-contained true -p:Platform=x64 -o artifacts/publish
iscc installer\Fullobby.iss
```

### Project Structure

```
src/
  Fullobby.App/         WinUI 3 app (views, view models, custom Main, tray)
  Fullobby.Core/        Non-UI logic (API, seeding engine, config, deep links)
  Fullobby.Core.Tests/  xUnit tests
tools/Fullobby.MockApi/ Local mock API for offline dev
installer/                Inno Setup script
docs/USER-GUIDE.md        Player guide (install, join, seed)
docs/OPERATOR-GUIDE.md    Community Operator guide (ready checks)
docs/ADMIN-GUIDE.md       Community Admin guide (servers, staff, Discord)
docs/ARCHITECTURE.md      Architecture reference
```

## Contributing

Contributions are welcome! See [CONTRIBUTING.md](CONTRIBUTING.md) for setup,
conventions, and the PR checklist. In short:

1. Open an issue to discuss anything non-trivial before submitting a PR
2. Fork the repo and create a feature branch off `main`
3. Make your changes
4. Run `dotnet build src/Fullobby.sln -c Release` and `dotnet test src/Fullobby.Core.Tests`
5. Submit a pull request

Found a security issue? Please report it privately per [SECURITY.md](SECURITY.md) — don't open a public issue.

## Contact

For questions, feedback, or support: [GitHub Issues](https://github.com/Cat-Tree-Gaming-L-L-C/fullobby-windows/issues)

## License

Copyright (C) 2026 Cat Tree Gaming L.L.C. This desktop client is licensed under
[AGPL-3.0](LICENSE); see [NOTICE](NOTICE) for third-party components.

The server-side seeding API (`api.fullobby.com`) is a separate, proprietary
service operated by Cat Tree Gaming L.L.C. It is not part of this repository and
is not covered by the client's license.

## Disclaimer

Fullobby is an independent, unofficial platform. Cat Tree Gaming L.L.C. is not
affiliated with, endorsed by, sponsored by, or partnered with the developers or
publishers of any game the platform supports — including Team17 Digital Limited
(Hell Let Loose and HLL are their property) and Pocketpair, Inc. (Palworld).
We're just community members trying to support the player bases around games we
love. All trademarks are the property of their respective owners.
