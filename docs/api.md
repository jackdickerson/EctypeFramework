# API index

Every type here is public in `EctypeFramework.dll`, namespace `Ectype`. Anything else in
the assembly is implementation and can change without notice. `EctypeFramework.xml`, shipped
beside the DLL, carries the member documentation that IDEs show.

A role mod can be written with no code and no reference to this assembly: see the manifest
reference. The types below are for role mods that add behaviour and for mods that use the
utilities without defining a role.

Statuses, loot registrations, HUD buttons and ability keys cross the network as strings.
Register them from literals that are identical on every client.

## Role mods

| Type | Purpose |
| --- | --- |
| `RoleRegistry` | Links a manifest role to code and answers questions about it. `RegisterAbilityFactory(roleId, factory)` attaches the role ability. `PerkGuid(roleId, perkKey)` returns the asset GUID a perk registers under. `IsAvailableInRoom(roleId)` is true while the role is registered and not suppressed in the room, and gives the same answer on every client. |
| `EctypeRoleAbility` | Base class for a role ability. Implement `OnApplyEffect` and `OnRemoveEffect`; the constructor takes cooldown and duration in seconds. Exceptions in the hooks are caught and logged. The stop path always runs, the start path may not have finished, so write `OnRemoveEffect` to tolerate a half-applied effect. |
| `RoleStats` | Role-scoped stats. `Id(roleId, statName)` returns the id of a custom stat declared in a role manifest, or 0. `RegisterAbilityTiming` registers an ability's duration and cooldown under role-declared stats so a perk can modify one role's ability without touching the others. |
| `FrameworkStats` | Stats the framework registers for every player whether or not a role is installed: grapple hook speeds and cooldowns, external-force and motor damping, motor acceleration, gravity, incoming projectile speed, hand attractor values, module install speed, ship drag. Each name is a `const string`. `Id(name)` returns the stat id (0 before registration) to build a `StatMod` against; manifests can name the same stats. `EnsureRegistered()` runs registration early. |
| `HandAttractorStats` | Stat surface for a hand-held attractor ability. `Register(stats, ref range, ref pullVelocity)` registers the two floats under their framework ids and the mirrored vanilla attractor ids on the ability's own collection. `RegisterTiming(ability, ref beams)` registers the ability's duration and cooldown under the hand ids and the beam count. Both are idempotent per id and return false when the framework ids are not yet available. |

## Runtime ability replication

| Type | Purpose |
| --- | --- |
| `RuntimeAbilities` | Opts an Opsive ability that a mod installs at runtime into key-based replication. `Register(ability, key)` after the ability is in its locomotion's array; `Unregister`; `KeyOf`. Peers resolve the key against their own copy of the ability list, so array order stops mattering. The key must be the same literal on every client. |

## Room checks

| Type | Purpose |
| --- | --- |
| `SelectionGate` | Reports whether every room member runs the same build of a mod, so a mod can refuse or revert content a member cannot load. `Register(modGuid, onChanged)` tracks a mod and calls back when its result changes. `Evaluate(modGuid)` and `Current(modGuid)` return a `GateResult`. `Changed` is the global event. `Reevaluate()` forces a pass. `PeersWithMod` and `PeerHasMod` answer presence only, on any client. |
| `GateResult` | The outcome for one mod. `Verdict`, `Failed`, `HostVersion`, `Reason`, `Members`, `FailingNames`, `DescribeFailures()`, `DescribeNonMatching()`. |
| `GateVerdict` | `Pass`, `Pending`, `Unverified`, `Fail`, `NotHost`, `Unavailable`. |
| `MemberModResult` | One member's state: `Player`, `Name`, `State`, `Version`, `Failed`. |
| `MemberModState` | `Match`, `NotKnownYet`, `Unverified`, `Missing`, `VersionMismatch`, `HashMismatch`. |

## Loot

| Type | Purpose |
| --- | --- |
| `LootTables` | Adds a runtime item to the stock loot rolls. `Register(LootDrop)` is idempotent and returns false when the item or anchor is missing or another owner holds the item. Anchors that decide which stock entries place the item: `AnyCompositeWeapon`, `AnyCarryable<T>()`, `AnyCarryableTagged(tagNames)`, `AnyOf(guids)`. `ToLootRarity` maps the ship rarity enum onto the loot rarity enum. `IsRegistered(guid)` and `Status` report state. |
| `LootDrop` | The registration: `Owner` (log text), `Item`, `Rarity`, `IsAnchor`, optional `Gate` asked at each roll, `RequireRarityInTable`, `ExcludeTables`. |

## Statuses

