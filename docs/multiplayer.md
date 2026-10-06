# Multiplayer

What every player in a room needs when a mod depends on EctypeFramework, how the game and
VoidManager refuse a join, and what a dependent mod has to tell its players. The VoidManager
behaviour described here is that of VoidManager 1.2.11.

## The short version

- Every player in a room runs the framework, and every role mod any player has. The same
  build of each: the same version number and the same DLL bytes.
- A role mod declares `MultiplayerType.All`. The framework declares it too.
- Two independent checks refuse a join. Passing one says nothing about the other.
- Hosting a room with the framework installed marks the session as modded. The mark cannot be
  withdrawn from that room, and a modded session does not count toward progression.
- Both checks run at join. A member already in the room is never re-checked. A dependent that
  lets the host select mixed content mid-session uses `SelectionGate` to check the room itself.

## The two join checks

### VoidManager's mod-list check

VoidManager publishes each player's mod list: for every plugin that declares a
`VoidPlugin`, its name, GUID, version and `MultiplayerType`. The list reaches every player
through a Photon player property and, republished by the host, a room property. A plugin
declared `Hidden` is left out of the list entirely.

The build hash travels separately. It is the SHA-256 of the plugin DLL file, computed when
the plugin is discovered, and a client sends it once, at join, to the master client only.
No path delivers a hash to any other client. The hash covers the whole assembly, so an
embedded manifest or icon is covered with the code.

The check runs twice:

