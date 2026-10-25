# Interaction gate: refusing what a player may use

`InteractionGate` lets a mod refuse a player the use of a seat, a terminal, a door, a switch, an
item or any other interactable, by a rule it registers. The framework asks every rule at the
places where the game decides an interaction, on the acting player's own game and again on the
game that owns the part, and refuses when any rule refuses.

```csharp
InteractionGate.Register("mymod.host-helm", q =>
    q.Kind == InteractionKind.Helm && !q.ActorIsHost
        ? Refusal.Because("helm is host-only", "ONLY THE HOST MAY PILOT.")
        : Refusal.Allowed);

InteractionGate.Unregister("mymod.host-helm");
```

A complete plugin: `examples/interaction-gate`.

## Verdict

- **Gates only refuse.** A gate returns `Refusal.Allowed` (or `null`, read the same way) or a
  refusal. Nothing grants, so no gate can open what another closed, and the verdict does not
  depend on load order.
- **The first refusal decides.** Gates run in registration order. The first that refuses stops
  the run, and its text is the one shown. A silent refusal (no text) shows nothing, even when a
  later gate would have refused with text.
- **A gate that throws counts as allowing.** The exception is logged once per gate id, and the
  remaining gates still run.
- **Ids are unique.** `Register` returns false and logs a warning for an id already registered;
  unregister first to replace a gate. `Registered` lists the ids in order and `AnyRegistered`
  says whether any exist.
- **Keep a gate cheap.** It runs on the main thread, every frame for the interactable the player
  is looking at.

`Evaluate(query)` runs the gates on a query a mod built itself, for an interaction of its own;
it shows and logs nothing.

## The query

| Member | Meaning |
| --- | --- |
| `Actor` | The acting `Photon.Realtime.Player`. Null only outside a room. |
| `ActorNumber`, `ActorIsHost` | Its actor number (0 outside a room); whether it is the room's host. |
| `Character` | The actor's character, or null (not spawned, spectating). |
| `Interactable` | What is being used. One thing reaches every chokepoint, on both sides, as one component: an item is its `CarryableObject` (not its grab handle), an item sitting in a socket and a socket's interactable are the `CarryablesSocket`, as the game's own raycast reads them. Otherwise it is the part the chokepoint names: the seat, the terminal, the door, the airlock, the power component, the panel, the module, or the network object an ownership event names. A gate that tests a type should still prefer `GetComponent` on it. |
| `Ship` | The player ship `Interactable` belongs to, through `Topology.ShipOfPart`, or null. |
| `Kind` | What the player is trying to do (below). |
| `Side` | `Client` or `Owner` (below). |

`InteractionGate.Classify(component)` gives the kind the shared classifier reads for a
component, for a mod that wants to reason about the same categories.

## Kinds

| Kind | Covers |
| --- | --- |
| `Seat` | A one-player seat (`TakeoverChair`) that is neither the helm nor a turret. The game's multi-seat bench component (`Chairs`) is not judged. |
| `Helm` | The helm's seat. |
| `Turret` | A turret's seat. |
| `Terminal` | A module terminal (the astral map is one), a world-space terminal screen. |
| `Periscope` | The periscope. |
| `CloneTube` | A clone tube. |
| `Minigame` | A minigame that is not a System Enhancement panel's: a repair's or a defect's. |
| `EnhancementPanel` | A System Enhancement panel: its switch, its minigame, a request to change its state. |
| `Door` | A door's control, an airlock door's button, a request to open or close either. |
| `AirlockControl` | An airlock's pressurise lever or safety override, and the requests they send. |
| `PowerSwitch` | A module's power switch, the central computer's, a power line's, a breaker, any switch outside a module, and the requests they send. |
| `CarryablePickup` | Picking up a loose item, or taking one from a carrier that is not a socket. |
| `SocketInsert` | Putting the carried item into a socket. |
| `SocketRelease` | Taking an item out of a socket, or ejecting it. |
| `ModuleUpgrade` | The module upgrade tool working on a module. |
| `Other` | Anything else: a synced setting the classifier does not name, an unknown interactable. |

