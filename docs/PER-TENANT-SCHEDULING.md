# Per-tenant scheduling — design (draft)

Fullobby is multi-tenant: networks are independent operators, and a user may
belong to any number of them. The seeding **schedule**, though, is still
single-tenant on the wire — one flat window list for the whole fleet. This
document specifies what has to change so that networks and users stay
independent, and what the client can build once it has.

Status: **client side implemented (0.3.0), shipping dormant**. Window exposure
and directive scoping remain backend work (`fullobby-api`); the client's wake
scheduling and reconciliation are built and ship with 0.3.0, inert until the
backend starts sending the per-network fields — until then the client behaves
exactly as before (one server-derived wake). See "As built (client, 0.3.0)"
below for where the implementation deliberately deviates from this draft. The
Settings time picker (user-chosen wake times) is still future work.

## The problem

`GET /api/seeding/config` returns one `SeedingConfig` for everyone:

```jsonc
{
  "active_windows": [ { "start_min": 360, "end_min": 540 } ],  // fleet-wide
  "daily_reset_hour_utc": 10,                                   // fleet-wide
  "missed_autoseed_window_hours": 4                             // fleet-wide
}
```

`TimeWindow` is minutes-of-day (0–1439), `start > end` wraps midnight. The
field is documented as "Daily UTC active windows during which seeding runs" —
singular, for the whole service.

Two independent networks that seed at different hours have **no representable
answer** in that field. Whatever the server puts there is wrong for at least
one of them, and the client cannot tell whose schedule it is honouring. Three
concrete failures:

1. **Wake time.** `AutoSeedService.WakeTimeUtc` takes `windows.Min(w =>
   w.StartMin)` — the earliest start across the fleet. A user in a 22:00
   network and a 06:00 network gets woken at 06:00 for both, and there is no
   way to offer them a choice, because the client cannot see that there are
   two schedules.
2. **Orphan detection.** The rule we want is "drop a wake when its window no
   longer exists". With one global list, one tenant moving its window looks
   identical to *the* window moving, so the client would delete a wake another
   tenant still needs.
3. **Overlap.** Two networks sharing 06:00–09:00 should wake the machine once,
   not twice. Collapsing requires knowing the two windows are distinct
   tenants' and that they coincide.

`NetworkSeedingStatus` cannot stand in. It is per-network and already carries
`active` and `next_active_in_secs`, but those are **derived state for right
now**, not boundaries — enough to say "this network is open", not enough to
draw a picker or decide whether a 22:00 wake is still justified.

## Why this is closer than it looks

The backend already computes all of this per network; it just doesn't say so.

- `NetworkSeedingStatus.active` / `next_active_in_secs` are **per-network**
  window evaluations. The server necessarily knows each tenant's boundaries to
  produce them.
- `SeedingDirective.scheduled_pause` + `next_active_in_secs` are the server
  resolving the multi-tenant schedule question for the current instant, on the
  client's behalf. The decision already exists server-side.
- Attribution is largely solved: `DirectiveTarget` carries `db_id`/`index`, so
  the server can already resolve a seeded server to its owning network without
  the client naming one. `start-session` posting only `game` + `index` is
  therefore not the attribution hole it first appears to be.

So the schedule half is an **exposure** change, not a modelling one.

## Wire format

### 1. Windows per network (unblocks all scheduling work)

Add the boundaries to the existing per-network carrier, `NetworkSeedingStatus`,
which the client already fetches via `GET /api/seeding/status`:

```jsonc
{
  "networks": [
    {
      "network_id": 12,
      "network_tag": "example",
      "active": true,
      "next_active_in_secs": null,
      "active_windows": [ { "start_min": 360, "end_min": 540 } ],  // NEW
      "daily_reset_hour_utc": 10,                                  // NEW
      "missed_autoseed_window_hours": 4                            // NEW
    }
  ]
}
```

Semantics:

- `active_windows` empty ⇒ that network is always active (matches the current
  fleet-wide meaning).
- The three new fields are **authoritative per network**. The identically named
  fields on `SeedingConfig` are demoted to *fleet defaults*, used only for a
  client with no memberships, and for a network that omits them.
- No breaking change: existing fields keep their names and meanings, so an old
  client keeps working on the fleet defaults.

### 2. Network-scoped directive (independence and honest labelling)

