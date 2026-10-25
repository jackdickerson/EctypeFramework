# Ship arrivals: leaving the void

`ShipArrivals` raises events on every machine from the one game method that changes the
void-jump state.

```csharp
ShipArrivals.Arrived += a =>                 // ShipArrival { Ship, Kind, ServerMs }
{
    if (a.Kind == ShipArrivalKind.Normal) { /* ... */ }
};
ShipArrivals.LeftNormalFlight += ship => { /* alignment toward a jump began */ };
ShipArrivals.FleetArrived += jump => { /* every ship that jumped is out of the void */ };
bool flying = ShipArrivals.InNormalFlight(ship);
```

Every player ship has its own void-jump state machine. In a room with several ships,
`Arrived` and `LeftNormalFlight` fire once per ship per jump, each time with that ship.
`FleetArrived` fires once per jump.

- **`Arrived`** fires when the jump state returns to `Inactive` from the void. On a machine
  that ran the exit state, the destination sector has been entered and its objective started
  by then. A machine whose stream skipped it enters through the room property on its own
  schedule. The run's first sector raises nothing: the state machine starts at `Inactive`, and
  asking for the state it is in changes nothing. A joiner's fresh state machine starts at
  `Inactive` and adopts the room's index on its first read, which therefore can never be an
  arrival. A resumed save's first arrival is a real arrival and fires.
- **`Kind`** is `Normal` when the spin-down ends, or the approach on a machine whose stream
  skipped the 3 s spin-down. It is `Interdiction` when an unstable tunnel throws the ship out;
  vanilla flings it backward at 300000 / Mass and spins it. It is `Unknown` when this machine
  went straight from a travelling state to `Inactive` and never saw which exit ran. Treat
  `Unknown` as not normal.
- **`ServerMs`** is when the exit state's owner left the void, on
  `PhotonNetwork.ServerTimestamp`: the replicated enter stamp plus the state's fixed length. A
  timed effect started from it on every machine has the same phase everywhere, however late
  the transition reached each one.
- **`LeftNormalFlight`** fires when the state leaves `Inactive` / `ExitVectorSet`, the set
  vanilla allows cruise control and thruster boosts in. A joiner adopting a room already in the
  void raises it too.
- **`Arrived` names the ship that arrived.** A subscriber that acts on one ship compares
  `a.Ship` with that ship. It never compares with `ClientGame.PlayerShip`: a multi-ship mod can
  make that getter return the ship each client is viewing, so the comparison would come out
  differently on each client. [topology.md](topology.md) resolves a player's own ship.
- **`FleetArrived`** fires once per jump on every machine. Each machine keeps the set of ships
  that have left normal flight. A ship leaves the set when it is back in normal flight, whether
  it arrived or the jump was called off, and when it dies or is destroyed. When the set empties
  and at least one ship arrived, `FleetArrived` fires. Its value describes the whole jump:
  `Ship` is the last ship to arrive, `Kind` the worst kind any ship had (`Interdiction`, then
  `Unknown`, then `Normal`), and `ServerMs` the latest stamp. With one ship it fires right after
  that ship's `Arrived`, with the same value. Each machine raises it when its own copy of the
  last ship's state arrives, so the moment differs between machines by the replication delay.
  `ServerMs` is the same on every machine that saw each ship's exit state; a machine whose
  stream skipped a ship's spin-down stamps that ship with its own clock, as for `Arrived`.
- Each subscriber runs in its own try/catch. The process's first arrival and first fleet
  arrival log at `[Info]`; later ones log under `VerboseLogging`:

  ```
  Ectype: ship arrived from the void (Normal, from VoidJumpSpinningDown, server <ms>, seen <n> ms later here, <k> subscribers).
  Ectype: fleet arrived from the void (<ships> ship(s), Normal, server <ms>, <k> subscribers).
  ```

  A jump with three ships logs the first line three times and the second once.

Related: [timed-stat-contributions.md](timed-stat-contributions.md) and
[ship-speed-surge.md](ship-speed-surge.md) both start from an arrival's `ServerMs`.

Sample: [examples/ship-arrivals](../../examples/ship-arrivals/).
