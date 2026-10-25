# Changelog

Newest first. Each entry lists what changed for a mod that depends on the framework.
A version that is not listed carried no change a dependent can observe, or has no recorded
build.

## 0.28.0

- `CharacterStatuses.Request(player, key, seconds, magnitude, attacker)` puts a status on another
  player's character. On this client's own character it is `Apply`. Otherwise it sends a request
  to the target's client alone, which judges it against its own view of both characters,
  applies it there as a local `Apply` would or refuses it with a throttled warning, and answers.
  The checks, in order: template registered and `AcceptsRequests`; duration at least
  `MinSeconds` (default 0.25 s) and magnitude above 0, cut to `MaxSeconds` and `MaxMagnitude`
  (default 30 s and 1); target alive; requester has a character and it is alive; optionally not
  seated (`RefuseSeated`) and neither in a clone tube (`RefuseInTube`); `Stance`; `MaxRange`
  (metres, 0 = none); `RequiresLineOfSight`; rate per requester and key (`MinInterval`, `Burst`,
  with one extra token on the target for network jitter). Each requester is also capped across
  keys at a burst of 21, refilled at 20 a second; past it requests are dropped unanswered, and the
  requesting client keeps the same cap itself. The requesting client runs the same checks first, with a range
  15% or 1 m looser, and sends nothing they refuse.
- `Request` returns a `CharacterStatusRequest`: `Sent`, `Done`, `Refusal`, `Applied` and
  `OnCompleted(callback)`. It completes at once when this client applied or refused the request,
  else on the target's answer, at once with `SendFailed` when Photon refuses the send, or with
  `NoAnswer`, which means the outcome is unknown, after 10 s or when 40 newer requests to the same
  player are waiting.
- A requested application never shortens or weakens a running status: its end moves only later
  and a larger magnitude re-applies every stack at that value.
- `CharacterStatusDef.AcceptsRequests` (default off) opts a template into requests.
  `Replicated` keeps its meaning: it changes nothing, and `Apply` with it set logs one warning
  per key, which now names `AcceptsRequests` and `Presented`.
- `CharacterStatusDef.Presented`: the owning client announces each application to the peers
  `Visibility` allows, and each runs a presentation copy with `CreateVfx` and
  `CharacterStatuses.IsPresented` / `PresentedRemainingSeconds`, but no stats and no callbacks.
  A copy ends on the announced server time; an earlier end is announced.
- Every character status now ends when its character dies, with `OnRemoved`. The game keeps the
  character object through death and respawn, so a status used to run on through the respawn
  until it expired. Nothing is applied to a dead character: `Apply` logs one warning per key, and a
  request is refused `TargetDead`.
- `OnRemoved` runs after the status is unregistered and its modifiers are removed, so applying the
  same key from it starts a fresh status instead of leaking modifiers.
- Only the client that owns a character can remove a status on it. `CharacterStatuses.TryRemove`
  returns `Authority` for another player's character, and `Remove` logs it once per key.
- `CharacterStatuses.RequesterOf` and `AttackerOf` report the request that last applied a status.
- `Register` refuses a character status template with `Burst` below 1, a negative or non-finite
  range or interval, `MinSeconds` outside 0 to `MaxSeconds`, or a cap not above 0, and warns
  about request or presentation fields set without `AcceptsRequests` or `Presented`.
- `CharacterStatuses.Apply` on a character this client does not own still lands only on this
  client's copy; its once-per-key warning now points at `Request`.
- Network contract 4: three new message subtypes carry character status requests, the target's
  answers and presentation announcements. A framework on contract 3 or earlier is refused at
  join.

## 0.23.0

- `EnemyStatusDef.Stance`: `Any` (default), `Hostile` or `Friendly`. Every client that receives
  an application judges its sender's side against the target and drops one the stance
  forbids; the applying client checks first. A sender's side is its character's faction, else
  its crew ship's, else under Void Crew Fleet its team's; a sender with none is refused by any
  stance other than `Any`. When the master applies with an NPC as the attacker, the NPC's side
  is judged instead, so an enemy's debuff on a player ship can be `Hostile`; a receiver that
  can no longer find that NPC accepts the master's application. Refusals log as
  warnings, at most once per sender, operation and status per 30 s. Templates that keep `Any`
  apply and remove as before.
