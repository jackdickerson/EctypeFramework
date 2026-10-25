# Character statuses: timed buffs and debuffs on a player

`CharacterStatuses` is the counterpart to [enemy statuses](enemy-statuses.md) for a
`CG.Game.Player.Player` target. Register a template once at plugin `Awake` on every client, then
apply it for a duration. The framework drives the timer and removes everything the status added
on expiry, death or despawn.

```csharp
CharacterStatuses.Register(new CharacterStatusDef("mymod.dash-guard")
{
    DisplayName = "Dash Guard",
    MaxStacks = 1,                   // re-dashing refreshes rather than compounds
}.AddAllVulnerabilities("flat", -0.99f));

CharacterStatuses.Apply(player, "mymod.dash-guard", seconds: 1f);
CharacterStatuses.Remove(player, "mymod.dash-guard");   // early removal
CharacterStatuses.RemoveAll(player);                    // e.g. on a role switch
CharacterStatuses.IsActive(player, "mymod.dash-guard"); // + RemainingSeconds, Stacks
```

A status on another player's character is a request to that player's client:

```csharp
CharacterStatuses.Register(new CharacterStatusDef("mymod.slow")
{
    Replicated = true,               // accept requests from other clients
    Stance = StatusStance.Hostile,
    MaxRange = 6f, RequiresLineOfSight = true,
    MinInterval = 2f, Burst = 1,
    MaxSeconds = 4f, MaxMagnitude = 1f,
    Presented = true,                // other players see a copy
}.AddStat("MotorAcceleration", "percent", -0.4f));

CharacterStatusRefusal r = CharacterStatuses.Request(enemyPlayer, "mymod.slow", seconds: 3f);
```

## Where a status lives

A status lives on the client that owns the character. That client is the only one whose copy
of the character the game reads for the things a status changes:

- **Damage.** Character damage resolves on the victim's own client every way it can be dealt.
  NPC damage abilities gate `ApplyHitDamage` on `Target.AmOwner`. Environmental damage (void
  rifts, damage volumes, thruster wash, black holes, oxygen and temperature) reaches a character
  only through `LocalPlayer`. `CalculateRawDamage` calls `ApplyVulnerability` in-process, with no
  RPC inside the damage path to move that decision elsewhere. The one path that is not
  victim-side, a ship weapon's projectile computed on the shooter's client, is zeroed for
  same-faction targets before vulnerability is read.
- **Movement.** The owning client simulates its own character's locomotion, so a movement stat
  changed anywhere else moves nothing.

`Apply` runs where it is called. On a character this client does not own, it lands on this
client's copy, which changes nothing for that player, and the framework logs one warning per key
naming the character. `Request` is the call for another player's character.

## Requests

`CharacterStatuses.Request(target, key, seconds, magnitude = 1, attacker = null)` returns a
`CharacterStatusRefusal`. On this client's own character it is `Apply`. On another player's
character:

1. The requesting client runs every check below from its own view. A request any check refuses
   is not sent, and `Request` returns the reason.
2. The request goes to the target's client alone: the target's actor number, the key, the
   duration in milliseconds, the magnitude, and the attacker's view id (0 for none). Nothing
   about either character's position or state travels with it.
3. The target's client judges the request against its own view of both characters, in this
   order, and stops at the first refusal:

   | Order | Check | Refusal |
   | --- | --- | --- |
   | 1 | A template is registered under the key | `NotRegistered` |
   | 2 | The template sets `Replicated` | `NotReplicated` |
   | 3 | Duration above 0 and finite; magnitude 0 or more and finite. Values above `MaxSeconds` and `MaxMagnitude` are cut to them | `InvalidDuration`, `InvalidMagnitude` |
   | 4 | The target character is alive | `TargetDead` |
   | 5 | The requester has a character on this client | `SenderMissing` |
   | 6 | That character is alive | `SenderDead` |
   | 7 | `Stance`: `Hostile` needs the two characters on different sides, `Friendly` on the same side, `Any` accepts both | `Stance` |
   | 8 | `MaxRange`: the distance between the two characters, when above 0 | `OutOfRange` |
   | 9 | `RequiresLineOfSight`: no level geometry between the two characters' chests | `NoLineOfSight` |
   | 10 | Rate: a token bucket per requester and key, `Burst` tokens, one more every `MinInterval` seconds | `RateLimited` |