`GET /api/seeding/directive` currently takes `game`, `current_index`,
`session_id` — no network. The server picks the target from the user's priority
order (`SetNetworkPrioritiesAsync`), so a wake motivated by one network can
seed another's server.

Add an optional parameter and echo the resolution back:

```
GET /api/seeding/directive?game=hll&network_id=12
```

```jsonc
{
  "action": "seed",
  "target": { "game": "hll", "index": 3, "db_id": 91, "network_id": 12 }  // NEW
}
```

- `network_id` omitted ⇒ **current behaviour exactly** (priority order across
  all memberships). This must stay the default; see "one wake, many tenants".
- `network_id` present ⇒ restrict target selection to that network's servers.
  `scheduled_pause` then reflects *that* network's window rather than the
  aggregate.
- `target.network_id` on the response is what lets the client name the network
  it is actually seeding, in the UI and in the local activity log.

This change is **independently useful and separately shippable** from (1). It
is not required for scheduling — see below.

## Client consequences

### The two changes do different jobs

Worth stating plainly, because it decides shipping order:

- **Windows per network answers "when should this machine be awake?"** That is
  the whole scheduling feature: the picker, the wake set, orphan detection,
  overlap collapsing. It needs (1) and nothing else.
- **Network-scoped directive answers "who am I seeding for?"** That is
  attribution and labelling. It needs (2).

A wake is not owned by a tenant. It is "be awake at 06:00", justified by one or
more networks whose windows cover it. What gets seeded once awake is settled at
seed time by priority order, as it is today.

### One wake, many tenants

Consequently a wake **omits** `network_id` when more than one network justifies
it, and lets priority order decide — waking once and seeding the highest
-priority open network is the correct behaviour, not a compromise. A wake
justified by exactly one network may pass that `network_id` so its label is
truthful.

### `AutoSeedSlot` becomes a set

Today it is a single static slot:

```csharp
public static readonly AutoSeedSlot Default =
    new("auto_seed_time", Branding.ScheduledTaskName, "--autoseed");
```

It becomes a collection, one entry per wake time, each with its own store key,
Task Scheduler task name, and CLI argument. Everything that touches the task
by name has to enumerate instead:

- `AutoSeedService.SetupAsync` / `UninstallAsync` / `GetStatusAsync`
- `AutoSeedService.RemoveOrphanedTaskAsync` (the orphan sweep)
- `MissedAutoseedMonitor` — currently reads one stored UTC time and one task;
  becomes per-wake, and `AutoSeedState.WasTriggeredToday` must be keyed per
  wake rather than a single daily flag
- `installer/Fullobby.iss` — `RemoveScheduledTasks` deletes fixed names today,
  so it needs a prefix sweep (`schtasks /Query` filtered on `Fullobby`) rather
  than a hardcoded list
- `App.HasAutoseedArg` / `AutoSeedSlot.IsAutoseedArg` — the CLI argument has to
  identify *which* wake fired

**Naming must stay stable across window changes.** Task names keyed by ordinal
("Fullobby-1") churn when a wake is removed; key them by the wake time
(`Fullobby-0600`) so a task's identity survives its neighbours changing.

### Reconciliation rules

Replacing `RescheduleFromConfigAsync`'s current "silently re-arm to the
earliest window":

| Condition after a config refresh | Action |
| --- | --- |
| No network covering this wake still covers it | Remove the wake and its task, silently; log to the activity panel |
| Still covered, but the covering window moved | Prompt: update this wake to the new window, or keep it |
| Still covered, window unchanged | No-op |
| A network is joined whose windows nothing covers | Offer a new wake (the join-time prompt) |
| A network is left | Re-evaluate; wakes it solely justified are removed |

The first row is the rule that must **not** fire on one tenant moving its
window while another still covers the wake — the failure this whole document
exists to prevent.

### Overlap collapsing

Wakes are deduplicated by time. Two networks opening at 06:00 produce one task.
A wake is retained while ≥1 network justifies it, so leaving one of them does
not disturb it.

## As built (client, 0.3.0)

The client half above is implemented; this section records where the
implementation refines the draft, and the decisions taken on the open
questions. Code map: `WakePlanner` (pure wake-set derivation),
`WakeStore`/`StoredWake` (persistence), `AutoSeedSlot.ForTime` (identity),
`AutoSeedService.ReconcileAsync` (the diff/apply), per-wake
`MissedAutoseedMonitor` + `AutoSeedState`.

