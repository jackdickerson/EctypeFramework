# Enemy statuses: timed buffs and debuffs on space objects

Register a status template once at plugin `Awake`, then apply it to any `CG.Space.OrbitObject`
(NPC ships, turrets, asteroids) for a duration. The framework replicates the application to
every peer, drives the timer on Photon's server clock, and removes everything the status added
on expiry or target death.

```csharp
// at Awake, on every client (register from a literal - the key crosses the wire):
EnemyStatuses.Register(new EnemyStatusDef("mymod.breach")
{
    DisplayName = "Hull Breach",
    DamagePerSecond = 15f,        // raw damage, dealt by the target's owner
    TickSeconds = 0.5f,
    MaxStacks = 3,
    // marker on the affected object, countdown circle included (on by default):
    Icon = EnemyStatuses.IconFromEmbeddedPng(
               Assembly.GetExecutingAssembly(), "breach-icon"),   // null = stock icon
    MarkerRequiresTargetLock = true,          // hide unless the ship has it locked
    // looping visual on the object while active - a loaded stock prefab, cloned and
    // stripped to visuals:
    VfxDonorName = "FX_EngineTrail", VfxScale = 2f,
}.AddStat("MaxFlightSpeed", "percent", -0.25f));

// at runtime, from any client:
EnemyStatuses.Apply(target, "mymod.breach", seconds: 8f, magnitude: 1f,
                    attacker: myShip);
EnemyStatuses.Remove(target, "mymod.breach");        // early removal, everywhere
EnemyStatuses.IsActive(target, "mymod.breach");      // + RemainingSeconds, Stacks
```

## Rules and limits

- **Stat entries only bite stats the target registers.** Every `OrbitObject` has
  `MaxHitPoints` and `Invulnerability`. NPC ships (`SpaceCraft`) add `MaxFlightSpeed`.
  Level-scaled NPCs add their level-stat set. Child collections (modules, turrets) are reached
  too. An unmatched stat no-ops silently in game code; the framework logs one warning per
  (status, target type) when nothing matched. Use the callbacks for anything without a
  registered stat: `OnApplied` and `OnRemoved` run on every client, `OnTick` on the target's
  owner only.
- **Re-applying refreshes the timer** (`RefreshOnReapply`) **and compounds up to `MaxStacks`**.
  Each stack applies the stat entries again. `magnitude` scales every amount and the damage
  over time at apply time.
- **Any client may apply; only the owner deals damage.** HP replicates through the game's own
  serialization, so damage over time ticks exactly once.
- **The template must be registered on every client that should see it.** A peer without the
  key ignores the message with one log line. A status-applying mod should be `MPType.All`, or
  accept divergence on peers without it. Applying statuses changes what a run contains, so a
  mod that does it should treat the session as modded; see
  [../multiplayer.md](../multiplayer.md).
- **The marker is the game's own countdown visual.** It has the same `ObjectMarkerData` shape
  the game builds for an NPC's reinforcement-call channel, so the stock viewer draws and
  animates it. `Icon` null falls back to the stock ability icon. The marker's colour comes
  from the game's own affiliation rule for the target, the one its object markers use, then the
  marker table's per-asset override. `MarkerRequiresTargetLock` gates visibility on the target
  locks of the ship this client is viewing, and follows the view when it moves to another
  ship.
- **Presentation is capped.** `[EnemyStatuses] MarkerCap` and `VfxCap` bound concurrent
  markers and visuals; past the cap a status runs undecorated rather than failing. `Markers`
  and `Vfx` turn each layer off outright. See [config.md](config.md).
- **Visuals.** `CreateVfx` returns a fresh GameObject the framework manages. Alternatively
  `VfxDonorName` (plus optional `VfxDonorChild`) clones a loaded stock prefab, stripped to
  visuals only (no behaviours, colliders or PhotonView). A donor name matching nothing logs
  once and attaches nothing.
- **Late joiners do not receive statuses already running.** The effect exposes its header and
  description through `IDescriptiveModifierSource`.
- **`SuspendsAbilities`** freezes the target's abilities, and its turrets', for as long as the
  status runs. See [enemy-abilities.md](enemy-abilities.md). A refresh extends the same
  freeze.
- **`SkipsInvulnerableTargets`** makes `Apply` refuse a target whose `IsInvulnerable` is true
  at that moment (its `Invulnerability` stat above zero). The check runs on the applying
  client.

For a status on a player character, see [character-statuses.md](character-statuses.md). For a
modifier whose amount changes over time, see
[timed-stat-contributions.md](timed-stat-contributions.md).

Sample: [examples/enemy-statuses](../../examples/enemy-statuses/).
