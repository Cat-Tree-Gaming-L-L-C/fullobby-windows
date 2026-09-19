# Fullobby — admin guide

For **Org Admins**: you run your org on Fullobby. Your servers, your
staff, your Discord, your schedule — configured by you, without asking anyone.

Read [USER-GUIDE.md](USER-GUIDE.md) for install and sign-in, and
[OPERATOR-GUIDE.md](OPERATOR-GUIDE.md) for the ready-check job your operators do.

## The model, briefly

Three levels, and knowing which you're touching saves a lot of confusion:

- A **server** is one game server, administered by exactly one org.
- A **org** is you: your servers, your staff, your Discord.
- A **network** is an alliance running **one rotation** over its member
  orgs' servers, on its own schedule. It's the thing a customer buys. Your
  org belongs to at most one at a time.

Permissions are a **level** (operator / admin) × a **scope** (org, network,
global). You're `admin` scoped to your org.

Network authority reaches no member org's servers, and org authority
reaches no network setting. That's enforced, not a convention.

> **Everything resolves through your Discord identity.** Grants attach to a
> Discord user id — true even in a deployment that never installs the bot,
> because it's an identity, not an infrastructure dependency. Sign in with
> Discord, or link it from the account page and your permissions come with it.

## Your two surfaces

Neither requires the other.

### The panel — [api.fullobby.com/admin](https://api.fullobby.com/admin)

Sign in with Discord. As an Org Admin you get two tabs:

- **Hub** — today's rotation, the current target, the leaderboard, and any ready
  checks with the button to answer them
- **Servers** — your server list: thresholds, seed windows, enable/disable,
  CRCON keys

The other tabs (Networks, Orgs, Grants, Discord, Client versions) are
Global-Admin-only and stay hidden.

The desktop app's **Manage** tab opens this same panel in-app, so you don't need
a browser. Your operators get it too — it's gated on being able to reach *any*
panel surface, not on being an admin.

### The DM console — `/config`

Run `/config` in your own Discord and the bot opens a DM session bound to your
org. Plain text, one topic per command:

```
status   servers   channels   perms   network   guild   slash   leaderboard
```

`help <topic>` explains any of them. This is where org-level configuration
lives — your Discord link, hub channel and staff.

## Procedures

### Servers

Panel → **Servers**, or the DM console's `servers`.

- **Threshold** — the player count at which a server counts as seeded. Below it
  the server is a candidate; above it, done for the cycle.
- **Enable / disable** — takes a server in or out of the rotation immediately.
  Operator-level: your operators can do this without you.
- **CRCON key** — per-server, stored encrypted, for authenticated stats. Admin.
- **Seed window** — see below. Also Operator-level.

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

These are durable server-side grants and the only way someone gets access —
Discord roles confer nothing. You need Org Admin, or Manage Server in
your Discord.

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
the operators who hold grants in your org — a Discord-only nudge.

### Moving to a different Discord

DM console → `guild set <server id>`. Invite the bot to the target first; you
need Manage Server in both. Slash registration and channel slots move with you,
and so do grants (they attach to people, not to the Discord).

### Joining or leaving a network

DM console → `network`.

```
network                              show your membership
network join <name> <code>           redeem a join code
network leave                        detach — your servers stop rotating
```

Membership is a handshake, not a lock-in: a network admin issues a code,
you redeem it, and joining another network moves you. Leaving is yours to do.

## Network-level settings

These sit above your org, on the network itself. Network authority is its
**own scope**, granted explicitly (a Global Admin grants it from the panel, or
`/perms grant @user admin network:<name>` in the network's Discord) — no org
confers it, including one whose servers fill the rotation or one that hosts the
network's Discord. If you hold Network Admin you drive these from Discord with
`/network ...`; otherwise they belong to whoever does.

- The network's join code — `/network code rotate` to set or replace it,
  `/network code show` for its standing, `/network code disable` to close joining
- Pausing or resuming the network
- The network's seeding hours and daily reset hour
- Rotation order across member orgs
- Moving a server to a different org

### Reading a join code

`/network code show <network>` prints the current code, along with whether
joining is open and when it was last rotated. Reading it takes Operator or Admin
**on the network, or in any org that has joined it** — deliberately wider
than rotating, because the people onboarding players are usually member
orgs' staff rather than network staff.

Rotating stays Admin-only, and still retires the code in circulation. So reach
for `show` when someone has simply lost the code, and `rotate` only when it has
actually leaked or you want to close the door on whoever has it.

Codes set before this landed have no readable copy and say so; rotating once
issues one that can be read back afterwards.

## Not yours

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

**An operator can't see the Manage tab.** They should — operators get it, with
Enable/Disable and Window on each server they operate. If it's missing, they're
on the wrong account (see the permissions note above) or hold no grant in a
org that owns servers.