- **One store key, not one per wake.** The whole set persists as a JSON list
  under `auto_seed_wakes` rather than a key per wake: `ConfigService` stores
  typed values, so the plan swaps atomically — a crashed reconcile can never
  leave a half-written wake set. Task names and CLI args are still per-wake and
  time-keyed exactly as specified (`Fullobby-0600`, `--autoseed-0600`).
- **Reconcile triggers.** App start (this is also what adopts a legacy install),
  every SSE `seeding_status` push, the bootstrapper's 15-minute config refresh,
  and the auto-seed scheduled-pause path. The reconcile is an in-memory no-op
  when nothing changed, so the push cadence costs nothing.
- **Deletion needs positive evidence.** The reconciler drops a
  network-justified wake only when a *fresh* per-network status says no window
  covers it. With no fresh data (SSE down, cache stale) it refuses to touch
  attributed wakes — the "one tenant moving its window" failure cannot be
  reproduced by mere silence. The unattributed fleet wake just follows the
  fleet config, as it always has.
- **No prompts yet.** The reconciliation table's two prompt rows ("window
  moved — update or keep?", the join-time offer) collapse to silent re-arms:
  every wake today is server-derived, so there is no user choice to protect.
  The prompts become real when the Settings time picker introduces user-chosen
  times.
- **Wake cap: 4, client policy** (`WakePlanner.MaxWakes`). Earliest kept,
  deterministic, dropped wakes logged. Revisit if the server starts
  advertising a cap.
- **Missed window: max across justifying networks**, resolved at plan time and
  stored per wake. The unattributed fleet wake stores null and follows the
  live fleet value, matching the old single-slot behaviour.
- **Directive scoping: wired, unused.** `GetDirectiveAsync` takes `network_id`
  and `DirectiveTarget` carries it back, but the client passes null everywhere
  and labels wakes by time (migration step 4). Turning it on for
  single-network wakes is a follow-on once the backend supports it.
- **Uninstaller sweeps by prefix.** `schtasks /Query /FO CSV` filtered to
  root-folder tasks named `Fullobby` or `Fullobby-…`; the historic fixed names
  remain as fallback for a failed listing. The in-app orphan sweep
  (`RemoveOrphanedTasksAsync`) uses the same listing, since a wiped config
  can't name the time-keyed tasks.
- **Timezone presentation** stays point-in-time: each wake displays via the
  existing `AutoSeedTime.UtcToLocalDisplay`. Range presentation is a picker
  problem, deferred with the picker.
- **Mock API** serves the per-network fields (off by default = dormant wire
  shape); `POST /__mock/schedule` with e.g.
  `{"active_windows":[{"start_min":360,"end_min":540}]}` lights them up and
  pushes fresh SSE status, which drives the client reconciler end-to-end.

## Migration

1. Backend adds the per-network fields; they are additive, so shipped clients
   are unaffected and keep using the fleet defaults.
2. Client starts preferring per-network windows when present, falling back to
   `SeedingConfig` when a network omits them or the user has no memberships.
   This fallback is permanent, not transitional — it is the correct behaviour
   for a membership-less client.
3. Existing single tasks are adopted, not recreated: a `Fullobby` task found at
   upgrade becomes the wake for whichever window covers its stored time, or is
   removed by the normal orphan rule if none does.
4. Directive scoping lands whenever; until it does, the client omits
   `network_id` everywhere and labels wakes by time rather than by network.

## Open questions

Resolved in the 0.3.0 client (details in "As built" above) unless marked open:

- **Per-network `missed_autoseed_window_hours`.** *Resolved client-side:* the
  client takes the maximum across a wake's justifying networks. Whether the
  backend should expose it per network at all is still its call — the client
  degrades to the fleet value either way.
- **Wake count ceiling.** *Resolved client-side:* capped at 4 as client policy,
  earliest kept, dropped wakes logged. Becomes server-advertised if the
  backend ever wants control.
- **Timezone presentation.** *Deferred with the picker.* Windows are UTC on
  the wire and the picker is local; a window crossing midnight UTC lands on
  two local dates. The existing `AutoSeedTime.UtcToLocalDisplay` handles a
  point in time, not a range.
- **Who owns the default.** *Still open — becomes real with the picker.* Today
  every wake is server-derived and follows its window. Once user-chosen times
  exist: does an untouched default follow a moved window while a user-chosen
  time does not? (The as-built silent re-arm suggests yes for defaults.)
