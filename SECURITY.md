# Security Policy

## Reporting a vulnerability

Please report security issues **privately** — do not open a public GitHub issue
for anything exploitable.

- **Email:** privacy@fullobby.com
- **Discord:** the Fullobby Discord (DM an admin)

Include enough detail to reproduce (affected component, steps, and impact). We aim
to acknowledge within a few days and will coordinate a fix and disclosure timeline
with you. This is a community project with no paid bug bounty, but we credit
reporters who want it.

## Supported versions

Only the latest released version of the desktop client is supported. Fixes ship in
new releases off `main`; there are no long-term support branches. The in-app updater
can enforce a minimum client version, so keeping current is expected.

## Threat model

This client is open source, and **publishing it discloses no secret that protects
the system.** Security comes from server-side verification, not from the client
being closed or trusted.

**The client is fully untrusted by the API.** All seeding credit (verified time,
the leaderboard) is granted only when a player's *linked* platform ID is observed in
the real server roster, which the API confirms itself. Editing and running a modified
client gains an attacker nothing they could not do by hand. The server-side
verification model is maintained separately; report API/server security
concerns to the contact below.

What the client itself is responsible for:

- **Credential storage at rest.** Auth tokens and API keys are encrypted with
  Windows DPAPI, scoped to the current Windows user account and mixed with an
  app-specific entropy value, and stored in
  `%APPDATA%\com.fullobby.app\config.json`. No other user on the machine can
  decrypt them, and the entropy binds the ciphertext to this application so another
  process running as the same user cannot decrypt our secrets by replaying the blob to
  DPAPI. The entropy ships in the binary — it is a domain separator, not a secret key,
  so it does not defend against a same-user attacker who reads it out of the executable
  (that attacker is out of scope; see below).
  - **Plaintext fallback.** If a DPAPI *encrypt* call fails (a rare, typically
    transient crypto error), the token is written to `config.json` as plaintext rather
    than dropped, and a warning is logged. This trades confidentiality for availability
    so a momentary crypto failure never signs the user out or loses their credential;
    the value is re-encrypted on the next successful save. The file is still protected
    by the current-user config-directory ACLs, so exposure is limited to the same
    Windows account (already out of scope). Decryption failures likewise fall back to
    treating the stored value as plaintext, never throwing.
- **Config directory ACLs.** On startup the app strips inherited ACEs from its
  config directory and grants access only to the current user.
- **Atomic writes.** Config and game-settings files are written temp-then-rename to
  avoid corruption on crash or power loss.
- **Update integrity & authenticity.** Every update must carry a valid **ECDSA P-256
  signature over its `(version, sha256)`**, verified against a public key hardbaked into
  the client; the matching private key is held offline and never touches CI or the
  server, so a **compromised update origin cannot push a release it can't sign** (a
  missing/invalid signature is refused). On top of that, update URLs are validated
  against a hardcoded domain allowlist and must use HTTPS; the downloaded installer is
  verified against the signed SHA-256 before being run (and re-verified immediately
  before launch); filenames are sanitized against path traversal and capped at 500 MB.
  (The maintainer-only signing procedure lives in the private `fullobby-api` repo.)
- **Hardbaked API host.** Release builds talk only to the production HTTPS API host;
  the `FULLOBBY_API_URL` override exists only in debug builds.
- **Input validation.** Provider names, Steam/user IDs, config keys, display names,
  server IPs, and deep-link parameters are validated against strict whitelists or
  format rules with length limits and null-byte rejection before use.
- **Process safety.** Before terminating any process, the app verifies the process
  image path is inside the expected Steam game install directory.

## Scope

In scope: the desktop client in this repository (credential handling, update
verification, deep-link parsing, process targeting, input validation).

Out of scope: the closed-source API backend (report those to the same contact), and
issues that require an attacker to already have control of the user's Windows account
(DPAPI and the config ACLs are scoped to that account by design).
