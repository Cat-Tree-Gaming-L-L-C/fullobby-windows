# Fullobby — user guide

For players. Install the app, join a seeding network, and help fill your
org's servers.

If you run servers or answer ready checks, you'll also have a **Manage** tab in
the app — see [OPERATOR-GUIDE.md](OPERATOR-GUIDE.md) or
[ADMIN-GUIDE.md](ADMIN-GUIDE.md). This guide covers what everyone needs.

## What you need

- **Windows 10 (version 2004 / build 19041) or Windows 11.**
- **Steam, with Hell Let Loose installed.** The app launches your existing copy.
- **An invite link** from the staff of a network — usually posted in their
  Discord. Fullobby is in limited beta and seeding happens inside networks — the
  app can't finish setup without one.

## Install

Download the latest `Fullobby-Setup-<version>.exe` from
[Releases](https://github.com/Cat-Tree-Gaming-L-L-C/fullobby-windows/releases)
and run it. After this the app updates itself; you won't download an installer
again.

**Windows will warn you.** Releases aren't Authenticode-signed yet, so first
launch shows *"Windows protected your PC"* — click **More info → Run anyway**.

Please don't disable SmartScreen or Defender to get past it. If you want real
assurance, verify the download instead — it's stronger than a publisher
signature. See [Verify your download](../README.md#verify-your-download-optional):
the update manifest carries the installer's SHA-256, signed with an offline key
the app pins.

## First run

### 1. Sign in

**Steam** or **Discord**, or **Continue as Guest**. All three seed.

- **Guest** is fine for trying it out. It's anonymous, gets a random display
  name, and is cleaned up after 14 days of inactivity. Signing in with a real
  provider later upgrades it in place.
- **If you've been invited as staff** — operator, admin or owner — sign in with
  the account you accepted the invite on. Roles belong to that Fullobby account,
  whichever provider it uses; Discord roles don't make anyone staff.
- Epic and Xbox appear as options but aren't switched on yet.

### 2. Join a network

Paste the **invite link** you were given and press **Check link**. The app
shows which network it's for — *"Join Comp HLL?"* — and **Join** puts you in.

Paste the whole link, `https://…/invite#…`; it's too long to retype. Opening
the link in a browser works too: sign in there with the same account you use in
the app, accept, and the app picks it up.

If the app says the link is for org staff, it's an org or staff invite, not a
player one — open it in a browser instead, or ask for a player link.

**Given a network name and join code instead?** Some networks still hand those
out. Open **Have a join code instead?** under the link box and enter both.
Capitalisation doesn't matter and surrounding spaces are trimmed, but the dashes
in the code are part of it — paste rather than retype.

This is required: until you're in a network, the app has nothing to seed and
won't finish onboarding. Joining more than one is fine — **Settings → Seeding
Networks → Join a network** takes another link — and you can reorder them by
priority later.

### 3. Link your other account

Link whichever of Steam/Discord you didn't sign in with.

Your seeding time is credited by matching the player on the server to a linked
Steam account, so an unlinked account seeds without ever appearing on the
leaderboard.

If the account you're linking already has its own Fullobby account, linking
**combines** them — history, scores, network memberships and permissions move
across, and the confirmation page tells you exactly what will move before you
agree.

## Seeding day to day

Leave the app running. It tells you which server needs players right now and
launches Hell Let Loose straight into it.

While you're seeding it sends a heartbeat so the server knows you're there. When
the server crosses its target the app either moves you to the next one in the
rotation or stops, and you get a warning before a switch so you can snooze or
stop first.

**Networks run on a schedule.** Outside its seeding hours a network doesn't
rotate at all and the app will say so rather than sending you somewhere. "Nothing
to seed" usually means either you're outside those hours, or every server is
already above its target — both are the system working.

### Auto-seed (optional)

The app can wake your machine and start seeding at the right time. The wake times
come from your networks' seeding hours, so a network that seeds 12:00–16:00 UTC
produces a wake at 12:00 UTC, and from any seed windows set on individual servers,
so a server whose window opens at 14:30 UTC produces a wake then too. If you're
in several networks, one wake can cover more than one.

Waking early is normal: if the app comes up and every remaining server is still
waiting for its own window, it says "Nothing to seed yet — the next server window
opens at HH:MM", re-arms, and lets the machine sleep until then.

If you have both Hell Let Loose and Hell Let Loose: Vietnam installed, a wake
seeds whichever game your networks need most at that moment, not necessarily
the one whose window caused the wake. The Seed button shows which game it will
start underneath, for example "(Hell Let Loose: Vietnam)". Wakes only come from
servers in games you have installed.

If a game is already open when a seed starts, Fullobby never closes it without
asking. Pressing Seed asks "Swap games?" and closes the open game only if you say
yes. When an auto-seed wake finds a game open, the "Seed now?" prompt says what
Seed Now will close. If nobody answers, your game is left running and a
notification says which game needs seeding.

If you're playing one of the games when the other one needs seeding, Fullobby
sends a notification ("Hell Let Loose: Vietnam needs seeding") with Swap and
Not now buttons, and shows the same offer on the Seed page. Swap closes your
game and starts seeding the other one. Not now keeps it quiet for two hours.
Otherwise it won't ask again for 30 minutes.

Other Unreal Engine games (Wardogs and the like) get the same treatment.
Two Unreal games running at once usually stops the second one from launching,
so before seeding or launching, Fullobby checks for one:

- **Pressing Seed or Launch:** it asks "Close Wardogs?". You can pick Close,
  Continue anyway, or Cancel. Continue anyway is there in case it guessed
  wrong, for example an Unreal Editor window.
- **Auto-seed wake:** the "Seed now?" prompt names the game, and only answering
  Seed Now closes it.
- **While you're playing one:** the same nudge as above, with a Close and seed
  button.

Fullobby never closes another game without you saying so.

If the machine slept through a wake, a watchdog can still fire the seed for a
while afterwards.

## Your stats

Time is credited only while you're confirmed on the server that's actually being
seeded. You can see your own totals and streaks in the app, and there's a
leaderboard per network and org.

Don't want to appear on it? There's a **leaderboard opt-out** on the account
page. It hides you from the board; your seeding still counts for the servers.

## Updating

The app checks for new releases and can install them for you. Every update is
verified against a signed manifest before it runs — see
[Update security](../README.md#desktop-client).

## If something's wrong

| What you see | What's happening |
|---|---|
| "This invite is no longer valid — ask for a new one" | The link expired, was used up or was revoked. The message is the same for all three on purpose. Ask the network's staff for a fresh link. |
| "This link is for org staff, not players" | It's an org or staff invite. Open it in a browser, signed in — or ask for a player link. |
| "Invalid network or join code" | Join codes only. The message is deliberately vague so codes can't be guessed at. Check the name, re-paste the code with its dashes. Still failing? Ask for an invite link instead. |
| Nothing to seed | Outside the network's hours, or every server is already full. The app says which. |
| Signed in but nothing works / no permissions | You're probably on a different account than the one holding your access — see below. |
| Installer won't run | Windows 10 build 19041 (version 2004) or newer is required. Check Winver. |
| SmartScreen warning | Expected. **More info → Run anyway**; don't disable Defender. |

### "I had permissions and now I don't"

Almost always two accounts. Signing in with Steam and signing in with Discord
create separate accounts, and your permissions live on the Discord one.

Fix it by linking Discord from the account page — the permissions come with it,
and if that Discord already had its own account the two are combined. You don't
lose anything either way.

## What the app does and doesn't do

Worth knowing what you're running. The README covers it precisely:

- [How the app interacts with Steam and HLL](../README.md#how-the-app-interacts-with-steam-and-hell-let-loose)
  — launching, process detection, and how it only ever terminates a game process
  inside your Steam install directory
- [What the app does NOT do](../README.md#what-the-app-does-not-do)
- [Data sent to the API](../README.md#desktop-client) — session ids, the server
  you're on, your Steam id if linked, heartbeats. No hardware fingerprints, no
  installed-software lists, no file contents.
