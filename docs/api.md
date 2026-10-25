# API index

Every type here is public in `EctypeFramework.dll`, namespace `Ectype`. `EctypeFramework.xml`,
shipped beside the DLL, carries the member documentation that IDEs show.

A role mod can be written with no code and no reference to this assembly: see the manifest
reference. The types below are for role mods that add behaviour and for mods that use the
utilities without defining a role.

Statuses, loot registrations, HUD buttons and ability keys cross the network as strings.
Register them from literals that are identical on every client.

## Role mods

| Type | Purpose |
| --- | --- |
| `RoleRegistry` | Links a manifest role to code and answers questions about it. `RegisterAbilityFactory(roleId, factory)` attaches the role ability. `PerkGuid(roleId, perkKey)` returns the asset GUID a perk registers under. `IsAvailableInRoom(roleId)` is true while the role is registered and no other active role suppresses it through `suppressRoles`, and gives the same answer on every client running the same role mods. |
| `EctypeRoleAbility` | Base class for a role ability. Implement `OnApplyEffect` and `OnRemoveEffect`; the protected constructor takes cooldown and duration in seconds, and `Locomotion` is the Opsive locomotion the ability is installed on. Exceptions in the hooks are caught and logged. The stop path always runs, the start path may not have finished, so write `OnRemoveEffect` to tolerate a half-applied effect. Override `OwnerCanUse()` to decide usability on the owning client. Assign `Cancel` in the constructor to make the channel cancelable; `RequestCancel(reason)` ends it from the ability's own code, and `LastEnd` says how a channel ended, readable from `OnRemoveEffect`. |
| `CancelPolicy` | A channel's cancel policy: `On` (the triggers), `Cooldown`, and `CollisionSpeed`, read at every contact. |
| `CancelTriggers` | Flags: `AbilityKey`, `Collision`, `GrappleAttach`, `InertiaZoneEntry`, `JetpackFlightEnd`, `Requested`. |
| `CancelCooldown` | `Full`, or `Partial`: the cooldown times the share of the channel used. |
| `ChannelEnd` | How a channel ended: `Cause`, `Fired`, `Reason`, `Early`, `UsedSeconds`, `ChannelSeconds`, `CooldownSeconds`. |
| `RoleStats` | Role-scoped stats. `Id(roleId, statName)` returns the id of a custom stat declared in a role manifest, or 0. `RegisterAbilityTiming` registers an ability's duration and cooldown under role-declared stats so a perk can modify one role's ability without touching the others. |
| `FrameworkStats` | Stats the framework registers for every player whether or not a role is installed: grapple hook speeds and cooldowns, external-force and motor damping, motor acceleration, gravity, incoming projectile speed, hand attractor values, `SystemModuleConstructionSpeed`, ship drag. Each name is a `const string`. `Id(name)` returns the stat id (0 before registration) to build a `StatMod` against, and `TryGet(name, out id)` reports whether it is registered; manifests can name the same stats. `EnsureRegistered()` runs registration early. `Status` describes the registration state. |
| `HandAttractorStats` | Stat surface for a hand-held attractor ability. `Register(stats, ref range, ref pullVelocity)` registers the two floats under their framework ids and the mirrored vanilla attractor ids on the ability's own collection. `RegisterTiming(ability, ref beams)` registers the ability's duration and cooldown under the hand ids and the beam count. Both are idempotent per id and return false when the framework ids are not yet available. |

## Runtime ability replication

| Type | Purpose |
| --- | --- |
| `RuntimeAbilities` | Opts an Opsive ability that a mod installs at runtime into key-based replication. `Register(ability, key)` after the ability is in its locomotion's array; `Unregister`; `KeyOf`. Peers resolve the key against their own copy of the ability list, so array order stops mattering. The key must be the same literal on every client. |

## Loot

| Type | Purpose |
| --- | --- |
| `LootTables` | Adds a runtime item to the stock loot rolls. `Register(LootDrop)` is idempotent and returns false when the item or anchor is missing or another owner holds the item. Anchors that decide which stock entries place the item: `AnyCompositeWeapon`, `AnyCarryable<T>()`, `AnyCarryableTagged(tagNames)`, `AnyOf(guids)`. `ToLootRarity` maps the ship rarity enum onto the loot rarity enum. `IsRegistered(guid)` and `Status` report state. |
| `LootDrop` | The registration: `Owner` (log text), `Item`, `Rarity`, `IsAnchor`, optional `Gate` asked at each roll, `RequireRarityInTable`, `ExcludeTables`. |

## Statuses

