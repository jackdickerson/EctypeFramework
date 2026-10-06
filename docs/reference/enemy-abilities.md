# Enemy abilities: one model over the game's systems

Enemies start special actions through seven separate game systems. The framework classifies
each by its game type, so any enemy built from them is covered, including enemies a game
update or another mod adds.

| Kind | Game type | Cast | Channel / sustained | Cooldown |
| --- | --- | --- | --- | --- |
| `npc-ability` | every `BaseNpcAbility` on an `NpcAI` | `CastTime` | `Duration`, or until turned off | `Cooldown` |
| `sniper-cycle` | `BehSniperAttack` | warm-up, lock-on | the volley | post-fire wait |
| `debuff-latch` | `BehDebuffApplier` | | latched while in range | orbit wait |
| `stat-aura` | `BehStatModifyWhileOrbiting` | | buff held on an ally | |
| `shield-pulse` | `BehShieldGenerateAOE` | | | the pulse period |
| `reinforcement-behaviour` | the call made by `BehSpawnReinforcements` | | the call | |
| `reinforcement-caller` | `NpcReinforcementCaller` | | the call | once per life |

```csharp
List<EnemyAbilityInfo> list = EnemyAbilities.Describe(target);  // target + its turrets
bool any = EnemyAbilities.HasAny(target);
bool held = EnemyAbilities.IsSuspended(host);
```

Each `EnemyAbilityInfo` carries its host, kind, name (the ability's `ContextInfo` header where
it has one) and phase: `Ready`, `Casting`, `Channeling`, `Sustained`, `Cooldown` or `Spent`.
Basic weapon fire and movement are not abilities. A behaviour type outside the lists the
framework knows (ability or movement/attack) logs one warning naming it the first time an
enemy carrying it is described. That line is how a new game behaviour that should be an
ability gets noticed.

## Suspension

Suspension is what an enemy status with `SuspendsAbilities` does (see
[enemy-statuses.md](enemy-statuses.md)). While it runs, casts hold where they are, channelled
and sustained effects are lifted, cooldowns stop, and nothing new starts. At the end
everything resumes from where it stopped. It runs on every client that has the status.

- **Ticks are skipped.** Every ability's `Tick` and `PassiveTick`; the ability steps of the
  four ability behaviours, whose movement still runs (a sniper keeps repositioning, a latch
  keeps following its target); both reinforcement calls; and the `NpcWeapon` carrying a volley
  in progress (one held by an active volley ability, or a sniper's while it fires). Weapons
  not in a volley keep firing.
- **Clocks are moved.** Every timer these systems read is a server timestamp, and the
  per-tick accumulators stop on their own when their tick is skipped. Each frame of
  suspension moves the timestamps forward by that frame's server time, so a remaining cast,
  channel or cooldown is unchanged at the end. A timer the game resets during the suspension
  (a boss phase change readying its next abilities) only moves for the frames after its
  reset. The total is capped at the status's own end, which every client shares, so a client
  the status reached late freezes the same span.
- **Effects are lifted.** An ability's stat modifiers carry the ability as their source.
  Every modifier on the sector's ships, the players and the enemy whose source is one of the
  enemy's abilities or stat actions is removed, from the outermost collection holding it, and
  re-applied at the end. A source whose state changed meanwhile (an ability that stopped or
  started, a stat action that changed target) is left to manage its own. Nothing is
  re-applied for a host destroyed or disabled meanwhile, whose own cleanup has already run.
- **Starts are held.** `TryActivateAbility(force: false)` is refused. `force: true` is a
  replica copying its controller's ability state from the network stream, and it passes, so a
  replica never disagrees with its suspended controller. When such a start lands on a
  suspended replica (the stream can trail the status), its effects are lifted as it applies
  them. The two cast behaviours retry every tick, so their refusals cost nothing. Every other
  caller fires once (a boss's on-aggro list, a passive's `OnEnable`, a shield generator's
  pulse on a defend command), and its refusal is replayed through the normal path on the
  frame after the suspension ends.
- **The countdown freezes.** The game's own cast and reinforcement-call markers over the enemy
  are paused and restarted at the end from the same point. Only a marker a held system owns
  is frozen. The same provider draws an enemy's jump-out charge, which keeps counting.

A ship's turrets are suspended with it, found both under its transform and through
`Turret.ParentSpaceCraft` (some turrets are spawned separately and point at their ship). Two
suspending statuses on one enemy share one freeze.

## Known gaps

- **A client the status reached late runs its own copy of the ability systems for that round
  trip.** It can show a cast completing that the master has frozen. The master's state and
  the clocks agree again when the stream catches up.
- **`PlayableArea` definitions** an AOE ability adds on start stay in place while it is
  suspended.
- **Effects outside stat modifiers**, such as a shield pulse's hit points or a repair's
  restored hull, are instant and stay applied. Suspension only stops the next one.
- **`ScoutController`** (a scout that plants a tracker on the player ship) is not covered.
  No shipped game asset uses it.

Sample: [examples/enemy-abilities](../../examples/enemy-abilities/).
