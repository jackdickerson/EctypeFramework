# Lasting stat contributions: a modifier for a ship's or a character's lifetime

`StatContributions` puts one modifier on one stat of every player ship, or of every player
character, from the moment the object spawns until it is gone. A mod with no role uses it where
it would otherwise call the game's `AddModifier` by hand from a spawn patch.

```csharp
StatContributions.Register(new StatContribution
{
    Id     = "com.example.mymod/pvp-hull",          // <plugin GUID>/<name>
    Target = StatTarget.PlayerShip,                 // or StatTarget.Character
    Stat   = "MaxHitPoints",                        // vanilla, framework or "<owner>/Name" mod stat
    Kind   = SessionStatModifierKind.Factor,        // Flat, Percent or Factor
    Amount = target => pvp ? 4f : (float?)null,     // null: no contribution on this target
    Source = "Fleet Battle",                        // label for stat displays
});
StatContributions.Refresh("com.example.mymod/pvp-hull");     // ask Amount again on every live target
StatContributions.Unregister("com.example.mymod/pvp-hull");  // withdraw everywhere
StatContributions.Current(ship, "com.example.mymod/pvp-hull"); // the amount applied there, or null
```

## What it is, next to the other two

- **A session stat** is one run-wide number in the framework's session collection. No game
  system reads that collection; mod code reads it and decides what to do with the number. See
  [mod-stats.md](mod-stats.md).
- **A timed contribution** modifies a stat on any collection for a fixed window, with an amount
  that follows a curve. See [timed-stat-contributions.md](timed-stat-contributions.md).
- **A lasting contribution** modifies a stat on the game's own objects, the ships and the
  characters, for as long as each object lives. The game reads the stat as it reads any other:
  the hull bar, the damage model and the movement code see the modified value.

The first and the last compose. An `Amount` can read a session stat
(`ModStats.Get(StatCollectionKind.Session, "<owner>/Name")`): the session collection is built
at session start, before the first sector and its ships exist, so every client reads the run's
value when its ships spawn.

## Rules

- **Register on every client, in your plugin's `Awake`.** Stat modifiers are not replicated:
  each client holds its own copy of every ship and character, and the game's simulation and
  displays read that copy's stats. Each client applies every contribution to every target,
  the local player's character and ship and every replica.
- **`Amount` must answer alike on every client.** It is asked once per target on each client,
  so it reads replicated state only: which ship belongs to whom (see [topology.md](topology.md)),
  session data, a session stat. An answer that differs between clients gives each client a
  different ship. The local player, a local config value or `ClientGame.PlayerShip` differ
  between clients.
- **Applied at spawn.** A player ship is caught in a postfix on
  `GameSession.ApplyGlobalOrbitObjectSpawnProperties`. Every orbit object's `Start` calls it on
  every client, and the next lines of `Start` set the object's estimated hit points to its
  maximum on every client and its hit points to its maximum on the master client, which
  replicates them. A contribution to `MaxHitPoints` is therefore the hull every ship starts with,
  on every screen. Every player ship passes there, extra ships included: a mod that spawns them
  through the game's loadout path gets a networked `PlayerControlledShip` whose `Start` runs on
  every client. A character is caught in the `Player.Awake` postfix, after the framework
  registers its stat surfaces there and before `Start` reads any stat.
- **Once per target and id.** Repeated spawn calls for one object do nothing more, and a second
  `Register` of a live id is refused, so a contribution never stacks with itself.
- **`Amount` returning null withdraws.** At spawn it means no contribution there. On a
  `Refresh` it removes one that was applied.
- **`Refresh(id)`** asks `Amount` again on every live target: a new amount replaces the old one
  in place, null withdraws, and a value where there was none applies. `Register` does the same
  for targets that already exist.
