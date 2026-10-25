# API index

Every type here is public in `EctypeFramework.dll`, namespace `Ectype`. `EctypeFramework.xml`,
shipped beside the DLL, carries the member documentation that IDEs show.

A role mod can be written with no code and no reference to this assembly: see the manifest
reference. The types below are for role mods that add behaviour and for mods that use the
utilities without defining a role.

Statuses, loot registrations, HUD buttons and ability keys cross the network as strings.
Register them from literals that are identical on every client.

Every identifier a mod hands the framework follows one rule: an owner is the registering
plugin's BepInEx GUID, and a single-string id is `"<plugin GUID>/<name>"`. See
[identifiers.md](identifiers.md) for the check, the exceptions, and what each API does with an
identifier that does not follow it.

## Role mods

| Type | Purpose |
| --- | --- |
| `RoleRegistry` | Links a manifest role to code and answers questions about it. `RegisterAbilityFactory(roleId, factory)` attaches the role ability. `PerkGuid(roleId, perkKey)` returns the asset GUID a perk registers under. `IsAvailableInRoom(roleId)` is true while the role is registered and no other active role suppresses it through `suppressRoles`, and gives the same answer on every client running the same role mods. |
| `EctypeRoleAbility` | Base class for a role ability. Implement `OnApplyEffect` and `OnRemoveEffect`; the protected constructor takes cooldown and duration in seconds, and `Locomotion` is the Opsive locomotion the ability is installed on. Exceptions in the hooks are caught and logged. The stop path always runs, the start path may not have finished, so write `OnRemoveEffect` to tolerate a half-applied effect. Override `OwnerCanUse()` to decide usability on the owning client; the manifest's `abilityWhen` conditions are checked before it. Assign `Cancel` in the constructor to make the channel cancelable; `RequestCancel(reason)` ends it from the ability's own code, and `LastEnd` says how a channel ended, readable from `OnRemoveEffect`. |
| `RoleContext` | A role ability owner's ships and situation, resolved from the owner's actor number so every client gives the same answer: `ShipOf(ability)` (crew ship), `ShipOf(ability, aboard: true)`, `ForceOf(ability)`, `IsOnOwnShip`, `IsOnAllyShip`, `IsOnEnemyShip`, `IsInOpenSpace`, `OwnShipAlive`, `IsSpectating`, `IsTeamMatch`. Role code reads ships through it, never through the game's player-ship getter; see [reference/roles.md](reference/roles.md#ships-in-role-code). |
| `CancelPolicy` | A channel's cancel policy: `On` (the triggers), `Cooldown`, and `CollisionSpeed`, read at every contact. |
| `CancelTriggers` | Flags: `AbilityKey`, `Collision`, `GrappleAttach`, `InertiaZoneEntry`, `JetpackFlightEnd`, `Requested`. |
| `CancelCooldown` | `Full`, or `Partial`: the cooldown times the share of the channel used. |
| `ChannelEnd` | How a channel ended: `Cause`, `Fired`, `Reason`, `Early`, `UsedSeconds`, `ChannelSeconds`, `CooldownSeconds`. |
| `RoleStats` | Role-scoped stats. `Id(roleId, statName)` returns the id of a custom stat declared in a role manifest, or 0. `RegisterAbilityTiming` registers an ability's duration and cooldown under role-declared stats so a perk can modify one role's ability without touching the others. |
| `FrameworkStats` | Stats the framework registers for every player whether or not a role is installed: grapple hook speeds and cooldowns, external-force and motor damping, motor acceleration, gravity, incoming projectile speed, hand attractor values, `SystemModuleConstructionSpeed`, ship drag, and the generic factors `MeleeDamage`, `MeleeCooldown`, `MeleeReach`, `AntiPersonnelDamage` and `HostileSpawnScale`. Each name is a `const string`. `Id(name)` returns the stat id (0 before registration) to build a `StatMod` against, and `TryGet(name, out id)` reports whether it is registered; manifests can name the same stats, except `HostileSpawnScale`, a session stat no perk target reaches. `EnsureRegistered()` registers these stats early and is safe from a plugin's `Awake`; stats files and roles are discovered later, once BepInEx has loaded every plugin. `Status` describes the registration state. |
| `HandAttractorStats` | Stat surface for a hand-held attractor ability. `Register(stats, ref range, ref pullVelocity)` registers the two floats under their framework ids and the mirrored vanilla attractor ids on the ability's own collection. `RegisterTiming(ability, ref beams)` registers the ability's duration and cooldown under the hand ids and the beam count. Both are idempotent per id and return false when the framework ids are not yet available. |