| Type | Purpose |
| --- | --- |
| `EnemyStatuses` | Timed buffs and debuffs on any `OrbitObject`: NPC ships, turrets, asteroids. `Register(def)` once at startup on every client, then `Apply(target, key, seconds, magnitude, attacker)`, `Remove`, `IsActive`, `RemainingSeconds`, `Stacks`. Applications replicate to every peer running the framework and the timer follows the Photon server clock. `IconFromEmbeddedPng(assembly, key)` loads a marker icon. |
| `EnemyStatusDef` | A status template: `Key`, `DisplayName`, `DamagePerSecond`, `TickSeconds`, `MaxStacks`, `RefreshOnReapply`, `SuspendsAbilities`, `SkipsInvulnerableTargets`, callbacks `OnApplied`, `OnTick`, `OnRemoved`, marker fields (`ShowMarker`, `Icon`, `MarkerText`, `MarkerRequiresTargetLock`) and visual fields (`CreateVfx`, `VfxDonorName`, `VfxDonorChild`, `VfxScale`, `VfxLocalOffset`). `AddStat` and `AddIntStat` attach stat modifiers that exist while the status runs. |
| `CharacterStatuses` | The same for a `Player`. `Register(def)`, `Apply(player, key, seconds, magnitude)`, `Remove`, `RemoveAll`, `IsActive`, `RemainingSeconds`, `Stacks`. Local to the applying client, which is where character damage resolves. |
| `CharacterStatusDef` | A character status template: `Key`, `DisplayName`, `TickSeconds`, `MaxStacks`, `RefreshOnReapply`, `OnApplied`, `OnTick`, `OnRemoved`. `AddStat`, `AddIntStat` and `AddAllVulnerabilities` attach modifiers. |

## Enemy abilities

| Type | Purpose |
| --- | --- |
| `EnemyAbilities` | One model over the seven systems enemies use to start special actions. `Describe(target)` lists the abilities on a target and its turrets, `HasAny(target)`, `IsSuspended(host)`. Suspension itself is requested with `EnemyStatusDef.SuspendsAbilities`. |
| `EnemyAbilityInfo` | One ability: `Host`, `Kind`, `Name`, `Phase`. |
| `EnemyAbilityPhase` | `Ready`, `Casting`, `Channeling`, `Sustained`, `Cooldown`, `Spent`. |

## Ship

| Type | Purpose |
| --- | --- |
| `ShipArrivals` | Events raised on every machine when the player ship leaves the void. `Arrived` carries a `ShipArrival`; `LeftNormalFlight` fires when the ship leaves the flight state in which cruise control is allowed; `InNormalFlight(ship)` queries it. |
| `ShipArrival` | `Ship`, `Kind`, and `ServerMs`, the moment the ship left the void on the Photon server clock. A timed effect started from `ServerMs` has the same phase on every machine. |
| `ShipArrivalKind` | `Normal`, `Interdiction`, `Unknown`. Treat `Unknown` as not normal. |
| `TimedStatContributions` | One `StatMod` on any `StatTagCollection` whose amount follows a curve over a fixed window. `Start(key, target, statAlias, type, amountAt, durationSeconds, startServerMs, keepAlive, label)`, `End`, `Find`, `LinearDecay(startAmount)`. Starting a live key again replaces it. Nothing is networked: machines starting from the same server timestamp agree. |
| `TimedStatContribution` | The handle `Start` returns: `Key`, `Active`, `Progress`, `Amount`, `EndReason`. |
| `ShipSpeedSurge` | A top-speed boost on the player ship that decays linearly to nothing, built on `ShipDrag`. `Start(arrival, magnitude, durationSeconds)` or `Start(ship, magnitude, durationSeconds, startServerMs)`, `End(ship)`, `IsActive(ship)`. Magnitude 1 doubles top speed at the start. Start it on every machine from `ShipArrivals.Arrived`. |

## HUD

| Type | Purpose |
| --- | --- |
| `HudAbilityBar` | A row of up to `MaxButtons` (4) square ability buttons beside the role-ability widget. `Register(HudButtonSpec)` returns a `Handle`, or null when the bar is full, the id is taken or the id is empty. Safe before the HUD exists. The bar draws and fills the button; the mod owns the input and the cooldown. |
| `HudAbilityBar.Handle` | Returned by `Register`. `Id`, and `Dispose()` removes the button. |
| `HudButtonSpec` | `Id`, `Glyph`, `ReadyColor`, `IdleColor`, `Order`, and per-frame callbacks `Key`, `Fill` (0 ready, 1 just used) and `Visible`. A callback that throws hides its button for the session. |
