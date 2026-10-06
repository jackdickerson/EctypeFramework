# Maintainer notes

Internal material for whoever maintains the framework through a game update. It is not linked
from the landing page and is not part of the public documentation. Nothing here is a
supported API.

Start a game-update triage with the hooks table below: a Harmony patch that fails to bind
names itself in the log, and the row says what the patch was for.

## Source map

Paths are relative to the framework project folder, except where marked.

| Area | Where |
| --- | --- |
| Class-framework design and work plan | `plans/07-class-framework.md` in the VoidCrewModding repository |
| Helpers shared with the VoidCrewModding mods (flag policy, reflection, F5 menu, kitbash, dev keys) | `src/shared/`, copies of that repository's `shared/` files |
| Engine facts the framework is built on (Harmony, UI Toolkit, Opsive, Photon, stats and perks) | `docs/engine-notes.md`, a map into the `vcm` plugin's reference set |
| Manifest model and validation | `src/Manifest.cs` |
| Framework-owned stat declarations | `src/FrameworkStats.cs` |
| Field-backed stat wrapper | `src/FieldStat.cs` |
| Mutator safety | `src/MutatorSafety.cs` |
| Selection gate | `src/SelectionGate.cs` |
| Loot tables | `src/LootTables.cs` |
| Enemy statuses | `src/EnemyStatus.cs`, `src/EnemyStatusFx.cs` |
| Enemy abilities and suspension | `src/EnemyAbilities.cs` |
| Character statuses | `src/CharacterStatus.cs` |
| Ship arrivals | `src/ShipArrivals.cs` |
| Timed stat contributions | `src/TimedStatContributions.cs` |
| Ship speed surge | `src/ShipSpeedSurge.cs` |
| HUD ability bar | `src/HudAbilityBar.cs` |
| Hand attractor stats | `src/HandAttractorStats.cs` |
| Runtime ability replication | `src/AbilitySync.cs` |
| Geometry watcher | `src/PresentationDiagnostics.cs` |

## Hooks relied on

Each row is a seam in the game, VoidManager or Unity that the framework patches, reads or
calls. A game update that renames or reshapes one of them breaks the feature in the "Why"
column.