## Mod-declared stats

| Type | Purpose |
| --- | --- |
| `ModStats` | Stats a mod declares in a file ending `ectype-stats.json`, embedded or beside its DLL, registered into every character, ship or session collection, and the reading API for them and the framework's stats. `Id(owner, name)` is the id formula; `Id(name)` takes `"owner/Name"`, `"ectype/Name"` or a bare framework name. `TryGet` and `Get` by collection or by `StatCollectionKind` return the composed value, or the declared base when the collection does not hold the stat. `LocalCollection(kind)`, `IsDeclared`, `FileSuffix`, `Status`. See [reference/mod-stats.md](reference/mod-stats.md). |
| `StatCollectionKind` | `Character`, `Ship`, `Session`: the collection a declared stat is registered into. |
| `SessionStats` | The run-wide collection, one per session. `Opening` is raised on the host while it starts a session, with `Collection` already the new collection; `Contribute(statId, modifier, source)` on the host and `Withdraw(source)` are the only way to contribute, since no perk target reaches it. The host publishes the composed values under the session data key `framework/sessionStats`, and every other client sets each stat's base to them. `Collection`, `SessionId`, `Status`, `SessionDataKey`, `HolderObjectName`, `MaxStats` (128). |
| `SessionStatHolder` | The component on the GameObject `Ectype.SessionStats` that holds the session collection, through the game's `IStatCollectionHolder`. |
| `SessionStatMutators` | Mutators in the game's Mutators menu whose rule is a set of session-stat modifiers. `Register(MutatorSpec)` returns a `MutatorHandle`; the asset is registered with the Required effect, made selectable, and re-verified. While the host starts a session that will activate one, selected or forced by its quest, the framework contributes its effects before the values are published. `DeriveGuid(owner, key)` (MD5 of `ectype:mutator:<owner>/<key>`), `IsActive(guid)`, `MaxEffects` (16), `GuidSeedPrefix`, `Status`. A role manifest's `mutators` block registers through it. See [reference/mod-stats.md](reference/mod-stats.md#contributions-from-mutators). |
| `MutatorSpec` | `Owner`, `Key`, optional `Guid`, `DisplayName`, `Description`, `Icon` (a `Sprite`), `IconDonor` (a mutator GUID), `Effects`. |
| `SessionStatEffect`, `SessionStatModifierKind` | One effect: `Stat` (a session stat, `"owner/Name"` or a framework name), `Kind` (`Flat`, `Percent`: 0.25 is +25%, `Factor`: 1.5 is x1.5), `Amount`. |
| `MutatorHandle` | `Owner`, `Key`, `Guid`, `State` (`ContentState`: `Pending`, `Registered`, `Failed`), `Reason`, `Asset` (the `Mutator`, once registered). |
| `SessionStatMutatorEffect`, `SessionStatModifier` | The game-side effect a registered mutator carries: a `MutatorEffectSession` holding `Modifiers` (stat id, `ModifierType`, amount). A mod's own `Mutator` asset may carry one. |
| `MutatorManifestSpec`, `MutatorEffectManifestSpec` | The manifest form of `MutatorSpec` and its effects. |

## Runtime ability replication

| Type | Purpose |
| --- | --- |
| `RuntimeAbilities` | Opts an Opsive ability that a mod installs at runtime into key-based replication. `Register(ability, key)` after the ability is in its locomotion's array; `Unregister`; `KeyOf`. Peers resolve the key against their own copy of the ability list, so array order stops mattering. The key must be the same literal on every client. |

## Loot

| Type | Purpose |
| --- | --- |
| `LootTables` | Adds a runtime item to the stock loot rolls. `Register(LootDrop)` is idempotent and returns false when the item or anchor is missing or another owner holds the item. Anchors that decide which stock entries place the item: `AnyCompositeWeapon`, `AnyCarryable<T>()`, `AnyCarryableTagged(tagNames)`, `AnyOf(guids)`. `ToLootRarity` maps the ship rarity enum onto the loot rarity enum. `IsRegistered(guid)` and `Status` report state. |
| `LootDrop` | The registration: `Owner` (the registering plugin's GUID), `Item`, `Rarity`, `IsAnchor`, optional `Gate` asked at each roll, `RequireRarityInTable`, `ExcludeTables`. |

## Content

| Type | Purpose |
| --- | --- |
| `RuntimeContent` | `Publish(kind, guid, assets, entry)` registers a runtime weapon module, module or carryable in every container its kind needs, lists it in GUID order in its container's authored list, writes its unlock def and records its entry in `ModContent`. Refuses a carryable or weapon module without crafting data, and `IsComponent` on anything but a carryable. Queued until the containers exist. `Status` counts publications; `Handles` lists them. |
| `ContentKind` | `WeaponModule`, `Module`, `Carryable`. |
| `ContentAssets` | The game-side data: `Asset`, `Description`, `Icon`, `BuildBoxIcon`, `Crafting`, `Rarity`, `Unlock` (default `Session`), `ModFlag` (default the framework build's network-spawned effect: `IsNetworkSpawned`, or `None` in a build without the session flag), `ItemCategory`, `ModuleCategory`. |
| `ContentHandle` | One publication: `Kind`, `Guid`, `Owner`, `State` (`ContentState.Pending`, `Registered`, `Failed`), `Reason`, `Attempts`. |
| `ModContent` | The registry of registered items, for catalogs: `ApiVersion` (1), `TryGet(guid, out entry)`, `All`, `IsAvailable(guid)`, event `Changed`. Returns copies. |
| `ModContentEntry` | `Guid`, `Kind`, `DisplayName`, `Owner`, `Category`, `Tier` (0 unranked), `IsComponent`, `PvpLegal`, `AvailableInRoom`, `IconHint`. BCL fields plus `GUIDUnion`, readable by reflection. |

## Statuses

| Type | Purpose |
| --- | --- |
| `EnemyStatuses` | Timed buffs and debuffs on any `OrbitObject`: NPC ships, turrets, asteroids. `Register(def)` once at startup on every client, then `Apply(target, key, seconds, magnitude, attacker)`, `Remove`, `IsActive`, `RemainingSeconds`, `Stacks`. `TryApply` and `TryRemove` take the same arguments and return a `StatusRefusal`. Applications replicate to every peer running the framework and the timer follows the Photon server clock. Every receiver judges the sender of an application or a removal against the template's `Stance`; the master may always remove. `IconFromEmbeddedPng(assembly, key)` loads a marker icon. |
| `EnemyStatusDef` | A status template: `Key`, `DisplayName`, `DamagePerSecond`, `TickSeconds`, `MaxStacks`, `RefreshOnReapply`, `SuspendsAbilities`, `SkipsInvulnerableTargets`, `Stance`, `CoalesceMs` (batch this client's applications to one target into one message per window), callbacks `OnApplied`, `OnTick`, `OnRemoved`, marker fields (`ShowMarker`, `Icon`, `MarkerText`, `MarkerRequiresTargetLock`), `Visibility` and visual fields (`CreateVfx`, `VfxDonorName`, `VfxDonorChild`, `VfxScale`, `VfxLocalOffset`). `AddStat` and `AddIntStat` attach stat modifiers that exist while the status runs. |
| `StatusStance` | Which senders a status accepts, for enemy and character statuses: `Any` (default), `Hostile` (another side from the target), `Friendly` (the target's side). |
| `StatusVisibility` | Which viewers draw an enemy status's marker and visual, and which peers receive a character status's presentation copy: `All` (default), `OwnForce`, `TargetForce`, `HostileToTarget`. |
| `StatusRefusal` | Why `TryApply` or `TryRemove` did nothing: `None`, `InvalidTarget`, `NotRegistered`, `Inert`, `InvalidDuration`, `Invulnerable`, `Stance`, `UnknownSide`, `Authority`, `Failed`. |
| `CharacterStatuses` | The same for a `Player`. `Register(def)`, `Apply(player, key, seconds, magnitude)`, `Remove`, `TryRemove`, `RemoveAll`, `IsActive`, `RemainingSeconds`, `Stacks`. A status lives on the client that owns the character, where its damage and movement resolve, and ends on expiry, on death and when the character is destroyed; `Apply` on a character this client does not own logs one warning per key. `Request(player, key, seconds, magnitude, attacker)` returns a `CharacterStatusRequest`: on this client's character it applies; on another player's it sends a request that the target's client judges against its own view (template opt-in, both characters alive, seat and clone tube when asked, `Stance`, `MaxRange`, line of sight, rate), applies or refuses, and answers. A request never shortens or weakens a running status. Only the owning client may remove a status. `IsPresented` and `PresentedRemainingSeconds` answer for a presentation copy; `RequesterOf` and `AttackerOf` for the request that last applied a status. |
| `CharacterStatusDef` | A character status template: `Key`, `DisplayName`, `TickSeconds`, `MaxStacks`, `RefreshOnReapply`, `OnApplied`, `OnTick`, `OnRemoved`. `Replicated` changes nothing and logs one warning per key on `Apply`. Requests: `AcceptsRequests` (accept them; default off), `Stance`, `MaxRange` (metres, 0 = none), `RequiresLineOfSight`, `RefuseSeated`, `RefuseInTube`, `MinInterval` (seconds, 0 = none), `Burst` (default 1), `MinSeconds` (default 0.25), `MaxSeconds` (default 30) and `MaxMagnitude` (default 1). Presentation: `Presented` (announce to other players), `Visibility`, `CreateVfx`. `AddStat`, `AddIntStat` and `AddAllVulnerabilities` attach modifiers. |
| `CharacterStatusRequest` | The handle `CharacterStatuses.Request` returns: `Target`, `Key`, `Sent`, `Done`, `Refusal`, `Applied`, and `OnCompleted(callback)`, which runs once with the verdict, at once if it is already in. A request applied or refused on this client is done when `Request` returns; a sent one when the target's client answers, at once with `SendFailed` when Photon refuses the send, or with `NoAnswer` (outcome unknown) after 10 s or when 40 newer requests to the same player are waiting. |
| `CharacterStatusRefusal` | Why a character status request or `TryRemove` did nothing, or why a character's client refused a request or an announcement: `None`, `InvalidTarget`, `NotRegistered`, `Inert`, `InvalidDuration`, `InvalidMagnitude`, `NotAccepted`, `NoPeer`, `TargetDead`, `SenderMissing`, `SenderDead`, `SenderSeated`, `InCloneTube`, `Stance`, `OutOfRange`, `NoLineOfSight`, `RateLimited`, `Authority`, `NotPresented`, `NoAnswer` (the outcome is unknown), `Failed`, `SendFailed`. |

## Enemy abilities

| Type | Purpose |
| --- | --- |
| `EnemyAbilities` | One model over the seven systems enemies use to start special actions. `Describe(target)` lists the abilities on a target and its turrets, `HasAny(target)`, `IsSuspended(host)`. Suspension itself is requested with `EnemyStatusDef.SuspendsAbilities`. |
| `EnemyAbilityInfo` | One ability: `Host`, `Kind`, `Name`, `Phase`. |
| `EnemyAbilityPhase` | `Ready`, `Casting`, `Channeling`, `Sustained`, `Cooldown`, `Spent`. |

## Ship

| Type | Purpose |
| --- | --- |
| `Topology` | Which player ships exist and which is whose, answered the same on every client. `Ships` (living, by ship number), `Multi`, `CrewShipOf(player)`, `AboardShipOf(player)`, `ShipOfPart(component)`, `ShipNumberOf(ship)`, `ShipByNumber(n)`, `ForceOf(ship or player)`, `Hostile(a, b)`, `TeamMatch`, `Changed`, `Stamp`, `Provider`, and `FleetPluginGuid`, the Void Crew Fleet plugin GUID it binds to. Use it instead of the game's player-ship getter for anything about another player; see [reference/topology.md](reference/topology.md). |
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
