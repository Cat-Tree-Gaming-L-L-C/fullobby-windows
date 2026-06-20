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
dotnet build src/ChllSeeding.sln -c Release
```

The built exe lands at:

```
src/ChllSeeding.App/bin/x64/Release/net9.0-windows10.0.22621.0/win-x64/CHLLSeeding.exe
```

(Swap `Release` → `Debug` in both the command and the path for a Debug build.)

## Build + run against a backend (easiest)

`scripts/run-local.ps1` builds if needed, sets `CHLL_SEEDING_API_URL` for that
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
- `-ApiUrl <url>` — backend base URL; falls back to `$env:CHLL_SEEDING_API_URL`,
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

See `tools/ChllSeeding.MockApi/README.md` for the `/__mock/...` control plane and
scenario presets.

## Tests

```powershell
dotnet test src/ChllSeeding.Core.Tests
```

## Where things go

- **Logs:** `%LOCALAPPDATA%\CHLLSeeding\logs`
- **Solution:** `src/ChllSeeding.sln` (App, Core, Core.Tests)
- **Deep-link protocol:** `chllseeding://` (OAuth callbacks)

## Releasing

Releases are tag-driven (`.github/workflows/release.yml`). Push a semver tag and the
workflow builds + tests, publishes the self-contained app, builds the Inno installer,
optionally code-signs it, computes the SHA-256, and creates a GitHub Release with the
installer, its `.sha256`, and an update manifest.

```powershell
# stable
git tag v1.2.3 -m "1.2.3 — <highlights>"
git push origin v1.2.3

# beta / prerelease (any tag with a -suffix → GitHub prerelease + latest-beta.json)
git tag v1.2.3-beta.1 -m "1.2.3-beta.1"
git push origin v1.2.3-beta.1
```

The tag version is injected into the build (`-p:Version`) so the app's runtime version
matches the release. The numeric `X.Y.Z` (prerelease suffix stripped) is what
`Assembly.GetName().Version.ToString(3)` reports and what the updater compares against —
so the manifest's `version` field uses the numeric form.

**Code signing** is optional and runs only when the repo secrets `WINDOWS_CERT_BASE64`
(base64 of the `.pfx`) and `WINDOWS_CERT_PASSWORD` are set; `scripts/sign.ps1` signs the
published exe and the installer via `signtool`. Until a cert is configured, releases are
unsigned (SmartScreen will warn) and the manifest `signature` is `null`.

### Update-feed contract (backend)

The in-app updater (`Core.Update.UpdaterService`) reads `GET /api/releases/latest`
(`?channel=beta` for the beta channel). The release workflow emits a manifest in exactly
that shape as a release asset — `latest.json` (stable) / `latest-beta.json` (prerelease):

```json
{
  "version": "1.2.3",
  "download_url": "https://github.com/catalloc/chll-seeding-windows/releases/download/v1.2.3/CHLL-Seeding-Setup-1.2.3.exe",
  "notes": "…annotated tag message…",
  "sha256": "<lowercase hex>",
  "signature": null
}
```

The backend should serve the latest stable manifest at `/api/releases/latest` and the
latest prerelease at `/api/releases/latest?channel=beta`. `download_url` points at the
GitHub release asset (`github.com`), which is in the updater's trusted-domain allowlist
(alongside `objects.githubusercontent.com` and the configured API host). A missing
`sha256` makes the client **refuse** the download.
