# Role mods

A role in Void Crew is a perk tree whose row-0 perk has display type `Hero`. The game's
screens assume exactly four trees: several are closed sets keyed on the four stock roles. The
framework patches those once per process, and a role mod carries only content: a JSON
manifest, PNG icons and, optionally, code for a role ability.

This page covers what a role mod consists of, how the framework loads it, and what the
framework does with suppression, abilities and ship-targeted perks. Every manifest field is
in [manifest.md](../manifest.md), and the stat names an effect can use are in
[stat-names.md](../stat-names.md).

## What a role mod consists of

1. A plugin with `[BepInDependency("jack.voidcrew.ectype")]` and a `VoidPlugin` that declares
   `MultiplayerType.All` (see [../multiplayer.md](../multiplayer.md) for why a role mod cannot
   be anything else).
2. An embedded JSON manifest whose resource name ends `ectype-role.json`. That suffix is the
   discovery marker; a data-only role needs no reference to the framework assembly.
3. Icons as PNGs embedded in the assembly, named by the manifest's icon keys. An icon
   resolves in this order: the embedded PNG, then the perk's `iconDonor` if it names one, then
   the framework's own placeholder, built with the same sprite settings:

   | Slot | Placeholder |
   | --- | --- |
   | Perk icon, Normal and Hero cells (the hero perk's icon feeds the HUD ability button) | square, 512x512 |
   | Perk icon, the row-2 Wide cell | wide, 2048x512 |
   | Role icon (crew HUD, lobby room list, gene tree) | square, 512x512 |
4. Optionally, a call to `RoleRegistry.RegisterAbilityFactory` at plugin `Awake` with a
   factory that returns an `EctypeRoleAbility` subclass (see "Role ability wiring" below).
   Without it the role is data-only and its hero perk grants no ability.

Two lookups help role code:

- `RoleRegistry.PerkGuid(roleId, perkKey)` returns the asset GUID a perk registers under,
  the same derivation registration uses. Code that reads `PlayerPerkLoader.PerkBuffLevels`
  for one of its own perks needs no pinned literal.
- `RoleRegistry.IsAvailableInRoom(roleId)` is true while the role is registered and no
  other active role lists it in `suppressRoles`. Both halves come from the installed role
  mods, and every player in a room runs the same set, so every client gives the same answer.
  Gate role-owned content (loot, spawns) on it.

## Manifest rules in brief

The details are in [manifest.md](../manifest.md). The rules that shape a role:

- **The grid is the stock tree's grid.** Each role tree is a copy of the game's authored
  Scavenger tree, and the manifest's `row` and `col` only mark which of its cells are filled.
  The manifest has no field to change the shape or the row gating: every tree renders as 8
  rows `[1,3,1,3,3,1,3,1]` and gates at 3 points per prior row.
- **`wide` marks the wide cells.** A perk on row 2, 5 or 7 sets `"wide": true`. A perk on any
  other row leaves it out or sets `false`, and either mistake rejects the role.
- **`color` is the role's entry in the game's named-colour dictionary.** The framework
  clones every consumer stylesheet with a renamed role-colour family per role, so trees,
  loadout slots and role UI all colour through the game's own functions.
- **`roleId` and every perk `key` are GUID seeds.** Renaming one orphans saved selections
  exactly like deleting the perk. A perk may pin an explicit `guid` to survive a rename.
- **A perk's `target` picks where its effects land**: `character`, `seat`, `seatShip` or
  `characterShip`.
- **Custom stats** get deterministic ids under bit `0x20000000` (space the game never uses)
  and a `StatStructure` in the live `StatTable`, so tooltips render them. A custom stat is
  inert until code reads it.
- **Framework-owned stats** are named from any manifest without being declared. See
  [framework-stats.md](framework-stats.md).

## How the framework loads a role

- **Fail-inert patching.** A partial patch set is fatal: Harmony does not roll back a
  `PatchAll` that aborts midway. The framework logs one named error, starts no driver, and
  every registration entry point checks that the patches fully applied before it does
  anything.
- **Discovery.** The framework scans the loaded BepInEx plugin assemblies for manifests once,
  and the scan closes at the first terminal build. A role mod's plugin therefore has to
  load before the terminal does, which `[BepInDependency]` on the framework guarantees.
- **Ordinal allocation.** Roles sort by `roleId` (ordinal comparison) and are numbered
  from `(EPerkTree)4`. Identical mod sets, which VoidManager's `MPType.All` matching enforces
  room-wide, give identical ordinals on every client.
- **Registration pipeline.** Registration runs in stages, each step guarded and re-runnable.
  One failed role means one named error and one absent role; the terminal stays intact.
  `[General] RegisterStage` in [config.md](config.md) stops registration after a chosen
  stage, to bisect a failure.
- **Tree UI injection.** One `PerkTreeVE` is created per role, lifted from a pristine
  instantiation of the owning document. It is never cloned from the live document, which
  carries runtime state. A role whose element cannot be built is filtered out of that
  terminal build, so the terminals survive without it.
- **Tree-row scrolling.** Past four surviving trees, the row becomes a horizontal carousel
  with mouse-wheel and gamepad support and a page indicator (lit pips, one per role, above
  the row). Four or fewer trees never touch the layout. A document can opt out by name with
  `[Presentation] ScrollExcludeDocuments`.
- **Stat registry.** Vanilla aliases are enumerated from the game's `StatType` at runtime,
  then each role's custom stats, then the framework's own.

## Role ability wiring

A factory-built ability installs into the local player's Opsive locomotion on spawn, with its
perk-identity fields pointed at the role's hero, row-5 and row-7 banner perks. After that
the game's own machinery owns it: perk investment selects it, the shared UseClassAbility key
binds it, cooldown and duration tick natively, and manifest `StatMod`s on
`ClassAbilityDuration` and `ClassAbilityCooldown` tune it like any stock hero. The HUD gets a
fifth ability slot built the way the game builds its own four.

`EctypeRoleAbility` wraps the effect hooks so a throwing ability logs once instead of
breaking Opsive's update loop. A failed factory or wiring fails only that role's ability.
The stop path always runs and the start path may not have finished, so write the remove hook
to tolerate a half-applied effect.

`RoleStats.RegisterAbilityTiming` registers an ability's own duration and cooldown under two
role-declared custom stats. Use it to scope a perk to one role's ability, because the
vanilla `ClassAbilityDuration` and `ClassAbilityCooldown` reach every role ability on the
character. Avoid the `*Pip` stat variants on role abilities unless you know the pip system.

The framework registers its role abilities for network replication under the key
`ectype:<roleId>`; see [runtime-abilities.md](runtime-abilities.md).

`EctypeRoleAbility` overrides `AbilityStarted`, `AbilityStopped`, `ActivationKeyPressed`,
`Update`, `InactiveUpdate` and `OnDestroy` to run the two features below. A subclass override
of any of them calls its base.

### Usability

Override `OwnerCanUse()` to make the ability unusable in some states. On the owning client,
on every tick the ability is selected and idle (Opsive's `InactiveUpdate`), the framework
evaluates it and writes the result to the game's `AbilityUsable`. The game's start check
(`ClassAbility.CanStartAbility`) refuses an unusable ability, and the setter raises the HUD's
usable-changed event, which toggles the slot's `ClassAbility__unusable` class. Selecting the
ability republishes the current value, so a decision made while it was deselected reaches the
HUD.

A replica is held usable. The same start check runs on every client: a crewmate's copy of
your character starts the ability when your start reaches it through the framework's ability
replication. The decision was made on your machine, so a replica that decides again from its
own copy of the character's state can only disagree, and a disagreement refuses the start
and costs that crewmate the effect.

Role code never writes `AbilityUsable`. The framework decides it again on the next idle tick,
and the two writes alternate, each raising the HUD event.

### Cancel policy

By default a channel runs its full duration: `FiniteDurationClassAbility.CanStartAbility`
refuses while a channel runs, so pressing the ability key again does nothing. Assign `Cancel`
in the subclass constructor to let the channel end early:

```csharp
Cancel = new CancelPolicy
{
    On = CancelTriggers.AbilityKey | CancelTriggers.Collision | CancelTriggers.InertiaZoneEntry,
    Cooldown = CancelCooldown.Partial,
    CollisionSpeed = () => 3f,
};
```

| Trigger | Fires when | Mechanism |
| --- | --- | --- |
| `AbilityKey` | The ability key is pressed while the channel runs. | `ClassAbility.ActivationKeyPressed`, overridden. The game binds the key on the owning client only. |
| `Collision` | A contact's approach speed, the character's velocity against the surface normal, reaches `CollisionSpeed` (default 3 m/s, never below 0.1). | `CharacterLocomotion.OnCollision`, raised on the owning client for every contact of the movement sweep, grazing and ground contacts included. The speed filter is what separates a hit from a graze. |
| `GrappleAttach` | A grappling hook on the character attaches to a surface. | `ChannelGrapplingHook.OnGrapplingStart`, raised at the end of the hook's attach-to-valid-surface handler. Every hook in the character's ability list at the channel's start is watched, whichever mod installed it. |
| `InertiaZoneEntry` | The character enters a ship's inertia zone. | `CharacterAtmosphericDataTracker.OnInInertiaZoneChange` with `true`, raised from the tracker's zone setter. |
| `JetpackFlightEnd` | The character is not flying the EVA jetpack on a tick of the channel: it landed or entered gravity. A channel started out of flight ends on its first tick, so list it for an ability usable only in flight. | `FlyJetpack.IsActive`, polled each tick, because the jetpack raises no event when it stops. |
| `Requested` | The ability calls `RequestCancel(reason)`. | The ability's own code. Without `Requested` in `On` the call logs one warning and does nothing. |

The policy's rules:

- **Owner only.** The triggers are subscribed on the owning client when the channel starts and
  unsubscribed when it stops. A replica's channel ends when the owner's stop reaches it
  through the framework's ability replication.
- **The stop waits for the ability's tick.** A trigger records the cancel; the ability stops
  from its own `Update`, the tick from which the game's countdown stops a channel that has run
  out. No stop runs inside the movement sweep, the grapple's attach handler, the tracker's
  setter or the input callback.
- **`LastEnd` says how the channel ended, before `OnRemoveEffect` runs.** `Cause` is the first
  trigger that fired, `Fired` every trigger that fired before the stop landed, `Early` whether
  the channel had time left, `Reason` the cause in words. An effect that has to end
  differently for one trigger reads it there: one that snaps back on entering an inertia zone
  checks `Fired` for `InertiaZoneEntry`, which also catches a zone entered in the same tick as
  a collision. `Cause` is `None` for a channel that ran out and for a stop from outside the
  policy: a deselect, a despawn, or the owner's stop arriving at a replica.
- **Cooldown.** `Full` is the game's own. `Partial` scales the cooldown by the share of the
  channel used: 3 s of a 4 s channel on an 8 s cooldown leaves 6 s. The game's stop path
  removes the effect, sets `CurrentCooldown` to the full `CooldownSeconds`, and only then
  zeroes the channel's remaining time. The remaining time is read before that path and the
  cooldown rewritten after it through the game's `RefundCooldown`. The HUD draws
  `CurrentCooldown / CooldownSeconds` every frame, so the shorter cooldown shows at once.
  `Partial` applies to every end with time left, a deselect included, and never goes below
  one physics step, so the cooldown still ends in the game's countdown, which raises
  `ClassAbilityCooldownEnded`. A channel that runs out keeps the full cooldown.
- **One log line per channel end.** With `[Debug] VerboseLogging` on, the owning client logs
  how each channel ended and the cooldown it left.

## Suppression

Two manifest fields remove a tree from the game while a role mod is installed. Both leave
the suppressed content registered, so every player still needs every role mod (see
[../multiplayer.md](../multiplayer.md)).

### Vanilla-tree suppression

A manifest's `suppress` list names vanilla perk-tree GUIDs that should not exist while that
role is installed (the "this role replaces Gunner" mechanism), unioned across all role
mods. A suppressed tree is filtered out of every terminal build, and its perks are scrubbed
from saved loadouts with vanilla refund semantics. A framework role's GUID in `suppress` is
refused.

### Modded-role suppression

A manifest's `suppressRoles` list names other framework roles by `roleId`. Each is
suppressed while the listing role is itself active: its tree is kept out of both terminals,
its role ability stays deselected, its perks stop applying and their points count as
unspent, and its own `suppress` and `suppressRoles` entries stop counting. Its payloads are
not adopted. The save is never written: the perks stay in each loadout and are read as
absent, so removing the listing role mod restores the build. The list is resolved from the
manifests alone, because tree and perk GUIDs derive from the `roleId`, so registration
order between role mods does not matter. A cycle is broken in `roleId` order with a
warning. An id that is not installed is ignored with a warning.

### Adoption

Suppressing a stock tree orphans two things the game keys to its perks in code rather than
data: the role ability, and every fabricator unlock whose `PlayerPerk` criteria names one of
those perks. The suppressing role inherits both. The vanilla ability's identity fields
repoint at the new role's banners and the game's own selection check re-runs, so behaviour,
cooldown, input binding and HUD slot stay vanilla and only "which perk grants this" changes.
A role with its own ability factory keeps its own ability instead. A capstone `StatMod`
payload on an adopted ability is taken over if the adopting role authors its own capstone
effects, and otherwise left untouched.

## The characterShip target

`characterShip` is `PerkBuffTarget` value 4. It needs framework 0.18.1 or newer, so a role
that uses it sets `minFrameworkVersion` to at least `0.18.1`. The effects stay on the perk asset's levels,
where the tooltip renders them. The game's perk loader skips the value, so the game never
applies them itself. After `ReApplyPerks` the framework deep-copies the holder's level into
the player ship's collection with the perk as `InformationSource`, one contribution per
holder, and `ResetPerks` takes it off again. Both run on every client for every player, so
every client carries every holder's effects.

- Each `StatMod` gets its own source object. Standing up removes every ship modifier sourced
  from the player, so a player-sourced one would not survive leaving a chair.
- A new player ship gets every holder's contribution rebuilt onto it, and a player leaving the
  room takes theirs off. Death and respawn keep the perk loader, so nothing is done for them.
- The ship's collection does not reach every module. A module joins the ship's tree through
  its build socket or by registering itself, and a core system nested in the ship prefab that
  does neither (the Fabricator) roots its own tree. The contribution therefore also goes onto
  the root of every ship module collection that does not chain up to the ship's. When such a
  root later gains a parent, the parent replays its modifiers into it and every holder is
  rebuilt, so the contribution sits there once.
- `seatShip` has no such step: a vanilla seat-ship modifier never reaches the Fabricator.

## Target reach

A stat's category never constrains a perk's target, so an effect can name a stat that its
target's collection tree does not hold. It still registers and changes nothing. After
`ReApplyPerks` the framework walks that tree (the `Stats` keys, recursively through
`ChildCollections`, the same match `ApplyModifiers` makes) and logs one warning per perk,
stat and target. A miss counts only when a reapply at least 10 s later still misses, because
modules and abilities join the tree after spawn. Seat effects are checked against the player
ship's tree, which contains every mannable module.

## Related pages

- [manifest.md](../manifest.md): every manifest field.
- [stat-names.md](../stat-names.md): the stat names an effect can use.
- [framework-stats.md](framework-stats.md): stats the framework registers for every player.
- [config.md](config.md): the framework's config keys.
