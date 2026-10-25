# Maintainer notes

Internal material for whoever maintains the framework through a game update. It is not linked
from the landing page and is not part of the public documentation. Nothing here is a
supported API.

Start a game-update triage with the hooks table below: a Harmony patch that fails to bind
names itself in the log, and the row says what the patch was for.

## Source map

Paths are relative to the framework project folder.

| Area | Where |
| --- | --- |
| Helpers compiled into the framework (flag policy, reflection, prefab cloning; F5 menu and dev keys in a dev tools build only) | `src/shared/` |
| Manifest model and validation | `src/Manifest.cs` |
| Framework-owned stat declarations | `src/FrameworkStats.cs` |
| Field-backed stat wrapper | `src/FieldStat.cs` |
| Mutator safety | `src/MutatorSafety.cs` |
| Loot tables | `src/LootTables.cs` |
| Enemy statuses | `src/EnemyStatus.cs`, `src/EnemyStatusFx.cs` |
| Enemy abilities and suspension | `src/EnemyAbilities.cs` |
| Character statuses | `src/CharacterStatus.cs` |
| Ship arrivals | `src/ShipArrivals.cs` |
| Ship registry and per-player ship resolution | `src/Topology.cs` |
| Crafting-data audit | `src/CraftingAudit.cs` |
| Timed stat contributions | `src/TimedStatContributions.cs` |
| Ship speed surge | `src/ShipSpeedSurge.cs` |
| HUD ability bar | `src/HudAbilityBar.cs` |
| How-to hints | `src/HowToHints.cs` |
| Hand attractor stats | `src/HandAttractorStats.cs` |
| Runtime ability replication | `src/AbilitySync.cs` |
| Network channel (Photon event, subtypes, peer property) | `src/EctypeNet.cs`, `PeerPresence` in `src/AbilitySync.cs` |
| Network contract marker asset | `src/ContractMarker.cs` |
| VoidManager integration (optional: mod type, F5 page) | `src/VoidManagerBridge.cs` |
| Role ability cancel policy and usability | `src/EctypeRoleAbility.cs`, `src/AbilityCancel.cs` |
| Geometry watcher | `src/PresentationDiagnostics.cs` |
| Gene-tree layout values | `src/LayoutSpec.cs` |
| Dev tools (dev tools build only) | `src/dev/` |
| Role wording | `src/RoleWording.cs` |

## Hooks relied on

Each row is a seam in the game, VoidManager or Unity that the framework patches, reads or
calls. A game update that renames or reshapes one of them breaks the feature in the "Why"
column.

