# Building & Running the Client (WinUI 3)

The app is an **unpackaged** WinUI 3 desktop client: .NET 9, x64 / `win-x64`,
self-contained Windows App SDK 1.8.x — **no MSIX**. The installer is a separate
Inno Setup step and is not needed to build or run locally.

## Prerequisites

- **.NET 9 SDK** (installed machine-wide on this box)
- Windows App SDK 1.8.x — restored automatically via NuGet on first build
- Windows 11 (build 22000+; the target framework references the 10.0.22621.0 SDK)

## Build

```powershell
dotnet build src/Fullobby.sln -c Release
```

The built exe lands at:

```
src/Fullobby.App/bin/x64/Release/net9.0-windows10.0.22621.0/win-x64/Fullobby.exe
```

(Swap `Release` → `Debug` in both the command and the path for a Debug build.)

## Build + run against a backend (easiest)

`scripts/run-local.ps1` builds if needed, sets `FULLOBBY_API_URL` for that
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
- `-ApiUrl <url>` — backend base URL; falls back to `$env:FULLOBBY_API_URL`,
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

See `tools/Fullobby.MockApi/README.md` for the `/__mock/...` control plane and
scenario presets.

## Tests

```powershell
dotnet test src/Fullobby.Core.Tests
```

## Where things go

- **Logs:** `%LOCALAPPDATA%\Fullobby\logs`
- **Solution:** `src/Fullobby.sln` (App, Core, Core.Tests)
- **Deep-link protocol:** `fullobby://` (OAuth callbacks)

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
SSL.com eSigner secrets are configured: `SSL_COM_USERNAME`, `SSL_COM_PASSWORD`,
`SSL_COM_CREDENTIAL_ID`, and `SSL_COM_TOTP_SECRET` (the automated-2FA OTP seed). The release
workflow cloud-signs the published exe and the installer through SSL.com's eSigner
(`SSLcom/esigner-codesign`, pinned by commit SHA) — the OV cert lives in a cloud HSM, so there is
no `.pfx`. A post-sign step verifies both files carry a valid Authenticode signature before the
release is published. Until the cert is configured, releases are unsigned and trigger a
SmartScreen warning. This is independent from update-feed signing below.

**Update-feed signing** (the manifest `signature`, mandatory) is *not* done by CI — the
release-signing private key is held offline. After CI builds, a maintainer signs the release's
`(version, sha256)` on the offline device, finalizes the manifest, and publishes it. The in-app
updater **refuses** any update whose `signature` is missing or doesn't verify against the client's
hardbaked public key. The maintainer-only runbook + signing scripts live in the **private
`fullobby-api` repo** (`docs/RELEASE-SIGNING.md`), kept out of this public repo so the release
procedure and its attack surface aren't advertised.

### Update-feed contract (static feed)

The in-app updater (`Core.Update.UpdaterService`) fetches a static manifest from the update
feed — `UpdateConfig.FeedBaseUrl`, **`https://updates.fullobby.com`** (a GitHub Pages site,
deliberately *not* the API, so the backend is never in a position to strip the signature).
Stable reads `latest.json`; the beta channel reads `latest-beta.json`. The release workflow
emits a manifest in exactly this shape as a release asset:

```json
{
  "version": "1.2.3",
  "download_url": "https://github.com/Cat-Tree-Gaming-L-L-C/fullobby-windows/releases/download/v1.2.3/Fullobby-Setup-1.2.3.exe",
  "notes": "…annotated tag message…",
  "sha256": "<lowercase hex>",
  "signature": "<base64 ECDSA P-256 signature over (version, sha256)>"
}
```

`version` is the **full SemVer** — for a prerelease it's `1.2.4-beta.2`, not `1.2.4` — so the
beta channel orders correctly (and the signed payload matches). CI emits the manifest with
`signature: null`; a maintainer fills it in offline and **publishes the signed manifest to the
Pages feed** (`latest.json` / `latest-beta.json`), see **Update-feed signing** above.
`download_url` points at the GitHub release asset (`github.com`), which is in the updater's
trusted-domain allowlist (alongside `objects.githubusercontent.com`). A missing `sha256` **or a
missing/invalid `signature`** makes the client **refuse** the update.

> Debug builds can point the updater elsewhere with the `FULLOBBY_UPDATE_FEED_URL`
> environment override (compiled out of Release); handy for testing against a locally-served,
> locally-signed manifest.