Most kinds come from one classifier, the same on the client side and the owner side, that reads
the component's type, then the station objects above it (a periscope, a clone tube, a terminal
screen, an enhancement panel, a minigame). A control that sits outside the object it controls
is registered by that object as it starts: a door's clickers, an airlock panel's lever and
safety switch, a panel's switch and minigame, a seat's clicker, a module terminal's clicker, a
repair's and a defect's clicker. A switch is judged by what it switches: a power switch when a
module above it names it as its power switch, else exactly as the component holding the value
it controls is judged (the component the owner side judges when the switch's request arrives).
A switch with no module above it and no value holder is a power switch; one met while its
module is still being assembled keeps that answer and is computed again two seconds later,
three computations in all. Where the
chokepoint itself says what is happening, the chokepoint names the kind: a socket request, a
door or airlock request, a power request, the upgrade tool.

## Where the gate is asked

One interaction is judged twice. The acting player's own game asks first, for the prompt and
the warning. The game that owns the part asks again when the request arrives, because a
modified client can skip its own check. A gate sees `Side` and may answer differently, but see
the consistency rule below.

### Client side

The acting player's game, before anything is sent. It also covers the host's own actions,
which never cross the network.

| Chokepoint | What a refusal does |
| --- | --- |
| The interaction raycast's final pick (after occlusion, and after an item in a socket became the socket) | No pick: no highlight, no prompt. A warning shows when the pick is within 2.3 m, the game's default reach. One verdict per frame. |
| A seat's takeover (`TakeoverChair.TrySitInChair`) | Nothing is requested; warning. A player already in the seat is not judged again when the game re-asks after a host change. |
| A module terminal opening, a terminal screen opening | The terminal does not open; warning. |
| A repair or a defect repair starting | Nothing starts: no instant-win roll, no skill modifier; warning. |
| The station activation event (clone tubes, the periscope, a mod's own station) | The station does not start; warning. |
| The carry interaction (pick up, insert, take out) | Nothing happens; warning. A swap at an occupied socket is judged as all three of its steps: the insert, the release and the pick-up of the old item. |
| The pick-up itself, at the end of the click's frame, and the pick-up that follows a swap | Nothing is requested, and the carry lock the game set is cleared; warning. |
| The module upgrade tool's target check | The module is an invalid target and the tool stops; warning. |

Each station is refused at its own entry, before the game changes anything. The game raises
the activation event while it takes a seat, switches the view to a terminal around that event,
and applies a minigame's skill modifier and rolls its instant win before it, so a refusal at the
event would leave a seat taken with nobody in it, a terminal view the player cannot leave, or a
modifier applied with no minigame running. A System Enhancement panel's minigame is refused at
the raycast only, because the panel's animation reacts to the click itself; its state change is
refused again on the owner side. A minigame already running is not interrupted when a gate
starts refusing.

### Owner side

The game that owns the part, when a request arrives from another player. A refusal drops the
request and shows nothing to anyone.

| Chokepoint | Asked on | Sender from | What a refusal does |
| --- | --- | --- | --- |
| A seat request | the seat's owner (the host while it is free) | the RPC's message info | dropped, as the game drops one while the seat's module is being rebuilt |
| An ownership request for an item a carrier holds | the carrier's owner | Photon's callback | the item is not handed over |
| A socket insert, release or eject request | the socket's owner | the Photon event being dispatched | a rejected promise, which the game answers with its own failure reply |
| A synced switch or setting request | the part's owner | the RPC sender | dropped |
| A power request (system, power line, whole ship) | the part's owner | the RPC sender | dropped |
| A panel state request | the panel's owner | the RPC sender | dropped |
| A door request | the host | the RPC's message info | the host answers with the door's real state, which undoes the asker's early swing |
| An airlock door, cycle or safety request | the host | the RPC's message info | dropped |
| An ownership change (PUN events 209, 210 and 212) of a ship's part, an item in a socket or a loose item | the object's owner, or the host when no owner is present | the event (209) or the new owner it names (210, 212) | ignored there, and the owner hands the object back to itself (below) |

Where the asker's game has already moved, the hook sends the game's own reply that undoes it:
the host answers a refused door with the door's real state, and a refused socket request ends
in the game's own failure reply. Nothing else is sent.

**Never judged on the owner side:**

- **The host.** The host's game sends requests of its own to a part a pilot owns (a system
  switching off when the power goes), and the host's own actions are judged on its client side.
- **The owner's own requests.** A request the owner's game sends itself runs through the same
  RPC path with the owner as sender.
- **A request whose sender is unknown.** Outside an RPC, or before the RPC sender hook is in, a
  request is game logic calling the method directly, and it passes.
- **A hand-off by the host or by the object's owner**, such as the game passing a socket's item
  to the socket's new owner. **An object following its holder**: a socket's item going to whoever
  owns the socket, a seat's connected object going to whoever owns the seat. **An item a player
  carries.**

The owner side stops what a client asks for through the game's request paths. A modified client
that writes state directly (a broadcast "seat taken" call, a serialized value) is outside it.

### Ownership changes

Most items, and the network objects a seat hands its pilot, can be taken over at will: PUN
changes their owner on each client by itself when a player takes one (209) or is handed one
(210), without asking the owner. The ownership update (212) sets owners on any object, and only
the game's host sends it.

- **Either judge refusing returns the object to its previous owner.**
- **The owner decides.** The client that owns the object, or the host for an object whose owner
  is absent, judges the change. On a refusal it ignores the change and hands the object back to
  itself. The server delivers that hand-back after the refused change to every client, so a
  client that applied the change, with or without the framework, falls back to the owner.
- **The taker's own game judges its own request too**, with its client-side verdict. On a
  refusal it answers the game's pending pick-up as a failure, so the carry interaction is not
  left waiting for an owner change that never comes, and hands the object back to the owner it
  still records. Every client that applied the request falls back with it, so the room
  converges even where the owner allowed the change. An owner that answers a request-only
  object by handing it over gets it handed back by the taker on the next frame.
- **A grab that loses a race is settled too.** When PUN refuses the local player's takeover
  because the item changed hands first (two players grabbing one item, or a grab during a
  hand-back), the pending pick-up is answered as a failure, so the grabber can carry again.
- **An ownership update from anyone but the host** has its refused pairs rewritten to the
  current owner; the rest of the update applies unchanged.
- **A client without the framework is harmless for ownership once the hand-back arrives**, about
  one round trip to the owner after its own request came back. Until then it holds the item. Its
  game drops an item it is still pulling in when the hand-back lands (the pull takes about 0.3 s
  per metre); a pick-up that completed before the hand-back stays in that player's hands on
  their game, while the owner keeps simulating it, until they put it down. A taker without the
  framework whose request a request-only owner refused waits on it, locked, as in the game.
- Every client that judges an ownership change must give the same answer. A gate that differs
  between the owner and the taker leaves the taker holding an item the owner then takes back.

### The RPC sender

Most of the game's request RPCs take no message info, so the method cannot tell who asked. PUN
runs every RPC, from the network or from a player to themselves, inside one internal method
(`PhotonNetwork.ExecuteRpc`) that is handed the sender the Photon server stamped on the event,
and calls the RPC method before it returns. A prefix and finalizer on that method record the
sender and the target network object for the length of the call, restoring the outer pair
after an RPC run inside another. An owner-side check asks for the sender of the RPC now running
on its own network object.

The socket requests are Photon events that each carrier answers while Photon is dispatching
them, so a prefix and finalizer around the dispatch hold the event being handled.

These Photon hooks are installed once a gate is registered and the game's state machine has
left its first state and the loading screen, never at plugin load: patching `PhotonNetwork` at
start-up sets Photon up before the game does, and the game's connection then never gets past
the name server. With no gate registered they are never installed. Until they are, the
owner-side checks that rely on them (power, panel, synced-switch and socket requests, ownership
changes) let everything through.

## The consistency rule

A gate must give the same answer on every client that judges an interaction: the acting
client, the part's owner, and for an ownership change the object's owner and the taker. It may
read only state they agree on: room properties, replicated statuses, replicated ability state,
factions, the actor's identity, and positions with slack. A gate that reads state only one game
has, or a configuration only one player set, makes the client allow and the owner refuse: the
player sees the prompt, uses it, and nothing happens, or picks an item up and loses it a moment
later.

Check: with the gate registered, the same interaction on a client and on the host logs the same
verdict (turn on `[Debug] VerboseLogging` for the client side).

## Warnings

A client-side refusal with text shows it through the game's quick-warning channel, the line the
game itself uses for "already occupied". The throttle is framework-wide, whichever gate refused:
at most one warning every 3 s, and the same text at most every 20 s. Two gates therefore cannot
double the rate.

- `Refusal.Because(reason, text)` refuses with a warning; `Refusal.Silently(reason)` refuses
  without one. `reason` goes to the log only.
- `InteractionGate.SuppressWarningsFor(id)` keeps a gate's refusals and drops their text, for a
  mod that shows its own warning for the same rule. `SuppressWarningsFor(id, false)` undoes it.
  It applies to a gate registered later under that id.

## Living alongside Void Crew Fleet

Void Crew Fleet patches the same client and owner chokepoints for its boarding rules. The
framework's hooks are ordered after Fleet's, and:

- a framework hook never changes the outcome when every gate allows, so Fleet's refusals keep
  their effect, and either mod refusing refuses;
- on a client hook, when Fleet has already refused (the method will not run, or the pick is
  already cleared), the framework hook returns without asking any gate, so the player sees
  Fleet's warning alone.

Two warnings can still follow each other when Fleet refuses one interaction and a gate refuses
a different one, since each mod throttles its own. A mod that wants one warning stream for
both rules registers its rule as a gate and turns its own client-side checks off; with
`SuppressWarningsFor` it can keep its own warning text instead.

## Gates and manifest suppression

Suppression and the gate answer different questions:

| | Suppression | Gate |
| --- | --- | --- |
| Unit | content: a stock gene tree, a role | an action by a player on a part |
| Scope | the process (manifest), or the room (host setting) | each interaction: per player, per side, per moment |
| When | resolved once, or when the host changes it | on every interaction |

"Remove the Gunner tree" is suppression. "Only the Captain may pilot" is a gate. A gate cannot
stop another mod's background system (automatic turrets, a spawner), because that is not an
interaction.

A role manifest does not declare gates. The shape a declaration would take, compiled to a
registered gate when the role registers:

```json
"gates": [
  { "id": "captain.helm", "refuse": ["Helm"], "unless": ["holder"],
    "text": "THE CAPTAIN HAS THE CONN." }
]
```

`refuse` names kinds, `unless` names conditions every client can evaluate (the role's holder,
an ability running, the host), and `text` is the warning. Each condition must obey the
consistency rule, which is why the vocabulary would be closed rather than free code.

## Uses

| Use | Gate | Notes |
| --- | --- | --- |
| A role forbids everyone else the helm | refuse `Helm` unless the actor holds the role and its ability is running | Ability state replicates, so both sides agree. A player already seated is not ejected. |
| A mutator disables turrets for a period | refuse `Turret` while the server clock is before an end stamp held in a room property | Automatic turrets are not interactions. Keep them off with a validator on their power drain's `IsOn` value (`ChangeValidators`, a list of `Func<old, new, bool>` every change must pass). |
| Sabotage as a status | refuse `PowerSwitch` and `EnhancementPanel` on a ship while a replicated status runs on it, with the remaining time in the text | |
| Host moderation of a griefer | refuse every kind for actors on a host-written list in a room property | The room property reaches every client, so the owner side agrees. |
| Lockdown | refuse `Door` to players outside the holder's side while an ability runs | |
| A tutorial | refuse `Terminal` until an objective completes | |

## Things that look like they should work

- **A client without the gate gets no prompt hiding and no warning.** The owner side still
  drops its requests, so the player clicks and nothing happens; an item such a player takes
  over is handed back by its owner about one round trip later (see Ownership changes). Install the gating mod on every client.
- **A seat taken before a gate started refusing stays taken**, and a minigame already running
  runs to its end. The gate judges the start, not what follows.
- **A refused airlock safety request leaves the asker's switch shown flipped** until the next
  real change of the safety state: the switch flips its look locally before asking. Only a
  client that skipped its own check reaches this.
- **A refused socket release or eject on the owner side** makes the game log its own error for
  the rejected promise on the asker's game. The client side judges a swap's release up front,
  so an asker running the gate does not reach it.
- **A station whose clicker sits outside it** and that registers nothing shows a prompt; the
  use is refused at the station's own entry, with the warning.
- **The superior grappling hook pulls a loose item in** without the raycast. The pick-up at the
  end goes through the carry interaction and is judged there.
- **Owner-side checks before the game has loaded, or before the first gate registers,** let
  power, panel, synced-switch and socket requests and ownership changes through.
- **A refusal on the owner side shows the acting player nothing** unless their own game ran the
  gate first.

## Log lines

| Line | Meaning |
| --- | --- |
| `Ectype: interaction gate '<id>' registered (<n> gate[s]).`, `Ectype: interaction gate '<id>' unregistered (<n> left).` | A gate was added or removed. |
| `Ectype: interaction gate '<id>' is already registered; unregister it first. This registration is ignored.` | `Register` with a taken id returned false. |
| `Ectype: interaction gate: Register needs an id and a gate; ignored.` | `Register` with an empty id or a null gate. |
| `Ectype: interaction gate '<id>' threw and counts as allowing (logged once per gate): <exception>` | A gate threw. It is still asked; each later throw is silent. |
| `Ectype: interaction gate '<id>' refused <Kind> <object> by player <n> (Owner)[: <reason>] (further refusals of this kind to this player are logged in verbose mode only).` | The first owner-side refusal of a kind to a player by a gate. |
| `Ectype: interaction gate '<id>' refused <Kind> <object> by player <n> (<Client or Owner>)[: <reason>].` (verbose) | A client-side refusal, logged when it differs from the last one logged, and each repeated owner-side one. |
| `Ectype: interaction gate: <n> of <m> hooks patched; the Photon hooks wait for the game to load.` | At load. A missing hook has its own line naming the member and what is left unchecked. |
| `Ectype: interaction gate: <n> of <m> Photon hooks patched after the game loaded.` | Once per launch, once a gate is registered and the game has left its loading screen. |
| `Ectype: dev check: a refused pick-up left the carry interaction unlocked.` or `Ectype: dev check: FAILED - the carry interaction is still locked 0.5 s after a refused pick-up (or a new pick-up started within that time).` (dev tools build only) | Half a second after the local player's pick-up was refused. |
| `Ectype: interaction gate: <Type>.<method> not found (game update?); <what is left unchecked>.`, `... (Photon update?); ...`, `... could not be patched; <what is left unchecked>: <message>` | One chokepoint is missing; the rest work. |
| `Ectype: interaction gate: <Type>.<member> not found (game update?).`, `Ectype: interaction gate: <Type>.<method> not usable (game update?); <consequence>.` | A classifier registration seam is missing; those controls classify by type and parents alone. |
| `Ectype: interaction gate: <hook> hook failed (logged once): <exception>` | A hook threw. It let the interaction through. |
| `Ectype: interaction gate: the quick-warning channel failed (logged once); warnings are not shown: <message>` | Refusals still refuse; the player sees no text. |
| `Ectype: interaction gate: the ownership failure listener could not be added; a grab that loses a race stays locked as in the game: <message>` | Refusals still work; only the race settle is missing. |

Related: [topology.md](topology.md) (ships, sides, `ShipOfPart`),
[multiplayer.md](../multiplayer.md), [troubleshooting.md](../troubleshooting.md).