| Seam | Why |
| --- | --- |
| `PlayerDataHelper.GetPlayerRole(Dictionary<GUIDUnion,int>)` prefix (replacing) | Replaces fixed Roles array with N-tree to allow more PerkTreeDefs. N-tree seeds from framework-registered PerkTreeDefs. |
| `PlayerPerkLoader.ReApplyPerks` prefix | null-def dereference and unchecked `Levels[level - 1]` wipe perks after `ResetPerks`; drop unknown GUIDs and perks with no levels (covers all mods' strays), plus suppressed perks so their effects never apply, and clamp a held level above the perk's `Levels.Count` to its top level (backstop for a peer whose owner did not refund at load) |
| `PlayerPerkLoader.ReApplyPerks` postfix + finalizer | applies the holder's `characterShip` effects to the holder's resolved ship (`ClientGame.PlayerShip` only while one ship object exists, else `Topology`'s crew-ship rules); the finalizer re-runs the apply when the method threw after `ResetPerks` had already removed them. The postfix also runs the target-reach check against the same ship |
| `PlayerPerkLoader.ResetPerks` postfix | removes the holder's `characterShip` modifiers, each by its own source |
| `ClientGame.OnPlayerShipSet` (subscribed) | raised from `PlayerControlledShip.Start` after `Awake` has set `PlayerShip`, once per ship; requests the `characterShip` pass on the next frame, which rebuilds only holders whose resolved ship changed |
| `PlayerControlledShip.Awake` postfix, `OnDestroy` prefix, `NetPublishShipDeathBegan` postfix | the `Topology` ship registry. `Awake` runs on every client for every ship and is where the ship assigns itself as the game's player ship; the death-began method is an RPC sent to all and sets `StartedDeath` |
| `ClientGame.PlayerShip` (read) | presentation only (the status-marker lock gate), and simulation only while `Topology.Multi` is false. A multi-ship mod can postfix the getter to return the viewing client's ship |
| `ClientGame.Players`, `Player.ActorId`, `Player.ZonedPlatform` / `Platform`, `OrbitObject.Faction`, `ShipHull.Ship`, `CarryableObject.ParentPlatform` (read) | `Topology`'s answers: a character by actor, the ship a character is aboard, a part's ship, sides |
| Plugin `com.captainchunck.voidcrew.fleet`: `VoidCrewFleet.FleetApi.ApiVersion`, `ShipOfPlayer(int)`, `TeamOf(int)`, `TeamOfShip(int)`, events `ShipsChanged` / `TeamsChanged` (bound by reflection); room property `VCF_Ships` (read, shape-checked) | `Topology`'s crew-ship and team answers and ship numbering under Void Crew Fleet. Bound only for `ApiVersion` 1; answers used only while the plugin component and its `[General] Enabled` entry are on. A missing member is logged once and answered from game data |
| `GuidedProjectile.Target`, `SyncedProjectile.Source` / `Velocity` / `ProjectileId` (read) | the per-shot ship choice for `IncomingProjectileSpeed` |
| `ObjectMarkerHelpers.GetAffiliationBasedOnFaction(OrbitObject)` (called) | status-marker colour; the same helper the game's own object markers use |
| `ModelEventBus.OnPlayerRemoved` (subscribed) | raised by `ClientGame.RemovePlayerInternal` on every client; removes the leaving player's `characterShip` contribution |
| `StatTagCollection.RegisterChildCollection` postfix | a detached module root carrying a `characterShip` contribution has just had its new parent's modifiers replayed into it; every holder is rebuilt so the contribution is not counted twice |
| `PlayerPerkLoader.ReApplyPerks` postfix | catch-up for ability adoption: the spawn-time sweep skips a role whose registration has not settled, and this path runs on every perk change |
| `CG.Profile.PerkLoadout(Dictionary<GUIDUnion,int>)` ctor prefix | profile-load vs. registration race, the load half of the vanilla-tree suppression scrub (vanilla degrade semantics), and the lost-level refund: a saved level above the perk's `Levels.Count` is lowered to it, so the points come back as unspent and the owner's `PBL` publishes the lowered level |
| `PlayerProfile.UsedPerkPoints` getter postfix | suppressed role perks stay in the save but do not count as spent |
| `ClassAbility.SetAsSelectedAbility` prefix | a request to select an ability whose perk is suppressed becomes a deselect, so nothing ever selects-then-deselects it; each such pair restarts the cooldown and republishes the HUD selection event |
| `UnlockContainer.IsUnlocked(GUIDUnion)` postfix | a `PlayerPerk` unlock gated on a suppressed perk reads as locked |
| `UI.Token.PerkTreeController` ctor prefix | tree elements must exist before the ctor's unchecked `Q`; failed injections and suppressed vanilla trees filter the def collection, and suppressed elements are removed from the document |
| `UI.Token.PerkTreeVE.CreateClassTooltip` postfix | the game re-localizes the ClassName label ~1s after attach; restore the manifest title for our trees |
| `UI.NavigatorVE.TryFocusElement` postfix | pad bridge: when controller focus lands inside the tree scroller, scroll the owning tree column into view |
| `UI.Token.TokenTerminal.UpdateLoadoutButtons` postfix | toggles `loadout-role__<role>` classes for framework roles, mirroring the game's own idiom; the rules those classes match come from `UssPalette` - per-role clones of the game's own stylesheet with the role selectors renamed and only the role-hue color entries rewritten, attached via `styleSheets.Add` |
| `BasePlayerHUD.ChangeRole` prefix, `MatchmakingRoom.AddPlayerRoleIcons` prefix | the crew HUD and the lobby room list draw a role icon only through the USS class `coop-buddy-role-state__<role>`; for a framework role each attaches the `UssPalette` clones to its own panel first, since a sheet only styles the panel it is attached to. In those clones every image named for the gunner is repointed at the role's `roleIcon` |
| `ClassAbility.Start` postfix | installs each registered role's factory-built ability into the local character's Opsive ability list, then repoints any vanilla ability whose perk tree is suppressed onto the role that suppressed it (both idempotent per spawn; `OnDestroy` postfix forgets the instance so a respawn reinstalls) |
| `ClassAbility.ActivationKeyPressed`, `AbilityStarted`, `AbilityStopped`, `Update`, `InactiveUpdate`, `OnDestroy` (overridden by `EctypeRoleAbility`) | the cancel policy and owner-only usability. Relies on three facts of the stop and start paths: `ClassAbility.AbilityStopped` removes the effect, then `GoOnCooldown` sets `CurrentCooldown` to `CooldownSeconds.Value`; `FiniteDurationClassAbility.AbilityStopped` zeroes `CurrentActiveDuration` only after that; `FiniteDurationClassAbility.AbilityStarted` copies `FiniteTimeDuration` into `CurrentActiveDuration` after the effect applies. `FiniteDurationClassAbility.InactiveUpdate` calls the virtual `Update`, so `Update` also runs while idle |
| `ClassAbility.RefundCooldown(float)` (called) | the partial cooldown of an early end; a plain `CurrentCooldown` write the HUD reads every frame |
| `ClassAbility.AbilityUsable` setter (called) | owner-only usability; the setter raises `OnLocalPlayerClassAbilityUsableChanged` for the owner's selected ability, and `CanStartAbility` refuses an unusable ability on every client |
| `CharacterLocomotion.OnCollision`, `ChannelGrapplingHook.OnGrapplingStart`, `CharacterAtmosphericDataTracker.OnInInertiaZoneChange` and `OnUpdateSpacePlatform` (subscribed per channel); `FlyJetpack.IsActive` (polled) | the cancel triggers `Collision`, `GrappleAttach`, `InertiaZoneEntry` and `JetpackFlightEnd`. `OnUpdateSpacePlatform` runs on every zone check, so only a change of player ship while still in a zone counts: the hop from one ship's zone into another's that raises no zone event |
| `ClassAbility.VerifyIfIsSelectedAbility` (called, not patched) | the game's own selection check, re-run after a repoint so selection, input binding and the HUD event all run through unmodified game code |
| `UnlockContainer.GetItemsLockedBy(PlayerPerk)` (read) | the same enumeration the fabricator re-reads on every perk change, so an adopted unlock is by construction one the fabricator will see |
| `ClassAbilityDisplay.OnLocalPlayerEnabledClassAbility` postfix | the HUD slot for framework abilities (vanilla resolves by hard type-check on the four stock ability classes), plus status-subscription normalization |
| `ClassAbilityDisplay` `container` / `classAbilityVE` (read), `KeyBindVE._boundInput` + `UpdateKeybind` (written, called) | the HUD ability bar's anchor and parent, and the key drawn on each button's chip |
| `CustomPunCharacter.OnAbilityActive` prefix | diverts a registered runtime ability off the vanilla index RPC and onto the framework's keyed message on its own Photon event; prefab abilities fall through untouched |
| `CustomPunCharacter.OnPlayerEnteredRoom` postfix | late-join sync for running registered abilities, sent to the joiner alone and only if the joiner's `Ectype` player property carries this build's network contract |
| `PhotonNetwork.RaiseEvent` with `TargetActors`, `PhotonNetwork.NetworkingClient.EventReceived` (subscribed) | the framework's message channel, event code 181. PUN creates `NetworkingClient` once, in `PhotonNetwork`'s static constructor; the driver still re-subscribes if the instance changes. The event code must stay clear of the game (0-10), VoidManager (95-99), Opsive's PUN add-on (155-160), Void Crew Fleet (173) and Photon (200 and above) |
| `PhotonNetwork.LocalPlayer.SetCustomProperties` (called), `Player.CustomProperties` (read) | peer presence: the `Ectype` property. Set outside a room it stays local, and `LoadBalancingClient` merges the local player's custom properties into every game-server join request, so the property arrives with the join |
| `RuntimeAssetsRegister.RegisterAsset` with `RuntimeAssetInfo.Name` (called); `GameSessionManager.ValidateMods` and the missing-asset reply, events 7 and 8 (relied on) | the network contract marker. The joiner's rejection screen prints the `Name` the host sends back for an asset the joiner lacks |
| `VoidManager.MPModChecks.MPModCheckManager.ModChecksClientside(Hashtable, bool)` prefix, patched by name when VoidManager is loaded | the joiner's VoidManager check deserializes the room's `Mods` property whenever the room is tagged, and throws when a host without VoidManager tagged it through `R_Mod` alone. The prefix passes a tagged room with no `Mods`. A miss: a joiner with VoidManager cannot join a host without it |
| `CloudSyncController.Write(string key, string, bool, Action<bool>)`, `LocalPlayerProfile.OnQuit`, Steamworks `SteamUserStats.SetStat(string, int)` / `SetStat(string, float)` / `SetAchievement` / `IndicateAchievementProgress` / `StoreStats` prefixes | `ProgressionGuard`: skipped once the client has been in a room whose `R_Mod` is `mod_session`, until restart, with or without VoidManager. `Write` is filtered by key: `BlockedPlayers`, `PlayerLoadout`, `ActivePerkLoadout`, `PerkLoadout_*` and `ShipLoadout_*` pass; every other key (`PlayerProfileData`, `Unlock*`, `AchievementComplete`, `AchievementClaimed`, `Metrics`, `MetaquestTimes`, `Rewards`, `PreservedSession`, and any key a game update adds) is skipped. After a game update, re-list the keys the cloud writers pass to `Write`. `CloudLocalProfile.OnQuit` is not patched: it saves only the unseen-item badges. The game never reads `R_Mod` to stop progression, and VoidManager's switch on the same methods reacts only to `DisableProgression`; the two skip-prefixes coexist. A miss is logged by name and that write is not blocked |
| `VoidManager.PluginHandler.DiscoverPlugins` postfix, `CustomGUI.GUIMain.Instance` / `settings` (inserted into), `ModSettingsMenu.MyVoidPlugin` (written), `PluginHandler.ActiveVoidPlugins` / `GeneratedVoidPlugins` (read) | the F5 page: a `ModSettingsMenu` subclass emitted at runtime, inserted where VoidManager's own name sort would put it. A miss costs the F5 page alone |
| `ChannelGrapplingHook.Awake` postfix | registers the framework's grapple stats onto that hook's own stat collection, and a hook-scoped alias onto the cooldown the base class already registered |
| `PlayerControlledShip.RegisterStats` postfix | registers the framework's ship-scoped stats (`IncomingProjectileSpeed`, and `ShipDrag` over the `MovingSpacePlatform` on the same GameObject, since `Platform` is assigned after `RegisterStats` runs) onto the player ship's own collection |
| `SyncedProjectilesSynchronizer.RegisterNpcProjectile` postfix | binds each hostile NPC shot to one ship as it registers and applies that ship's incoming-projectile-speed factor |
| `VoidJumpSystem.ChangeActiveStateByIndex` prefix + postfix | every void-jump state change on every machine: the owner's from `UpdateState`, everyone else's from `OnPhotonSerializeView`. Raises `ShipArrivals` |
| `VoidJumpSpinningDown.enterTimestamp`, `VoidJumpInterdiction.enterTimestamp` (read) | the replicated start of the exit state, plus its length constant, stamps an arrival on the server clock |
| `StatTagCollection.ApplyModifier(StatMod)` and `ApplyModifiers(IEnumerable<StatMod>, IModifierSource, bool)` postfixes | finds a copy of a timed-stat modifier when a collection applies it: the game copies a dynamic value's initialised flag and never initialises the copy again, so this is the only place a copy of an applied value can be found and put back on the curve. Inert until the first contribution starts |
| `ModDynamicValue<FloatModifier, float>` (subclassed), `StatTagCollection.ApplyModifiers` / `RemoveModifier` (called) | a timed stat contribution's amount is rewritten in place through the game's dynamic-value recalculation, which dirties the stat in each collection that registers it; removal is by source |
| `Player.Awake` postfix | registers the character's field-backed stats and `SystemModuleConstructionSpeed` onto the player's own collection; covers the local player and every replica, idempotent per collection |
| `BuildProcess.Run` prefix | master only (the game runs build processes there): advances a `ConstructionProcess` / `DeconstructionProcess` / `UpgradeProcess`'s private `StateTimer`, in the `Working` and `Completing` states, by `deltaTime * (SystemModuleConstructionSpeed - 1)` of the requesting player |
| `BaseNpcAbility.TryActivateAbility(bool)` prefix | ability suspension: refuses a non-forced start on a suspended enemy; a refusal outside a cast behaviour's tick is held for replay. See [reference/enemy-abilities.md](reference/enemy-abilities.md) |
| every declared `Tick` / `PassiveTick` in the `BaseNpcAbility` hierarchy, prefix | ability suspension: an enumerated set, so a subclass a game update adds is held too |
| `CastAbilitiesRandomlyPhaseDrivenBeh.Tick`, `CastAbilitiesSequentiallyPhaseDrivenBeh.Tick` prefix + finalizer | marks the callers that retry every tick, so their refusals are not replayed |
| `BehSniperAttack.Tick`, `BehDebuffApplier.Tick`, `BehShieldGenerateAOE.Tick`, `ActApplyStatMod.Tick`, `ActShieldTargets.Tick`, `ActReinforce.Tick`, `NpcReinforcementCaller.Update`, `NpcWeapon.Tick` prefix | ability suspension: each system's step, effect or call is held; movement inside the behaviours still runs |
| `BaseToggleableAbility.LastUsageTimestamp`, `ActTimer.idleStartTime`, `BehShieldGenerateAOE.AbilityStartTime`, `ActReinforce.reinforceStartTimestamp`, `NpcReinforcementCaller._callStartTimeInMilliseconds` (written) | the server-timestamp clocks suspension moves forward |
| `NpcAbilityMarkerProvider.Markers`, `MarkerVE.abilitySchedule` / `abilityTime` / `AbilityProgress` (read, written) | the game's cast and call countdown, paused and resumed with the enemy |
| `RuntimeAssetsRegister` / `PerkBuffContainer` / `PerkTreeContainer` / `StatTable` | runtime asset + stat surface |
| `RuntimeAssetsRegister.GetAllIds`, `CarryableContainer.HasItem`, `CraftingDataContainer.HasItem` (called) | the crafting-data audit: a carryable with no crafting definition makes `GetPlayerShipRecycledAlloyValue` throw when it dereferences the missing definition's crafting rules |
| `Client.Utils.ObjectExtensions.DeepCopy` (called, dev tools build only) | the timed-stat copy self-test runs the game's own copier, the one the perk loader runs on every reapply |
| `LootTable.ApplyLimiters` postfix, patched by name after the attribute pass | not role-related - appends registered items to each per-roll table copy; see [reference/loot-tables.md](reference/loot-tables.md). A miss costs loot alone |
| `LootTable.Loot`, `LootTableEntry` (read, appended on the copy) | the anchor baseline (smallest `Amount`, union of `PossibleSpawnLocationTypes`, authored entries only), the rarities the table offers, and the added entry |
| `Resources.LoadAll<LootTable>("Loot")`, `Resources.FindObjectsOfTypeAll<LootTable>()` | the loot warm-up's table list: every shipped table sits in `Resources/Loot`, and nothing else does |
| `ClientGame.OnPlayerShipSet` (subscribed), `ClientGame.PlayerShip` (read) | starts the loot warm-up; a game whose ship is already set counts as raised |
| `CarryableDef.Ref`, `ResourcePaths.TryGetPath`, `Resources.LoadAsync<Object>` | the loot warm-up's prefab preload: the same call as the game's own `ResourceAssetRef.PreloadAssetAsync`, on the path the def's synchronous `Resources.Load` uses |
| `OrbitObject.CsTags` (read on the prefab), `CsTagTable` | `AnyCarryableTagged`: the prefab's authored tags, matched by reference against the named stock tags |
| `LocalizedStringDatabase.TablePostprocessor` (set, chaining any existing one); the internal `LocalizedDatabase.TableOperations` map (read) | `[Presentation] RoleWording`: rewrites listed English string-table entries as each table loads. The map is read once at install to catch tables loaded before the hook; a miss costs only that catch-up |
| `LocalizationExtensions.LocalizationStringCache` / `LocalizedStringCache` (cleared), `InputKeyBinding.RebuildKeybindArray` via `ServiceBase<InputService>._instance._inputKeyBinding` (called) | the same invalidation the game runs on a language change, repeated after a rewrite of a table it may already have read |
| `PlayerProfileData.SetHintSeen(Hint)` prefix, patched by name after the attribute pass | `HowToHints`: returns false for a framework-built hint, so its GUID never reaches the profile's cloud-saved `SeenHints`. A miss turns how-to toasts off rather than let them write the profile |
| `HintsController.TryShowHint` (called), `HintsController.HintsEnabled` postfix | the show path; the postfix returns true only while a forced show is inside its own `TryShowHint` call and `ForceDisableHints` is false, so stock hints keep the player's setting. `HintsEnabled` is the only reader of the Show-hints setting, and the controller's private `ShouldShowHint` is its only gate. Its per-session check is `TimesSeenThisPlaysession > MaxNumberShowsPerPlaysession`, so the framework refuses at `>=` first |
| `UI.HUD.HintDisplayVE.Show(ContextInfo)` postfix | the HUD hint element sets the icon as a background image on its `Icon` child and never hides it; the postfix collapses that child when the hint has no icon and restores it otherwise, because the elements are pooled across stock and mod hints |
| `HintsController.shownHint` / `stackedHints` / `animationFinishedPlaying` (read), `UpdateHintToShow` (called by delegate), `RepeatEventScheduler.ToInvokeEvents` (read), `RepeatEventScheduler.RegisterEvent(float, Func<bool>, Action, float)` | the forced-show countdown. The controller runs `UpdateHintToShow` from a 0.5 s scheduler entry that its settings handler registers only while hints are enabled, so with the setting off a forced hint would never close. After a forced show with no controller tick in the scheduler list, the framework registers one of its own (at most one live per process) and retires it when the hint and the stack are empty, when the controller changes, or when any entry running the controller's own `UpdateHintToShow` appears: the setting turned on, or a dismissal (`RequestHideHint` registers the controller's tick whatever the setting). The check scans the list rather than the controller's handle field, because the settings handler registers a new entry without removing the old one. A retired entry is dropped by the scheduler through its alive callback, never removed during the scheduler's iteration. Any of these missing makes `ShowForced` refuse with the setting off |
| `GameSession.InitStartingMutators` prefix | not role-related; see [reference/mutator-safety.md](reference/mutator-safety.md) |
| `MutatorsElementListVE.UpdateMutatorsList(ReadOnlyCollection<GUIDUnion>)` prefix | not role-related; see [reference/mutator-safety.md](reference/mutator-safety.md) |

## Dev tools build

`dotnet build -c Release -p:DevTools=true` adds `ECTYPE_DEV` to the compile constants and
compiles `src/dev/` plus the F5 menu and dev-key helpers from `src/shared/`. A release build
compiles none of it, so a player's `.cfg` has no `[Dev]` section and the F5 page has no
developer section. The dev build carries `AssemblyMetadata("EctypeDevTools", "true")` and logs
`Ectype Framework <version> (dev tools build)` at startup.

Never ship a dev build. Its bytes differ from the release build at the same version, and
VoidManager's host-side check removes a joiner whose DLL differs from the host's.

What it adds:

- `[Dev] WriteVanillaDump`, the vanilla dump below.
- `[Dev] LayoutWorkbench` and `[Dev] LayoutPollSeconds`: the gene-tree layout reloads from
  `BepInEx/EctypeLayout/layout.json` while the game runs, and the F5 section re-styles,
  rebuilds and captures the open terminal. A release build uses the defaults in
  `src/LayoutSpec.cs`; settle values in the file, then copy them into those defaults.
- `[Dev] CharacterShipTestPerk`: names one framework perk as `roleId/perkKey`, which then
  registers as `characterShip` with JumpChargeSpeed +50% at every level.
- F5 developer section (VoidManager installed and its debug mode on): gene-tree grid dump,
  HUD ability bar dump, the test-perk field, and the layout workbench buttons.
- A self-test at load: each framework message layout is packed, serialized and deserialized
  through Photon's Protocol18 and unpacked as the receive path does, and must come back with
  the same types and values. It logs `dev self-test: net payload round trip passed` or
  `FAILED` with the first difference. Add a layout to it with each new message subtype.
- Ability diagnostics: every framework role ability logs its own lifecycle on the owning
  client, whatever `VerboseLogging` says: input bound and unbound with a running count, the
  ability disabled while still selected, a channel stopped with time left (with a short
  stack), and a key press refused for a reason other than cooldown or already running. Each
  line is a player-caused event, never a per-frame poll. Refusals log at most once per 2 s per
  ability, and all of these lines share a budget of 20 per 10 s, with the count of dropped
  lines carried on the next one.

## Geometry watcher

With `[Debug] VerboseLogging` on, a watcher starts on every terminal build. With it off, no
watcher runs. It polls the tree row's width every 500 ms rather than waiting for a
geometry-changed event, because that event fires only on a layout change and never arrives for
a controller rebuilt into an already-sized document. A row with a width stops the watcher
silently. After 20 s without a width the watcher classifies the cause. A document that is
detached, under a `display:none` ancestor or in an unsized panel is expected, and polling
continues without a log line. A document that is attached, shown and in a sized panel logs one
warning, with the geometry dump appended, and the watcher stops.

## Vanilla dump

In a dev tools build, `[Dev] WriteVanillaDump` (default off) writes the game's stock role trees
to `BepInEx/EctypeDump/vanilla-roles.json` once per launch, as soon as the perk and tree
containers are populated. The file records perk names, tooltip text, grid positions, display
types, targets and each level's stat modifiers: the input for tooling that reads the stock
trees, and the record of what a game update changed in a stock tree. With the switch off, or
in a release build, the framework never reads the stock trees for this purpose and writes
nothing.

## Role wording

Every on-screen "Class" that names a playable role is a Unity Localization string-table entry:
no UXML literal, C# literal or input-action display name carries the word. The game reads those
entries three ways - `LocalizedString.GetLocalizedString`, its own memoising wrappers, and
`StringTableEntry.LocalizedValue` directly - so a postfix on any one lookup method misses the
others. The entry's value is the one point they share, and `TablePostprocessor` is Unity
Localization's hook for editing it: it runs once per table as the load completes, before
anything reads the table, and again on the fresh table loaded after a language change. A
postprocessor set after a table has loaded is never offered that table, hence the catch-up walk
of the loaded-table map at install.

The rewrite is a key list, not a pattern, because the same tables hold ship classes ("REIGN-Class
Frigate", "Ship Class", "Beta class", "DESTROYER class") that must keep the word. Only locales
whose code starts with `en` are touched. The `ContextInfo` assets in `resources.assets` also
carry an English copy of the text as their fallback string; the game shows it only when the
table lookup fails, so it is left alone.

After a game update, re-extract the English tables (`localization-string-tables-english(en)`
bundle under `StreamingAssets/aa`) and search them for "class": a new role string needs a key
added to the list, and a renamed key logs `no entry '<key>'`.

## Standing rules for framework code

- **A raw game field is a stat registration candidate before it is a private write.** Any raw
  field framework or role-mod code is about to read or write gets a framework-registered stat
  with the field as its sink, so perks and abilities contribute through the game's own
  modifier composition. A private write needs a concrete reason.
- **Adding a framework-owned stat is four edits:** a name constant and its spec in
  `src/FrameworkStats.cs`, a consumer that reads it, a row in `docs/stat-names.csv` so tooling that
  reads the alias list can offer it, and an entry in `docs/framework-stats.json` saying what the number
  does and what value the game authored. A stat may be added before anything consumes it, as
  a contributor's way to offer a modifier surface ahead of its reader. A framework stat with
  no consumer is inert and logged at startup. `.github/scripts/check-stats.py` fails when the
  constants, the JSON entries and the framework rows of the CSV disagree.
- **The framework-owned names and the reserved owner string their ids hash from are part of
  the shipped contract across a room.**
- **A framework-owned name that shadows a vanilla one is a framework bug**, caught at
  registration by the `StatTable` collision check.
- Regenerating `docs/stat-names.csv` after a game update uses
  `ilspycmd -t StatType -r <Managed> <Managed>\Assembly-CSharp.dll`, where `<Managed>` is the game's `Void Crew_Data\Managed` folder. The
  category derives from the id's category-flag bits, `(id & 0x0FFFFFFF) >> 20`. The
  regeneration drops the `framework` rows, because those ids are hashes of the reserved owner
  string and the name. Re-append them, taking the ids from the `framework-owned stats
  registered` line the framework logs at startup rather than recomputing them.
- `CharacterStatusReplication` (in `src/CharacterStatus.cs`) records what building replication
  for character statuses involves.
- The saved-mutator-list warning reports a missing mutator by GUID.

## Manifest notes

- `src/shared/ModFlagPolicy.cs` is a compile-time switch for whether a build flags the session.
  Its `MPType` is `Session` or `Client` and never `All`, so a role mod that declares a
  `VoidPlugin` declares `MPType => MultiplayerType.All` itself and returns
  `SetMod_Session = true` from `OnSessionChange`. The framework compiles with
  `VOIDMANAGER_OPTIONAL`, which leaves `MPType` out, and declares no VoidManager type: an `All`
  framework would make every VoidManager host kick every joiner without VoidManager, and a
  `Client` one would make the mod launcher advertise it as client-side only.
- No compiled type in the framework may derive from, or name in a field or signature, a
  VoidManager type. Such a type fails to load without VoidManager, and
  `Assembly.GetTypes()` then throws for the whole assembly, which HarmonyX's
  `AccessTools.TypeByName` and `PatchAll` log as a warning on every call from any mod. A
  VoidManager member may appear only inside a method body that runs solely from VoidManager's
  own code (`MenuKit.DevZone`, reached from the F5 page). The check: the release DLL's
  referenced assemblies do not include `VoidManager`.
- Role mods may copy their boilerplate from `examples/data-only-role/`.

## Posture

Required for everyone in the room: an unmodded peer runs `ReApplyPerks` over framework GUIDs and wipes the
modded player's perks on the peer's client; only a process with this DLL carries that guard.
The mutator-safety prefix needs the same posture for the same reason. Every player in a room
should run the same Thunderstore version of the framework. The mod launcher installs it for a
joiner; nothing in the game compares framework versions. The game's asset check refuses a peer
on another network contract (the contract marker) or with other role content (role assets),
with or without VoidManager. VoidManager's version and hash check covers role mods declared
`All` when both sides run VoidManager, never the framework. `PLUGIN_VERSION` is bumped on every
build.
Registered role content sits in the Required mask, so hosting with any role mod installed
flags the session (one-way). The framework alone also flags at load, through its
contract marker, a `Required` asset. The flag is a process-wide static that is never reset, so a room hosted later in the
same process carries it too. The public statement is [multiplayer.md](multiplayer.md).
