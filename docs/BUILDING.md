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

# run against a LAN / WSL backend (use the backend's IP address)
./scripts/run-local.ps1 -ApiUrl http://<your-lan-ip>:3000
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
matches the release. Two forms are derived from it:

- The **numeric** `X.Y.Z` (prerelease suffix stripped) is the `AssemblyVersion`, which
  `Assembly.GetName().Version.ToString(3)` reports; it's sent as the `x-client-version`
  header (the API version-gate parses `X.Y.Z` only).
- The **full** SemVer including any `-beta.N` suffix is the `AssemblyInformationalVersion`.
  The self-updater compares on this (via `AppVersion.ForUpdateCheck`), because the manifest's
  `version` field carries the full tag (`v1.2.4-beta.2` → `1.2.4-beta.2`). Comparing on the
  numeric core alone would make the next beta look like a downgrade — a bare `1.2.4` outranks
  `1.2.4-beta.*` by SemVer precedence — so beta→beta updates would never be offered.

**Authenticode code signing** (SmartScreen/publisher trust) is optional and runs only when the
repo secrets `WINDOWS_CERT_BASE64` (base64 of the `.pfx`) and `WINDOWS_CERT_PASSWORD` are set;
`scripts/sign.ps1` signs the published exe and the installer via `signtool`. Until a cert is
configured, releases trigger a SmartScreen warning. This is independent from update-feed signing
below.

**Update-feed signing** (the manifest `signature`, mandatory) is *not* done by CI — the
release-signing private key is held offline. After CI builds, a maintainer signs the release's
`(version, sha256)` on the offline device, finalizes the manifest, and publishes it. The in-app
updater **refuses** any update whose `signature` is missing or doesn't verify against the client's
hardbaked public key. The maintainer-only runbook + signing scripts live in the **private
`chll-seeding-api` repo** (`docs/RELEASE-SIGNING.md`), kept out of this public repo so the release
procedure and its attack surface aren't advertised.

### Update-feed contract (backend)

The in-app updater (`Core.Update.UpdaterService`) reads `GET /api/releases/latest`
(`?channel=beta` for the beta channel). The release workflow emits a manifest in exactly
that shape as a release asset — `latest.json` (stable) / `latest-beta.json` (prerelease):

```json
{
  "version": "1.2.3",
  "download_url": "https://github.com/Cat-Tree-Gaming-L-L-C/chll-seeding-windows/releases/download/v1.2.3/CHLL-Seeding-Setup-1.2.3.exe",
  "notes": "…annotated tag message…",
  "sha256": "<lowercase hex>",
  "signature": "<base64 ECDSA P-256 signature over (version, sha256)>"
}
```

CI emits this with `signature: null`; a maintainer fills it in offline before publishing
(see **Update-feed signing** above). The backend should serve
the latest stable manifest at `/api/releases/latest` and the latest prerelease at
`/api/releases/latest?channel=beta`. `download_url` points at the GitHub release asset
(`github.com`), which is in the updater's trusted-domain allowlist (alongside
`objects.githubusercontent.com` and the configured API host). A missing `sha256` **or a
missing/invalid `signature`** makes the client **refuse** the update.
