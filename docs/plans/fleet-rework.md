# Fleet rework plan

Make EctypeFramework a library that a plain BepInEx mod can depend on, fix what breaks
in rooms with more than one player ship, and give Void Crew Fleet (and any multi-ship or
PvP mod) surfaces worth adopting. Review material behind every item is in the session
scratchpad under `reports/` (five reviews, five option documents).

Phases are ordered by dependency. Each ships as its own release. Each item names the
check that proves it, because most of these failures are silent.

## Phase 0: clear the deck (0.20.1)

- Merge `drop-mod-matching`: SelectionGate and the host per-role toggle are gone; the
  launcher handles matching. Add the `## 0.20.1` heading to `release/CHANGELOG.md`.
- Thunderstore categories for the Framework, Salvager and Skirmisher include
  `All Required`. The launcher reads VoidManager's type first; this covers a lobby where
  that list is absent.
- Tell the test crew to run Fleet 0.4.5 or later. The crash was Fleet 0.4.4 deep-copying
  the Framework's timed-stat value.

## Phase 1: VoidManager becomes optional (0.21.0)

Goal: `[BepInDependency(VoidManager, SoftDependency)]`. A mod depending on the Framework
pulls in nothing else.

1. **Transport.** One Framework-owned Photon event code (pick one outside 0-10, 98-99,
   155-160, 173, 200+), payload `[subtype byte, ...]`. Sender comes from the event's
   actor number. Peer filtering uses a player property `Ectype` carrying the contract
   version, set on join. Ability sync and enemy-status replication move onto it.
   Check: two clients, one without VoidManager, exchange an ability activation and a
   status apply; the log names the subtype and sender for each.
2. **Presence.** `PeerPresence` reads the player property instead of VoidManager's peer
   manager. Check: a peer without the property is excluded from recipients.
3. **Settings menu.** The F5 page, `MenuKit` and the dev-mode flag bind VoidManager by
   reflection and vanish when it is absent. Check: the game runs with VoidManager removed
   and logs one info line "settings menu unavailable".
4. **Launcher need letter.** Without a `VoidPlugin`, the launcher falls back to Thunderstore
   categories. Keep `All Required` on the package and emit
   `AssemblyMetadata("MPType", "All")` so the launcher can read it if its author adds
   support.
5. **Contract marker.** Register one runtime asset whose GUID encodes the transport
   contract version, with a readable name carrying the Framework version. The game's own
   asset check then refuses an incompatible peer by name. Check: a dev build with the
   constant bumped is refused at join and the rejection screen names the host's version.

## Phase 2: multi-ship correctness (0.22.0)

Framework-only. Nothing needs Fleet. Identical behaviour in a one-ship room.

1. **Copy-safe timed stats.** `CurveValue` holds an integer handle into a static table;
   the modifier's `Source` becomes a small token. Check: a dev-build self-test deep-copies
   a stat modifier carrying the value and returns.
2. **Ship registry and topology service.** Every living `PlayerControlledShip` from
   Awake/OnDestroy patches, death marked from the death-began RPC. `Topology.CrewShipOf(player)`,
   `AboardShipOf(player)`, `ShipOfPart(component)`, `ForceOf(ship)`, `ForceOf(player)`,
   `Hostile(a, b)`, `Changed`. One-ship default from game data; a Fleet provider binds
   `FleetApi` by reflection, probing each member by name and signature, and reads the
   `VCF_Ships` room property as a guarded fallback until Fleet publishes `ShipObject`.
   Check: the mission-start log names the ship count and provider.
3. **`characterShip` per holder.** Resolve each holder to their crew ship (Fleet) or the
   ship they are aboard on their own side. A 2 Hz pass re-resolves and rebuilds only
   holders whose ship changed. Check: two ships, holders on each; every client, host
   included, shows each ship carrying only its own crew's contributions; in PvP nothing
   lands on an enemy ship.
4. **Projectile speed per shot.** The guided target's ship, else the hostile ship nearest
   the shot's line, cached by projectile id. Skip shots whose source is a player ship.
   Check: two ships with different factors; each shot carries its target ship's factor on
   both clients.
5. **Presentation and lifting.** Lock gate rebinds when the player-ship reference changes;
   affiliation through the game's own helper; suspension lifting across every registry
   ship; `TargetReach` against the holder's ship. Check: walking to the other ship in co-op
   moves lock gating; a suspension on ship 2 lifts on a client viewing ship 1.
6. **Arrivals.** Document that `Arrived` fires per ship; add `FleetArrived` raised once
   per jump. Check: a three-ship jump raises three and one.
7. **Scale.** Cache the perk-tree lookup; one ability-array rebuild per character; cache
   the build-stat lookup failure; watch platform changes for the inertia cancel.
8. **Crafting-data audit.** After registration settles, warn once for each carryable with
   no crafting data, naming the end-of-run tally it breaks. Fix Skirmisher's payload.
9. **Docs.** A "rooms with more than one ship" section in `multiplayer.md`; dependent
   guidance: never read the player-ship getter for another player; spawn through game
   paths; dynamic rules hold no live objects before init; grafts shift child indices.

## Phase 3: status authority and scale (0.23.0)

1. **Stance.** `EnemyStatusDef.Stance = Any | Hostile | Friendly`, default Any. Receivers
   drop an apply whose sender faction fails the stance against the target, using the
   game's faction check (Fleet keeps factions team-correct). Remove accepted only from the
   applier, the target's owner or the master. DoT skips a tick when applier and target are
   no longer hostile. Check: a simulated same-faction sender on a Hostile status is logged
   and ignored.
