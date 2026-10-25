# Enemy statuses: timed buffs and debuffs on space objects

Register a status template once at plugin `Awake`, then apply it to any `CG.Space.OrbitObject`
(NPC ships, turrets, asteroids) for a duration. The framework replicates the application to
every peer, drives the timer on Photon's server clock, and removes everything the status added
on expiry or target death.

```csharp
// at Awake, on every client (register from a literal - the key crosses the wire):
EnemyStatuses.Register(new EnemyStatusDef("mymod.breach")
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
EnemyStatuses.Apply(target, "mymod.breach", seconds: 8f, magnitude: 1f,
                    attacker: myShip);
StatusRefusal why = EnemyStatuses.TryApply(target, "mymod.breach", 8f);   // None, or why not
EnemyStatuses.Remove(target, "mymod.breach");        // early removal, everywhere
EnemyStatuses.IsActive(target, "mymod.breach");      // + RemainingSeconds, Stacks
```

## Rules and limits

- **Stat entries only bite stats the target registers.** Every `OrbitObject` has
  `MaxHitPoints` and `Invulnerability`. NPC ships (`SpaceCraft`) add `MaxFlightSpeed`.
  Level-scaled NPCs add their level-stat set. Child collections (modules, turrets) are reached
  too. An unmatched stat no-ops silently in game code; the framework logs one warning per
  (status, target type) when nothing matched. Use the callbacks for anything without a
  registered stat: `OnApplied` and `OnRemoved` run on every client, `OnTick` on the target's
  owner only.
- **Re-applying refreshes the timer** (`RefreshOnReapply`) **and compounds up to `MaxStacks`**.
  Each stack applies the stat entries again. `magnitude` scales every amount and the damage
  over time at apply time.
- **Only the owner deals damage.** HP replicates through the game's own serialization, so
  damage over time ticks exactly once. Who may apply and remove is below.
- **The template must be registered on every client that should see it.** A peer without the
  key ignores the message with one log line. A status-applying mod should be `MPType.All`, or
  accept divergence on peers without it. Applying statuses changes what a run contains, so a
  mod that does it should treat the session as modded; see
  [../multiplayer.md](../multiplayer.md).
- **The marker is the game's own countdown visual.** It has the same `ObjectMarkerData` shape
  the game builds for an NPC's reinforcement-call channel, so the stock viewer draws and
  animates it. `Icon` null falls back to the stock ability icon. The marker's colour comes
  from the game's own affiliation rule for the target, the one its object markers use, then the
  marker table's per-asset override. `MarkerRequiresTargetLock` gates visibility on the target
  locks of the ship this client is viewing, and follows the view when it moves to another
  ship.
- **Presentation is capped, nearest first.** `[EnemyStatuses] MarkerCap` and `VfxCap` bound
  concurrent markers and visuals. Past the cap, a status asking for a slot takes the one held
  by the status farthest from the ship this client views, when it is nearer than that one;
  otherwise it runs undecorated. Freed slots go to the nearest undecorated statuses, checked
  once a second. Shown markers never trade places on their own, so they do not flicker.
  `Markers` and `Vfx` turn each layer off outright. See [config.md](config.md).
- **Visuals.** `CreateVfx` returns a fresh GameObject the framework manages. Alternatively
  `VfxDonorName` (plus optional `VfxDonorChild`) clones a loaded stock prefab, stripped to
  visuals only (no behaviours, colliders or PhotonView). A donor name matching nothing logs
  once and attaches nothing.
- **Late joiners do not receive statuses already running.** The effect exposes its header and
  description through `IDescriptiveModifierSource`.
- **`SuspendsAbilities`** freezes the target's abilities, and its turrets', for as long as the
  status runs. See [enemy-abilities.md](enemy-abilities.md). A refresh extends the same
  freeze.
- **`SkipsInvulnerableTargets`** makes `Apply` refuse a target whose `IsInvulnerable` is true
  at that moment (its `Invulnerability` stat above zero). The check runs on the applying
  client.

## Who may apply and remove

Every application travels with the actor number of the client that sent it. Each receiver
judges that sender against the target itself, so a modified client cannot put a status on a
target its stance forbids, or strip one it has no claim to. The applying client runs the same
checks first, so a refused change is applied nowhere.

**Stance** (`EnemyStatusDef.Stance`) is the sender-to-target relation the status accepts:

| `Stance` | Accepted from |
| --- | --- |
| `Any` (default) | every sender |
| `Hostile` | a sender on another side from the target |
| `Friendly` | a sender on the target's side |