- A removal is accepted from the master and from any sender the stance would let apply the
  status: anyone for `Any`, a sender on another side from the target for `Hostile`, one on its
  side for `Friendly`. The rule depends only on the sender and the target, so clients that
  agree on the sides reach the same verdict. Each client remembers a removal for `CoalesceMs`
  plus 2 s and ignores applications started before it, so one already in flight does not
  re-create the status.
- `EnemyStatuses.TryApply` and `TryRemove` return a `StatusRefusal` saying why nothing
  happened. `Apply` and `Remove` keep their signatures and log a stance refusal once per key.
- Re-applying a status that refreshes moves its end only later, never earlier, so
  applications from several clients leave the same end in any arrival order.
- A damage-over-time status registered with `Stance = Any` logs one warning recommending
  `Hostile`. A `Hostile` one skips each tick while the client that created it (or its NPC
  attacker) is no longer on another side from the target.
- A client that creates a status from a message that arrived more than `CoalesceMs` plus
  500 ms after the status's start pays ticks, and starts ability suspension, from that grace
  before arrival rather than from the start.
- An `OnRemoved` callback that applies the same key again starts a new status; it used to
  stack onto the ending one and leave stat modifiers behind.
- `EnemyStatusDef.Visibility`: `All` (default), `OwnForce`, `TargetForce` or `HostileToTarget`
  decides which viewers draw the marker and the visual. `OwnForce` draws for every side that
  applied the status. The status itself runs everywhere.
- `EnemyStatusDef.CoalesceMs` (default 0): this client's applications of the status to one
  target travel as one message per window, carrying how many there were and the local
  status's timer, so stacks from several clients add up. The applying client still applies
  each at once. A batch never spans two lives of the status, and a removal, sent or received,
  drops the queued batch. A status applied per projectile hit should set about 150; a
  duration within `CoalesceMs` plus 500 ms logs a warning, since it can end before its batch
  arrives.
- Past `[EnemyStatuses] MarkerCap` or `VfxCap`, a new status takes the slot of the one
  farthest from the ship this client views when it is nearer by 10% of that distance or 25 m,
  whichever is larger, and freed slots go to the nearest statuses without one.
- `CharacterStatuses.Apply` on a character this client does not own logs one warning per key.
- Network contract 2: a new message subtype carries coalesced status applications. A
  framework on contract 1 is refused at join.

## 0.22.0

- `Topology`: which player ships exist and which is whose, answered the same on every client.
  `Ships`, `Multi`, `CrewShipOf`, `AboardShipOf`, `ShipOfPart`, `ShipNumberOf`,
  `ShipByNumber`, `ForceOf`, `Hostile`, `Changed`, `Stamp`, `Provider`, `FleetPluginGuid`.
  With Void Crew Fleet installed and enabled it binds Fleet's public API (version 1) by
  reflection for crew membership and teams; without it every answer comes from game data. In a
  room with one ship every answer is that ship, unless ships of two sides have been seen this
  mission and the player is on the other side.
- `characterShip` perks land on each holder's own crew ship in a room with several ships,
  instead of on whichever ship each client was viewing. A holder with no crew ship yet
  (Fleet places nobody, the player spectates, or their ship was lost) contributes nothing until
  one exists, and a holder whose ship changes is moved within half a second. When one side's
  ship is the last one left, the other side's players contribute nothing to it. One-ship co-op
  rooms are unchanged.
- `IncomingProjectileSpeed` is per ship. Each hostile shot takes the factor of one ship, chosen
  as it registers: the guided target's ship, else the hostile ship nearest the shot's line.
  Shots fired by a player ship are never slowed. One-ship rooms are unchanged.
- `ShipArrivals.FleetArrived`: raised once per jump on every machine, after every ship that
  jumped is back in normal flight. Its `ShipArrival` carries the last ship to arrive, the worst
  kind and the latest stamp. `Arrived` is documented as once per ship per jump, which it
  already was.
- Status markers take their colour from the game's own affiliation rule, the one its object
  markers use: a marker on a neutral object (faction 0) now draws as neutral, where it drew as
  enemy. Lock-gated markers follow the ship this client views when the view moves to another
  ship.
