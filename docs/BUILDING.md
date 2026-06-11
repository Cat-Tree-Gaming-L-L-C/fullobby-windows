# Building & Running the Client (WinUI 3)

The app is an **unpackaged** WinUI 3 desktop client: .NET 9, x64 / `win-x64`,
self-contained Windows App SDK 1.8.x — **no MSIX**. The installer is a separate
Inno Setup step and is not needed to build or run locally.

## Prerequisites

- **.NET 9 SDK** (installed machine-wide on this box)
- Windows App SDK 1.8.x — restored automatically via NuGet on first build
- Windows 10 build 19041+ (target framework references 10.0.22621.0)

## Build

```powershell
dotnet build src/ChllSeeder.sln -c Release
```

The built exe lands at:

```
src/ChllSeeder.App/bin/x64/Release/net9.0-windows10.0.22621.0/win-x64/CHLLSeeder.exe
```

(Swap `Release` → `Debug` in both the command and the path for a Debug build.)

## Build + run against a backend (easiest)

`scripts/run-local.ps1` builds if needed, sets `CHLL_SEEDER_API_URL` for that
process only (no global env changes), then launches the exe.

```powershell
# rebuild, then run against localhost:3000
./scripts/run-local.ps1 -Build

# run against a LAN / WSL backend (use the IP, not a .home.arpa name —
# VPN breaks home.arpa DNS)
./scripts/run-local.ps1 -ApiUrl http://192.168.1.194:3000
```

Options:

- `-Configuration Debug` — build/launch the Debug config (default: Release)
- `-Build` — force a rebuild first (also auto-builds if the exe is missing)
- `-ApiUrl <url>` — backend base URL; falls back to `$env:CHLL_SEEDER_API_URL`,
  then `http://localhost:3000`

## Mock backend (no real server needed)

`scripts/run-mock.ps1` runs a standalone, scriptable mock of the seeding API
(full REST + SSE contract, controllable state) for testing the client without the
real backend or the game — force a server switch, demo Seed All rotation, inject
auth/rate-limit errors, simulate offline/passworded servers.

```powershell
./scripts/run-mock.ps1                                   # http://localhost:3000
# then, in another shell:
./scripts/run-local.ps1 -ApiUrl http://localhost:3000
```

See `tools/ChllSeeder.MockApi/README.md` for the `/__mock/...` control plane and
scenario presets.

## Tests

```powershell
dotnet test src/ChllSeeder.Core.Tests
```

## Where things go

- **Logs:** `%LOCALAPPDATA%\CHLLSeeder\logs`
- **Solution:** `src/ChllSeeder.sln` (App, Core, Core.Tests)
- **Deep-link protocol:** `chllseeder://` (OAuth callbacks)
