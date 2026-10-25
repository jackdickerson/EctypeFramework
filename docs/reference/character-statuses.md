# Character statuses: timed buffs and debuffs on a player

`CharacterStatuses` is the counterpart to [enemy statuses](enemy-statuses.md) for a
`CG.Game.Player.Player` target. Register a template once at plugin `Awake` on every client, then
apply it for a duration. The framework drives the timer and removes everything the status added
on expiry, on death, and when the character is destroyed.

```csharp
CharacterStatuses.Register(new CharacterStatusDef("com.example.mymod/dash-guard")
{
    DisplayName = "Dash Guard",
    MaxStacks = 1,                   // re-dashing refreshes rather than compounds
}.AddAllVulnerabilities("flat", -0.99f));

CharacterStatuses.Apply(player, "com.example.mymod/dash-guard", seconds: 1f);
CharacterStatuses.Remove(player, "com.example.mymod/dash-guard");   // early removal
CharacterStatuses.RemoveAll(player);                                // e.g. on a role switch
CharacterStatuses.IsActive(player, "com.example.mymod/dash-guard"); // + RemainingSeconds, Stacks
```

A key is `"<plugin GUID>/<name>"`, the [identifier rule](../identifiers.md). A key that does not
follow it logs one warning and still works.

A status on another player's character is a request to that player's client:

```csharp
CharacterStatuses.Register(new CharacterStatusDef("com.example.mymod/slow")
{
    AcceptsRequests = true,          // other clients may request it
    Stance = StatusStance.Hostile,
    MaxRange = 5f, RequiresLineOfSight = true, RefuseSeated = true,
    MinInterval = 2f, Burst = 1,
    MaxSeconds = 4f, MaxMagnitude = 1f,
    Presented = true,                // other players see a copy
}.AddStat("MotorAcceleration", "percent", -0.4f));

CharacterStatuses.Request(enemyPlayer, "com.example.mymod/slow", seconds: 3f)
    .OnCompleted(r =>
    {
        // NoAnswer means the outcome is unknown: the status may have landed.
        if (r.Refusal != CharacterStatusRefusal.None && r.Refusal != CharacterStatusRefusal.NoAnswer)
            RefundCooldown(r.Refusal);
    });
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

`Replicated` changes nothing. It declares that a status should be visible everywhere; a template
that sets it logs one warning per key on `Apply`, naming `AcceptsRequests` and `Presented`, the
two fields that do the work.

## Requests

`CharacterStatuses.Request(target, key, seconds, magnitude = 1, attacker = null)` returns a
`CharacterStatusRequest`. On this client's own character it is `Apply`. On another player's
character:

1. The requesting client runs every check below from its own view, with a looser range (see
   **Range**). A request any check refuses is not sent.
2. The request goes to the target's client alone: a request id, the target's actor number, the
   key, the duration in milliseconds, the magnitude, and the attacker's view id (0 for none).
   Nothing about either character's position or state travels with it.
3. The target's client judges the request against its own view of both characters, in this
   order, and stops at the first refusal:

   | Order | Check | Refusal |
   | --- | --- | --- |
   | 1 | Across all keys, the requester stays within a burst of 21, refilled at 20 a second. Past that, requests are dropped unanswered | `RateLimited` |
   | 2 | The key is at most 128 characters | `Failed` |
   | 3 | The request names this client's actor, and this client has a character | `InvalidTarget` |
   | 4 | The framework's patches applied this session | `Inert` |
   | 5 | A template is registered under the key | `NotRegistered` |
   | 6 | The template sets `AcceptsRequests` | `NotAccepted` |
   | 7 | Duration finite and at least `MinSeconds`; magnitude above 0 and finite. Values above `MaxSeconds` and `MaxMagnitude` are cut to them | `InvalidDuration`, `InvalidMagnitude` |
   | 8 | The target character is alive | `TargetDead` |
   | 9 | The requester has a character on this client | `SenderMissing` |
   | 10 | That character is alive | `SenderDead` |
   | 11 | `RefuseSeated`: the requester is not seated | `SenderSeated` |
   | 12 | `RefuseInTube`: neither character is in a clone tube | `InCloneTube` |
   | 13 | `Stance`: `Hostile` needs the two characters on different sides, `Friendly` on the same side, `Any` accepts both | `Stance` |
   | 14 | `MaxRange`: the distance between the two characters, when above 0 | `OutOfRange` |
   | 15 | `RequiresLineOfSight`: nothing between the two characters' chests | `NoLineOfSight` |
   | 16 | Rate: a token bucket per requester and key, `Burst + 1` tokens, one more every `MinInterval` seconds | `RateLimited` |

4. An accepted request applies the status as a local `Apply` would, with the cut duration and
   magnitude and the same stacking and callbacks, with one difference: **a request never
   shortens or weakens a running status.** Its end moves only later, and a larger magnitude
   re-applies every stack at the larger value. Without that rule, a 0.25 s request would end
   another player's 30 s status, a remote removal under another name.
5. The target's client answers the requester with the verdict: `None` for applied, else the
   refusal. A refusal is also logged there as a warning naming the key, the requester's actor
   number and the reason; at most one line per requester, operation and key per 30 s.

**The handle.** `CharacterStatusRequest` carries `Target`, `Key`, `Sent` (the request left this
client), `Done`, `Refusal` and `Applied` (done with `None`). `OnCompleted(callback)` runs the
callback once when the verdict is in, at once if it already is, and returns the handle. A request
applied here or refused by this client's own checks is done before `Request` returns. A sent one
is done when the answer arrives; with `SendFailed` at once when Photon refuses to send it (not
connected); or with `NoAnswer`. `NoAnswer` means the outcome is unknown: after 10 s with no answer,
or when 40 newer requests to the same player are waiting and this is the oldest. No answer comes
when the target left, its client dropped the request at its cap across keys or failed while
handling it, or the answer was lost or arrived late; in the last case the status was applied. A
role ability that spends a cooldown on a request can refund it from the callback on a refusal
other than `NoAnswer`.

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
  the owning client and is not read. A client that joined after a player died learns of it from
  that character's first health update, which marks a character at 0 health dead.
- **Seated** is the seat ability (chairs, the helm, turrets) being active; its state is
  synchronised to every client. A seat can be a legitimate source, so the check is opt-in.
- **In a clone tube** is a clone tube holding the character as its occupant, or, on the client
  that owns the character, its in-tube ability being active. The game keeps a respawning
  character alive while it sits in the tube.
- **Range** is the distance between the two characters' positions (their feet). The target's
  client checks `MaxRange` exactly. The requesting client checks the larger of `MaxRange` × 1.15
  and `MaxRange` + 1 m. Each client sees the other's character one network update late, so a
  request near the edge looks in range on one client and out of range on the other; the looser
  check on the requesting side lets such a request reach the target's client, whose view is the
  one that counts, and the answer reports its verdict.
- **Line of sight** is a line cast between points 1.2 m above each character's feet along its up
  axis, against the `StaticPlatform`, `MovingPlatform` and `SpaceObjects` layers, triggers
  ignored: hull and level geometry, and asteroids, NPC hulls and wrecks between two players in
  space. Each ship's colliders stay in the main physics scene, so the cast also hits another
  ship's walls. Interactive objects are left out so a console or a carried item does not hide a
  player standing behind it. Characters sit on their own layer, so neither end of the line hits
  its own body. A game without those layers refuses every line-of-sight check and logs one
  error.
- **Rate** counts accepted requests only. The requesting client keeps `Burst` tokens per target
  and key; the target's client keeps one more. Two requests sent exactly `MinInterval` apart can
  arrive closer together when the first is delayed more than the second, and the extra token
  absorbs that. A modified client gains at most one extra request at once, never a higher
  sustained rate.

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
| `AcceptsRequests` | `false` | Accept requests. Off, every request is refused `NotAccepted`. |
| `Stance` | `Any` | `Any`, `Hostile` (another side from the target) or `Friendly` (the target's side). |
| `MaxRange` | `0` | Metres, measured on the target's client. 0 checks no range. |
| `RequiresLineOfSight` | `false` | Refuse when something blocks the line between the two characters. |
| `RefuseSeated` | `false` | Refuse a seated requester. |
| `RefuseInTube` | `false` | Refuse when the requester or the target is in a clone tube. |
| `MinInterval` | `0` | Seconds per request from one requester for this key. 0 checks no rate. |
| `Burst` | `1` | Requests sent back to back before `MinInterval` applies. |
| `MinSeconds` | `0.25` | A shorter requested duration is refused. |
| `MaxSeconds` | `30` | A longer requested duration is cut to this. |
| `MaxMagnitude` | `1` | A larger requested magnitude is cut to this. A magnitude of 0 or below is refused: it would turn a debuff into a buff, or pin the status at no effect. |

`Register` refuses a template with `Burst` below 1; a negative or non-finite `MinInterval` or
`MaxRange`; a `MaxSeconds` that is not above 0; a `MinSeconds` outside 0 to `MaxSeconds`; or a
`MaxMagnitude` that is not above 0. It logs one error naming the field. A template that sets
request checks without `AcceptsRequests`, or presentation fields without `Presented`, registers
with one warning.

**Refusals from the requesting client's own checks** are the reasons above plus
`InvalidTarget` (null, no stat collection or no `PhotonView`), `NoPeer` (the target's client does
not run this framework on this network contract), `TargetDead` for this client's own dead
character, and `RateLimited` past the target's cap across keys (kept here too: a burst of 20,
refilled at 20 a second, per target) or when 256 requests are already waiting for an answer.

## Removal

Only the client that owns the character can end a status on it. No removal travels on the
network. `Remove` on another player's character removes only a copy this client's own `Apply`
left there; otherwise it logs one warning per key, and `TryRemove` returns `Authority`.
`TryRemove` returns `None` when it removed the status or there was none.

## Death

The game keeps the character object through death and respawn: it marks the character dead, and
the clone-tube respawn marks the same object alive again. Every death passes through the
character's life-state setter on every client, and the framework hooks it. When a character's
state leaves alive, each client ends every status it runs on that character, with `OnRemoved`,
and every presentation copy shown on it. On the owning client that ending also tells the peers'
copies.

Nothing is applied to a dead character. `Apply` on one logs one warning per key and does
nothing; `Request` on this client's own dead character is done with `TargetDead`, and the target's
client refuses a request for a dead character the same way. A status put on a corpse would
otherwise outlive the respawn. So a respawned character starts with no status, and a status
applied from the game's respawn or clone events lands on the living character.

`OnRemoved` runs after the status's modifiers are gone and it is no longer active, so applying
the same key again from it starts a fresh status. On death it runs inside the game's own death
handling, before the game marks the character's health dead, and, when a tick killed the
character, inside that status's own `OnTick`. An `OnRemoved` must not kill the character: the
game would announce the death twice.

A character that is destroyed outright (leaving the room, a scene change) ends its statuses on
the next poll.

## Presentation copies

A template with `Presented = true` is shown to other players. After an application on a
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
  (removal, death, a role switch's `RemoveAll`) is sent to every peer that received the copy.
- **A re-application** is sent again when it moves the end by 250 ms or more, and otherwise only
  to audience peers that have not received the copy yet. One status is announced at most every
  0.5 s; a re-application inside that gap is sent when it ends.
- **Only a character's own client may announce a status on it.** A receiver refuses an
  announcement naming any other actor's character, one for a key whose template is not
  `Presented` on the receiver. A copy lasts at most an hour, and a receiver takes a burst of 20
  announcements from one player, refilled at 20 a second. A copy may be announced for a character
  the receiver still sees dead: the owner's respawn reaches the other clients after any status the
  respawn applied.
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
- **Every client that should take or see a status registers its template** under the same
  literal key. A client without it ignores requests and announcements for the key, logs a
  throttled warning, and answers a request `NotRegistered`. A mod that requests statuses on
  other players therefore runs on every client: publish it with the Thunderstore category
  `All Required`.

## Checking it

Turn on `[Debug] VerboseLogging` on both clients.

| Line | Check |
| --- | --- |
| `Ectype: character status '<key>' requested on <name> (actor <a>, request <id>, <s> s, magnitude <m>).` | On the requester: the request left. |
| `Ectype: character status '<key>' applied on request from actor <a> (<s> s, magnitude <m>, <d> m).` | On the target: accepted, with the distance it measured. |
| `Ectype: refused character status '<key>' request from actor <a> on <name>: <reason>.` | On the target: refused, and why. |
| `Ectype: character status '<key>' request <id> to actor <a> answered <refusal>.` | On the requester: the verdict arrived. |
| `Ectype: presenting character status '<key>' on <name> for <s> s.` | On a third client: a copy started. |
| `Ectype: <name> died: ended <n> character status(es) and <m> presentation copy(ies).` | On each client: a death ended what it ran on that character. |

[troubleshooting.md](../troubleshooting.md) lists every line with its cause.

Sample: [examples/character-statuses](../../examples/character-statuses/).