| Type | Purpose |
| --- | --- |
| `EnemyStatuses` | Timed buffs and debuffs on any `OrbitObject`: NPC ships, turrets, asteroids. `Register(def)` once at startup on every client, then `Apply(target, key, seconds, magnitude, attacker)`, `Remove`, `IsActive`, `RemainingSeconds`, `Stacks`. `TryApply` and `TryRemove` take the same arguments and return a `StatusRefusal`. Applications replicate to every peer running the framework and the timer follows the Photon server clock. Every receiver judges the sender of an application or a removal against the template's `Stance`; the master may always remove. `IconFromEmbeddedPng(assembly, key)` loads a marker icon. |
| `EnemyStatusDef` | A status template: `Key`, `DisplayName`, `DamagePerSecond`, `TickSeconds`, `MaxStacks`, `RefreshOnReapply`, `SuspendsAbilities`, `SkipsInvulnerableTargets`, `Stance`, `CoalesceMs` (batch this client's applications to one target into one message per window), callbacks `OnApplied`, `OnTick`, `OnRemoved`, marker fields (`ShowMarker`, `Icon`, `MarkerText`, `MarkerRequiresTargetLock`), `Visibility` and visual fields (`CreateVfx`, `VfxDonorName`, `VfxDonorChild`, `VfxScale`, `VfxLocalOffset`). `AddStat` and `AddIntStat` attach stat modifiers that exist while the status runs. |
| `StatusStance` | Which senders a status accepts: `Any` (default), `Hostile` (another side from the target), `Friendly` (the target's side). |
| `StatusVisibility` | Which viewers draw a status's marker and visual: `All` (default), `OwnForce`, `TargetForce`, `HostileToTarget`. |
| `StatusRefusal` | Why `TryApply` or `TryRemove` did nothing: `None`, `InvalidTarget`, `NotRegistered`, `Inert`, `InvalidDuration`, `Invulnerable`, `Stance`, `UnknownSide`, `Authority`, `Failed`. |
| `CharacterStatuses` | The same for a `Player`. `Register(def)`, `Apply(player, key, seconds, magnitude)`, `Remove`, `RemoveAll`, `IsActive`, `RemainingSeconds`, `Stacks`. Local to the applying client, which is where character damage resolves; applied to a character this client does not own, it logs one warning per key. |
| `CharacterStatusDef` | A character status template: `Key`, `DisplayName`, `TickSeconds`, `MaxStacks`, `RefreshOnReapply`, `OnApplied`, `OnTick`, `OnRemoved`. `Replicated` is not implemented: the status still runs locally and the framework logs a warning naming the key. `AddStat`, `AddIntStat` and `AddAllVulnerabilities` attach modifiers. |

## Enemy abilities

| Type | Purpose |
| --- | --- |
| `EnemyAbilities` | One model over the seven systems enemies use to start special actions. `Describe(target)` lists the abilities on a target and its turrets, `HasAny(target)`, `IsSuspended(host)`. Suspension itself is requested with `EnemyStatusDef.SuspendsAbilities`. |
| `EnemyAbilityInfo` | One ability: `Host`, `Kind`, `Name`, `Phase`. |
| `EnemyAbilityPhase` | `Ready`, `Casting`, `Channeling`, `Sustained`, `Cooldown`, `Spent`. |

## Ship

| Type | Purpose |
| --- | --- |
| `Topology` | Which player ships exist and which is whose, answered the same on every client. `Ships` (living, by ship number), `Multi`, `CrewShipOf(player)`, `AboardShipOf(player)`, `ShipOfPart(component)`, `ShipNumberOf(ship)`, `ShipByNumber(n)`, `ForceOf(ship or player)`, `Hostile(a, b)`, `Changed`, `Stamp`, `Provider`, and `FleetPluginGuid`, the Void Crew Fleet plugin GUID it binds to. Use it instead of the game's player-ship getter for anything about another player; see [reference/topology.md](reference/topology.md). |
| `ShipArrivals` | Events raised on every machine when a player ship leaves the void. `Arrived` carries a `ShipArrival` and fires once per ship; `FleetArrived` fires once per jump, after every ship that jumped is back in normal flight; `LeftNormalFlight` fires when a ship leaves the flight state in which cruise control is allowed; `InNormalFlight(ship)` queries it. |
| `ShipArrival` | `Ship`, `Kind`, and `ServerMs`, the moment the ship left the void on the Photon server clock. A timed effect started from `ServerMs` has the same phase on every machine. For `FleetArrived`: the last ship to arrive, the worst kind, the latest stamp. |
| `ShipArrivalKind` | `Normal`, `Interdiction`, `Unknown`. Treat `Unknown` as not normal. |
| `TimedStatContributions` | One `StatMod` on any `StatTagCollection` whose amount follows a curve over a fixed window. `Start(key, target, statAlias, type, amountAt, durationSeconds, startServerMs, keepAlive, label)`, `End`, `Find`, `LinearDecay(startAmount)`. Starting a live key again replaces it. Nothing is networked: machines starting from the same server timestamp agree. |
| `TimedStatContribution` | The handle `Start` returns: `Key`, `Label`, `Target`, `StatId`, `StartServerMs`, `DurationSeconds`, `Active`, `Progress`, `Amount`, `EndReason`. |
| `ShipSpeedSurge` | A top-speed boost on the player ship that decays linearly to nothing, built on `ShipDrag`. `Start(arrival, magnitude, durationSeconds)` or `Start(ship, magnitude, durationSeconds, startServerMs)`, `End(ship)`, `IsActive(ship)`. Magnitude 1 doubles top speed at the start. `TopSpeedFactor(magnitude, progress)` and `DragFactor(magnitude, progress)` give the curve. Start it on every machine from `ShipArrivals.Arrived`. |

## Interactions

| Type | Purpose |
| --- | --- |
| `InteractionGate` | Refuses players the use of seats, terminals, doors, switches, items and other interactables by registered rules, on the acting player's game (no prompt, a throttled warning) and on the part owner's game (the request is dropped). `Register(id, Func<GateQuery, Refusal>)`, `Unregister(id)`, `SuppressWarningsFor(id)`, `Evaluate(query)`, `Classify(component)`, `Registered`, `AnyRegistered`. Gates only refuse; the first refusal in registration order decides; a gate that throws counts as allowing. See [reference/interaction-gate.md](reference/interaction-gate.md). |
| `GateQuery` | One interaction: `Actor`, `ActorNumber`, `ActorIsHost`, `Character`, `Interactable`, `Ship`, `Kind`, `Side`. |
| `Refusal` | `Allowed`, or `Because(reason, text)` / `Silently(reason)`: `Reason` for the log, `Text` for the player, `Silent`, `IsAllowed`. |
| `InteractionKind` | `Seat`, `Helm`, `Turret`, `Terminal`, `Periscope`, `CloneTube`, `Minigame`, `EnhancementPanel`, `Door`, `AirlockControl`, `PowerSwitch`, `CarryablePickup`, `SocketInsert`, `SocketRelease`, `ModuleUpgrade`, `Other`. |
| `GateSide` | `Client` (the acting player's own check) or `Owner` (the part owner's authority check). |

## Session data

| Type | Purpose |
| --- | --- |
| `SessionData` | Values the host decides for a whole session, carried inside the game's own session data to every client, late joiners included, and restored on a resumed run from a sidecar file. `Register<T>(key, hostProduce, apply, options)` at `Awake` on every client; the host produces each value once per session and every machine, the host included, gets `apply` at the same point of its session start, before the quest is generated. `T` is plain JSON data. `Current<T>(key)`, `TryGetCurrent<T>`, `Has`, `SessionId` (the host's id), and `Changed`, raised with a key whose value appeared, changed or went away. Values are capped per key (default 4 KB) and in total (16 KB). `HostValue(configEntry, key)` returns a `HostValue<T>`; `HostValuesRoomProperty` names its hub room property. See [reference/session-data.md](reference/session-data.md). |
| `SessionDataOptions` | `MaxBytes`, the per-key cap; `KeepOnResume`, whether a resumed run sends the value it started with (default true). |
| `HostValue<T>` | A config entry (bool, number, enum or string) read as the host's value: `Value`, `IsFromHost`, `Source`, `Key`, `Entry`, `Changed`. Live in the hub through a room property the host publishes; frozen for a mission from its start on every client, the host included. |
| `HostValueSource` | `LocalConfig`, `Room`, `Session`: where a `HostValue<T>`'s value came from. |

## HUD

| Type | Purpose |
| --- | --- |
| `HudAbilityBar` | A row of up to `MaxButtons` (4) square ability buttons beside the role-ability widget. `Register(HudButtonSpec)` returns a `Handle`, or null when the bar is full, the id is taken or the id is empty. Safe before the HUD exists. The bar draws and fills the button; the mod owns the input and the cooldown. |
| `HudAbilityBar.Handle` | Returned by `Register`. `Id`, and `Dispose()` removes the button. |
| `HudButtonSpec` | `Id`, `Glyph`, `ReadyColor`, `IdleColor`, `Order`, and per-frame callbacks `Key`, `Fill` (0 ready, 1 just used) and `Visible`. A callback that throws hides its button for the session. |
| `HowToHints` | A how-to toast drawn by the game's own hint system. `Build(guidHex, header, body, icon, seconds, maxPerSession)` makes a `Hint`; a null `icon` collapses the icon slot so the toast is text only; the GUID must not collide with a stock hint, and the hint is never written to the player's saved seen-hint list. `Show(hint)` shows it under the player's "Show hints" setting. `ShowForced(hint)` shows it with that setting off, for a toast the player must see before the setting governs it; a scenario's hint suppression and the per-session cap still refuse it, and the framework runs the countdown the game does not run while the setting is off. Both return true only when the hint went on screen, so a mod counting shows counts the true returns; a hint already showing or stacked, or at `maxPerSession` shows this process, returns false. `Owns(hint)`. Body text takes the game's tags: `<emphasize>`, `<warning>`, `<positive>`, `<negative>`, `<keybind>Fire</keybind>`. |

Public types not listed here are implementation and may change in any version.
