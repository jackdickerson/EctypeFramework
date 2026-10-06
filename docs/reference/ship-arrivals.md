# Ship arrivals: leaving the void

`ShipArrivals` raises events on every machine from the one game method that changes the
void-jump state.

```csharp
ShipArrivals.Arrived += a =>                 // ShipArrival { Ship, Kind, ServerMs }
{
    if (a.Kind == ShipArrivalKind.Normal) { /* ... */ }
};
ShipArrivals.LeftNormalFlight += ship => { /* alignment toward a jump began */ };
bool flying = ShipArrivals.InNormalFlight(ship);
```

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
- Each subscriber runs in its own try/catch. The process's first arrival logs at `[Info]`;
  later ones log under `VerboseLogging`.

Related: [timed-stat-contributions.md](timed-stat-contributions.md) and
[ship-speed-surge.md](ship-speed-surge.md) both start from an arrival's `ServerMs`.

Sample: [examples/ship-arrivals](../../examples/ship-arrivals/).