| Seam | Why |
| --- | --- |
| `PlayerDataHelper.GetPlayerRole(Dictionary<GUIDUnion,int>)` prefix (replacing) | Replaces fixed Roles array with N-tree to allow more PerkTreeDefs. N-tree seeds from framework-registered PerkTreeDefs. |
| `PlayerPerkLoader.ReApplyPerks` prefix | null-def dereference and unchecked `Levels[level - 1]` wipe perks after `ResetPerks`; drop unknown GUIDs and perks with no levels (covers all mods' strays), plus suppressed perks so their effects never apply, and clamp a held level above the perk's `Levels.Count` to its top level (backstop for a peer whose owner did not refund at load) |
| `PlayerPerkLoader.ReApplyPerks` postfix + finalizer | applies the holder's `characterShip` effects to `ClientGame.Current.PlayerShip.Stats`; the finalizer re-runs the apply when the method threw after `ResetPerks` had already removed them. The postfix also runs the target-reach check |
| `PlayerPerkLoader.ResetPerks` postfix | removes the holder's `characterShip` modifiers, each by its own source |
| `ClientGame.OnPlayerShipSet` (subscribed) | raised from `PlayerControlledShip.Start` after `Awake` has set `PlayerShip`; rebuilds every holder's `characterShip` contribution onto the new ship |
| `ModelEventBus.OnPlayerRemoved` (subscribed) | raised by `ClientGame.RemovePlayerInternal` on every client; removes the leaving player's `characterShip` contribution |
| `StatTagCollection.RegisterChildCollection` postfix | a detached module root carrying a `characterShip` contribution has just had its new parent's modifiers replayed into it; every holder is rebuilt so the contribution is not counted twice |
| `PlayerPerkLoader.ReApplyPerks` postfix | catch-up for ability adoption: the spawn-time sweep skips a class whose registration has not settled, and this path runs on every perk change |
| `CG.Profile.PerkLoadout(Dictionary<GUIDUnion,int>)` ctor prefix | profile-load vs. registration race, the load half of the vanilla-tree suppression scrub (vanilla degrade semantics), and the lost-level refund: a saved level above the perk's `Levels.Count` is lowered to it, so the points come back as unspent and the owner's `PBL` publishes the lowered level |
| `PlayerProfile.UsedPerkPoints` getter postfix | suppressed class perks stay in the save but do not count as spent |
| `ClassAbility.SetAsSelectedAbility` prefix | a request to select an ability whose perk is suppressed becomes a deselect, so nothing ever selects-then-deselects it; each such pair restarts the cooldown and republishes the HUD selection event |
| `UnlockContainer.IsUnlocked(GUIDUnion)` postfix | a `PlayerPerk` unlock gated on a suppressed perk reads as locked |
| `PlayerPerkLoader.ReApplyPerks` (called) | on a host-suppression change, every player's stats re-apply from their published `PBL` with `OnPerkBuffLevelsUpdated` held, then one pass flips only the local abilities whose selection changed, carrying the running cooldown across a select |
| `TokenTerminal` / `LogBookManager` `treeController` (read, replaced) | both build their tree controller once in `Start`; a host-suppression change disposes it and constructs a new one over fresh authored tree elements |
| `UI.Token.PerkTreeController` ctor prefix | tree elements must exist before the ctor's unchecked `Q`; failed injections and suppressed vanilla trees filter the def collection, and suppressed elements are removed from the document |
| `UI.Token.PerkTreeVE.CreateClassTooltip` postfix | the game re-localizes the ClassName label ~1s after attach; restore the manifest title for our trees |
| `UI.NavigatorVE.TryFocusElement` postfix | pad bridge: when controller focus lands inside the tree scroller, scroll the owning tree column into view |
| `UI.Token.TokenTerminal.UpdateLoadoutButtons` postfix | toggles `loadout-role__<role>` classes for framework roles, mirroring the game's own idiom; the rules those classes match come from `UssPalette` - per-class clones of the game's own stylesheet with the role selectors renamed and only the role-hue color entries rewritten, attached via `styleSheets.Add` |
| `BasePlayerHUD.ChangeRole` prefix, `MatchmakingRoom.AddPlayerRoleIcons` prefix | the crew HUD and the lobby room list draw a role icon only through the USS class `coop-buddy-role-state__<role>`; for a framework role each attaches the `UssPalette` clones to its own panel first, since a sheet only styles the panel it is attached to. In those clones every image named for the gunner is repointed at the class's `roleIcon` |
| `ClassAbility.Start` postfix | installs each registered class's factory-built ability into the local character's Opsive ability list, then repoints any vanilla ability whose perk tree is suppressed onto the class that suppressed it (both idempotent per spawn; `OnDestroy` postfix forgets the instance so a respawn reinstalls) |
| `ClassAbility.VerifyIfIsSelectedAbility` (called, not patched) | the game's own selection check, re-run after a repoint so selection, input binding and the HUD event all run through unmodified game code |
| `UnlockContainer.GetItemsLockedBy(PlayerPerk)` (read) | the same enumeration the fabricator re-reads on every perk change, so an adopted unlock is by construction one the fabricator will see |
| `ClassAbilityDisplay.OnLocalPlayerEnabledClassAbility` postfix | the HUD slot for framework abilities (vanilla resolves by hard type-check on the four stock ability classes), plus status-subscription normalization |
| `ClassAbilityDisplay` `container` / `classAbilityVE` (read), `KeyBindVE._boundInput` + `UpdateKeybind` (written, called) | the HUD ability bar's anchor and parent, and the key drawn on each button's chip |
| `CustomPunCharacter.OnAbilityActive` prefix | diverts a registered runtime ability off the vanilla index RPC and onto the framework's keyed `ModMessage`; prefab abilities fall through untouched |
| `CustomPunCharacter.OnPlayerEnteredRoom` postfix | late-join sync for running registered abilities, sent to the joiner alone and only if VoidManager reports them running the framework |
| `ChannelGrapplingHook.Awake` postfix | registers the framework's grapple stats onto that hook's own stat collection, and a hook-scoped alias onto the cooldown the base class already registered |
| `PlayerControlledShip.RegisterStats` postfix | registers the framework's ship-scoped stats (`IncomingProjectileSpeed`, and `ShipDrag` over the `MovingSpacePlatform` on the same GameObject, since `Platform` is assigned after `RegisterStats` runs) onto the player ship's own collection |
| `SyncedProjectilesSynchronizer.RegisterNpcProjectile` postfix | applies the incoming-projectile-speed factor to each hostile NPC shot as it registers |
| `VoidJumpSystem.ChangeActiveStateByIndex` prefix + postfix | every void-jump state change on every machine: the owner's from `UpdateState`, everyone else's from `OnPhotonSerializeView`. Raises `ShipArrivals` |
| `VoidJumpSpinningDown.enterTimestamp`, `VoidJumpInterdiction.enterTimestamp` (read) | the replicated start of the exit state, plus its length constant, stamps an arrival on the server clock |
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
| `LootTable.ApplyLimiters` postfix, patched by name after the attribute pass | not class-related - appends registered items to each per-roll table copy; see [reference/loot-tables.md](reference/loot-tables.md). A miss costs loot alone |
| `LootTable.Loot`, `LootTableEntry` (read, appended on the copy) | the anchor baseline (smallest `Amount`, union of `PossibleSpawnLocationTypes`, authored entries only), the rarities the table offers, and the added entry |
| `Resources.LoadAll<LootTable>("Loot")`, `Resources.FindObjectsOfTypeAll<LootTable>()` | the loot warm-up's table list: every shipped table sits in `Resources/Loot`, and nothing else does |
| `ClientGame.OnPlayerShipSet` (subscribed), `ClientGame.PlayerShip` (read) | starts the loot warm-up; a game whose ship is already set counts as raised |
| `CarryableDef.Ref`, `ResourcePaths.TryGetPath`, `Resources.LoadAsync<Object>` | the loot warm-up's prefab preload: the same call as the game's own `ResourceAssetRef.PreloadAssetAsync`, on the path the def's synchronous `Resources.Load` uses |
| `OrbitObject.CsTags` (read on the prefab), `CsTagTable` | `AnyCarryableTagged`: the prefab's authored tags, matched by reference against the named stock tags |
| `GameSession.InitStartingMutators` prefix | not class-related; see [reference/mutator-safety.md](reference/mutator-safety.md) |
| `MutatorsElementListVE.UpdateMutatorsList(ReadOnlyCollection<GUIDUnion>)` prefix | not class-related; see [reference/mutator-safety.md](reference/mutator-safety.md) |

## Ability diagnostics (temporary)

Every framework class ability logs its own lifecycle on the owning client, whatever
`VerboseLogging` says: input bound and unbound with a running count, the ability disabled
while still selected, a channel stopped with time left (with a short stack), and a key press
refused for a reason other than cooldown or already running. It exists to catch an
intermittent "ability dead after cancel, until the loadout changes" bug in its next repro,
and comes out once that is found. Each line is a player-caused event, never a per-frame poll.
Refusals log at most once per 2 s per ability, and all of these lines share a budget of 20 per
10 s, with the count of dropped lines carried on the next one.

## Geometry watcher

`[Presentation] GeometryLog` starts a watcher on every terminal build. It polls the tree row's
width every 500 ms rather than waiting for a geometry-changed event, because that event fires
only on a layout change and never arrives for a controller rebuilt into an already-sized
document. A row with a width logs its dump (with `VerboseLogging`) and the watcher stops. After
20 s without a width the watcher classifies the cause. A document that is detached, under a
`display:none` ancestor or in an unsized panel is expected, and logs at Info under
`VerboseLogging` while polling continues. A document that is attached, shown and in a sized
panel logs the warning and stops. The config entry's own description text says "~3s after
construction", which the code does not do: the verdict constant is 20 s.

## Standing rules for framework code

- **A raw game field is a stat registration candidate before it is a private write.** Any raw
  field framework or class-mod code is about to read or write gets a framework-registered stat
  with the field as its sink, so perks and abilities contribute through the game's own
  modifier composition. A private write needs a concrete reason.
- **Adding a framework-owned stat is four edits:** a name constant and its spec in
  `src/FrameworkStats.cs`, a consumer that reads it, a row in `docs/stat-names.csv` so the perk
  builder can offer it, and an entry in `docs/framework-stats.json` saying what the number
  does and what value the game authored. A framework stat with no consumer is inert, logged at startup.
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
- The saved-mutator-list warning names the mod when the mutator GUID is one of the
  repository's own, from a small table in `src/MutatorSafety.cs`.

## Manifest notes

- `ModFlagPolicy` is VoidCrewModding infrastructure: its mods link `shared\*.cs` from that
  repository, and the framework compiles its own copy in `src/shared/`. Class mods there declare
  `MPType => MultiplayerType.All` hardcoded instead of using `ModFlagPolicy.MPType` (which is
  Session or Client for the repository's non-class mods and never All), and return
  `SetMod_Session = ModFlagPolicy.FlagsSession` from `OnSessionChange`. The three
  `AssemblyMetadata` attributes (`ModCategories` under the `NO_SESSION_FLAG` conditional,
  `MPType`, `ProgressionFlag`) are read by that repository's packaging scripts.
- Class mods may copy their boilerplate from `examples/data-only-class/`.

## Posture

`MultiplayerType.All`: an unmodded peer runs `ReApplyPerks` over framework GUIDs and wipes the
modded player's perks on the peer's client; only a process with this DLL carries that guard.
The mutator-safety prefix needs the same posture for the same reason. The room is version- and
hash-locked, updates are all-hands re-sends, and `PLUGIN_VERSION` is bumped on every deploy.
Registered class content sits in the Required mask, so hosting with any class mod installed
flags the session (one-way). The framework alone also flags at load, from its declared
`MPType`. The mechanism is in `vcm/references/modded-session-flag.md`; the public statement is
[multiplayer.md](multiplayer.md).
