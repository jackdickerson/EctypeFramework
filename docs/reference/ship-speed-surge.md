# Ship speed surge

`ShipSpeedSurge` is a top-speed boost on the player ship that decays linearly to nothing. It
is built from [timed stat contributions](timed-stat-contributions.md), [ship
arrivals](ship-arrivals.md) and the `ShipDrag` stat (see [framework-stats.md](framework-stats.md)).

```csharp
ShipArrivals.Arrived += a =>
{
    if (MyGate() && a.Kind == ShipArrivalKind.Normal)
        ShipSpeedSurge.Start(a, magnitude: 1f, durationSeconds: 20f);  // optional keepAlive
};
ShipSpeedSurge.End(ship);                                              // + IsActive(ship)
```

- **The formula.** The top-speed factor is s = 1 + magnitude * (1 - u), where u is progress
  through the window. It is applied as `ShipDrag` x 1/s^2 (`ShipSpeedSurge.DragFactor`). Drag
  is quadratic in speed, so full-thrust top speed goes as 1/sqrt(drag) and the cut makes it s
  times normal. Magnitude 1 doubles top speed at the start. A negative magnitude above -1
  slows the ship.
- **Why drag.** Thrust acts only while the pilot holds forward, and the pilot aid's auto-brake
  is a thrust input scaled by `ForwardPower`, so a thrust boost stops a coasting ship sooner
  than vanilla. Vanilla already throws the ship out of the void at 25000 / Mass (312.5 m/s on
  an 80-mass hull), and drag bleeds that to cruise speed in about two seconds. A drag cut lets
  that throw carry, piloted or not.
- **The cut is isotropic.** Strafe and vertical speed scale too, and braking, including the
  20 m/s floor that stops a coasting ship, is 1/s^2 as strong. An interdiction's backward
  fling would carry the same way, so start the surge on `Normal` arrivals only.
- One surge per ship; starting another replaces it. It refuses to start outside normal flight
  and ends when the ship leaves it.
- Start it on every machine from `Arrived`. A machine that joined after the arrival has no
  surge, and if it then takes the helm the room's authoritative simulation has none, so the
  surge ends early for everyone.

Sample: [examples/ship-speed-surge](../../examples/ship-speed-surge/).
