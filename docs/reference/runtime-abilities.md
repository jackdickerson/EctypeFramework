# Runtime ability replication by key

The game replicates an Opsive ability by its position in the character's ability array. An
ability a mod adds to a character after spawn, rather than building it into the prefab, breaks
vanilla replication unless every client installed the same abilities in the same order.

`RuntimeAbilities.Register(ability, key)` routes such an ability through a keyed VoidManager
message instead of the vanilla index-based RPC. Order across clients stops mattering, and a
peer without the ability simply ignores the message.

- Call `Register` after the ability is in its locomotion's array. `Unregister` and `KeyOf`
  are the other two members.
- Register with a literal key. A key derived from runtime state can differ between clients.
- The framework registers its own role abilities as `ectype:<roleId>`.
- A running registered ability is also synchronised to a late joiner. The message is sent to
  the joiner alone, and only when VoidManager reports that peer as running the framework
  (see `PeerHasMod` in [selection-gate.md](selection-gate.md)).
- A start that reaches a character whose look source is not attached yet, as a late joiner's
  copy of a crewmate is for a moment after spawning, is held and retried for up to 5 s; a
  stop for the same character and key cancels it.
- A prefab ability, one built into the character prefab, falls through to vanilla replication
  untouched.

## Recipients

Start and stop messages go only to peers that VoidManager reports as running the framework.
A peer whose mod list has not arrived yet is not known to run it, so a start or stop sent in
that window does not reach it. The late-join sync above covers a joiner. The gap that remains,
for a peer already in the room whose list is late, is accepted and not repaired.

## TODO: install helper and fuller documentation

This page and its sample describe the call that replicates an ability, not the work around it.
Installing an ability on every character at runtime takes a recipe: poll for characters that
have spawned, grow the locomotion's ability array, register after the ability is in it,
start a manual ability by hand, and do the same for remote characters. The sample spells that
recipe out in its `Update` and `Install` methods. The framework should provide a helper that
does it, and this page should then document it fully. Until then, treat the sample as the
install recipe.

Sample: [examples/runtime-abilities](../../examples/runtime-abilities/).