4. An accepted request applies the status exactly as a local `Apply` with the cut duration and
   magnitude: same stacking, same refresh, same callbacks, on the target's own client.
5. A refusal is logged on the target's client as a warning naming the key, the requester's actor
   number and the reason; at most one line per requester, operation and key per 30 s. The
   requester learns nothing of it.

**Why the target's client judges.** Any client can be modified, and a request is a claim by the
requester. The two facts a check could not do without are the requester's identity and both
characters' state. Photon stamps the sender's actor number on every event, so the requester
cannot forge who it is; the target's client simulates its own character exactly and sees the
requester through the game's own replication, which the requester's mod cannot rewrite. A
check against a position or state carried in the message would accept whatever the requester
wrote. The view is fair to the target too: the requester appears where the target's client last
saw it, so a hit that looked close to the requester but far to the target is refused.

**Facts each check reads:**

- **Sides** come from the game's own different-faction check between the two characters. A
  multi-ship mod that keeps character factions team-correct makes the check team-correct. In a
  room with one crew every player is on one side, so a `Hostile` request between crewmates is
  always refused.
- **Alive** is the character's replicated life state: the game's death notification sets it on
  every client and the respawn clears it. The health component's own dead flag is set only on
  the owning client and is not read.
- **Distance** is between the two characters' positions (their feet). Give `MaxRange` a metre or
  two of slack: the target's client sees the requester one network update late.
- **Line of sight** is a line cast between points 1.2 m above each character's feet along its up
  axis, against the `StaticPlatform` and `MovingPlatform` layers, triggers ignored. Those are
  the game's interaction ray mask without `InteractiveObjects`, and the pair its jetpack treats
  as environment. Interactive objects are left out so a console or a carried item does not hide
  a player standing behind it. Characters sit on their own layer, so neither end of the line hits
  its own body. A game without those layers refuses every line-of-sight check and logs one
  error.
- **Rate** counts accepted requests only. The requesting client keeps the same budget per target
  and key, so a requester that keeps to it is never refused for rate; `Burst` absorbs requests
  the network delivers bunched up.

**The attacker argument is informational.** It travels as a view id, and the target's client
reports it through `AttackerOf(target, key)`, with the requester's actor through
`RequesterOf(target, key)`. Neither is judged: any client could name any attacker.

**Requests on someone else's behalf keep `Stance = Any`.** Every check reads the requesting
client's own character. A request sent for another party, such as the master forwarding an
NPC's effect or a client relaying another player's hit, is judged by the forwarder's character,
not the party it acts for. Declare such a status `Stance = Any` with no `MaxRange`, or apply it on
the target's own client with `Apply`. NPC damage abilities already run there.

**Template fields for requests.** None runs for the owning client's own `Apply`.

