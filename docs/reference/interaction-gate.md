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
| `Interactable` | What is being used. An item sitting in a socket is the socket, as the game's own raycast reads it. On the owner side it is the part the request names: the seat, the socket, the door, the airlock, the power component, the panel, or the network object an ownership event names. |
| `Ship` | The player ship `Interactable` belongs to, through `Topology.ShipOfPart`, or null. |
| `Kind` | What the player is trying to do (below). |
| `Side` | `Client` or `Owner` (below). |

`InteractionGate.Classify(component)` gives the kind the shared classifier reads for a
component, for a mod that wants to reason about the same categories.

## Kinds

| Kind | Covers |
| --- | --- |
| `Seat` | A seat that is neither the helm nor a turret. |
| `Helm` | The helm's seat. |
| `Turret` | A turret's seat. |
| `Terminal` | A module terminal (the astral map is one), a world-space terminal screen. |
| `Periscope` | The periscope. |
| `CloneTube` | A clone tube. |
| `Minigame` | A minigame that is not a System Enhancement panel's: a repair's or a defect's. |
| `EnhancementPanel` | A System Enhancement panel: its switch, its minigame, a request to change its state. |
| `Door` | A door's control, an airlock door's button, a request to open or close either. |
| `AirlockControl` | An airlock's pressurise lever or safety override, and the requests they send. |
| `PowerSwitch` | A module's power switch, the central computer's, a power line's, a breaker, and the requests they send. |
| `CarryablePickup` | Picking up a loose item, or taking one from a carrier that is not a socket. |
| `SocketInsert` | Putting the carried item into a socket. |
| `SocketRelease` | Taking an item out of a socket, or ejecting it. |
| `ModuleUpgrade` | The module upgrade tool working on a module. |
| `Other` | Anything else: a synced setting the classifier does not name, an unknown interactable. |

Most kinds come from one classifier that reads the component's type, then the station objects
above it (a periscope, a clone tube, a terminal screen, an enhancement panel, a minigame). A
control that sits outside the object it controls is registered by that object as it starts: a
door's clickers, an airlock panel's lever and safety switch, a panel's switch and minigame, a
seat's clicker and a module terminal's clicker. A switch is a power switch when a module above
it names it as its power switch; a switch met while its module is still being assembled is
read again two seconds later. Where the chokepoint itself says what is happening, the
chokepoint names the kind: a socket request, a door or airlock request, a power request, the
upgrade tool.

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
| The interaction raycast's hit test | The interactable is not a valid hit: no highlight, no prompt. A warning shows when the player is within 2.3 m, the game's default reach. |
| The raycast's final pick (after an item in a socket became the socket) | No pick. |
| A seat's takeover (`TakeoverChair.TrySitInChair`) | Nothing is requested; warning. A player already in the seat is not judged again when the game re-asks after a host change. |
| A module terminal opening, a terminal screen opening | The terminal does not open; warning. |
| A minigame starting (repairs, defects, panels) | The minigame does not start; warning. |
| The station activation event (clone tubes, the periscope, a mod's own station) | The station does not start; warning. |
| The carry interaction (pick up, insert, take out) | Nothing happens; warning. A backstop for what the raycast offered. |
| The module upgrade tool's target check | The module is an invalid target and the tool stops; warning. |

Seats and terminals are refused at their own entry, before the game changes anything. The game
takes a seat before it raises the activation event, and switches the view to a terminal around
it, so a refusal at the event would leave a seat taken with nobody in it, or a terminal view the
player cannot leave.

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
| An ownership takeover (PUN events 209 and 210) of a ship's part, an item in a socket or a loose item | every client | the event (209) or the new owner it names (210) | ignored, so the object stays with its owner everywhere except, at most, on the taker's own game |

The game's own reply is the only thing sent back, and only where the asker's game has already
moved and needs it to undo: a door's real state, a socket's failure reply. The gate never sends
a message of its own.

**Never judged on the owner side:**

- **The host.** The host's game sends requests of its own to a part a pilot owns (a system
  switching off when the power goes), and the host's own actions are judged on its client side.
- **The owner's own requests.** A request the owner's game sends itself runs through the same
  RPC path with the owner as sender.
- **A request whose sender is unknown.** Outside an RPC, or before the RPC sender hook is in, a
  request is game logic calling the method directly, and it passes.
- **An item a player carries**, in an ownership takeover. The interaction rules keep those.

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

These Photon hooks are installed once the game's state machine has left its first state and
the loading screen, never at plugin load: patching `PhotonNetwork` at start-up sets Photon up
before the game does, and the game's connection then never gets past the name server. Until
then, owner-side checks that need the RPC sender let everything through.

## The consistency rule

A gate must read only state the acting client and the owner agree on: room properties,
replicated statuses, replicated ability state, factions, the actor's identity, and positions
with slack. A gate that reads state only one game has makes the client allow and the owner
refuse: the player sees the prompt, uses it, and nothing happens, or a seat request hangs. For
ownership takeovers every client judges, so a gate that disagrees between clients splits who
owns an object.

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
- on a client hook, when Fleet has already refused (the method will not run, or the hit is
  already invalid), the framework hook returns without asking any gate, so the player sees
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

A role manifest does not declare gates in this version. The shape it would take, compiled to a
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
  drops its requests, so the player clicks and nothing happens. Install the gating mod on every
  client.
- **A seat taken before a gate started refusing stays taken.** The gate judges the takeover,
  not the sitting.
- **A station whose clicker sits outside it** and that registers nothing shows a prompt; the
  use is refused at the station's own entry, with the warning.
- **The superior grappling hook pulls a loose item in** without the raycast. The pick-up at the
  end goes through the carry interaction and is judged there.
- **Owner-side checks before the game has loaded** let requests that need the RPC sender
  through.
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
| `Ectype: interaction gate '<id>' refused <Kind> <object> by player <n> (<Client or Owner>)[: <reason>].` (verbose) | Every client-side refusal, and each repeated owner-side one. |
| `Ectype: interaction gate: <n> of <m> hooks patched; the Photon hooks wait for the game to load.` | At load. A missing hook has its own line naming the member and what is left unchecked. |
| `Ectype: interaction gate: <n> of <m> Photon hooks patched after the game loaded.` | Once per launch, when the game leaves its loading screen. |
| `Ectype: interaction gate: <Type>.<method> not found (game update?); <what is left unchecked>.`, `... (Photon update?); ...`, `... could not be patched; <what is left unchecked>: <message>` | One chokepoint is missing; the rest work. |
| `Ectype: interaction gate: <Type>.<member> not found (game update?).`, `Ectype: interaction gate: <Type>.<method> not usable (game update?); <consequence>.` | A classifier registration seam is missing; those controls classify by type and parents alone. |
| `Ectype: interaction gate: <hook> hook failed (logged once): <exception>` | A hook threw. It let the interaction through. |
| `Ectype: interaction gate: the quick-warning channel failed (logged once); warnings are not shown: <message>` | Refusals still refuse; the player sees no text. |

Related: [topology.md](topology.md) (ships, sides, `ShipOfPart`),
[multiplayer.md](../multiplayer.md), [troubleshooting.md](../troubleshooting.md).