2. **Visibility.** `All | OwnForce | TargetForce | HostileToTarget` for markers and icons.
3. **Coalescing.** `CoalesceMs` on the definition; an `ApplyN` delta message per
   (target, key) per window. PPC Soak sets 150. Check: a ten-bolt volley yields one message
   per target per window and matching stacks on both clients.
4. **Markers.** Cap ranked by distance from the view ship.

## Phase 4: surfaces for Fleet (one minor release each, in this order)

Each is built with Fleet's existing code as the reference client, so the hooks are patched
once and he can delete his copy.

1. **Interaction gate.** `InteractionGate.Register(id, Func<Player, Component, Refusal>)`.
   Client-side at the raycast and ability-activation chokepoints; owner-side at every
   network request chokepoint (seat, carrier, door, airlock, power, synced switches,
   ownership events 209/210), sender recovered by wrapping `ExecuteRpc` after game load.
   Gates only refuse. Throttled warning text. Check: a gate that refuses the helm blocks it
   on the acting client and refuses the request on the owner.
2. **Session data and host-synced settings.** `SessionData.Register(key, serializer)`
   riding `GameSession.AsJObject/FromJObject`, frozen at mission start, reaching late
   joiners; a host sidecar for saved runs. `HostValue<T>(ConfigEntry<T>)` published through
   it. Check: a late joiner reads the host's value; a resumed run restores it.
3. **Mod-declared stats.** Stats owned by a mod with no role, declared in a JSON beside the
   DLL; a session-level collection for run-wide contributions with a mutator effect.
   First names: `MeleeDamage`, `MeleeCooldown`, `MeleeReach`, `AntiPersonnelDamage`,
   `HostileSpawnScale`. Fleet reads by id with its constants as fallback.
   Follow-up: a mutator effect that contributes to session stats.
4. **Replicated character statuses.** Attacker requests; the victim's client validates
   stance, range, line of sight and rate, applies, and confirms. Modelled on Fleet's punch.
5. **Content registry.** `RuntimeContent.Publish(container, guid, def, entry)` with tier,
   category, component flag, PvP legality; mandatory crafting data; GUID-ordered append to
   the game's authored list so Customizer and loot tables see it. Move Railgun, PPC and the
   payload onto it.

## Phase 5: manifest targets and conditions

On top of the topology service. One game-visible target value; the scope lives in a
Framework table.

- Targets: `characterFleet`, `seatFleet`, `characterAboard`; distribution `each`, with
  `split` for flat and percent effects.
- `RoleContext.ShipOf(ability)` so role code resolves the owner's ship identically on
  every client.
- Conditions: owner-evaluated first (`onOwnShip`, `onAllyShip`, `onEnemyShip`,
  `inOpenSpace`, `teamMatch`, `ownShipAlive`, `spectating`), then broadcast-edge
  conditions on other ships' state.

Shipped in 0.29.0 (network contract 5): the three targets through the scope table, `each`
and `split`, `RoleContext`, the seven owner-evaluated conditions on perks (`when`) and on
role abilities (`abilityWhen`), carried as a per-holder mask.

Follow-up, broadcast-edge conditions (validation refuses them by name until then): conditions
on other ships' state, such as `allyShipHullBelow(x)`, `ownShipHullBelow(x)`,
`enemyShipWithin(m)`, `allyShipLostRecently(s)`, `forceShips >= n`, `outnumbered`. Hull and
distance cross a threshold at slightly different moments on each client, so one client must
decide each edge and broadcast it: the holder's owner (or the host where the holder is not
trusted) evaluates with hysteresis (enter below 30%, leave above 33%), sends the edge, and every
client flips the contribution on receipt; a late joiner needs the current state replayed. The
manifest needs an object form (`{"allyShipHullBelow": 0.3}`), a second mask or an edge message
per (holder, condition), and a hull replication rate measured first to set the margin. Trigger:
a role that needs one. Until then a timed status applied when the event happens covers most
designs.

## Collaboration track (parallel, starts after Phase 1 is on a branch)

1. Send the proposal (in `reports/ideas-collab.md` §5), pointing at the branch.
2. API asks, all additive: `ShipObject`, `ShipNumberOf`, `IsShipAlive`, `IsSpectator`,
   `CrewsChanged`, `ShipLost`, `FleetActive`.
3. Bug reports with patches: ship spacing into the fleet plan, hack-terminal registration
   retry, UI sound leak, spectator null path.
4. Suggest the Framework repo as a git submodule in his repos so his agents read `docs/`.
5. Offer the surfaces of Phase 4 in the order he names a use.
6. Adopt his offline Mono.Cecil checker pattern for our own reflection and Harmony targets,
   and a cross-mod API snapshot test.

Rules: bind by reflection in both directions until he takes a dependency; reimplement from
mechanism, never copy his GPL code into the MIT Framework; one integer contract constant
per reflection-facing surface.

## Deferred, each with its trigger

- Shared contract assembly: two independent consumers want compile-time types for one surface.
- VoidManager-free core split: a VoidManager-free author commits to two or more core services.
- Shared UI kit: he agrees to adopt it file by file; start with a stateless source file.
- Shared transport beyond the Framework's own code: a second mod needs peer filtering.
