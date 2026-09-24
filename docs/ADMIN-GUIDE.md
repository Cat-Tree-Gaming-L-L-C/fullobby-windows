# Fullobby — admin guide

For **Org Admins**: you run your org on Fullobby. Your servers, your
staff, your Discord, your schedule — configured by you, without asking anyone.

Read [USER-GUIDE.md](USER-GUIDE.md) for install and sign-in, and
[OPERATOR-GUIDE.md](OPERATOR-GUIDE.md) for the ready-check job your operators do.

## The model, briefly

Three levels, and knowing which you're touching saves a lot of confusion:

- A **server** is one game server, administered by exactly one org.
- An **org** is you: your servers, your staff, your Discord.
- A **network** is an alliance running **one rotation** over its member
  orgs' servers, on its own schedule. It's the thing a customer buys. Your
  org belongs to at most one at a time.

Permissions are a **level** — operator, admin, or owner — in a **scope**: an
org, a network, or global. You're `admin` (or `owner`) scoped to your org.

- **Operator** — day-to-day: ready checks, enable/disable, seed windows.
- **Admin** — everything in this guide, and appointing Operators.
- **Owner** — an Admin who can also appoint Admins and other Owners.

Network authority reaches no member org's servers, and org authority
reaches no network setting. That's enforced, not a convention.

> **Discord confers nothing.** Your permissions are grants on your Fullobby
> account, appointed with invite links. Discord roles and Manage Server don't
> make anyone staff. Discord is where Fullobby *shows* things — your hub,
> announcements, the leaderboard — and one way to sign in.

## Where you do it: the panel

[api.fullobby.com/admin](https://api.fullobby.com/admin), or the desktop app's
**Manage** tab, which opens the same panel in-app. As an Org Admin you see:

- **Hub** — today's rotation, the current target, the leaderboard, ready checks
  with the button to answer them, and your hub's Discord mirror settings
- **Servers** — thresholds, seed windows, enable/disable, CRCON keys
- **Staff** — who holds a role in your org, and invite links to appoint more
- **Org settings** — display name, public invite, your Discord server, your
  network membership, partner webhooks

Networks, Grants, Discord and Client versions are for network staff or
Global Admins and stay hidden.

Discord's `/config` still works, but it's read-only now: it DMs you your org's
status and a link back here.

## Procedures

### Servers

Panel → **Servers**.

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

Panel → **Staff**.

To appoint someone, pick a level and set the link up:

- **Label** — who or what it's for. Only staff see it, in the list of open links.
- **Expiry** — 30 minutes up to 7 days. Staff links always expire, 7 days at
  most.
- **Use limit** — how many people can accept it. Staff links always have one,
  and an **Owner** link is single-use.

**Create link**, and send it to them. They open it in a browser, sign in with any
account (Steam, Discord, …), and accept. The link is shown only when you create
it — copy it then. Open links are listed with their uses and expiry, and a
Revoke button.

An Admin can invite and remove Operators. Inviting or removing Admins and Owners
takes an Owner. You can't revoke your own role, and the last Owner can't be
removed — appoint another first.

### The hub channel

Panel → **Hub** → *Discord mirror*.

One channel in your Discord carries today's rotation, the current target, the
leaderboard and the ready-check widget — posted once and edited in place on a
five-minute refresh. Register it with its channel id (the bot must be in your
server — see below), and optionally:

- **Escalation ping** — after this many minutes, an unanswered ready check is
  announced below the widget, mentioning the **role to mention** you set. The
  role only decides who gets pinged; who can answer is decided on the Staff tab.
- **Webhook mirror** — post a copy of the hub into another server's channel,
  e.g. a public one. Needs no bot there.

**Optional.** The panel's Hub tab shows the same information, and answering ready
checks doesn't need Discord.

### Your Discord server

Panel → **Org settings** → *Connect a Discord server*.

This runs Discord's own "Add to server": pick your server, approve the bot, and
you come back to the panel to confirm. Adding a bot needs Manage Server there,
which is how Fullobby knows the server is yours — it confers nothing else. The
same button moves you to a different server; your hub needs registering again in
the new one.

The same card sets whether your server shows Fullobby's display commands
(`/servers list`, `/seeding …`) or only `/config`.

### Joining or leaving a network

Panel → **Org settings** → *Network* shows where you stand.

To join, ask the network's staff for an **org invite link**. A Network Admin
makes it in the panel (**Networks** → *Invite links*). Open it in a browser while
signed in as your org's Admin, pick your org, and accept. Joining another network
moves you out of the one you're in — the page warns you first. **Leave network**
detaches you and your servers stop rotating.

Membership is a handshake, not a lock-in: a network admin issues a link, you
accept it.

### Partner webhooks

Panel → **Org settings** → *Partner webhooks*.

Post your org's announcements or leaderboard into another Discord (a partner's
server). Paste the webhook URL once — it's a secret, so it's never shown back —
then turn it on or off, send a test, or remove it.

## Network-level settings

These sit above your org, on the network itself. Network authority is its
**own scope** — no org confers it, including one whose servers fill the
rotation. Whoever holds Network Admin runs these from the panel's **Networks**
tab:

- The network's invite links (**Networks** → *Invite links*): **Player** links
  to post where seeders are, and **Org** links to send to an org's Admin. Each
  has a label, an expiry (or none) and a use limit (or none), and can be revoked
- The network's join code, the legacy way in: set, rotate or disable it
- Pausing or resuming the network
- The network's seeding hours and daily reset hour
- Rotation order across member orgs
- Moving a server to a different org

### Reading a join code

Join codes are legacy. Players join with an invite link from the network's staff
— ask them for a Player link to hand out. A code is only for the odd network
that still uses one, and for app versions that predate links.

Panel → **Hub** (your org's) → *Network join code (legacy)* → **Show code**. You
don't need to be network staff: Operators and Admins of any member org can read
it. Rotating stays with network admins and retires the code in circulation — so
ask for a rotation only when a code has leaked, not when someone has lost it.

A code set before read-back existed says it can't be shown; a network admin
rotating it once fixes that.

## Not yours

- Global client-version gating and platform config

## Troubleshooting

**Someone says they have no permissions.** Check the Staff tab lists them. If it
does, they're signed in to a different account — a Steam login and a Discord
login are separate accounts until linked from the account page. If it doesn't,
send them an invite.

**Someone had permissions from a Discord role and lost them.** Roles no longer
confer anything. Invite them.

**A server never becomes the target.** Enabled? Sitting on an unanswered ready
check? The Hub's rotation board shows a per-server status.

**The hub channel stopped updating.** The Hub's mirror card reports delivery
health — a deleted channel or a revoked bot permission shows there, which is
invisible from inside Discord itself.

**An operator can't see the Manage tab.** They should — operators get it, with
Enable/Disable and Window on each server they operate. If it's missing, they're
on the wrong account (see above) or hold no role in an org that owns servers.