- **`Unregister(id)`** withdraws the contribution from every target.
- **Lifetime.** A destroyed target is forgotten without its stats being touched. When the
  session changes, every contribution is withdrawn from the previous session's targets. A
  character belongs to the session active at its `Start`, since its `Awake` can run before that
  session is active. A character's death and clone-tube revival keep the same object, so its
  contributions stay.
- **The target's own stat only.** The modifier goes on the stat in the target's own collection,
  not through `StatTagCollection.ApplyModifiers`, which recurses into every child collection: a
  ship's modules, its central computer and its helm are child collections and register their own
  `MaxHitPoints`, so a recursed hull modifier would scale every module too. A stat that only a
  child registers (`ForwardPower` lives on the engine) logs a warning and has no effect.
- **Kinds** map as a session-stat effect does: `Flat` adds to the base (`PrimaryAddend`),
  `Percent` adds a fraction to the multiplier (`AdditiveMultiplier`, 0.25 is +25%), `Factor`
  multiplies (`ExponentialMultiplier` with an amount of factor - 1). A factor at or below 0,
  a non-finite amount and a throwing `Amount` count as null, with one warning per contribution.
- **A copy of the modifier is harmless.** The game deep-copies stat modifiers, and other mods
  copy them. The copier recurses through every field, so the modifier's source holds only an
  integer handle and the label, never the contribution or its target. A copy is found when the
  stat it was added to recalculates: while the contribution is live there, the copy follows the
  original's amount; once it is withdrawn the copy is set to 0, which every modifier type
  composes as no change.
- **The label.** `Source` is the modifier source's header, through the game's
  descriptive-source interface. The game's displays that list a stat's modifiers by header (the
  module stat panel, a module's hover information, the clone tube's regeneration panel) read
  module collections only, and skip a modifier whose amount is 0. No stock screen lists the
  modifiers on a ship's own stats or a character's, so the label shows wherever a mod or a
  debugging tool lists them.
- **Late joiners need nothing extra.** A joiner's game spawns its own copy of every ship and
  character and applies the contributions there, provided `Amount` can answer from what the
  joiner holds at that moment.
- With nothing registered, a spawn adds one entry to a list, and the driver's step is a count
  check once a second.

## Log lines

