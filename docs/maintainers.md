# Maintainer notes

Internal material for whoever maintains the framework through a game update. It is not linked
from the landing page and is not part of the public documentation. Nothing here is a
supported API.

Start a game-update triage with the [offline checks](#offline-checks), which name every patch
target and by-name lookup the update broke without starting the game, then the hooks table
below, whose row says what each patch was for.

## Source map

Paths are relative to the framework project folder.

| Area | Where |
| --- | --- |
| Helpers compiled into the framework (flag policy, reflection, prefab cloning; F5 menu and dev keys in a dev tools build only) | `src/shared/` |
| Manifest model and validation | `src/Manifest.cs` |
| Framework-owned stat declarations | `src/FrameworkStats.cs` |
| Field-backed stat wrapper | `src/FieldStat.cs` |
| Mutator safety | `src/MutatorSafety.cs` |
| Session-stat mutators (registration walk, opening contribution, shared-unlock republish) | `src/SessionStatMutators.cs` |
| Loot tables | `src/LootTables.cs` |
| Enemy statuses | `src/EnemyStatus.cs`, `src/EnemyStatusFx.cs`, `src/EnemyStatusOutbox.cs` |
| Enemy abilities and suspension | `src/EnemyAbilities.cs` |
| Character statuses | `src/CharacterStatus.cs`; requests, validation and presentation copies in `src/CharacterStatusSync.cs` |
| Session data and host-synced settings | `src/SessionData.cs` |
| Host room values (the host's copy, the channel, `HostRoomValue<T>`) | `src/HostRoomValues.cs` |
| Ship arrivals | `src/ShipArrivals.cs` |
| Ship registry and per-player ship resolution | `src/Topology.cs`; the per-actor facts other features read, and the dev build's synthetic-view seam, in `src/TopologyView.cs` |
| Framework-routed perk targets (scope table, ship sets, distribution, the per-holder route) | `src/PerkScopes.cs`, `src/CharacterShipRoute.cs` |
| Owner-evaluated conditions (parsing, evaluation, the condition-mask message) | `src/HolderConditions.cs` |
| `RoleContext` | `src/RoleContext.cs` |
| Interaction gate (API, kinds and throttle, classifier, chokepoints) | `src/InteractionGate.cs`, `src/InteractionClassifier.cs`, `src/InteractionGatePatches.cs` |
| `RpcContext` (the sender of the RPC or event being dispatched) | `src/RpcContext.cs` |
| Crafting-data audit | `src/CraftingAudit.cs` |
| Content registry (publication, `ModContent`, ordered authored-list insert) | `src/RuntimeContent.cs`, `src/ModContent.cs`, `src/AssetDescriptionOrder.cs` |
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
| Offline checks (a separate console project, never shipped) and the public API snapshot | `tools/offline-checks.ps1`, `tools/OfflineChecks/`, `docs/api-snapshot.txt` |

## Hooks relied on

Each row is a seam in the game, VoidManager or Unity that the framework patches, reads or
calls. A game update that renames or reshapes one of them breaks the feature in the "Why"
column.

| Seam | Why |
| --- | --- |
| `PlayerDataHelper.GetPlayerRole(Dictionary<GUIDUnion,int>)` prefix (replacing) | Replaces fixed Roles array with N-tree to allow more PerkTreeDefs. N-tree seeds from framework-registered PerkTreeDefs. |
| `PlayerPerkLoader.ReApplyPerks` prefix | null-def dereference and unchecked `Levels[level - 1]` wipe perks after `ResetPerks`; drop unknown GUIDs and perks with no levels (covers all mods' strays), plus suppressed perks so their effects never apply, and clamp a held level above the perk's `Levels.Count` to its top level (backstop for a peer whose owner did not refund at load) |
| `PlayerPerkLoader.ReApplyPerks` postfix + finalizer | applies the holder's framework-routed effects (every perk asset with `PerkBuffTarget` value 4, which the loader's switch skips; its meaning is the scope table entry for its GUID) to the ships each scope names (`ClientGame.PlayerShip` only while one ship object exists, else `Topology`'s rules), or to the character for a conditional `character` perk, gated on the holder's condition mask; the finalizer re-runs the apply when the method threw after `ResetPerks` had already removed them. The postfix also runs the target-reach check against the same ship |
| `PlayerPerkLoader.ResetPerks` postfix | removes the holder's routed modifiers, each by its own source |
| `SittingInChair.AbilityStarted` / `AbilityStopped` postfixes, patched by name after the attribute pass | the seat ability runs on every client's copy of a character; each requests the route's pass on the next frame so `seatFleet` follows sitting and standing at once. A miss leaves the 4 Hz pass to notice it. The seat's ship is `Topology.ShipOfPart(seat.Chair)` while the ability `IsActive` |
| `ClientGame.OnPlayerShipSet` (subscribed) | raised from `PlayerControlledShip.Start` after `Awake` has set `PlayerShip`, once per ship; requests the route's pass on the next frame, which rebuilds only holders whose resolved ships changed |
| `PlayerControlledShip.Awake` postfix, `OnDestroy` prefix, `NetPublishShipDeathBegan` postfix | the `Topology` ship registry. `Awake` runs on every client for every ship and is where the ship assigns itself as the game's player ship; the death-began method is an RPC sent to all and sets `StartedDeath` |
| `ClientGame.PlayerShip` (read) | presentation only (the status-marker lock gate, and the distance ranking of status markers and visuals past their caps), and simulation only while `Topology.Multi` is false. A multi-ship mod can postfix the getter to return the viewing client's ship |
| `EventData.Sender` (through the framework's channel), `Room.GetPlayer(int)`, `PhotonNetwork.MasterClient` / `IsMasterClient` (read); `OrbitObject.IsObjectFromDifferentFaction` (through `Topology.Hostile`) | enemy-status authority: the sender's side against the template's stance for an application and a removal, the master's removal exemption, and the master-with-NPC-attacker case. The game's different-faction check is a plain faction comparison, so neutral objects (faction 0) are on another side from every player |
| `Player.State` (`LifeState`) setter postfix, and read | character statuses end on death, and requests need both characters alive. The `NetworkNotifyDeath` RPC (sent to all) sets it to `Dead` on every client, `CustomCharacterHealth.SimulateRemoteDeath` sets it on a client that learns of the death from a health update, and the clone-tube respawn sets the same object back to `Alive`; the game keeps the character object through death and respawn, so the setter is the only place a death shows. `CustomCharacterHealth.IsDead` is set only on the owning client and is not read |
| `LayerMask.GetMask("StaticPlatform", "MovingPlatform", "SpaceObjects")`, `Physics.Linecast` with `QueryTriggerInteraction.Ignore` (called) | character-status line of sight: hull and level geometry plus asteroids, NPC hulls and wrecks. Each ship's colliders stay in the main physics scene (the per-ship simulation scene holds copies), so the cast hits another ship's walls. A renamed layer leaves the mask 0 and every line-of-sight request refused, with one error |
| `SittingInChair` ability (`IsActive`, synchronised), `SpawnSystem.clonetubes` (private, read by reflection), `CloneTube.GetOccupant()`, `InClonetube` ability on the owning client (read) | the `RefuseSeated` and `RefuseInTube` request checks. A missing field reads as not in a tube |
| `Player.ActorId` (`photonView.CreatorActorNr`) through `Topology.CharacterOf`, `PhotonView.IsMine` (read) | character-status requests: the requester's character by the event's sender actor, the target's own character, and who may announce a status on a character |
| `ClientGame.Players`, `Player.ActorId`, `Player.ZonedPlatform` / `Platform`, `OrbitObject.Faction`, `ShipHull.Ship`, `CarryableObject.ParentPlatform` (read) | `Topology`'s answers: a character by actor, the ship a character is aboard, a part's ship, sides |
| Plugin `com.captainchunck.voidcrew.fleet`: `VoidCrewFleet.FleetApi.ApiVersion`, `ShipOfPlayer(int)`, `TeamOf(int)`, `TeamOfShip(int)`, events `ShipsChanged` / `TeamsChanged` (bound by reflection); room property `VCF_Ships` (read, shape-checked) | `Topology`'s crew-ship and team answers and ship numbering under Void Crew Fleet. Bound only for `ApiVersion` 1; answers used only while the plugin component and its `[General] Enabled` entry are on. A missing member is logged once and answered from game data |
| `GuidedProjectile.Target`, `SyncedProjectile.Source` / `Velocity` / `ProjectileId` (read) | the per-shot ship choice for `IncomingProjectileSpeed` |
| `ObjectMarkerHelpers.GetAffiliationBasedOnFaction(OrbitObject)` (called) | status-marker colour; the same helper the game's own object markers use |
| `ModelEventBus.OnPlayerRemoved` (subscribed) | raised by `ClientGame.RemovePlayerInternal` on every client; removes the leaving player's routed contribution |
| `StatTagCollection.RegisterChildCollection` postfix | a detached module root carrying a routed contribution has just had its new parent's modifiers replayed into it; every holder is rebuilt so the contribution is not counted twice |
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
| `RuntimeAssetConverter.RegisterCarryableToContainers(GUIDUnion, string, string, Sprite)` (called) | a published carryable's spawnable-object, carryable, unlock and crafting defs, the game's own path for a carryable it converts at runtime. Unguarded (`Dictionary.Add` in each container), so it is called once per GUID and a throw part-way refuses the publication |
| `ResourceAssetContainer<,,>.AssetDescriptions` (public `List<TDef>`, inserted into), `RegisterRuntimeAsset`, `HasItem`, `TryGetByGuid` on the composite-weapon, module, carryable, spawnable-object (`CloneStarObjectContainer`), crafting and unlock containers | the content registry. The authored list is what `GetAssetDefBySelfRef`, `GetAssetIndex`, `GetFullAssetRefList`, the debug spawn menus and catalog mods read; the game sends an index into an authored list over the network for mutators, pings and emote and multi-slot cosmetic choices, none of them a container the registry writes. Runtime entries are inserted in GUID order after the authored ones; role perk defs by GUID and tree defs by tree ordinal, the order the role argmax breaks ties in |
| `CompositeWeaponDataDef.contextInfo` / `buildBoxIcon` (written by field), `CraftableItemDef.SetCraftingRules`, `PurchasableResourceAssetDef.SetPurchaseCategory`, `UnlockItemDef.UnlockOptions` / `rarity`, `AbstractCloneStarObject.ContainerGuid`, `CompositeWeaponData.ContainerGuid` (written) | the content registry's per-kind data. The purchasable defs' `ContextInfo` property setter resolves the presentable owner of its own GUID and recurses once the def is published; the framework never calls it |
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
| `RaycastHandler.FindInteractable(out AbstractInteractable, out Vector3?)` postfix with `cameraTransform` | interaction gate, client side: the final pick, after the game resolved occlusion and swapped an item in a socket for its `SocketInteractable`. A refused pick clears both outs (the intersection point too, so the handler never holds a point with no interactable) and warns when the point is within 2.3 m of the camera. `IsValidInteractableHit` is not patched: it runs per ray hit before occlusion, so judging there would warn through walls and allocate per hit |
| `TakeoverChair.TrySitInChair` prefix; `TerminalModuleMediator.OpenTerminalUi`, `TerminalScreen.OnInteractClicked`, `RepairObject.ClickedRepairStart`, `Defect.StartDefectRepair` prefixes | interaction gate, client side, at each station's own entry. `OnChairTakenLocal` raises the activation event and then sends the global taken RPC, after the ownership transfer; the terminal paths switch the interaction mode around the event; the repair and defect handlers roll `InstantCrypticWinChance` (a win never calls `CrypticCore.Activate`) and the repair applies a `BreachRepairSkill` mod before `Activate`. Refusing at the event or at `Activate` would strand that state, so neither is judged. `EnhancementCrypticActivator.OnClicked` is not patched either: `EnhancementCrypticAnimator` reacts to the clicker's `ClickStarted` itself, so the panel's minigame is refused at the raycast and its state change at `StateChangePun`. `TrySitInChair` is not judged while the private `isUsing` is set (the game re-asks after a host change) |
| `CustomCharacterLocomotion.OnActivateAbilities` prefix | interaction gate, client side, for activators whose entry raises only that event: clone tubes, the periscope, a mod's station. Registered as an Opsive event handler in `Awake`; the patch is on the method the delegate calls. Seats, `Helm`, `ITakeoverModule`, `TerminalModule`, `TerminalScreen` and `CrypticCore` activators pass here |
| `CarryableInteract.StartInteraction(AbstractInteractable, bool)` and `PickupGrabable(AbstractInteractable)` prefixes with `player` and `lockInteraction`, `InteractiveCarryableModuleUpgrader.IsInvalidUpgradeTarget(CellModule)` postfix | interaction gate, client side. `StartInteraction` judges what the raycast offered; at an occupied socket while carrying it judges the swap's insert, release and follow-up pick-up. `PickupGrabable` runs at the end of the click's frame from `DelayedPickupGrabable` (`WaitForEndOfFrame`), also after a swap with no click of its own; a refusal there clears the `lockInteraction` the caller set and `Carrier.OverrideSentPayloadToNull`, which the game clears only in the insert promise's callbacks. The upgrader is judged only while its `Carrier` is the local player; a true result stops it |
| `DoorActor.Start`, `AirlockButtonPanel.Initialize`, `EnhancementCrypticActivator.Awake`, `EnhancementSwitchActivator.Awake`, `TakeoverChair.Awake`, `TerminalModuleMediator.Start`, `RepairObject.Initialize`, `Defect.Awake` postfixes; private fields `clickers`, `pressurizationSwitch`, `safetySwitch`, `cryptic`, `crypticClicker`, `switchClicker`, `clicker`, and `Defect.clicker`, `TakeoverChair.Clicker` / `ConnectedPhotonViews` (read) | the interaction classifier's registrations: controls that sit outside the object they control, and the seats whose connected views follow their pilot. Each owner registers once, so the map is never cleared; entries whose control is destroyed are pruned every 30 s. Each postfix is patched in its own `try`, so one failure costs that registration alone. A miss leaves those controls classified by type and parents |
| `Grabbable.Carryable`, `CarryableObject.Carrier`, `SocketInteractable.SocketActor.Socket` (read) | the canonical component every query starts from: an item is its `CarryableObject`, an item in a socket and a socket's interactable are the `CarryablesSocket`. The raycast hands the gate a `Grabbable` or a `SocketInteractable`, the ownership events a view on the item, so without this one thing would reach a gate as different components at different chokepoints |
| `AbstractModuleMediator<T>.PowerSwitch` (read by reflection per mediator type, found through `IShipModuleMediator`), `IShipModuleMediator.Module`, `CentralShipComputerMediator` / `CentralShipComputerModule` (type tests), `SwitchClicker.ControlledValue` (`MemberReference.Component`), `PowerSystem`, `PowerPropagator`, `PowerDrain`, `PowerBreaker`, `PowerDistributor`, `Airlock` (type tests) | the classifier's switch rule: a mediator's power switch is `ModulePowerSwitch`, or `ShipPowerSwitch` for the central computer's mediator, whose `Awake` initializes its switch with `ControlledPowerSystem.IsOn` (every other mediator's base initializes it with the module's `PowerDrain.IsOn`); otherwise a switch is classified exactly as the component holding its value, the component `ChangeResponsivePun` names when the owner judges the request, so both sides run one computation on one component. Power types name their kind most derived first: `PowerSystem` (and `ProtectedPowerSystem`, `SelfTogglePowerSystem`) `ShipPowerSwitch`, `PowerPropagator` `PowerLineSwitch`, `PowerDrain` `ModulePowerSwitch`, `PowerBreaker` `Breaker`, any other `PowerDistributor` `OtherPowerSwitch`; `Airlock` is `AirlockPressurize`. A switch with no mediator above it and no holder is `Breaker` when a `PowerBreaker` is on it or above it, else `OtherPowerSwitch`, computed three times in all, 2 s apart, in case its module was still being assembled. A breaker's and a line switch's clicker wiring lives in prefabs and is not readable in the decompile |
| `TakeoverChair.RequestSitInChair(PhotonMessageInfo)`, `DoorController.RequestOpenOnMaster(bool, int, PhotonMessageInfo)` with `_shipDoors` and the `NotifyClientOfOutcome` RPC, `AirlockController.NetworkAirlockOpenRequest` / `NetworkStartPressurization` / `NetworkSafetyRequest` with `airlocks` prefixes | interaction gate, owner side, sender from the message info. The airlock open request is `Door`, the pressurisation request `AirlockPressurize`, the safety request `AirlockSafety`. A refused door answers the asker with the door's real state; the game's `TakeoverRejected` is not an RPC, so a refused seat is dropped as the game drops one under its `BlockedNoReply` |
| `ChangeResponsivePun.RequestBool` / `RequestByte` / `RequestInt` with `ChangeResponsives` and `MemberReference.Component`, `PowerDrain.RequestSetPowerState`, `PowerPropagator.RequestSetPowerState`, `PowerSystem.RequestPowerChange`, `Enhancement.StateChangePun` prefixes | interaction gate, owner side, sender from `RpcContext.TryGetSender` on the component's own view. A power request's kind is the asked component's type (drain, propagator, power system), the synced request's the classification of the value's holder. A call outside an RPC (game logic calling the method directly) has no sender and passes |
| `Carrier.OnOwnershipRequest(PhotonView, Player)` prefix; `CarrierCarryableHandler.TryInsertCarryable` / `TryReleaseCarryable` / `TryEjectCarryable` prefixes with `photonView` and `carrier` | interaction gate, owner side. Every carrier hears every request-type ownership request; only the holder of the requested item on its owner's game is judged. Socket requests are Photon events 3, 4 and 5 whose `data[0]` is the carrier's view id, answered by the carrier inside Photon's dispatch; a refusal returns a rejected promise, which the handler turns into the game's failure reply (event 6) |
| `PhotonNetwork.ExecuteRpc(Hashtable, Player)` prefix + finalizer, `LoadBalancingClient.OnEvent(EventData)` prefix + finalizer | `RpcContext`: a stack of one frame per message being dispatched, the outer frame saved in `__state` and restored by the finalizer. `ExecuteRpc` is PUN's single entry for every RPC: `PhotonNetwork.OnEvent` calls it for event 200 with the room's player for `EventData.Sender` (null for an actor not in the room), and `RPC` calls it directly with `LocalPlayer` for an RPC that targets the local player; key byte 0 of the RPC data is the target view id. A network RPC with a null player takes its actor number from the enclosing event 200. `LoadBalancingClient.OnEvent` dispatches every received event to PUN and to every `EventReceived` listener, carriers' callbacks included, so subscription order does not matter; `EventData.Sender` is -1 when parameter 254 is absent, reported as 0. The gate's socket requests read the innermost event from it. **Patched from the driver once `RpcContext.Enable()` was called, a gate is registered or a member was read, and `GameStateMachine.Instance.CurrentState` is neither `GSNull` nor `GSLoading`, never at plugin load**: patching `PhotonNetwork` at start-up sets Photon up before the game does and the connection never passes the name server. Nothing asking, nothing patched |
| `PhotonNetwork.OnEvent` prefix | the interaction gate's ownership hook: ownership events 209 (sender takes the view; `data[1]` is the owner it expects), 210 (`data[1]` is the new owner) and 212 (`int[]` of view id, new owner pairs; PUN applies it from any sender to any view, the game's host alone sends it). **Patched from the driver once a gate is registered and `GameStateMachine.Instance.CurrentState` is neither `GSNull` nor `GSLoading`, never at plugin load**: patching `PhotonNetwork` at start-up sets Photon up before the game does and the connection never passes the name server |
| `PhotonView.TransferOwnership(int)` (called), `CarryableObject.OwnerChange` (invoked) | ownership changes are decided by the view's owner, or the host for a view whose owner is absent. PUN applies a Takeover 209/210 on each client independently and only when the event arrives, the sender's own echo included. The deciding client ignores a refused change and transfers the view back to itself (to 0 for a room object); the server orders that 210 after the refused event for every receiver, so clients that applied it converge. Skipped: a 210 sent by the master, the owner or the controller (the game's own hand-offs, such as `CarryablesSocket.OnOwnerChange` passing its payload to the socket's new owner, sent by every client) and a view following its holder (a socket's payload to the socket's owner, a seat's `ConnectedPhotonViews` to the seat's owner). The taker's own client judges its own 209 and on a refusal invokes the item's `OwnerChange` with the unchanged owner: `CarrierCarryableHandler.CarryableOwnerChange`, its only subscriber, then rejects the insert promise with "Carryable given to different owner", and `PickupGrabable`'s `Catch` clears `lockInteraction`. Without that, the promise stays pending, `lockInteraction` stays set and every later carrier request is refused as "another request pending". The taker then transfers a Takeover view to the owner it still records; on every client that applied the 209 the taker is the owner, so that 210 passes as an owner's hand-off and the room converges even when the decider allowed it. A 210 handing a view the taker refused in the last 5 s back to it (a request-only owner that allowed it) is applied and transferred back from the driver once the taker owns it. A 210 naming the owner a client already records is not judged. A non-master 212's refused pairs are rewritten in the event's own `int[]` (PUN reads the same cached array) to the current owner. An `IPunOwnershipCallbacks` listener, added when the Photon hooks are patched, settles the local player's pick-up the same way on `OnOwnershipTransferFailed` (PUN refuses a Takeover 209 whose expected owner `data[1]` is stale) |
| `ViewEventBus.OnShowQuickWarning` (published) | the interaction gate's warning, the game's own quick-warning line |
| Every interaction gate hook carries `HarmonyAfter("com.captainchunck.voidcrew.fleet")` | Void Crew Fleet patches the same chokepoints. Running after it, a client hook sees `__runOriginal == false`, or a pick Fleet already cleared, when Fleet refused and stays silent. HarmonyX runs every prefix even after one returned false |
| `GameSessionManager.PrepareNextSession(bool asHost)` postfix (private), `GameSession.AsJObject` postfix, `GameSession.FromJObject(JObject)` postfix, all patched by name after the attribute pass | `SessionData`. `PrepareNextSession` makes the new session object active on every machine (`HostGameSession` and `JoinGameSession`, after `UnloadCurrentSessionData`, before `LoadQuest` and `LoadInitialSector`); its postfix captures on the host (`asHost` true) and applies everywhere. The `AsJObject` postfix only writes the envelope attached to that very object. The host serialises once per session, in `DistributeSessionData`, which raises event 9 (`AddToRoomCacheGlobal`, `ReceiverGroup.Others`, reliable) after `CleanupPhoton` has cleared the room cache; the host never calls `FromJObject` on its own session. The `FromJObject` postfix checks and attaches the envelope to the rebuilt object, which `JoinGameSession` activates; a joining client's `PrepareNextSession(false)` then gives that object a new random `GameSessionID`, so the host's id is kept with the envelope. Both start states (`GSMasterStartGame`, `GSClientStartGame`) load the session scene and the game-state object first, and every scene but `Bootstrap` is unloaded before them, so no hub object exists at capture. A miss turns session data off and `HostValue` follows each client's config |
| `PhotonService.OnEvent(EventData)` prefix + finalizer | records the sender of event 9 for the `FromJObject` postfix, which acts only inside that handler. The game's case 9 checks only that the receiver is not the master, and the game never calls `SetMasterClient`. Photon delivers the room's global cache as actor 0, so a late joiner checks only that the envelope's `a` stamp does not name a present player other than the master; a forger who caches a session stamped with the master's number or any number not in the room, a never-joined one included, passes for joiners until the host's next session clears the cache. The prefix also keeps the event's raw string: the game parses it with Newtonsoft's default date detection, so the `FromJObject` postfix re-reads the envelope from that string with date detection off. A miss leaves only the stamp check |
| `GameSession.GetPreservedState` postfix; `PreservedGameSession.GameSessionID` (read) | the saved-run sidecar under `BepInEx/config/jack.voidcrew.ectype.sessions/`, written at every save and named by the id the save carries; the game holds one saved run per profile (`PlayerPreservedSessionData.PreservedSession`). Without this hook the sidecar is written at mission start instead. `GameSessionID` is `Guid.NewGuid().ToString()` from `GenerateSessionID`; a fresh session gets one on the host and on every client as it activates, and `PreservedGameSession.GenerateGameSession` copies the saved id on resume while dropping the envelope. A host who took over mid-run saves under its own client-side id, which the sidecar follows |
| Room property `Ectype.HostValues` (written by the master in the hub, read), `LoadBalancingClient.EventReceived` event 253 (subscribed: `Sender`, parameter 253 target actor, parameter 251 properties) | `HostValue<T>`'s hub channel, one property for every key. `EventReceived` runs after `LoadBalancingClient` has merged the change into the room's copy, so the writer and the merged value are seen together. A join response carries properties with no writer |
| Room properties `Ectype.Value:<key>` (written by the master, read), `LoadBalancingClient.EventReceived` event 253 (the same subscription as `Ectype.HostValues`), `PhotonNetwork.AddCallbackTarget` with `IMatchmakingCallbacks.OnJoinedRoom` / `OnLeftRoom` and `IInRoomCallbacks.OnMasterClientSwitched` | `HostRoomValue<T>`. The join callback runs as the join response is read, before any property event of the room is dispatched, so a joiner's first read is the join response's and a later foreign write is judged by its sender. The master-switch callback runs as the leave event is read, before the departed host's successor dispatches anything. `Room.SetCustomProperties` with `BroadcastPropsChangeToAll` (the default; the game's `RoomOptions` leave it) echoes the host's own write back with the host as sender, which the host ignores. The game's `RoomOptions` leave `DeleteNullProperties` false, so a value put back as none stays as a null property |
| `GameSession.InitStartingMutators` prefix | not role-related; see [reference/mutator-safety.md](reference/mutator-safety.md) |
| `MutatorsElementListVE.UpdateMutatorsList(ReadOnlyCollection<GUIDUnion>)` prefix | not role-related; see [reference/mutator-safety.md](reference/mutator-safety.md) |
| `GameSessionManager.PrepareNextSession(bool)` prefix, `GameSession.InitStartingMutators` postfix, `UnlockContainer.IsUnlocked(GUIDUnion)` postfix, all patched by name after the attribute pass | `SessionStatMutators`. The prefix re-verifies every registered mutator before the host snapshots its required assets and before a joiner compares them. The postfix compares the mutators the opening contributed with the session's `ActiveMutators`. The unlock postfix answers true for a registered framework mutator, which `PhotonPlayerUnlockSync`'s constructor reads when it writes the `P_SM` player property in full |
| `GameSessionManager.HostGameSession` order: `PrepareNextSession`, `LoadQuest` (`GameSession.LoadQuestMutators`: the quest asset's `ForcedMutators` through `AddMutator`), `LoadInitialSector`, `DistributeSessionData` (event 9), `LoadGameSessionPassiveAssets` (`InitStartingMutators`: `StartingMutators`, whitelist-filtered when `QuestAsset.UseMutatorWhitelist`) | why the opening predicts the active set instead of reading `ActiveMutators`: no mutator is active at the `PrepareNextSession` capture. The prediction mirrors both methods: forced list first, then the starting list skipping a GUID already active, one outside the whitelist and one the container cannot resolve; the hub (`IsHub`) has no `ActiveQuest`, so only its starting list counts. A reordering, or a new activation route, changes what clients receive |
| `GameSession.SessionQuestAsset`, `GameSession.StartingMutators` (read at `PrepareNextSession`) | the prediction's inputs. `HubQuestManager.StartQuest` sets both on a fresh session (`StartingMutators` is the hub's `SelectedMutators`); `PreservedGameSession.GenerateGameSession` sets both from the save |
| `ResourceAssetContainer.GetAssetIndex` (indexes `AssetDescriptions`), `GetAllItems` (returns `RuntimeDescriptions`), `PhotonPlayerUnlockSync.SetSharedUnlock` / `GetSharedUnlockIndices`, the profile's `UnlockedItems` chain (`ChainOfResponsibility.source`) | the shared-unlock republish and the runtime-list alignment: `P_SM` holds `GetAssetIndex` values and `GetSharedUnlocks` reads them back through `GetAllItems`, so the two lists must agree in order |

## Offline checks

The framework binds the game by name: Harmony patch targets, `AccessTools` and `Type.GetX`
lookups, `Traverse` chains. It binds Void Crew Fleet's `FleetApi` and VoidManager by name too.
A rename in any of them compiles cleanly and surfaces as a log line on a player's machine. The
offline checks find it first, without starting the game:

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\offline-checks.ps1
```

Run them after every game update, after a Fleet or VoidManager update, and before every
release. The script builds the framework (Release) against the game folder the build resolves,
then runs the checker, which reads the built DLL and every assembly in the game's `Managed`
folder, `BepInEx\core` and `BepInEx\plugins` as metadata. Nothing is loaded or executed. It
ends with `ALL CHECKS PASSED` and exit code 0, or lists each failure and exits 1.

Without PowerShell, the same run is
`dotnet build -c Release` and then
`dotnet run --project tools/OfflineChecks -c Release -- --framework bin/Release/EctypeFramework.dll --game "<Void Crew folder>" --targets tools/OfflineChecks/manual-targets.txt --snapshot docs/api-snapshot.txt`.

| Switch | Effect |
| --- | --- |
| `-VoidCrewDir <folder>` | Check against another install, for example a beta branch copied aside. |
| `-Plugin <dll>` | Also check bindings against this plugin DLL; repeatable. Fleet installed by a launcher lives outside `BepInEx\plugins`, so pass its `VoidCrewFleet.dll` here. |
| `-UpdateSnapshot` | Rewrite `docs/api-snapshot.txt` from the built DLL instead of comparing. |
| `-ShowAll` | List every check with what it resolved to, not only the failures. |
| `-NoBuild` | Check the DLL already in `bin\Release`. |

### What is checked

1. **Patch targets and by-name lookups.** The checker recovers every lookup from the compiled
   IL rather than from the source. It runs each method abstractly, tracking constants
   (strings, `typeof`, `Type[]` and `string[]` initialisers, `BindingFlags`) through the stack,
   locals and arguments. A call into another framework method that passes a constant is
   followed into that method with the constant bound, so a helper such as
   `Patch(type, "Name", args, ...)` is checked at each call site with the names its callers
   pass. Each lookup is resolved with the rules of the API that makes it:
   - `AccessTools.Field/Method/Property` walk the base types and see private members of each.
   - `AccessTools.Declared*` and `[HarmonyPatch]` targets see the type's own members only.
     HarmonyX resolves an attribute target with `AccessTools.DeclaredMethod`, so a target
     method that moved to a base class fails the patch.
   - `Type.GetX(name, flags)` follows the flags: an inherited member is seen only when it is
     not private, and an inherited static one only with `FlattenHierarchy`. A method that
     walks `Type.BaseType` itself is checked against the whole chain.
   - A method lookup without parameter types must match exactly one method: `Type.GetMethod`
     and `AccessTools.DeclaredMethod` throw `AmbiguousMatchException` on an overload.
   - The type checks the framework makes on what it found are checked too: a comparison of
     `FieldType`, `ReturnType`, `PropertyType` or `EventHandlerType` with a `typeof`,
     `typeof(F).IsAssignableFrom(...)`, the field type behind `FieldRefAccess<T, F>`, and the
     signature of a delegate bound with `Delegate.CreateDelegate` or
     `AccessTools.MethodDelegate`.
   - A value read through a looked-up member has that member's type, so in
     `Traverse.Create(sheet).Property("rules").GetValue()` followed by `.Field("m_Properties")`
     on an element, `m_Properties` is checked on the element type of `rules`.
   - The same name looked up twice on one type within one call is a fallback chain (a
     property, else a field; an exact overload, else any). It passes when one link resolves.

   Every patch method is then checked against the target it is applied to: it is static, and
   each injected parameter fits. `__instance` needs an instance target whose declaring type is
   assignable to the parameter; `__result` a non-void target whose return type fits;
   `___name` a field of that name on the target's type or a base; `__N` an argument index in
   range; any other name a target parameter of that name and a fitting type. A parameter
   carrying `[HarmonyArgument]` is matched by the name or index the attribute gives. Harmony
   checks few of these when it patches; a wrong one fails at patch time or produces bad code.
2. **Public API snapshot.** `docs/api-snapshot.txt` lists every public type and member of
   `EctypeFramework.dll`, plus the protected members of types a mod can derive from, one
   signature per line, grouped by type and sorted. The check prints each removed line with
   `-` and each added line with `+`. An intended change is committed together with the
   rewritten snapshot (`-UpdateSnapshot`), so the diff of the snapshot in a pull request is the
   API change.
3. **Bindings into other plugins.** Members the framework looks up in a plugin by a type name
   string, `VoidCrewFleet.FleetApi` and VoidManager's types, are checked like game members
   when the plugin's DLL is under `BepInEx\plugins` or passed with `-Plugin`. A version
   constant the framework compares, a const field looked up by name with a framework const
   named `Supported` plus that field's name, must have the same value. A plugin that is absent
   is skipped with one line, not failed. A type name is treated as a plugin's when no game or
   BepInEx assembly defines a type in its top-level namespace.

The run also lists lookups whose type or name is known only at run time. Those are not
checked. When one of them reaches a game member that matters, add the member to
`tools/OfflineChecks/manual-targets.txt`, which is checked like a recovered lookup; each entry
says above it which framework code needs it.

### Things that look like they should work

- **A helper whose parameters carry the names, called with names that are not constants.** The
  checker follows a call only when a constant goes in. A name built at run time, read from a
  config, or taken from a list built with `Add` leaves the lookup unchecked, and it appears
  under "not checked". Pass names as literals, or as literal arrays a `foreach` walks.
- **A lookup on `obj.GetType()` where `obj` is declared `object`.** The declared type is the
  only type information the IL has. Declare the variable as the game type (or cast to it) and
  the lookup is checked on that type and its subclasses.
- **Patching a method by name on the class that inherits it.** `AccessTools.Method` finds it,
  `[HarmonyPatch]` and `AccessTools.DeclaredMethod` do not, and a patch on the method found
  through the subclass patches the base class's method for every subclass. Name the declaring
  class.
- **An overload added by a game update to a method looked up by name alone.** The lookup that
  resolved before the update throws `AmbiguousMatchException` after it, and in a static
  initialiser the exception takes the whole class with it. The check lists the overloads; give
  the lookup parameter types.
- **Regenerating the snapshot from a dev tools build.** The dev build compiles `src/dev/` and
  has a different public surface. The script always builds Release; with `-NoBuild` the DLL in
  `bin\Release` must be a release build.

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
- A status coalescing self-test at load: ten applications in one 150 ms window must leave the
  outbox as one batch carrying 10, in fields matching the ApplyN layout. Two lives of one
  status must make two batches, a removal must drop open and closed batches of its own key
  only, and the cap margin is checked at four distances.
- A status authority self-test once a character and its crew ship exist: three templates with
  no stats and no marker are driven through the receive path with synthetic senders against
  that ship. A same-side `Hostile` apply must be refused twice with one log line; ApplyN 10
  then 3 must leave 13 stacks; an `Any` status must be removable by actor 9999; a `Hostile`
  status must refuse a non-master remover its stance would not let apply it, and accept the
  master; a received removal must drop this client's queued batch; and, on the master with a
  hostile NPC in the sector, a `Hostile` apply with that NPC as attacker must land. Its two
  refusal warnings are expected.
- An interaction gate self-test at load, logging `dev self-test: interaction gate passed` or
  `FAILED`: the warning throttle on a test clock, registration order, null and silent answers,
  unregistering, and the classifier on game components added to an inactive object (so their
  `Awake` never runs), including a switch outside any module reading `OtherPowerSwitch` twice in a
  row; each power type, a switch holding each power component's value, a switch under a breaker,
  and a module's and the central computer's mediator-named switch reading their split kind;
  `IsPowerSwitch` and `IsAirlockControl` covering exactly the split kinds; and an item, a socket
  and a switch with its value holder reaching gates as one component with one kind from every
  entry. It makes no Photon call and does not read `RpcContext`, since touching `PhotonNetwork` during
  plugin load sets Photon up before the game does.
- A refused pick-up check: half a second after the local player's pick-up is refused, the
  framework reads the carry ability's `lockInteraction` and logs `dev check: a refused pick-up
  left the carry interaction unlocked` or `FAILED`.
- `[Dev] RefuseAllSeats`: registers a gate refusing every seat, the helm and turrets, at once
  when switched. On the acting client the seat shows no prompt and warns; switched on only on
  the host, a client's seat request is dropped by the host. It is local configuration, so with
  it on only one game the two games disagree, which is what the check needs and what a real
  gate must never do.
- A session-data self-test on the first frame: an envelope round trip through the game's own
  `GameSession.AsJObject` and `FromJObject` with the framework's postfixes (diverted to the
  test), the per-key cap and the largest-key envelope refusal, the writer rule for live and
  cached delivery, sidecar write, replace, read and prune in a temporary folder, the host-value
  stamp and token kinds, and the type checks. The capture at `PrepareNextSession` needs a live
  session start and is not covered. It logs `dev self-test: session data passed` or `FAILED`
  with each difference.
- An identifier self-test in the framework's own `Awake`, while BepInEx is still loading
  plugins: the framework's GUID must already be in the chainloader's plugin table, and the
  check must accept a conforming owner and id and refuse a missing prefix, an unknown GUID, the
  GUID in other capitals, a reserved owner, and a role id unless the role exception is asked
  for. It logs `dev self-test: identifiers passed` or `FAILED` with each wrong answer.
- A session-stat mutator self-test at load, offline: the GUID vector of
  `com.captainchunck.voidcrew.fleet/pilgrimage`,
  every spec refusal, the kind mapping, the manifest form, the activation prediction for the
  hub, a mission and a quest whitelist, and a synthetic active mutator (through the dev-only
  activation-input override) run through the real opening handler, whose effects must be in the
  values the opening returns for publication. Once the stats files are read, a session stat must
  resolve and a character stat and an undeclared one must be refused. The live order of the
  game's activation against the capture needs a session start and is not covered. It logs
  `dev self-test: session-stat mutators passed` and `dev self-test: session-stat mutator stats
  passed`, or `FAILED` with each difference.
- A content-registry self-test at load, offline: the ordered insert gives one list from four
  publish orders and leaves authored entries in place; a carryable and a component carryable
  without crafting data are refused with a reason naming the GUID; an entry reads back through
  `TryGet` and `All` as a copy and raises `Changed` on add and removal. It logs
  `dev self-test: content registry passed` or `FAILED` with each failure.
- A character status request self-test at load: the request, answer, announcement and end
  messages as their builders make them must match their layouts and survive a Protocol18 round
  trip; the judge, fed synthetic views of the two characters, must return each refusal reason
  on its own input (a key without `AcceptsRequests`, a short duration, a zero magnitude, out of
  range on the target's view but in range on the requester's, a blocked line, a seat, a tube,
  each stance, dead and missing characters) and cut over-cap values; the rate buckets must hold
  `Burst` 2 per 1 s, accept jittered arrivals with the target's extra token, and cap a requester
  at 20 at once; a completed handle must run a late callback once; each `Visibility` rule must
  choose its viewers; and six bad templates must fail validation.
- A character status authority self-test once this client's character exists: requests are
  driven through the receive path against that character, with templates that accept requests
  only from 0.01 m. Actor 9999 must be refused `SenderMissing`, a key without
  `AcceptsRequests` `NotAccepted`, a request naming another actor `InvalidTarget`; this client's
  own actor must land the status and move `MotorAcceleration`, and a third request inside
  `MinInterval` must be `RateLimited`; a shorter, stronger request must keep the end and lower
  `MotorAcceleration` further; an `OnRemoved` that re-applies its own key must start a fresh
  status with one stack and leak no modifier; the life-state setter must carry the death-hook
  postfix, and the hook must end a status and a copy; a forged
  announcement must be refused `Authority`; and a presentation copy on the character must leave
  `MotorAcceleration` and the `OnApplied` count unchanged. Its five refusal warnings are
  expected.
- A perk scope self-test at load, offline: a synthetic two-side room (side 0 with two living
  ships and a dying one, side 1 with one) and a one-ship room are installed through
  `TopologyView.UseTestView` for the call. Each ship set must name the expected ships for eight
  actors (crew, boarder, floater, no character, ally visitor, seated on the other side, crew of
  the dying ship, visitor on the dying ship), `characterAboard` resolved from the answer each
  holder's own client would send and from answers naming no ship, another side's ship and a
  dying one; every set must collapse to the room's ship in the one-ship room, split factors
  and scaled copies must sum to the authored amount, validation must accept and refuse each
  documented case, each actor's condition mask must match, the debounce must adopt on the
  second read, every mask 0-127 with an aboard ship number must survive the condition-mask
  message's Protocol18 round trip and the receive record, and `RoleContext` must answer for a
  synthetic owner. It logs
  `dev self-test: perk scopes passed` or `FAILED` with each wrong answer.
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
- **A generic factor with no field behind it** (the melee family, `AntiPersonnelDamage`,
  `HostileSpawnScale`) takes a row in the collection table of `src/ModStats.cs` in place of a
  consumer. The framework registers it into that collection at base 1 and never reads it; the
  mod that owns the action reads it by id. Its JSON entry says so in `consumers`.
- **The stat id formula is a published contract.** Mods that never reference the framework
  compute it themselves, so `Guids.StatId`, the `ectype:stat:` prefix and the `owner/name`
  join never change. The dev self-test checks the documented vector.
- **Mod-declared stats are not rows of `docs/stat-names.csv`.** The CSV lists vanilla and
  framework-owned stats only; a mod's stats file is its own catalogue.
- **The session collection is decided by the host.** A change to how its values travel is a
  change to the `framework/sessionStats` session data key's format, which both sides of a room
  read; keep it `{"<id>": value}`.
- **A framework-owned name that shadows a vanilla one is a framework bug**, caught at
  registration by the `StatTable` collision check.
- Regenerating `docs/stat-names.csv` after a game update uses
  `ilspycmd -t StatType -r <Managed> <Managed>\Assembly-CSharp.dll`, where `<Managed>` is the game's `Void Crew_Data\Managed` folder. The
  category derives from the id's category-flag bits, `(id & 0x0FFFFFFF) >> 20`. The
  regeneration drops the `framework` rows, because those ids are hashes of the reserved owner
  string and the name. Re-append them, taking the ids from the `framework-owned stats
  registered` line the framework logs at startup rather than recomputing them.
- A character status is judged and applied on the target's own client, never on the requester's
  word: the request carries no position or state, and the target's client reads both
  characters from its own simulation. A new check belongs in the pure judge, fed by what the
  target's client gathers, so the requester can run the same function on its own view and the
  self-test can feed it synthetic views.
- The saved-mutator-list warning reports a missing mutator by GUID.

## Perk targets and conditions

- The perk asset's `Target` is what the game sees and the only thing it reads: the perk loader
  applies 1 (seat), 2 (character) and 3 (seatShip) and skips anything else, and the gene-tree
  tooltip (`ContextInfoMaker.ComposeBodyString`) renders the description and each level's
  `StatMod` descriptions and never the target. Every framework-routed target therefore
  writes 4 and keeps its meaning in the scope table keyed by perk GUID. A new target is a new
  scope, never a new game-visible value. After a game update, check that the loader's switch
  still skips 4 and that no new reader of `PerkBuff.Target` appeared.
- A condition is owner-evaluated: only the holder's client reads its own character, and
  every client gates on the answer it received, `[int mask, int aboardShip]`. Anything decided
  from where a character stands belongs in that answer, because each client samples another
  player's character against an inertia bubble at its own moment: `characterAboard` resolves
  from `aboardShip`, never from `TopologyView.AboardShip`, which is the owner's reading only. A
  new condition is a new bit in `HolderCondition`, which changes what the condition-mask message
  carries: raise `NET_CONTRACT_VERSION`. A condition on another ship's state is not
  owner-evaluated and does not belong in that mask.
- `TopologyView` is the only input the scope resolution, the condition evaluation and
  `RoleContext` read, so a rule added there is testable with a synthetic view. Never read
  `ClientGame.PlayerShip` in them except through the live view's one-ship branch.

## Manifest notes

- `src/shared/ModFlagPolicy.cs` is a compile-time switch for whether a build flags the session.
  Its `MPType` is `Session` or `Client` and never `All`, so a mod that declares an
  `All` `VoidPlugin` declares `MPType => MultiplayerType.All` itself and returns
  `SetMod_Session = true` from `OnSessionChange`. A role mod that follows the examples declares
  no `VoidPlugin`, so none of this applies to it. The framework compiles with
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
