# Enemy statuses: timed buffs and debuffs on space objects

Register a status template once at plugin `Awake`, then apply it to any `CG.Space.OrbitObject`
(NPC ships, turrets, asteroids, player ships) or to a part of a networked object (a panel or a
door of a ship) for a duration. The framework replicates the application to every peer, drives
the timer on Photon's server clock, and removes everything the status added on expiry or target
death.

```csharp
// at Awake, on every client (register from a literal - the key crosses the wire):
EnemyStatuses.Register(new EnemyStatusDef("com.example.mymod/breach")
{
    DisplayName = "Hull Breach",
    DamagePerSecond = 15f,        // raw damage, dealt by the target's owner
    TickSeconds = 0.5f,
    MaxStacks = 3,
    Stance = StatusStance.Hostile,            // only a sender on another side may apply it
    // marker on the affected object, countdown circle included (on by default):
    Icon = EnemyStatuses.IconFromEmbeddedPng(
               Assembly.GetExecutingAssembly(), "breach-icon"),   // null = stock icon
    MarkerRequiresTargetLock = true,          // hide unless the ship has it locked
    // looping visual on the object while active - a loaded stock prefab, cloned and
    // stripped to visuals:
    VfxDonorName = "FX_EngineTrail", VfxScale = 2f,
}.AddStat("MaxFlightSpeed", "percent", -0.25f));

// at runtime, from any client:
EnemyStatuses.Apply(target, "com.example.mymod/breach", seconds: 8f, magnitude: 1f,
                    attacker: myShip);
StatusRefusal why = EnemyStatuses.TryApply(target, "com.example.mymod/breach", 8f); // None, or why not
EnemyStatuses.Remove(target, "com.example.mymod/breach");     // early removal, everywhere
EnemyStatuses.IsActive(target, "com.example.mymod/breach");   // + RemainingSeconds, Stacks
```

A key is `"<plugin GUID>/<name>"`, the [identifier rule](../identifiers.md). A key that does not
follow it logs one warning and still works.

## Rules and limits

