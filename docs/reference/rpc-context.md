# RPC context: who sent the message being handled

`RpcContext` reports the sender of the Photon RPC or event the game is dispatching right now.
Most of Void Crew's request RPCs take no `PhotonMessageInfo`, so a prefix on one cannot tell who
asked; it reads the sender here.

```csharp
// Plugin.Awake, on every client that reads the sender:
RpcContext.Enable();

// A prefix on an RPC method of a network object:
static bool Prefix(PowerDrain __instance)
{
    Photon.Realtime.Player sender;
    if (!RpcContext.TryGetSender(__instance.photonView, out sender))
        return !RpcContext.FromAbsentPlayer(__instance.photonView); // not an RPC: game logic
    return MayChangePower(sender, __instance);
}
```

## Members

| Member | Meaning |
| --- | --- |
| `Enable()` | Asks for the hooks. Idempotent. |
| `Installed` | Whether the hooks are in. While false every member below reports no message. |
| `Source` | `None`, `Rpc` or `Event`: the kind of the innermost message being dispatched. |
| `Sender` | The `Photon.Realtime.Player` who sent it. Null outside a message, for a sender no longer (or not yet) in the room, and for a message the server sent. |
| `SenderActor` | The actor number Photon stamped on it, also for a sender who has left. 0 outside a message and for one that carries no sender. |
| `ViewId` | The view id the RPC targets; 0 for an event. |
| `EventCode` | The event's code; 0 for an RPC. Test `Source` before trusting a 0. |
| `TryGetSender(view, out sender)` | The sender of the RPC running now **on this view**. False outside an RPC, for an RPC on another view, for a sender no longer in the room, and before the hooks are in. |
| `FromAbsentPlayer(view)` | True when the RPC running now on this view has no sender in the room but carries the actor number Photon stamped: a buffered RPC PUN replays to a later joiner after its sender left. False for a message the server sent (actor 0). |

## How it knows

- **RPCs.** PUN runs every RPC, from the network or a player's RPC to themselves, inside one
  internal method, `PhotonNetwork.ExecuteRpc(Hashtable, Player)`. It is handed the sender
  Photon's server stamped on the event, which a client cannot forge, and calls the RPC method
  before it returns. Key byte 0 of the RPC data is the target view id.
- **Events.** Every event a client receives, PUN's RPC event and ownership events (209, 210,
  212) included, is dispatched inside `LoadBalancingClient.OnEvent`, which hands it to PUN and to
  every `EventReceived` listener before it returns. A raised event's sender is `EventData.Sender`.
- A prefix and finalizer on each method record the message for the length of the call and
  restore the outer one afterwards, so the innermost message is the one reported: an RPC that
  arrived over the network reports the RPC, not the event that carried it. A network RPC whose
  sender has left keeps the actor number of the event that carried it in `SenderActor`.
- The prefixes run first among the patches on each method, and a finalizer restores the outer
  message only when its own prefix ran: another patch's prefix that skips the original makes
  every finalizer run, and one whose prefix did not run must not clear the message of the
  dispatch it is nested in.
- The event being dispatched stays known inside an RPC dispatched within it, so a check that
  needs the event (a socket request) finds it under the RPC.

## When it is valid

- **Only on the call stack of the dispatch.** A value read in the RPC method, in a patch on it,
  or in anything they call synchronously is the message's. A coroutine, a callback scheduled for
  later, or the next frame sees whatever is being dispatched then, usually nothing.
- **Check the view.** A handler can call a method on another object directly. `TryGetSender`
  and `FromAbsentPlayer` compare the RPC's target view with the view passed, so that call is not
  taken for an RPC on the other object. `Sender` alone does not.
- **A local RPC has the local player as sender.** An RPC a player sends to themselves, or to
  all with themselves included, runs locally with the local player as sender, without the
  network. An owner-side check skips its own player's requests.
- **Outside a message, a request is game logic.** A method the game calls directly, not as an
  RPC, reports no sender. A check that refuses only what it can attribute lets it through;
  `FromAbsentPlayer` separates that case from a replay with no sender.
- **The host's game sends requests of its own** to a part another player owns (a system
  switching off when the power goes). Treat the master client as trusted in an owner-side check.

## When the hooks go on

The hooks are patched from the framework's driver once both hold:

1. `RpcContext.Enable()` was called, an interaction gate is registered, or a member was read.
2. The game's state machine has left its first state and the loading screen.

Never at plugin load: patching `PhotonNetwork` at start-up sets Photon up before the game does,
and the game's connection then never gets past the name server. A message dispatched before the
hooks are in reports no sender, so call `Enable()` from `Awake`: the hooks are then in before the
player can join a room. A read without `Enable()` asks for them from that read on and logs one
warning. With nothing asking, Photon is never patched.

## Things that look like they should work

- **Reading the sender in a coroutine or callback started by the RPC.** The dispatch has
  returned by then. Read it in the method or its patch and carry it.
- **`Sender != null` as "this is an RPC".** A raised event also has a sender. Test `Source`, or
  use `TryGetSender(view, ...)`.
- **Reading the sender in a prefix on a method of another object** that the RPC handler calls.
  `Sender` reports the RPC; `TryGetSender` with the other object's view reports false.
- **Expecting a sender for a message received before the game loaded.** Nothing is recorded
  until the hooks are in.

## Log lines

| Line | Meaning |
| --- | --- |
| `Ectype: RPC context: <n> of 2 Photon hooks patched after the game loaded.` | Once per launch, once the hooks were asked for and the game left its loading screen. |
| `Ectype: RPC context: <Type>.<method> not found (Photon update?); <what is left unknown>.`, `... could not be patched; <what is left unknown>: <message>` | One hook is missing; the other works. |
| `Ectype: RPC context: read before RpcContext.Enable() was called; the hooks go on once the game has loaded, and a message dispatched before then has no sender.` | A mod read a member without calling `Enable()`. |
| `Ectype: RPC context: <hook> hook failed (logged once); that message has no sender: <exception>` | A hook threw. The message is dispatched as usual. |

Related: [interaction-gate.md](interaction-gate.md), [multiplayer.md](../multiplayer.md),
[troubleshooting.md](../troubleshooting.md).