| Line | Meaning |
| --- | --- |
| `Ectype: stat contribution '<id>' applied to <target>: <stat> <kind> <amount> (now <value>).` (the process's first at `[Info]` with ` Further applications log under [Debug] VerboseLogging.`, later ones verbose) | A contribution was applied to a target as it spawned, on `Register`, or on a `Refresh` where there was none. |
| `Ectype: stat contribution '<id>' withdrawn from <target>.` (verbose) | `Amount` answered null on a refresh, the contribution was unregistered, or its target's session ended or the target was destroyed. |
| `Ectype: stat contribution '<id>' registered: <target> <stat> <kind>.` (verbose) | `Register` accepted it. |
| `Ectype: stat contribution '<id>' unregistered.` (verbose) | `Unregister` withdrew it everywhere. |
| `Ectype: stat contributions on <target> dropped (<reason>).` (verbose) | A target with contributions was destroyed (`target destroyed`: its stats are not touched) or belongs to a session that ended (`session ended`: every contribution is withdrawn). |
| `Ectype: stat contribution not registered: it needs an id ("<plugin GUID>/<name>").` | `Register` got no contribution or an empty id. |
| `Ectype: stat contribution '<id>' not registered: that id is already registered; unregister it first.` | The id is live. A contribution is registered once; change what `Amount` reads and call `Refresh` instead. |
| `Ectype: stat contribution '<id>' not registered: it needs a stat and an Amount function.` | `Stat` is empty or `Amount` is null. |
| `Ectype: stat contribution '<id>' not registered: unknown target <n>.` and `... unknown kind <n>.` | An enum value outside `StatTarget` or `SessionStatModifierKind`. |
| `Ectype: stat contribution '<id>' refreshed but not registered.` | `Refresh` named an id that is not registered. Reported once per id. |
| `Ectype: stat contribution '<id>' names a stat that does not resolve; it contributes nothing: <message>` | `Stat` is not a vanilla `StatType` name, a framework stat, a declared `"<owner>/Name"` or an int id. Reported once. |
| `Ectype: stat contribution '<id>' names <stat>, which <type>'s own stat collection does not register as a float stat; it has no effect there.` | The stat lives on a child collection (a module), or the target type does not register it. Reported once per contribution and target type. |
| `Ectype: stat contribution '<id>' Amount threw on <target>; it contributes nothing there (reported once): <exception>` | The mod's `Amount` threw. That target gets no contribution; a later `Refresh` asks again. |
| `Ectype: stat contribution '<id>' Amount is not a finite number on <target>; it contributes nothing there (reported once).` | `Amount` answered NaN or infinity. |
| `Ectype: stat contribution '<id>' is a factor and Amount answered <f>; a factor is above 0, so it contributes nothing there (reported once).` | A factor of 0 or less. Answer null for no contribution. |
| `Ectype: stat contribution '<id>': updating a copy threw; the copy is no longer followed: <message>` (verbose) | A copy of the modifier could not be updated, usually because the stat it was added to went away. |
| `Ectype: stat contribution copy check on recalculation failed (reported once): <exception>` | The check that finds copies of a lasting modifier as their stat recalculates threw. Copies may then keep their amount after the contribution is withdrawn. |
| `Ectype: registering stat contribution '<id>' failed: <exception>`, `Ectype: refreshing stat contribution '<id>' failed: <exception>`, `Ectype: unregistering stat contribution '<id>' failed: <exception>`, `Ectype: applying stat contribution '<id>' to <target> failed: <exception>`, `Ectype: withdrawing stat contribution '<id>' from <target> failed: <exception>` | An unexpected failure in that step. The other targets and contributions carry on. |
| `Ectype: stat contributions on a spawning ship failed: <exception>` and `Ectype: stat contributions on a spawning character failed: <exception>` | The spawn hook threw. That target has none of its contributions; a `Refresh` does not reach it, because it was never recorded. |
| `Ectype: dev self-test: stat contributions passed (validation; once per target; kinds; null withdraws; Refresh; copies inert after Unregister; Unregister everywhere).` (dev tools build only) | Registration refusals, the once-per-target rule, the kinds, withdrawal by null, in-place refresh, a deep copy following the original and reading 0 after `Unregister`, and withdrawal from every target came out as documented. A `FAILED` line names each wrong value. |

[../troubleshooting.md](../troubleshooting.md) lists every line with its cause.

## Things that look like they should work

- **Registering after the ships spawned, to give them a bigger hull.** The maximum rises at
  once, but the hit points were set at spawn and stay where they are. Register in `Awake`.
- **`Refresh` to heal a ship.** It changes the maximum, not the current hit points.
- **`Stat = "ForwardPower"` on a `PlayerShip` target.** That stat lives on the engine's
  collection, a child of the ship's, and a lasting contribution touches the ship's own stats
  only. Use a perk, or a timed contribution on the engine's collection.
- **`Amount` reading the local player or the ship this client views.** Each client answers
  for itself, and the ships stop matching between screens.
- **Registering the same id again with a new `Amount`.** Refused while the id is live.
  `Unregister` first, or keep the id and `Refresh` after changing the state `Amount` reads.
- **Calling `StatTagCollection.ApplyModifiers` on `ship.Stats` for a hull bonus.** It recurses
  into every module, and each module registers its own `MaxHitPoints`.
- **Expecting the label in the ship's stat panel.** The stock panels list module stats only.
- **`Kind = Factor` with `Amount` 0 to switch it off.** A factor is above 0. Answer null.
- **An id without the plugin GUID.** It must be `<plugin GUID>/<name>`, so two mods cannot
  collide.

Sample: [examples/timed-stat-contributions](../../examples/timed-stat-contributions/), which
registers a +25% hull contribution beside its timed thrust bonus.
