# Timed stat contributions: a modifier that follows a curve

`TimedStatContributions` puts one `StatMod` on any `StatTagCollection`, with an amount that is
a function of progress through a fixed window.

```csharp
TimedStatContribution c = TimedStatContributions.Start(
    "mymod.afterburn", ship.Stats, "ForwardPower", ModifierType.ExponentialMultiplier,
    TimedStatContributions.LinearDecay(0.5f),          // amount at progress u, 0..1
    durationSeconds: 8f, startServerMs: PhotonNetwork.ServerTimestamp,
    keepAlive: () => stillWanted);                     // optional
TimedStatContributions.End(c);                         // early
TimedStatContributions.Find(ship.Stats, "mymod.afterburn");  // + c.Active, Progress, Amount, EndReason
```

- **The amount moves through the game's dynamic-value path.** The mod carries a
  `ModDynamicValue`. Ten times a second its recalculation rewrites the amount in place, and
  each collection that registers the stat marks it dirty and raises `OnModifiersChange`.
  Nothing is re-applied until the end. Field-backed stats follow it (see
  [framework-stats.md](framework-stats.md)).
- **Starting a live key again replaces it** on that collection, so restarts never stack.
- **It ends** when the window closes, when `keepAlive` returns false, on `End`, when the active
  session changes, and when the collection's holder is destroyed. The last case leaves the
  dead collection untouched. The others call `RemoveModifier` with the mod's source.
- **A copy of the modifier is harmless.** The game deep-copies stat modifiers, and other mods
  copy dynamic values before applying them. The copier recurses through every field, so the
  mod's dynamic value and its source hold only an integer handle and a label, never the
  contribution or its target collection. A copy reads the same curve while the contribution
  runs and contributes nothing once it ends. The stat tooltip still shows the label and the
  progress.
- **Nothing is networked.** Progress is time on `PhotonNetwork.ServerTimestamp` since
  `startServerMs`, so machines that each start the same contribution from the same replicated
  signal and stamp agree without messages. A stamp in the past starts part-way along the
  curve. A machine that never saw the signal, such as a joiner who arrived after it, runs
  none.
- Use a status ([enemy statuses](enemy-statuses.md), [character
  statuses](character-statuses.md)) for a fixed amount on an enemy or a character. Use this
  for an amount that moves, or for any other collection. With nothing live the driver step is
  one count check.

A signal to start from on every machine is [ship-arrivals.md](ship-arrivals.md).

Sample: [examples/timed-stat-contributions](../../examples/timed-stat-contributions/).
