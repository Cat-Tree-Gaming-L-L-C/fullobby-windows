# Fullobby — operator guide

For **Org Operators**: the day-to-day role. You keep your org's
servers moving through the rotation — above all by answering ready checks.

Read [USER-GUIDE.md](USER-GUIDE.md) first for install and sign-in. If you also
add servers or appoint staff, you're an Admin — see
[ADMIN-GUIDE.md](ADMIN-GUIDE.md).

## What you hold

Permissions have a **level** (operator, admin or owner) and a **scope** (an org,
a network, or global). You're `operator` scoped to your org.

That means your org's own business: its servers' day-to-day state, and the
ready checks over them. It does **not** reach another org's servers, and it
does not reach network-level settings — nor does anyone else's authority reach
yours. A network admin cannot answer your ready check and commit you to a seed
you didn't agree to.

> **You were appointed with an invite link.** Your role is on the Fullobby
> account you accepted it with — any sign-in works (Steam, Discord, …). Discord
> roles don't make anyone staff. If you seem to hold nothing, you're probably
> signed in to a different account than the one that accepted.

## Ready checks — the job

A ready check asks a human to confirm a server is worth seeding before its
scheduled window. It matters because it **gates the rotation**: a server whose
check goes unanswered by the window opening **loses its place in the day's
rotation**. Answered late, it goes to the back of the queue.

They only exist for servers that have a **seed window**. A server without one is
always eligible and never produces a check.

Answering a check confirms the server; it does **not** start anyone's game. Seeders'
clients launch on their own auto-seed wakes (which include your server's window
start) or when someone presses Seed, and the rotation hands them your server once
its window is open.

A check opens **30 minutes before** the window. You can answer it three ways, and
any one of them settles it:

1. **The panel** — the app's **Manage** tab, or
   [api.fullobby.com/admin](https://api.fullobby.com/admin) in a browser. Same
   thing either way. Works with no Discord involved, and keeps working when your
   DMs are closed.
2. **A Discord DM** from the bot, with a button (if your account has Discord
   linked).
3. **The hub channel**, if your org registered one — a button per window
   awaiting confirmation.

### Finding them in the panel

Open **Hub**, and pick **your org** in the scope picker — not the network.
Checks belong to the org that operates the server, so they never appear on a
network's hub. If you're looking at the network and see nothing, that's why.

A check you can't action shows without a button: either someone already answered
it, or the window has passed.

## What else you can do

| Task | Where |
|---|---|
| See today's rotation, current target, leaderboard | Panel → Hub |
| Answer ready checks | Panel → Hub, Discord DM, or hub channel |
| Enable / disable one of your servers | Panel → Servers |
| Set or clear a server's seed window | Panel → Servers → Window |
| Read your network's join code, to hand to players | Panel → Hub (your org's) → Network join code |

### Your Servers tab

Open **Manage** in the app (or the panel in a browser) and pick **Servers**. It
lists the servers of the orgs you operate, with **Enable/Disable** and
**Window** on each. Adding, editing, removing and re-tagging servers are Admin,
so those controls aren't shown to you — the page says as much rather than
offering buttons that would be refused.

## What you can't do

Not restrictions on you personally, just the boundary of the operator level:

- Add, edit, remove or reorder servers, change a threshold, set a CRCON key →
  your Org Admin
- Appoint or remove staff → your Org Admin
- Register the hub channel, connect or move the org's Discord → your Org Admin
- *Changing* the network's join code, its seeding hours, pausing it, rotation
  order across orgs → a Network Admin. You can **read** the join code yourself
  (see the table above) — you're the one handing it to players.

## When something looks wrong

**A server never becomes the target.** Check it's enabled and isn't sitting on an
unanswered ready check. The Hub's rotation board shows a status per server:
`done`, `current`, `pending`, `skipped`, `not_ready`, `missed_ready`, `deferred`.

**Nothing is rotating at all.** The network may be outside its seeding hours, or
paused. The Hub's standing card says which, and how long until the window opens.

**You got no DM.** Closed DMs, no Discord linked to your account, or the bot
isn't in a Discord you share. The panel is unaffected — use it. Worth telling
your admin, who can switch on an escalation ping in the hub channel for
unanswered checks.

**You can see the hub but there are no checks, ever.** Your servers probably have
no seed windows. That's a valid setup — it means seeding is opportunistic rather
than scheduled — and it means there is nothing for you to answer.
