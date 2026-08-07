# Privacy Policy — Fullobby

Last updated: 2026-07-01

## Overview

Fullobby collects minimal, non-personally-identifiable analytics to improve the seeding experience. All data collection is transparent and documented here. Users may opt out of the leaderboard at any time.

## What We Collect

### Account Data

- **Username / Display Name** — chosen by registered users or randomly generated for guest accounts.
- **Auth Provider** — which sign-in method was used (Steam, Discord, or Guest API key).
- **Steam ID** — only if you link a Steam account, used for verifying in-game presence during seeding sessions.
- **Discord ID** — only if you sign in via Discord, used for account identity.

### Session Analytics (collected at seeding session start)

| Field | Example | Purpose |
|---|---|---|
| `client_version` | `1.2.0` | Track adoption of new releases; identify version-specific issues |
| `os_version` | `10.0.22631` | Understand OS distribution; prioritize compatibility work |
| `os_arch` | `x86_64` | Know which architectures to support and test |
| `efficiency_mode` | `true/false` | Measure adoption of the efficiency mode feature |
| `auto_seed` | `true/false` | Measure adoption of scheduled auto-seed |

### Session Lifecycle Data

| Field | Purpose |
|---|---|
| `started_at` / `ended_at` | Calculate session duration for leaderboard |
| `heartbeat_count` | Verify session liveness |
| `verified_secs` | Track time the player was confirmed on the server |
| `end_reason` | Understand why sessions end (normalized to: `user_stopped`, `server_seeded`, `server_rotation`, `game_closed`, `app_exit`, `timeout`, `error`, `update`, `other`) |

### Server Rotation Reasons

When the client requests a new server (e.g., because the current one filled up), we record:
- Which server the user moved from/to
- The reason for the rotation (same normalized set as end reasons)

This helps us understand server rotation patterns and improve the candidate selection algorithm.

## What We Do NOT Collect

- IP addresses (not stored in application logs or database)
- Hardware specifications beyond OS version and architecture
- Game activity outside of seeding sessions
- Browsing or app usage patterns
- Any data from other applications on your system
- Location data (region preference is a user setting, not detected)

## How We Use This Data

1. **Leaderboard** — session duration and verified time power the public seeding leaderboard. Opted-out users are excluded.
2. **Version adoption** — client version data tells us when it's safe to deprecate old versions and whether forced-update thresholds are reasonable.
3. **Feature prioritization** — feature flags (efficiency mode, auto-seed) help us understand which features are actually used so we can focus development effort.
4. **OS compatibility** — OS version and architecture data inform which platforms to test and support.
5. **Session health** — end reasons and rotation patterns help us identify and fix issues in the seeding flow (e.g., if sessions are ending in errors disproportionately).

## Data Retention

- Active session data is retained indefinitely for leaderboard purposes.
- Server rotation reason records are retained for 90 days.
- Guest accounts inactive for 90 days are automatically deleted along with all associated data.

## Opting Out

- **Leaderboard** — toggle "Show on Leaderboard" off in Settings or during onboarding. Your sessions will still be tracked for your personal stats but will not appear on the public leaderboard.
- **Analytics** — session analytics (OS version, feature flags) are collected alongside session tracking. Since these fields contain no personally identifiable information and are essential for maintaining software quality, they are collected for all active sessions. The data cannot be linked to your real identity.

## Data Access

Analytics data is only accessible to Fullobby administrators for the purposes described above. It is never sold, shared with third parties, or used for advertising.

## Contact

Questions about this policy can be directed to the Fullobby Discord, or by
email at privacy@fullobby.com.