- **Stat entries only bite stats the target registers.** Every `OrbitObject` has
  `MaxHitPoints` and `Invulnerability`. NPC ships (`SpaceCraft`) add `MaxFlightSpeed`.
  Level-scaled NPCs add their level-stat set. Child collections (modules, turrets) are reached
  too. An unmatched stat no-ops silently in game code; the framework logs one warning per
  (status, target type) when nothing matched. Use the callbacks for anything without a
  registered stat: `OnApplied` and `OnRemoved` run on every client, as do the
  [instance callbacks](#instance-callbacks), and `OnTick` runs on the target's owner only.
- **Re-applying refreshes the timer** (`RefreshOnReapply`) **and compounds up to `MaxStacks`**.
  Each stack applies the stat entries again. `magnitude` scales every amount and the damage
  over time at apply time.
- **Only the owner deals damage.** HP replicates through the game's own serialization, so
  damage over time ticks exactly once. Who may apply and remove is below.
- **The template must be registered on every client that should see it.** A peer without the
  key ignores the message with one log line. A status-applying mod should be required for the
  whole room (Thunderstore category `All Required`), or accept divergence on peers without
  it; see [../multiplayer.md](../multiplayer.md).
- **The marker is the game's own countdown visual.** It has the same `ObjectMarkerData` shape
  the game builds for an NPC's reinforcement-call channel, so the stock viewer draws and
  animates it. `Icon` null falls back to the stock ability icon. The marker's colour comes
  from the game's own affiliation rule for the target, the one its object markers use, then the
  marker table's per-asset override. `MarkerRequiresTargetLock` gates visibility on the target
  locks of the ship this client is viewing, and follows the view when it moves to another
  ship.
- **Presentation is capped, nearest first.** `[EnemyStatuses] MarkerCap` and `VfxCap` bound
  concurrent markers and visuals. Past the cap, a status asking for a slot takes the one held
  by the status farthest from the ship this client views, when it is nearer than that one by
  10% of the farther distance or 25 m, whichever is larger; otherwise it runs undecorated.
  The margin keeps two statuses at about the same distance from trading a slot on every
  re-application. Freed slots go to the nearest undecorated statuses, checked once a second.
  `Markers` and `Vfx` turn each layer off outright. See [config.md](config.md).
- **Visuals.** `CreateVfx` returns a fresh GameObject the framework manages. Alternatively
  `VfxDonorName` (plus optional `VfxDonorChild`) clones a loaded stock prefab, stripped to
  visuals only (no behaviours, colliders or PhotonView). A donor name matching nothing logs
  once and attaches nothing.
- **Late joiners do not receive statuses already running**, on space objects and on parts
  alike: a player who joins mid-window does not learn of it. The effect exposes its header and
  description through `IDescriptiveModifierSource`.
- **`SuspendsAbilities`** freezes the target's abilities, and its turrets', for as long as the
  status runs. See [enemy-abilities.md](enemy-abilities.md). A refresh extends the same
  freeze.
- **`SkipsInvulnerableTargets`** makes `Apply` refuse a target whose `IsInvulnerable` is true
  at that moment (its `Invulnerability` stat above zero). The check runs on the applying
  client.

## Who may apply and remove

Every application and removal travels with the actor number of the client that sent it. Each
receiver judges that sender against the target itself, so a modified client cannot put a status
on a target its stance forbids, or strip one its stance gives it no claim to. The sending client
runs the same checks first, so a refused change happens nowhere. The rules depend only on who
sends and on the target, never on which client happened to apply first, so clients that agree
on the sides reach the same verdict whatever order messages arrive in. They can disagree for a
moment while a multi-ship mod rewrites a respawned character's faction.

**Stance** (`EnemyStatusDef.Stance`) is the sender-to-target relation the status accepts:

| `Stance` | Applied by | Removed by |
| --- | --- | --- |
| `Any` (default) | every sender | every sender |
| `Hostile` | a sender on another side from the target | the same senders, and the master |
| `Friendly` | a sender on the target's side | the same senders, and the master |
| `MasterOnly` | the master client only | the master client only |

- **A sender's side** is its character's faction, compared with the target's faction by the
  game's own different-faction check. A multi-ship mod that keeps factions team-correct makes
  this team-correct too. A sender with no character (spectating, or between death and
  respawn) takes the faction of its crew ship, or, under Void Crew Fleet, of a living ship of
  its team, so a projectile that lands after its shooter died still counts, and a Fleet
  spectator acts for its team. With neither, its side is unknown, and a stance other than
  `Any` refuses it.
- **Neutral objects** (asteroids and debris, faction 0) are on another side from every
  player, so a `Hostile` status lands on them.
- **The `attacker` argument is ignored for stance, with one exception.** Any client could fill
  it in, so a sender is judged by its own side. The exception: when the sender is the master
  and the attacker is an NPC (a space object that is neither a player ship, a part of one, nor
  a character), the NPC's side is judged instead. NPCs are simulated on the master, so only the
  master applies a status on an NPC's behalf, and the master is trusted with any removal
  already. An enemy debuff on a player ship, applied by the master with the enemy as
  attacker, can therefore be declared `Hostile`. When a receiver can no longer find the named
  attacker (the NPC died or despawned while a coalesced batch waited), an application from the
  master is accepted, since the master judged the NPC when it applied. A status applied on
  behalf of a player from another player's client, such as the target's owner applying the
  shooter's effect, cannot use this, and keeps `Stance = Any`.
- **`MasterOnly` is for a status the host decides**, such as a window a host-run rule grants
  after checking a request. Sides do not enter into it: every receiver accepts the message only
  when its sender is the room's master client at the moment it arrives. A message from any other
  client is refused on receipt and logged once per sender and status, since only a modified
  client, or one that lost the master role while its message travelled, sends one. Locally,
  `TryApply` and `TryRemove` on a client that is not the master return `NotMaster`. Messages from
  one sender arrive in the order it sent them, so every client applies the master's changes in
  the same order. A character status with this stance accepts requests from the master alone.
- **The master may always remove.** It simulates the NPCs, and a multi-ship mod's arbiter
  runs there. A removal is judged when it arrives: one from a client that is no longer the
  master by then (it just handed the role on) is refused, is not retried, and the status runs
  to its end on the clients that refused it.
- **`Apply` and `Remove` are fire and forget.** `TryApply` and `TryRemove` take the same
  arguments and return a `StatusRefusal`: `None` when the change was made here and sent (or,
  with `CoalesceMs`, queued), else `Stance`, `UnknownSide`, `Authority` (a removal its stance
  refuses), `NotMaster` (a `MasterOnly` status on a client that is not the master),
  `InvalidTarget`, `NotRegistered`, `Inert`, `InvalidDuration`, `Invulnerable` or `Failed`. A role ability can show the player why nothing happened. A refusal for the stance
  through `Apply` or `Remove` logs one warning per operation and key; through the `Try`
  methods it logs only with `[Debug] VerboseLogging`, since they return it.
- **A refresh only ever moves the end later.** Re-applying with `RefreshOnReapply` sets the end
  to the later of the current end and the new application's, so applications from several
  clients leave the same end on every client whatever order they arrive in.

**Damage over time.** A status with `DamagePerSecond` above zero and `Stance = Any` logs one
warning at `Register`, because such a status also damages a target on the applier's own side.
Declare it `Hostile`. A `Hostile` status's owner then also checks before each tick that the
client whose application created the status (or the NPC attacker, for one the master applied
on an NPC's behalf) is still on another side from the target, after a team change or a
captured ship, and skips the tick when not. Skipped ticks are not paid later. When that client
has left the room, or the NPC is gone, the side recorded at creation stands in.

**A copy that arrives late does not owe the past.** A client that creates the status from a
message pays the ticks, and starts the ability suspension, from the status's start only when
the message arrived within a grace of `CoalesceMs` plus 500 ms; from an older start it begins
the grace before arrival. A coalesced batch carries the start of the sender's whole run of the
status, which can be long ago when this client's copy had ended in a gap in fire, and the
grace keeps such a copy from paying seconds of damage in one frame.

**Refusals in the log.** A receiver logs a refused message as a warning naming the status, the
operation, the sender's actor number, the target and the reason. One line per sender,
operation and status per 30 s; the next line counts the refusals left unreported in between.

## Tag: who a status is for

The tagged overloads carry one `int` with the application:
`Apply(target, key, seconds, magnitude, attacker, tag)`, and `Apply(part, key, seconds, tag)` for
a part. Every client reads it back with `EnemyStatuses.Tag(target, key)` or `Tag(part, key)`,
and the instance callbacks and `GetActive` carry it as `StatusInstance.Tag`. The framework gives
it no meaning: a team number, a ship number, an index into the mod's own table.

- **Each accepted application replaces the Tag**, whether or not it moves the timer. With
  `MasterOnly`, one sender's messages arrive in order, so every client ends on the same Tag. With
  several senders, two clients can receive their applications in different orders and end on
  different Tags.
- **One status per target and key.** A status holds one Tag, so two teams holding a window on the
  same ship at once need two keys, or a part target per team.
- An untagged application (Tag 0) travels in the same message as one made without the overload.

## Statuses on parts

A status can target a part of a networked object instead of a space object: a panel, a door, a
console. `StatusPart` names it the same way on every client, as the id of the PhotonView the part
sits under and the part's index among that view's components of the part's type.
`StatusPart.Of(component)` takes the nearest PhotonView at or above the component and its index
in that view's `GetComponentsInChildren<T>(true)`; `part.Resolve<T>()` is the inverse. The index
agrees across clients because every client instantiates the same prefab, so the walk meets the
same components in the same order. Name a part with the same `T` on every client.

```csharp
StatusPart panelPart = StatusPart.Of(panel);           // panel: the component to name
EnemyStatuses.Apply(panelPart, key, 120f, tag: 0);     // TryApply returns a StatusRefusal
EnemyStatuses.Remove(panelPart, key);                  // TryRemove likewise
EnemyStatuses.IsActive(panelPart, key);                // + RemainingSeconds, Stacks, Tag
```

- **Same timing, stacking and removal as on a space object.** The end is a server timestamp; a
  re-application stacks up to `MaxStacks` and, with `RefreshOnReapply`, moves the end later and
  never earlier; the removal rules, and the memory of a removal against applications still in
  flight, are the ones above. The status ends at its end time, on `Remove`, or when the part's
  PhotonView is destroyed.
- **Stance judges the part's owner.** `Hostile` and `Friendly` compare the sender's side with
  the player ship the part belongs to, or else the nearest space object above the view. A part
  with neither has an unknown side, so only `Any` and `MasterOnly` apply to it.
- **A part has no stats, hit points or marker.** Stat entries, `DamagePerSecond`, `OnTick`,
  `SuspendsAbilities`, the marker and the visual act on space objects only; a template that sets
  one of the first four logs one warning the first time it is applied to a part. `OnApplied` and
  `OnRemoved` take a space object and run for space objects only. What the status does to the
  part belongs in `OnInstanceApplied` and `OnInstanceRemoved`.
- **The mod draws its own countdown.** `GetActive(list, key)` appends every live status on this
  client, on space objects and parts, as `StatusInstance` values carrying `Part`, `Tag`,
  `Stacks` and `RemainingSeconds`; read it each frame to draw HUD rows.
- **`CoalesceMs` does not apply.** Each application to a part is one message.
- **The part's view must exist** on the client that applies and on each receiver. A receiver
  without it ignores the message, as for a space object it does not have.

## Instance callbacks

`OnInstanceApplied` and `OnInstanceRemoved` take a `StatusInstance` and run on every client, for
space objects and parts alike. `OnInstanceApplied` runs after `OnApplied` for each accepted
application, with `Created` true for the one that started this life of the status.
`OnInstanceRemoved` runs once when the status ends, after `OnRemoved`, with the Tag it ended
with. Add what the status enforces when `Created` is true and take it away in
`OnInstanceRemoved`: every client runs both, so every client enforces the same thing over the
same span. A callback that throws is logged once per status and does not stop the status.

## Worked example: a host-granted door window and a sabotaged panel

Two statuses a team game's host grants after judging a request: door access to an enemy ship
for the team that hacked it, and a panel switched off for two minutes. The host checks the
request (the hacker's team, the saboteur's reach), then applies; every client enforces the
result.

```csharp
const string DoorAccess = "com.captainchunck.voidcrew.fleet/door-access";
const string Sabotage = "com.captainchunck.voidcrew.fleet/sabotage";

sealed class Held { public PowerDrain Drain; public Func<bool, bool, bool> RefuseOn; }
static readonly Dictionary<StatusPart, Held> HeldOff = new Dictionary<StatusPart, Held>();
static readonly List<StatusInstance> Rows = new List<StatusInstance>();

// at Awake, on every client:
EnemyStatuses.Register(new EnemyStatusDef(DoorAccess)
{
    DisplayName = "Door access",
    Stance = StatusStance.MasterOnly,   // the host decides; any other sender is refused everywhere
    ShowMarker = false,                 // the countdown is drawn in the mod's HUD
});
EnemyStatuses.Register(new EnemyStatusDef(Sabotage)
{
    DisplayName = "Sabotaged",
    Stance = StatusStance.MasterOnly,
    OnInstanceApplied = s =>
    {
        if (!s.Created) return;
        Enhancement panel = s.Part.Resolve<Enhancement>();
        PowerDrain drain = panel != null ? panel.GetComponentInParent<PowerDrain>() : null;
        if (drain == null) return;
        var held = new Held { Drain = drain, RefuseOn = (was, now) => !now };
        drain.IsOn.ChangeValidators.Add(held.RefuseOn);   // every client refuses switching it on
        HeldOff[s.Part] = held;
    },
    OnInstanceRemoved = s =>
    {
        Held held;
        if (!HeldOff.TryGetValue(s.Part, out held)) return;
        HeldOff.Remove(s.Part);
        if (held.Drain != null) held.Drain.IsOn.ChangeValidators.Remove(held.RefuseOn);
    },
});

// on the host, after a hack completes (team: the hacking team's number):
EnemyStatuses.Apply(enemyShip, DoorAccess, 300f, 1f, null, team);
// on the host, after it accepts a sabotage request for a panel:
EnemyStatuses.Apply(StatusPart.Of(panel), Sabotage, 120f);

// on every client, whenever a door is used:
static bool HasAccess(int team, PlayerControlledShip ship)
{
    return EnemyStatuses.IsActive(ship, DoorAccess) && EnemyStatuses.Tag(ship, DoorAccess) == team;
}

// on every client, each frame, the HUD countdowns:
Rows.Clear();
EnemyStatuses.GetActive(Rows, DoorAccess);
EnemyStatuses.GetActive(Rows, Sabotage);
foreach (StatusInstance row in Rows) DrawCountdown(row);   // row.Tag, row.Part, row.RemainingSeconds
```

- **Why the validator holds the switch off.** `PowerDrain.IsOn` is a `ChangeResponsive<bool>`.
  Its `RequestChange` runs every function in `ChangeValidators` with the current and the
  requested value and declines the change when one returns false, so `(was, now) => !now`
  declines every switch-on while it is in the list. `ForceChange` skips the validators. The
  status does not switch running equipment off: the view's owner does that with `ForceChange`
  when the status lands.
- **Why `MasterOnly` and not a side.** The host judged the request: whether the hacker reached
  the terminal, whether the saboteur stands at the panel. A side stance would let any client on
  the right side grant the window without passing those checks.
- **The window's team is its Tag.** A second hack of the same ship by another team replaces the
  Tag; give each team its own key when two teams may hold a window on one ship at once.

## Who sees it

`EnemyStatusDef.Visibility` decides which viewers draw the marker and the visual:

| `Visibility` | Drawn for a viewer |
| --- | --- |
| `All` (default) | always |
| `OwnForce` | on the side of any player whose application of the status was accepted; for one the master applied on an NPC's behalf, the NPC's side, which no player shares in co-op |
| `TargetForce` | on the target's side: a warning to the afflicted crew |
| `HostileToTarget` | on another side from the target, whoever applied it |

The viewer's side is this client's player's, derived as a sender's is above; a viewer whose
side is unknown draws only `All` statuses. `OwnForce` reads the set of sides every accepted
application came from, which every client builds from the same reliable messages, so two sides
applying one status draw it for both on every client. Statuses with a rule are redrawn within
half a second of the viewer's side changing. A hidden status takes no marker or visual slot.

Only presentation is filtered. The status, its stat changes and its callbacks run on every
client, and `IsActive` answers on every client, so `Visibility` is not secrecy against a
modified client. In a room with one crew, every player is on the applier's side and NPC targets
are on another: `OwnForce` and `HostileToTarget` draw for everyone, and `TargetForce` on an NPC
draws for no player.

## Statuses applied per hit: coalescing

Each application is one reliable message to every other framework peer. A status applied once
per projectile therefore costs one message per hit per peer: in a room of 48 players, each
bolt that lands sends 47, and a ten-bolt volley 470, from one shooter alone.

`EnemyStatusDef.CoalesceMs` batches them. Above 0, `Apply` still takes effect on the applying
client at once, so a later hit in the same frame sees the stacks of the earlier ones. The
network send waits: per target and key, the first application opens a window of `CoalesceMs`
milliseconds, and when it closes one message carries how many applications it held. A status
applied per projectile should set about 150. The volley above then costs 47 messages per target
hit, and the stacks reach the peers at most 150 ms late.

- **The message carries a count, not a total.** Each receiver adds that many applications, as
  if each had arrived alone, so applications from several crews to one target add up in any
  order. Two totals for one target would overwrite each other.
- **The timer it carries is the local status's own:** its start and its end after the last
  application. A receiver that creates the status from the message ends it where the applier's
  ends and pays ticks only from the grace described under damage over time; one that already
  has it moves its end no earlier.
- **A batch belongs to one life of the status.** When the local status ends or is created
  anew, the open batch closes and goes out at the next flush, and later applications open a
  new one, so no message mixes the stacks or the timer of two lives. A receiver with no copy of
  the status ignores a batch that arrives after its end; one whose copy still runs (another
  client extended it) adds the stacks. A duration no longer than `CoalesceMs` plus 500 ms can
  end before its batch arrives, so the peers never see it; the first such `Apply` per key logs
  a warning.
- **A removal drops what is still queued** for that target and key, on the client that
  removed it and on every client that accepts the removal. A batch another client had already
  sent when the removal was made can still arrive after it; each client remembers the removal
  for `CoalesceMs` plus 2 s and ignores an application whose start precedes it, so such a batch
  does not re-create the status. An application started after the removal is new and lands.
- One message carries at most 255 applications; more within one window travel as a second
  message.
- `0` (the default) sends each application at once.

## Checking it

The verbose lines need `[Debug] VerboseLogging` on, on each client compared.

| Line | Check |
| --- | --- |
| `Ectype: refused enemy status '<key>' apply from actor <a> on <target>: stance Hostile and the sender is on the target's side.` | A `Hostile` status sent by a client on the target's side lands nowhere. A dev tools build runs this check on its own once a character and its crew ship exist. |
| `Ectype: refused enemy status '<key>' remove from actor <a> on <target>: stance Hostile and the sender is on the target's side; only a sender the stance lets apply it, or the master, may remove it.` | A non-host player on the target's side cannot strip a `Hostile` status; the master and any player on another side can. |
| `Ectype: refused enemy status '<key>' apply from actor <a> on <target>: stance MasterOnly and actor <a> is not the master client. Further refusals of this status from this actor are not logged.` | A `MasterOnly` status sent by a client that is not the master lands nowhere, and a second attempt writes no line. `TryApply` on that client returns `NotMaster`. |
| `Ectype: status '<key>' applied to a part sets stat entries, damage over time, OnTick or SuspendsAbilities, which apply to space objects only; on a part it runs its timer and callbacks.` | A template meant for space objects was applied to a part. |
| `Ectype: status '<key>' on <target>: <n> application(s) in one <w> ms window, 1 send.` (verbose) | A ten-bolt volley on one target logs one such line per window, and `Stacks` agrees on both clients once the window has passed. |
| `Ectype: status <key> on <target> hidden by visibility <rule> (viewer side <v>, applier sides <list>, target side <t>).` (verbose) | A status ruled `TargetForce` on an NPC is hidden for every player. |
| `Ectype: status marker for '<key>' on <target> evicted at <d> m from the view ship, for '<key>' on <target> at <d> m (MarkerCap <n>).` (verbose) | Past the cap, the farthest marker gives way to a clearly nearer one. |

Every line the statuses write is listed in [../troubleshooting.md](../troubleshooting.md).

For a status on a player character, see [character-statuses.md](character-statuses.md). For a
modifier whose amount changes over time, see
[timed-stat-contributions.md](timed-stat-contributions.md).

Sample: [examples/enemy-statuses](../../examples/enemy-statuses/).