- **A sender's side** is its character's faction, compared with the target's faction by the
  game's own different-faction check. A multi-ship mod that keeps factions team-correct makes
  this team-correct too. A sender with no character (spectating, or between death and
  respawn) takes the faction of its crew ship, or, under Void Crew Fleet, of a living ship of
  its team. With neither, its side is unknown, and a stance other than `Any` refuses it.
- **Neutral objects** (asteroids and debris, faction 0) are on another side from every
  player, so a `Hostile` status lands on them.
- **Stance judges the sending client, never the `attacker` argument**, which any client could
  fill in. A status applied on behalf of an NPC, such as an enemy debuffing a player ship, is
  sent by whichever client runs that code, usually the master, whose character is on the
  player side. Such a status keeps `Stance = Any`.
- **`Apply` is fire and forget.** `TryApply` takes the same arguments and returns a
  `StatusRefusal`: `None` when the status was applied here and sent, else `Stance`,
  `UnknownSide`, `InvalidTarget`, `NotRegistered`, `Inert`, `InvalidDuration`, `Invulnerable`
  or `Failed`. A role ability can show the player why nothing happened.

**Removal.** A status remembers the actor whose application created it; later applications by
other clients add stacks and move the timer but do not change it. `Remove` is accepted only
from that applier, the target's owner (the master for NPCs and other room objects) or the
master, whatever the stance. On any other client `TryRemove` returns `Authority` and sends
nothing, and a receiver ignores such a removal. A status not active on the removing client
is still sent, and each receiver judges it against its own copy.

**Damage over time.** A status with `DamagePerSecond` above zero and `Stance = Any` logs one
warning at `Register`, because such a status also damages a target on the applier's own side.
Declare it `Hostile`. A `Hostile` status's owner then also checks before each tick that the
applier is still on another side from the target (a team change, a captured ship) and skips
the tick when not. Skipped ticks are not paid later. When the applier has left the room, the
side it had when the status was created stands in.

**Refusals in the log.** A receiver logs a refused message as a warning naming the status, the
operation, the sender's actor number, the target and the reason. One line per sender and
status per 30 s; the next line counts the refusals left unreported in between. A refusal on
the applying client is logged only with `[Debug] VerboseLogging`, since `TryApply` returns it.

## Who sees it

`EnemyStatusDef.Visibility` decides which viewers draw the marker and the visual:

| `Visibility` | Drawn for a viewer |
| --- | --- |
| `All` (default) | always |
| `OwnForce` | on the side of the player who applied the status |
| `TargetForce` | on the target's side: a warning to the afflicted crew |
| `HostileToTarget` | on another side from the target, whoever applied it |

The viewer's side is this client's player's, derived as a sender's is above; a viewer whose
side is unknown draws only `All` statuses. The applier's side is the one it had when the status
was created. Statuses with a rule are redrawn within half a second of the viewer's side
changing. A hidden status takes no marker or visual slot.

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
- **The timer it carries is the one the local applications left:** the first application's
  start, and, when the status refreshes on re-apply, the last one's end.
- **A removal drops what is still queued** for that target and key, so a batch never re-adds
  a status after it.
- One message carries at most 255 applications; more within one window travel as a second
  message.
- `0` (the default) sends each application at once.

## Checking it

The verbose lines need `[Debug] VerboseLogging` on, on each client compared.

| Line | Check |
| --- | --- |
| `Ectype: refused enemy status '<key>' apply from actor <a> on <target>: stance Hostile and the sender is on the target's side.` | A `Hostile` status sent by a client on the target's side lands nowhere. A dev tools build runs this check on its own once a character and its crew ship exist. |
| `Ectype: refused enemy status '<key>' remove from actor <a> on <target>: only the applier (actor <n>), the target's owner or the master may remove it.` | A removal from a third client leaves the status running. |
| `Ectype: status '<key>' on <target>: <n> application(s) in one <w> ms window, 1 send.` (verbose) | A ten-bolt volley on one target logs one such line per window, and `Stacks` agrees on both clients once the window has passed. |
| `Ectype: status <key> on <target> hidden by visibility <rule> (viewer side <v>, applier side <a>, target side <t>).` (verbose) | A status ruled `TargetForce` on an NPC is hidden for every player. |
| `Ectype: status marker for '<key>' on <target> evicted at <d> m from the view ship, for '<key>' on <target> at <d> m (MarkerCap <n>).` (verbose) | Past the cap, the farthest marker gives way. |

Every line the statuses write is listed in [../troubleshooting.md](../troubleshooting.md).

For a status on a player character, see [character-statuses.md](character-statuses.md). For a
modifier whose amount changes over time, see
[timed-stat-contributions.md](timed-stat-contributions.md).

Sample: [examples/enemy-statuses](../../examples/enemy-statuses/).
