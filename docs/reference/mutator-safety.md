# Mutator safety

Nothing to call. A mod that adds a mutator gets this protection from the framework, so it
does not carry its own copy of the guard.

## The failure

`GameSession.InitStartingMutators` resolves every active mutator GUID against the client's
own `MutatorContainer` and dereferences the result unconditionally. A mutator whose content
mod is not installed on a client resolves to `null` and throws a `NullReferenceException` the
instant the host activates it. This happens for a mutator mod that is legitimately
client-optional (`Client-Side` on Thunderstore, `Client` to VoidManager) rather than
required: its effects are host-only, gated on `PhotonNetwork.IsMasterClient` and carried by
the game's normal replication, so a client without the mod has no reason to refuse the room.

## What the framework does

A prefix on `InitStartingMutators` filters `StartingMutators` down to the GUIDs the client's
`MutatorContainer` actually has, and logs one warning per GUID it drops.

The galaxy map has the same unguarded lookup. Its "continue" card lists the preserved
session's saved mutators through `MutatorsElementListVE.UpdateMutatorsList`, so a saved run
that used a mutator whose mod is now disabled throws on every click of the card. A prefix on
that method passes the original a filtered copy and warns once per GUID. The saved list is
not modified, so re-enabling the mod brings the selection back.

Registering a mutator asset still counts as content that changes what a run contains; see
[../multiplayer.md](../multiplayer.md) for the session flag and the join checks.
