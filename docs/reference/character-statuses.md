# Character statuses: timed buffs and debuffs on a player

`CharacterStatuses` is the counterpart to [enemy statuses](enemy-statuses.md) for a
`CG.Game.Player.Player` target. Register a template once at plugin `Awake`, then apply it for
a duration. The framework drives the timer and removes everything the status added on expiry,
death or despawn.

```csharp
CharacterStatuses.Register(new CharacterStatusDef("mymod.dash-guard")
{
    DisplayName = "Dash Guard",
    MaxStacks = 1,                   // re-dashing refreshes rather than compounds
}.AddAllVulnerabilities("flat", -0.99f));

CharacterStatuses.Apply(player, "mymod.dash-guard", seconds: 1f);
CharacterStatuses.Remove(player, "mymod.dash-guard");   // early removal
CharacterStatuses.RemoveAll(player);                    // e.g. on a class switch
CharacterStatuses.IsActive(player, "mymod.dash-guard"); // + RemainingSeconds, Stacks
```

## Rules and limits

- **Local to the applying client, which is correct for stats.** Character damage resolves on
  the victim's own client every way it can be dealt. NPC damage abilities gate
  `ApplyHitDamage` on `Target.AmOwner`. Environmental damage (void rifts, damage volumes,
  thruster wash, black holes, oxygen and temperature) reaches a character only through
  `LocalPlayer`. `CalculateRawDamage` calls `ApplyVulnerability` in-process, with no RPC
  inside the damage path to move that decision elsewhere. A resistance or vulnerability mod
  that exists only on the victim's client is therefore read by the code that matters. The one
  non-victim-side path, a ship weapon's projectile computed on the shooter's client, is zeroed
  for same-faction targets before vulnerability is read.
- **Callbacks and visuals are not shared.** `OnApplied`, `OnTick` and `OnRemoved` run only
  where `Apply` was called. A status whose payload the crew must see needs replication, which
  is not implemented. Setting `Replicated = true` on a template makes the framework log one
  warning naming the key, so the gap is noticed rather than silent.
- **Timing is `Time.time`.** Enemy statuses use the Photon clock; a local status has no peer
  to agree with. Expiry and `OnTick` are both indexed from the status's own start, so a frame
  hitch pays every missed tick and a double poll pays none twice.
- **Stat entries only bite stats the character registers.** That is whatever `Player.Awake`
  registered, plus the framework's locomotion stats (see
  [framework-stats.md](framework-stats.md)). Unmatched entries no-op silently in game code;
  the framework logs one warning per (status, target type). `AddAllVulnerabilities` covers
  every damage type plus the aggregate. That is the right default: vanilla character perks
  modify the per-type stats, and the aggregate's read path on characters is unverified.
- **A respawned character is a different target.** Statuses are keyed on the character
  instance, so a new body never inherits the old one's entries, and a status whose target was
  destroyed ends itself on the next poll.

Sample: [examples/character-statuses](../../examples/character-statuses/).
