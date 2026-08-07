# Fullobby — update feed (GitHub Pages source)

This folder is the **publishing source** for the static self-update feed served at
**https://updates.fullobby.com** (GitHub Pages, custom domain). It is deployed from the `main`
branch by `.github/workflows/pages.yml` (Actions → Pages). It is deliberately *not* the API:
keeping the feed off the backend means the backend is never in a position to strip the pinned-key
signature the client mandates.

The app's self-updater (`Fullobby.Core.Update.UpdaterService`, `UpdateConfig.FeedBaseUrl`) fetches:

- `latest.json` — **stable** channel
- `latest-beta.json` — **beta** channel

Each is `{ version, download_url, notes, sha256, signature }`. `version` is the **full SemVer**
(e.g. `1.2.4-beta.2`). `signature` is an ECDSA P-256 signature over `(version, sha256)`; the client
**refuses** any manifest with a missing/invalid signature or a missing `sha256`.

## Do not delete
- `CNAME` — binds the Pages site to `updates.fullobby.com`. Removing it drops the custom domain.
- `.nojekyll` — serves files verbatim (no Jekyll processing).

## Publishing a signed manifest (maintainer)
The manifests are **not** committed by CI — the release-signing key is offline. After signing a release
(runbook: private `fullobby-api` repo, `docs/RELEASE-SIGNING.md`), publish the finalized manifest
into this folder on `main`:

```bash
git switch main && git pull
cp /path/to/latest.json feed/          # or latest-beta.json for the beta channel
git add feed/latest.json
git commit -m "feed: publish <version> (stable)"
git push origin main
```

The Pages workflow redeploys within ~1 min (any push touching `feed/**`). Verify:

```bash
curl -fsSL https://updates.fullobby.com/latest.json | jq .
```

Until the first signed manifest is published, these paths 404 — the client treats that as
"no update available" (safe), never as an unverified update.
