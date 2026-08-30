# Fullobby — admin guide

For **Community Admins**: you run your community on Fullobby. Your servers, your
staff, your Discord, your schedule — configured by you, without asking anyone.

Read [USER-GUIDE.md](USER-GUIDE.md) for install and sign-in, and
[OPERATOR-GUIDE.md](OPERATOR-GUIDE.md) for the ready-check job your operators do.

## The model, briefly

Three levels, and knowing which you're touching saves a lot of confusion:

- A **server** is one game server, administered by exactly one community.
- A **community** is you: your servers, your staff, your Discord.
- A **network** is an alliance running **one rotation** over its member
  communities' servers, on its own schedule. It's the thing a customer buys. Your
  community belongs to at most one at a time.

Permissions are a **level** (operator / admin) × a **scope** (community, network,
global). You're `admin` scoped to your community.

Network authority reaches no member community's servers, and community authority
reaches no network setting. That's enforced, not a convention.

> **Everything resolves through your Discord identity.** Grants attach to a
> Discord user id — true even in a deployment that never installs the bot,
> because it's an identity, not an infrastructure dependency. Sign in with
> Discord, or link it from the account page and your permissions come with it.

## Your two surfaces

Neither requires the other.

### The panel — [api.fullobby.com/admin](https://api.fullobby.com/admin)

Sign in with Discord. As a Community Admin you get two tabs:

- **Hub** — today's rotation, the current target, the leaderboard, and any ready
  checks with the button to answer them
- **Servers** — your server list: thresholds, seed windows, enable/disable,
  CRCON keys

The other tabs (Networks, Communities, Grants, Discord, Client versions) are
Global-Admin-only and stay hidden.

The desktop app's **Admin** tab opens this same panel in-app, so you don't need
a browser. It appears for admins; operators use the browser panel.

### The DM console — `/config`

Run `/config` in your own Discord and the bot opens a DM session bound to your
community. Plain text, one topic per command:

```
status   servers   channels   perms   roles   network   guild   slash   leaderboard
```

`help <topic>` explains any of them. This is where community-level configuration
lives — your Discord link, hub channel, staff and role mappings.

## Procedures

### Servers

Panel → **Servers**, or the DM console's `servers`.

- **Threshold** — the player count at which a server counts as seeded. Below it
  the server is a candidate; above it, done for the cycle.
- **Enable / disable** — takes a server in or out of the rotation immediately.
- **CRCON key** — per-server, stored encrypted, for authenticated stats.
- **Seed window** — see below.

### Seed windows, and what they switch on

A seed window is a daily range you want a server filled by:
`HH:MM-HH:MM` in UTC, or append an offset to type local time
(`18:00-23:00 -5`).

**Optional, and not required for seeding.** A server with no window is always
eligible and seeds whenever it drops below its threshold, inside the network's
hours. A window doesn't switch seeding on — it constrains it.

What a window *does* switch on is **ready checks**. Once a server has one,
Fullobby opens a check 30 minutes before it and someone must confirm; unconfirmed
by the window opening, that server **loses its place in the day's rotation**. Set
windows only when you have people who'll answer — see the
[operator guide](OPERATOR-GUIDE.md).

Note the network also has its own hours, which are not yours to set. Outside them
nothing rotates regardless of per-server windows.

### Staff

DM console → `perms`.

```
perms                        list your controllers
perms grant @user operator   day-to-day: ready checks, toggles, windows
perms grant @user admin      everything in this guide
perms revoke @user operator
```

These are durable server-side grants — role sync never touches them. You need
Community Admin, or Manage Server in your Discord.

Prefer managing staff by Discord role? `roles` maps a role to a level and the bot
keeps grants in step with your role assignments. Both approaches coexist: a
manual grant sits alongside a synced one.

The panel's Grants tab is Global-Admin-only, so the DM console is your path here.

### The hub channel

DM console → `channels`.

```
channels                  show the current hub
channels set #seeding     register it (the bot renames it and posts the messages)
channels name <name>      rename it
channels ping 10          nudge an unanswered ready check after 10 minutes
channels webhook <url>    mirror the hub's embeds into another Discord
channels clear            stop using it
```

One channel carries today's rotation, the current target, the leaderboard and the
ready-check widget — posted once and edited in place on a five-minute refresh.

**Optional.** The panel's Hub tab shows the same information, and answering ready
checks doesn't need Discord. The exception is `channels ping`, which mentions
your operator roles — only Discord can mention a Discord role.

### Moving to a different Discord

DM console → `guild set <server id>`. Invite the bot to the target first; you
need Manage Server in both. Slash registration and channel slots move with you.
**Role mappings don't** — role ids don't survive the move, so re-map them after.

### Joining or leaving a network

DM console → `network`.

```
network                              show your membership
network join <name> <code>           redeem a join code
network leave                        detach — your servers stop rotating
```

Membership is a handshake, not a lock-in: the network's operator issues a code,
you redeem it, and joining another network moves you. Leaving is yours to do.

## Not yours

These belong to whoever operates the network. For the PF network that's Cat Tree
Gaming:

- The network's join code — rotating or disabling it
- Pausing or resuming the network
- The network's seeding hours and daily reset hour
- Rotation order across member communities
- Moving a server to a different community
- Global client-version gating and platform config

## Troubleshooting

**Someone says they have no permissions.** Nearly always a second account — they
signed in with Steam, and the grant is on their Discord. They link Discord from
the account page and it follows; if that Discord already had an account, the two
combine. Check `perms` lists them before assuming a grant problem.

**A server never becomes the target.** Enabled? Sitting on an unanswered ready
check? The Hub's rotation board shows a per-server status.

**The hub channel stopped updating.** The Hub's standing card reports delivery
health — a deleted channel or a revoked bot permission shows there, which is
invisible from inside Discord itself.

**Everything went quiet overnight.** A network pauses itself if its public lead
invite stops resolving. That's a network-level fix.

**An operator can't toggle a server from the panel.** Correct today: toggling and
seed windows are operator-level through Discord, but the panel's Servers tab and
its endpoints require Admin. Either you make the change, or they use the slash
command.
