# Selection gate: is every member on my build?

`SelectionGate` answers, on the host, whether every other member of the room runs a given mod
at the host's version and build. The join checks run only at join, so a member already in the
room when a mixed mod's content is selected is never re-checked. A mod registers its GUID and
applies its own policy (refuse, revert, warn) from the result. The gate decides nothing.

How the two join checks work, and why they leave this gap, is in
[../multiplayer.md](../multiplayer.md), which also lists the per-member states and the room
verdicts. This page covers the API and the sources of its answers.

```csharp
SelectionGate.Register("com.example.mymod", r =>
{
    if (r.Verdict == GateVerdict.Fail) Refuse(r.DescribeFailures()); // "Bob (missing), ..."
});
GateResult now = SelectionGate.Evaluate("com.example.mymod");   // on demand
```

## Where the answers come from

The check is host-side. The host's version and hash come from VoidManager's
`MPModCheckManager.MyModList`. That list is private; the framework reads it by reflection and
never rebuilds it from the chainloader. Each other member's version and hash come from
`NetworkedPeerManager.GetNetworkedPeerMods`.

Per member the state is `Match`, `NotKnownYet` (no list from that member yet), `Unverified`
(version matches, hash absent: a hashless record carries 32 zero bytes, not null), `Missing`,
`VersionMismatch` or `HashMismatch`. The verdict is `Fail` if any member fails, otherwise
`Pending`, `Unverified` or `Pass`. Solo, offline and out-of-room are `Pass`. A non-master
client gets `NotHost`. An unreadable host list is `Unavailable`.

A member whose list has not arrived is `NotKnownYet`. The gate does not state how long
VoidManager takes to deliver a list or what it does when none arrives.

VoidManager sends each hashfull list once, on join, to whoever is master then, so a host that
inherits the room holds only hashless records. On `MasterClientSwitched` every non-master
client's framework calls VoidManager's own `NetworkedPeerManager.SendModlistToHost` (internal,
by reflection) with that same list. The new master's handler stores it and raises
`ClientModlistRecieved`, and the members verify by hash again. The handler runs no mod check
and no kick. A member whose resend never arrives stays `Unverified`. See
[../multiplayer.md](../multiplayer.md#host-migration).

## When it re-evaluates

The gate re-evaluates on VoidManager's `ClientModlistRecieved`, `PlayerLeftRoom` (the leaver
is excluded, because the event fires before VoidManager drops their record),
`MasterClientSwitched`, `PlayerEnteredRoom`, `JoinedRoom` and `LeftRoom`. The events are bound
once, at framework `Awake`. A registered callback, and the global `SelectionGate.Changed`
event, fire only when a mod's result changes.

## Presence queries

`PeersWithMod` and `PeerHasMod` are hashless presence queries, valid on any client. The
framework's runtime-ability replication uses them to send only to peers that run the
framework; see [runtime-abilities.md](runtime-abilities.md).

A peer whose mod list has not arrived yet is not yet known to run any mod, so both queries
leave it out. A message addressed from these queries and sent in that window does not reach
that peer. The runtime-ability replication accepts this gap: late-join sync covers a joiner,
and an ability start or stop that a peer already in the room misses because its list is late
is not resent.

Sample: [examples/selection-gate](../../examples/selection-gate/).