- Enemy ability suspension lifts the enemy's modifiers from every player ship.
- `CancelTriggers.InertiaZoneEntry` also fires when the character moves from one ship's inertia
  zone straight into another's.
- Timed stat contributions are safe to deep-copy: the applied modifier's dynamic value and
  source hold an integer handle, not the contribution. The modifier's `Source` is no longer the
  `TimedStatContribution` itself: `collection.RemoveModifier(contribution)` no longer removes
  it, and a modifier's source no longer compares equal to the contribution. End one with
  `TimedStatContributions.End`. A copy of the modifier that a stat collection applies follows
  the same curve and reads neutral once the contribution ends.
- A carryable registered at runtime with no crafting data logs one warning naming it, because
  the game's end-of-run alloy tally throws on it.
- Fewer stalls at spawn: the ability install sweep rebuilds each character's ability array
  once (a role whose ability throws there is skipped alone), and the role lookup the host runs
  for every member caches its reflection. A build process whose requester has no live
  character no longer logs an error every frame, and picks the requester up once the character
  exists.
- A role ability factory that returns something other than an `EctypeRoleAbility` is refused
  with one error naming the role, and the role stays data-only for the session.

## 0.21.0

- VoidManager is optional. The framework declares it as a soft BepInEx dependency, and its
  Thunderstore package no longer depends on it, so a mod that depends on the framework pulls
  in BepInEx and nothing else. Without VoidManager the framework runs in full and logs
  `settings menu unavailable: VoidManager not loaded`; only the F5 page is missing. With
  VoidManager, the F5 page is unchanged.
- The framework no longer declares a VoidManager multiplayer type. VoidManager lists it under
  its non-VoidManager mods as `Unmanaged`, with no author or description, never compares it at
  join and never raises its session level for it. VoidManager therefore no longer checks the
  framework's version or DLL bytes; the Void Crew Mod Launcher installs the host's version, and
  the game's asset check refuses a different network contract. An `All` framework made every
  VoidManager host kick every joiner without VoidManager.
- Rooms can mix players with and without VoidManager. A joiner with VoidManager and the
  framework passes VoidManager's join check for a host without VoidManager, where VoidManager's
  own check failed. A VoidManager host that runs any mod declared `All` to VoidManager, role mods
  included, still kicks joiners without VoidManager: that is VoidManager's policy.
- The framework no longer blocks progression in any configuration; earlier builds never did so
  with VoidManager either. It still tags the rooms it hosts as modded, through its network
  contract marker and the role assets in the game's required set, which drives the game's
  join check and the modded tag in the server browser and the mod launcher's lobby list. With
  VoidManager installed, VoidManager's own progression switch applies: it reacts only to a mod
  calling its `DisableProgression`, never to the room's modded tag. The DLL's `ModCategories`
  metadata no longer lists `Disables Progression`.
- Removed `EctypeVoidPlugin`, `EctypeSettingsMenu`, `AbilityActiveMessage` and
  `EnemyStatusMessage` from the public types. None was supported API.
- Ability replication and enemy-status replication no longer use VoidManager's `ModMessage`.
  They ride the framework's own Photon event, code 181, with the same message contents.
  Recipients are the players whose Photon player property `Ectype` carries the framework's
  network contract, which every framework sets on its local player before joining a room,
  instead of the players VoidManager reports as running the framework. A dependent that
  raises its own Photon events should not use 181. A peer that publishes another contract
  number is logged once and its messages are dropped.
- Network contract 1. The framework registers one runtime asset in the game's Required mask
  whose GUID ends in the contract number and whose name is
  `Ectype Framework <version> (network contract <n>)`. The game's own asset check refuses a
  joiner on another contract and names the host's framework, with or without VoidManager.
  The asset also tags every room the process hosts as modded, with or without VoidManager. A
  framework disabled through its config registers no marker, so the game refuses a join
  between it and an enabled framework.

## 0.20.1