| Field | Default | Meaning |
| --- | --- | --- |
| `Replicated` | `false` | Accept requests. Off, every request is refused `NotReplicated`. |
| `Stance` | `Any` | `Any`, `Hostile` (another side from the target) or `Friendly` (the target's side). |
| `MaxRange` | `0` | Metres, measured on the target's client. 0 checks no range. |
| `RequiresLineOfSight` | `false` | Refuse when level geometry blocks the line between the two characters. |
| `MinInterval` | `0` | Seconds per request from one requester for this key. 0 checks no rate. |
| `Burst` | `1` | Requests accepted back to back before `MinInterval` applies. |
| `MaxSeconds` | `30` | A longer requested duration is cut to this. |
| `MaxMagnitude` | `1` | A larger requested magnitude is cut to this. A negative magnitude is refused: it would turn a debuff into a buff. |

`Register` refuses a template with `Burst` below 1, a negative or non-finite `MinInterval`,
`MaxRange` or `MaxMagnitude`, or a `MaxSeconds` that is not above 0, and logs one error naming the
field. A template that sets request checks without `Replicated`, or presentation fields without
`Presented`, registers with one warning.

**`Request` refusals from the requesting client's view** are the reasons above plus
`InvalidTarget` (null, no stat collection or no `PhotonView`), `Inert` (the framework's patches
did not apply) and `NoPeer` (the target's client does not run this framework on this network
contract). `None` means the status was applied here or the request was sent; the target's client
can still refuse it. A requester that needs the verdict reads it where it shows: the presentation
copy, when the template is `Presented`.

## Removal

Only the client that owns the character can end a status on it. No removal travels on the
network. `Remove` on another player's character removes only a copy this client's own `Apply`
left there; otherwise it logs one warning per key, and `TryRemove` returns `Authority`.
`TryRemove` returns `None` when it removed the status or there was none.

## Presentation copies

A template with `Presented = true` is shown to other players. After every application on a
character it owns, the owning client sends the target's actor number, the key, the end as a
Photon server timestamp, and the magnitude to the peers the template's `Visibility` allows. Each
runs a presentation copy: `CreateVfx` builds a visual the framework parents to the character at
its feet and destroys at the end, and `IsPresented(target, key)` and
`PresentedRemainingSeconds(target, key)` answer for it. A copy applies no stat and runs no
callback; `IsActive` stays false on every client but the owner's.

| `Visibility` | Receives a copy |
| --- | --- |
| `All` (default) | every peer running the framework |
| `OwnForce` | a peer on the side of any player whose application of the status was accepted |
| `TargetForce` | a peer on the target's side |
| `HostileToTarget` | a peer on another side from the target |

- **The owning client picks the recipients**, so a peer outside the audience never receives the
  message. Sides are judged as the message is sent; a peer whose side is unknown receives only
  an `All` status.
- **The end travels with the copy.** A copy ends on the server time it was told, with no further
  message, so every viewer's copy ends together whatever its frame rate. An end before that
  (removal, death, despawn, a role switch's `RemoveAll`) is sent to every peer that received the
  copy.
- **A re-application** is sent again when it moves the end by 250 ms or more, and otherwise only
  to audience peers that have not received the copy yet.
- **Only a character's own client may announce a status on it.** A receiver refuses an
  announcement naming any other actor's character, and one for a key whose template is not
  `Presented` on the receiver.
- **Late joiners** receive no copy of a status already running.
- `CreateVfx` does not run on the owning client: its player sees through the character's eyes.
  Use `OnApplied` and `OnRemoved` for that player's own feedback.

## Rules and limits

- **Timing.** The live status runs on `Time.time` on the owning client, the clock ability
  durations and cooldowns run on. Expiry and `OnTick` are both indexed from the status's own
  start, so a frame hitch pays every missed tick and a double poll pays none twice. Copies run on
  the Photon server clock.
- **Stat entries only bite stats the character registers.** That is whatever `Player.Awake`
  registered, plus the framework's locomotion stats (see
  [framework-stats.md](framework-stats.md)). Unmatched entries no-op silently in game code; the
  framework logs one warning per (status, target type). `AddAllVulnerabilities` covers every
  damage type plus the aggregate. That is the right default: vanilla character perks modify the
  per-type stats, and the aggregate's read path on characters is unverified.
- **A respawned character is a different target.** Statuses and copies are keyed on the
  character instance, so a new body never inherits the old one's entries, and a status whose
  target was destroyed ends itself on the next poll.
- **Every client that should take or see a status registers its template** under the same
  literal key. A client without it ignores requests and announcements for the key and logs once.
  A mod that requests statuses on other players therefore runs on every client: declare it
  `MultiplayerType.All`.

## Checking it

Turn on `[Debug] VerboseLogging` on both clients.

| Line | Check |
| --- | --- |
| `Ectype: character status '<key>' requested on <name> (actor <a>, <s> s, magnitude <m>).` | On the requester: the request left. |
| `Ectype: character status '<key>' applied on request from actor <a> (<s> s, magnitude <m>, <d> m).` | On the target: accepted, with the distance it measured. |
| `Ectype: refused character status '<key>' request from actor <a> on <name>: <reason>.` | On the target: refused, and why. |
| `Ectype: presenting character status '<key>' on <name> for <s> s.` | On a third client: a copy started. |

[troubleshooting.md](../troubleshooting.md) lists every line with its cause.

Sample: [examples/character-statuses](../../examples/character-statuses/).