1. **On the joining client, before it enters.** The client reads the host's list from the
   room properties and diffs it against its own in both directions. On any failure it
   disconnects itself with `Disconnected: Incompatable mod list` (VoidManager's spelling).
   This is the common rejection. A mod that only the client has is reported as `Host is
   missing the required mod`; a mod that only the host has, as `Client is missing the
   required mod`.
2. **On the host, when the joiner arrives.** The host matches the joiner's entries by GUID,
   compares `Version`, then compares the hash, and kicks on a failure. This is the only place
   a hash is compared, so it is the only place that catches two different builds carrying the
   same version number.

Six `MultiplayerType` values exist and two of them can refuse a join:

| Type | Refuses a join when |
| --- | --- |
| `All` | the versions differ, the hashes differ (host-side only), or either side lacks the mod |
| `Session` | the joiner declares it and the room is not already a modded session |
| `Client`, `Host`, `Unmanaged` | never |
| `Hidden` | never, and the mod is invisible to every peer |

`Client` keeps a mod in the published list without ever refusing anyone. A mod that does its
own room-composition logic needs that, because the published list is the only channel through
which peers learn what each other has installed. A `Session` mod is not version-matched: a
stale copy joins and desyncs silently, so ship it to the whole room as if it were `All`.

### The game's asset check

`GameSessionManager.ValidateMods`, called from `JoinGameSession`, compares registered
assets, and knows nothing about mods. It collects two lists: assets the host's session holds
that the joiner lacks, and assets the joiner holds that the host lacks. Either list refuses
the join. Both sides read the same source, the registered assets whose
`SessionModificationEffect` falls inside the `Required` mask (value 94), so the comparison is
symmetric. A client with required content cannot join a host that lacks it, and the reverse.

The rejection screen names individual assets. VoidManager's screens name mods. That is how
the two checks are told apart in a bug report.

## Why role content is mandatory for the room

The framework registers each role's gene tree and every perk inside the `Required` mask.
Three facts bear on a mixed room:

- The asset check runs at join, before the room's members are known and before any mod could
  negotiate. It cannot compute "the roles everyone shares".
- The check reads the asset registry. A screen can hide a role and leave its registry entries
  in place, and the framework's per-role suppression does exactly that. Only unregistering
  removes an asset, and nothing unregisters during a process.
- A client that lacks a role's perk GUIDs cannot apply another player's perks. The game's
  perk reapply throws on an unknown GUID and leaves that player's perk set empty on the
  client that threw. Only a process running the framework's guard avoids it.

A role mod therefore declares `All`, depends on the framework with `[BepInDependency]`, and
is installed by everyone in the room. A design that relies on players holding different role
sets is refused by the game regardless of the `MultiplayerType` a mod declares.


## Same build

The version number alone proves little. Asset GUIDs are fixed constants, so the asset check
sees no difference between two builds of the same mod. Two builds with one version number
pass VoidManager's client-side comparison, because that side holds no hash. Only the host's
check compares bytes.

Rules that follow:

- Raise the version in your `[BepInPlugin]` attribute on every build you hand to another
  player. The version is the part of the check that every client can run.
- A dependent's DLL records the version of the framework assembly it was compiled against. A
  rebuild against a newer framework changes the dependent's bytes at the same version number.
  After a framework update, rebuild every dependent, raise each version, and reship them
  together with the framework.
- Send the whole room one set of files. A player who rebuilds locally and a player who
  downloaded the release hold different hashes.

## Host migration

When the master client changes, the new host holds only hashless records for the members who
were already in the room, because VoidManager sends the hashed list once, at join, to whoever
is master at that moment. A hashless record carries a hash of 32 zero bytes. The host can
compare versions with it but not builds.

On `MasterClientSwitched` every non-master client's framework calls VoidManager's own send
routine with its own list, so the new host receives the hash it lacks. The send is a
reflection call into VoidManager internals. If VoidManager's internals move, the framework
logs one error and those members stay unverified on the new host. Members who join after the
migration send their hashed list in the normal way.

The per-role suppression toggles belong to the host, so on a migration they take the new
host's configuration. A role the old host had suppressed is available again if the new host
has not suppressed it, and the reverse.

## The selection gate

Both join checks run at join. A member who is already in the room when the host selects mixed
content (content that only some members' mods provide) is never re-checked. `SelectionGate`
answers on the host: does every other member run mod X at the host's version and build?

```csharp
SelectionGate.Register("com.example.mymod", r =>
{
    if (r.Verdict == GateVerdict.Fail)
        MyPolicy.Block(r.DescribeFailures());     // "Bob (missing), Cy (version 0.3.1)"
});

GateResult now = SelectionGate.Evaluate("com.example.mymod");   // on demand, unpublished
GateResult last = SelectionGate.Current("com.example.mymod");   // last published result
```

`Register` runs the callback once immediately and again whenever the result for that GUID
changes. `SelectionGate.Changed` is the same notification for every registered GUID. The gate
decides nothing: refusing, reverting and warning are the dependent's policy.

The host's own version and hash come from VoidManager's own list for this client. Each other
member's come from VoidManager's record of that peer.

Per member:

| State | Meaning | `DescribeFailures` / `DescribeNonMatching` text |
| --- | --- | --- |
| `Match` | same version and same hash as the host | name only |
| `NotKnownYet` | no mod list from this member yet | `(mod list not received)` |
| `Unverified` | same version, no hash to compare | `(version only, no hash)` |
| `Missing` | the member's list does not carry the mod | `(missing)` |
| `VersionMismatch` | different version | `(version X)` |
| `HashMismatch` | same version, different bytes | `(different build of X)` |

`Missing`, `VersionMismatch` and `HashMismatch` are failures. `DescribeFailures` lists those;
`DescribeNonMatching` lists every member who is not `Match`.

The verdict for the whole room:

| Verdict | Meaning |
| --- | --- |
| `Fail` | at least one member failed |
| `Pending` | no failure, but at least one member's list has not arrived |
| `Unverified` | no failure and every list is in, but at least one member could only be checked by version |
| `Pass` | every other member matches, or there is no one else (solo, offline, not in a room) |
| `NotHost` | this client is in a room and is not the master; the host owns the answer |
| `Unavailable` | the host's own list could not be read or does not carry the mod; `Reason` says why |

`Unverified` is what a host holds for a member after a migration until that member's resent
list arrives, and what any evaluation holds for a member whose hash never reached it. The gate
does not say whether to treat it as open or closed.

The gate re-evaluates on VoidManager's `ClientModlistRecieved`, `PlayerLeftRoom` (the leaver
is excluded, because VoidManager raises the event before dropping the leaver's record),
`MasterClientSwitched`, `PlayerEnteredRoom`, `JoinedRoom` and `LeftRoom`. It binds those events
once, in the framework's `Awake`, and a dependent loaded first still gets the binding on
`Register`. Bind `ClientModlistRecieved` for anything of your own that reads a peer's mods.
`PlayerEnteredRoom` fires before the list arrives, and reading that gap as "no mods" briefly
excludes every mod in the room.

`SelectionGate.PeersWithMod(guid)` and `SelectionGate.PeerHasMod(player, guid)` are hashless
presence queries and answer on any client. The framework's runtime-ability replication uses
them to send only to peers that run the framework.

## The host's per-role toggle

Every role the framework discovers gets a host-only suppress toggle in the framework's
config and in the F5 menu. The host's choice is published as a room property that every
client applies, so every client gives the same answer to
`RoleRegistry.IsAvailableInRoom(roleId)`. That call is true while the role is registered
and neither the toggle nor another role's `suppressRoles` hides it. Gate role-owned
content (loot, spawns) on it.

On a host migration the toggles take the new host's configuration. A suppressed role stays registered. Every player still needs it installed, the perks stay in
each saved loadout and are read as absent, and spent points count as unspent while the
suppression lasts. Lifting it restores the build. Leaving the room clears it. Suppressing a
role changes what every player in the room can do, so it flags the session as modded.

## The modded-session flag and progression

The game tags a room as modded with the room property `R_Mod`. The tag shows in the server
browser. VoidManager's progression handler blocks cloud profile writes and Steam stats and
achievements for a tagged session.

A mod sets the tag by any of three routes:

1. **Registering an asset in the `Required` mask.** The host publishes the tag whenever the
   registry holds any required asset, whether or not the feature is ever used.
2. **Declaring `Session` or `All`.** VoidManager raises a process-wide static,
   `SessionModdingType`, when such a plugin loads. The host publishes the tag from it for every
   room the process hosts afterwards. Declaring `Client` or `Hidden` does not raise it.
3. **Returning `SetMod_Session = true` from `OnSessionChange`.** VoidManager calls it on room
   creation, on session start and on join.

The tag is one-way. A room that has it keeps it, and `SessionModdingType` is never reset
during a process, so rooms hosted later in the same play session carry it too. Only a full
game restart clears it. A mod's own `Enabled` config key does not un-flag a session: for a
`Session` or `All` mod the static is raised at load, whatever the config says.

Consequences for a dependent of the framework:

- The framework declares `All`, so any room hosted by a process that loaded it is tagged, even
  with no role mod installed and no role selected. A dependent cannot defer the flag.
- A role mod's own assets sit in the `Required` mask, so route 1 applies as well.
- A mod that must stay silent until its content is used cannot depend on the framework.

## What your mod's page must tell players

For a mod that declares `All`:

- The framework is required, by its name and Thunderstore dependency, and every player in the
  room installs it.
- Every player in the room installs your mod. Say which build, and say that a different build
  of either DLL is refused at join or kicked by the host.
- When you release an update, every player updates together, including the framework if you
  rebuilt against a new one.
- Hosting with the mod installed marks the session as modded and turns off progression for it,
  and the mark lasts until the game restarts. Disabling the mod in its config does not undo it.
- A player who has the mod cannot join a host who lacks it. The asset check is symmetric.