- Removed `SelectionGate`, `GateResult`, `GateVerdict`, `MemberModResult` and
  `MemberModState`. A dependent that calls any of them fails at the first call with a
  `TypeLoadException` raised in its own code. Mod matching for a room now happens before
  launch, in the Void Crew Mod Launcher, and VoidManager's join check still refuses a joiner
  whose versions or DLL bytes differ from the host's. The framework no longer re-sends a
  client's mod list to a new host after a host migration.
- Removed the host's per-role suppress toggle: the `[RoleSuppression]` config section, the
  checkboxes and `Host suppress:` line on the F5 page, and the room property that carried the
  choice. Suppression comes only from role manifests (`suppress` and `suppressRoles`) and no
  longer changes during a session. `Suppress_<roleId>` keys in an existing `.cfg` are orphaned
  harmlessly.
- `RoleRegistry.IsAvailableInRoom(roleId)` keeps its signature. It is true while the role is
  registered and no other active role lists it in `suppressRoles`.

## 0.20.0

- First Thunderstore release. Carries the changes of 0.19.16 to 0.19.20 below; nothing else.

## 0.19.20

- `HowToHints.Build` takes a null icon: the toast draws text only, with the icon slot
  collapsed instead of left as an empty square.

## 0.19.19

- `HowToHints`: how-to toasts through the game's own hint system, moved into the framework from
  a helper each mod compiled for itself. `Build`, `Show` and `Owns` keep that helper's
  behaviour, including keeping the hint out of the player's saved seen-hint list. `Show` now
  refuses a hint at its per-session cap (the game's own check let one more through) and returns
  false for a hint already showing or stacked, which the game counted as seen without showing.
- `HowToHints.ShowForced` shows a hint with the player's "Show hints" setting off. Only the
  setting is bypassed, and only for that one call; stock hints keep obeying it. With the
  setting off the game runs no hint countdown, so the framework runs its own until the hint
  closes, and stops it as soon as the game's own countdown is running.

## 0.19.18

- `EctypeRoleAbility` takes a cancel policy. Assign `Cancel = new CancelPolicy { On, Cooldown,
  CollisionSpeed }` in the subclass constructor and the channel ends early on any trigger in
  `On`: the ability key pressed again, a collision at `CollisionSpeed` or more into the
  surface, a grapple attaching, entering a ship's inertia zone, jetpack flight ending, or the
  ability's own `RequestCancel(reason)`. Triggers are watched on the owning client for the
  channel's duration, and the stop runs on the ability's next tick. `CancelCooldown.Partial`
  scales the cooldown of any early end by the share of the channel used. `LastEnd` says how
  the channel ended, and is set before `OnRemoveEffect` runs. An ability with no policy
  behaves as before.
- `EctypeRoleAbility.OwnerCanUse()`: override it to decide when the ability is usable. The
  framework evaluates it on the owning client while the ability is selected and idle and
  writes the game's `AbilityUsable`; a replica is held usable, so a crewmate's copy never
  refuses the owner's replicated start. Role code does not write `AbilityUsable`.
- `EctypeRoleAbility` overrides `AbilityStarted`, `AbilityStopped`, `ActivationKeyPressed`,
  `Update`, `InactiveUpdate` and `OnDestroy`. A subclass override of any of them calls its
  base. A role mod built against an earlier framework keeps its behaviour; rebuild it to use
  the new API.
- With `[Debug] VerboseLogging` on, the owning client logs one line per role-ability channel
  end, naming how it ended and the cooldown it left.

## 0.19.17

- A replicated start for a registered ability is held while the receiving client's copy of
  the character has no look source yet, and delivered once it has one. A late joiner could
  otherwise receive a crewmate's running ability before that character finished spawning
  and start it there, where a stock ability that reads the look source on start (the
  grappling hook) threw. A held start is retried every 0.1 s for up to 5 s, cancelled by a
  later stop for the same character and key, and dropped with one warning at timeout.

## 0.19.16

- A role icon's sprite rect is fitted to its glyph: a square centred on the PNG's alpha
  bounds, sized so the glyph spans 70% of it, the top of the stock role icons' 61-71%. The
  crew HUD, the lobby room list and the gene tree's `ContextInfo` icon all take the fitted
  sprite, so a glyph authored small inside its canvas no longer draws smaller than the stock
  role icons. A glyph already at 70% or more keeps the full canvas; the log names each icon
  it fits.

## 0.19.15

- `SystemModuleConstructionSpeed` displays as Construction Speed (was Installation Speed).

## 0.19.14

- Dev tooling is compiled only into a dev tools build, `dotnet build -c Release
  -p:DevTools=true`, which defines `ECTYPE_DEV`, carries
  `AssemblyMetadata("EctypeDevTools", "true")` and logs
  `Ectype Framework <version> (dev tools build)` at startup. A release build has no `[Dev]`
  config section, no F5 developer section and no ability lifecycle logging, and does not read
  `BepInEx/EctypeLayout/layout.json`: the gene-tree layout always uses its built-in values.
  `[Dev]` keys in an existing `.cfg` are orphaned harmlessly. Never ship a dev build: its bytes
  differ from the release build at the same version, so the host's hash check bounces anyone
  on the other build.
- `HudAbilityBar.DumpTree()` is no longer public; it exists only in a dev tools build.
- The missing-mutator warning reports the mutator by GUID only.
- Removed `docs/hud-badge.json` and `assets/hud-button-glyph.png`, a HUD button preview spec
  and its glyph. Neither was embedded in the DLL.
- The plugin description, shown in VoidManager's mod list, is one sentence under 250
  characters.
- The build looks up the game folder in Steam's default library through the registry
  (`HKCU\Software\Valve\Steam`, `SteamPath`) when neither `VoidCrewDir` nor
  `VOID_CREW_DIR` is set, in the framework's and the examples' `Directory.Build.props`. The
  examples' props now stop with one error naming the folder when the game, BepInEx or
  VoidManager is missing, and accept VoidManager in a `VoidManager` subfolder of `plugins`.

## 0.19.13

- Removed `[Presentation] GeometryLog`. The geometry watcher now runs only with
  `[Debug] VerboseLogging` on, and emits one line: the 20 s "layout failed" warning, with the
  geometry dump appended to it. The healthy-row dump and the "not laid out yet" Info line are
  gone. An existing `.cfg` keeps the orphaned key harmlessly.
- Removed `[Dev] DemoHudButton` and the demo button on the HUD ability bar (it carried an F9
  chip). An existing `.cfg` keeps the orphaned key harmlessly.
- Removed the Shift+F10 layout reload key. The F5 page's layout workbench has the same
  "Reload layout file, re-style and capture the terminal" button.
- New `[Dev] WriteVanillaDump`, default off. `BepInEx\EctypeDump\vanilla-roles.json` is no longer
  written every launch: it is written once per launch only with the switch on. Tooling that
  reads the stock trees needs the file, so turn the switch on and launch once before using it.
- A second role whose perk lists an unlock item that another role's perk already claimed this
  session now logs a named warning (it was skipped silently). Behaviour is otherwise unchanged:
  the first claim holds.

## 0.19.12

- New `[Presentation] RoleWording`, default on: where the game's English text calls a playable
  role a "Class", it says "Role". Display text only; no game identifier, asset, save or network
  message changes, and other languages are untouched. Off restores the game's wording. The
  strings changed:
  - Settings -> Controls keybind name: "Class Ability" -> "Role Ability".
  - Item and relic stat lines: "Class Ability Cooldown" -> "Role Ability Cooldown", "Class
    Ability Duration" -> "Role Ability Duration".
  - The eight stock hero-perk descriptions (Engineer, Gunner, Pilot, Scavenger; base and
    Superior): "Gain the <ability> Class Ability." -> "Gain the <ability> Role Ability."
  - The hub's player-projection headers: "Class: Engineer", "Class: Gunner", "Class: Pilot",
    "Class: Scavenger" -> "Role: ...".
- With the switch on, the vanilla tree dump (`vanilla-roles.json`) records the hero-perk
  descriptions in the new wording, since it reads the displayed text.
- The examples' hero-perk descriptions say "Role Ability".

## 0.19.11

- Breaking: "class" is renamed "role" throughout, matching the game's own code (EPlayerRole). Manifest
  contract version 2. A contract 1 manifest does not load: under its old resource name it is
  not discovered, and renamed alone it is rejected for its `contractVersion`. Rename its
  fields, its resource and its `contractVersion`, then rebuild.
- Public API renames. Behaviour is unchanged.
  - `ClassRegistry` is `RoleRegistry`. `RegisterAbilityFactory(roleId, factory)`,
    `RegisterUnlockPredicate(roleId, predicate)`, `PerkGuid(roleId, perkKey)` and
    `IsAvailableInRoom(roleId)` keep their signatures; the first parameter is `roleId`.
  - `EctypeClassAbility` is `EctypeRoleAbility`.
  - `ClassStats` is `RoleStats`: `Id(roleId, statName)`,
    `RegisterAbilityTiming(ability, roleId, durationStat, cooldownStat)`.
  - `ClassManifest` is `RoleManifest`.
  - `LayoutSpec` (the layout workbench file): `showClassName`, `classNameFontSize`,
    `classNameColorHex`, `classNameTopPx` and `classNameHeightPx` are `showRoleName`,
    `roleNameFontSize`, `roleNameColorHex`, `roleNameTopPx` and `roleNameHeightPx`.
- Manifest fields: `classId` is `roleId`, `className` is `roleName`, `classDesc` is
  `roleDesc`, `suppressClasses` is `suppressRoles`. GUIDs, tree ordinals and stat ids derive
  from the id's value, not the field's name, so a role whose id string is unchanged keeps its
  saved perk selections.
- The manifest resource suffix is `ectype-role.json` (was `ectype-class.json`); a resource
  with the old suffix is not discovered. The schema is `docs/schemas/ectype-role.schema.json`
  and rejects the four contract 1 field names.
- Config: the `[ClassSuppression]` section is `[RoleSuppression]`, with keys
  `Suppress_<roleId>`. Values under the old section are not migrated: a host that had a role
  switched off sets the toggle again.
- The vanilla tree dump is written to `BepInEx\EctypeDump\vanilla-roles.json` (was
  `vanilla-classes.json`).
- Log lines say role where they said class, for example `Ectype: role '<name>' registered.`
  and `Ectype: discovered <n> role(s)`. Log filters keyed on the old wording need updating.
- The starter examples are `examples/data-only-role` and `examples/ability-role`.

## 0.19.10

- The framework stat `ModuleInstallSpeed` is renamed `SystemModuleConstructionSpeed`. Its id
  derives from its name, so it changes from 592760197 to 761402164; a perk or manifest that
  names the old name no longer resolves and must be rewritten. Behaviour is unchanged: the
  stat still scales install, uninstall and upgrade, because the game runs all three on one
  progress clock.

## 0.19.9

- No API or behaviour change. The framework now builds from its own repository against the
  game install, which changes the DLL's bytes. Every player in a room needs this build, and a
  dependent rebuilt against it records the new assembly version.

## 0.19.8

- `wide` is a validated layout field. A perk on a full-width cell (rows 2, 5 and 7 of the
  grid) must declare `"wide": true`, and `"wide": true` on any other row rejects the class.
  Absent means false. The hero is always row 0 and needs no flag. A manifest that omitted
  `wide` on a banner row, or set it on a normal cell, is now refused with a named error.

## 0.19.7

- New APIs: `SelectionGate` (host-side check that every room member runs a mod at the
  host's build, with a verdict and per-member states), `EnemyAbilities`, `LootTables`
  (add runtime-registered items to the game's loot rolls), `ShipArrivals` (an event on
  every machine when the ship leaves the void), `TimedStatContributions` (a modifier that
  follows a curve over a duration) and `ShipSpeedSurge`.
- New framework stats: `ShipDrag` (the ship platform's friction) and `ModuleInstallSpeed`
  (how fast a player installs, uninstalls and upgrades modules).
- A field-backed stat now tracks the stat after later modifier changes.

## 0.18.8

- `roleIcon` in the manifest is the glyph the crew HUD and the lobby room list draw
  beside a player of the class. A class with no `roleIcon` shows the framework
  placeholder there.

## 0.18.7

- New `ClassStats` API: `Id(classId, statName)` resolves a manifest-declared custom stat,
  and `RegisterAbilityTiming` registers one class ability's own duration and cooldown so a
  perk can be scoped to that ability.
- A perk level the class no longer defines is clamped on load, and the points spent on it
  are refunded.
- A suppressed tree's fabricator unlock can be adopted by any class that suppresses the
  tree, not only the first.

## 0.18.5

- The fallback for a perk or class icon with no embedded PNG and no `iconDonor` is the
  framework's embedded placeholder (square, or wide for the row-2 banner). It was a
  vanilla icon.

## 0.18.4

- Hand attractor duration, cooldown and beam-count stats.

## 0.18.3

- The galaxy-map mutator list skips mutator GUIDs that no loaded mod registers, so a
  client without a host's mutator mod no longer fails on that list.

## 0.18.2

- Hand attractor stats. The `VerboseLogging` config key turns on per-character ability and
  HUD bar logging.

## 0.18.1

- New perk `target` value `characterShip`: the effects go to the ship's stat collection
  for every holder of the perk, seated or not, and reach ship module trees the game
  detaches from the ship, such as the Fabricator's.
- New manifest field `minFrameworkVersion`. A framework older than the value rejects the
  class by name.
- A suppressed tree is reported absent to the profile (spent points, ability selection,
  fabricator unlocks), and already-built gene-tree screens rebuild when the suppressed set
  changes.

## 0.17.0

- New `CharacterStatuses` API: timed, named stat bundles on a player, replicated to every
  machine.
- New `HudAbilityBar` API: extra buttons on the class ability bar.
- New manifest field `suppressClasses`: a class suppresses another framework class by
  `classId`.
- New ship stat `IncomingProjectileSpeed`.
- Builds reporting 0.16.2 between 2026-09-12 and 2026-09-14 differ in which of these they
  contain. Require 0.17.0 or newer for any of them.

## 0.16.2

- `ClassRegistry.PerkGuid(classId, perkKey)` is public: class code resolves its own perk
  GUIDs without pinning literals.
- A class ability's HUD slot paints correctly on first attach and hides the previous
  class's ability element.

## 0.13.0

- New `EnemyStatuses` API: timed buffs and debuffs on NPC ships, turrets and other space
  objects, with an optional attached visual.

## 0.10.0

- The DLL is `EctypeFramework.dll`. It was `EctypeClassFramework.dll`. The plugin GUID
  `jack.voidcrew.ectype` and the manifest contract are unchanged. A dependent that
  references the assembly must be rebuilt against the new name, and the old DLL must be
  removed from `BepInEx\plugins\`: two copies register the same GUID.
- A host-only mutator no longer throws for a client that has the framework but not that
  mutator: the framework drops an active mutator GUID the client has no definition for.

## 0.9.1

- New `CharacterStats` and `FieldStat`: a raw game field wrapped as a framework stat, so
  perks and abilities contribute through the game's modifier composition. First stat
  added: `ExternalForceDamping`.

## 0.8.0

- New `RuntimeAbilities` API: replicates the active state of a runtime-registered ability
  to the other clients.

## 0.7.1

- New framework-owned stat registry (`FrameworkStats`), including the grapple hook stats.
- Host-side per-class suppression toggles and room-property sync, so every client applies
  the same suppressed set.

## 0.6.16

- A class that suppresses a vanilla tree takes over the tree's class ability and
  fabricator unlocks: the ability remains selectable and the unlocks stay reachable.
- New perk field `iconDonor`, and class field `roleIconDonor`: borrow the icon of any
  perk by GUID.

## 0.6.2

- New effect fields `requiredTags`, `requiredTagCriteria`, `requiredLocalTags`,
  `requiredLocalTagCriteria`, `disablingTags`, `tagsToAdd`: tag filters on a stat effect.
- New effect field `kind`: `"int"` builds an integer modifier.
- New perk field `unlocks`: Fabricator items the perk grants once bought.
- New perk field `emptyOk`: declares an empty level intentional.

## 0.6.1

- New `ClassRegistry.RegisterAbilityFactory`: a class mod supplies its hero perk's class
  ability in code.

## 0.5.10

- First recorded build. Manifest contract version 1: class manifest discovery by embedded
  resource, tree registration, vanilla tree suppression, tree presentation, and the
  stat registry.
