# Topology: which ship is whose

`Topology` answers "which player ships exist", "which ship does this player crew" and "are
these two on the same side" the same way on every client, in a room with one player ship or
several.

```csharp
IReadOnlyList<PlayerControlledShip> ships = Topology.Ships;   // living, by ship number
PlayerControlledShip crew   = Topology.CrewShipOf(photonPlayer);   // or a character
PlayerControlledShip aboard = Topology.AboardShipOf(character);
PlayerControlledShip owner  = Topology.ShipOfPart(component);
int number = Topology.ShipNumberOf(ship);                     // 1-6, 0 unknown
bool enemies = Topology.Hostile(a, b);
Topology.Changed += () => { /* re-resolve */ };
```

## Why it exists

The game has one player ship and one getter for it, `ClientGame.PlayerShip`. A mod that puts
several player ships in a room can make that getter return the ship the viewing client is
looking at: the ship a spectator watches, the ship a co-op player is standing in, the player's
own team's ship in a team match. Two clients then get two different answers, and one client's
answer changes as its player walks between ships.

Code that changes simulation state runs on every client and must reach the same ship on every
client. It cannot read that getter for another player. `Topology` answers from data every
client shares instead: the ship objects themselves, each character's platform and faction, and
the multi-ship mod's own crew list when one is installed.

## Members

| Member | Answer |
| --- | --- |
| `Ships` | Every living player ship (not dying), ordered by ship number. Empty outside a mission. |
| `Multi` | True while more than one player ship object exists, dying ones included. Only then can the player-ship getter differ between clients. |
| `CrewShipOf(Photon.Realtime.Player)`, `CrewShipOf(Player character)` | The ship the player crews, or null while none can be named. Rules below. |
| `AboardShipOf(...)` | The ship whose rooms or inertia bubble hold the character: its zoned platform, else its platform, then the ship above it. Null in open space or without a character. |
| `ShipOfPart(Component)` | The player ship a part belongs to: the ship above it in the hierarchy; for the hull, which the game unparents from its ship at spawn, the hull's own reference back; for a carryable, the platform it sits on or is carried on. |
| `ShipNumberOf(ship)`, `ShipByNumber(n)` | Ship numbers 1 to 6. Rules below. |
| `ForceOf(ship)` | The ship's game faction. Two ships with the same value are on one side. |
| `ForceOf(Photon.Realtime.Player)`, `ForceOf(Player character)` | The character's faction. A player without a character (a spectator) takes their crew ship's faction, or under a team match the faction of a living ship of their team; -1 when none is known. |
| `Hostile(a, b)` | True when a and b are on different sides. Each may be a ship, a character, a Photon player, any space object or a ship's part. Two space objects are compared with the game's own different-faction check. False when either side is unknown. |
| `Changed`, `Stamp` | `Changed` is raised on the main thread the frame after a ship spawns, starts dying or is destroyed, and when the multi-ship mod reports a change of ships or teams. `Stamp` advances with it; store it and compare to re-resolve only on change. Crew moves inside one side raise nothing, so poll `CrewShipOf` where they matter. |
| `Provider` | `"fleet"` while Void Crew Fleet's API is bound and the Fleet plugin is enabled, `"single"` otherwise. |
| `FleetPluginGuid` | `"com.captainchunck.voidcrew.fleet"`, the plugin GUID the binding looks for. |

### The crew ship

The first rule that applies decides; a rule that applies and refuses gives none:

1. One ship object in the room: that ship, if it is on the player's side, else none.
2. One living ship: that ship, if it is on the player's side, else none.
3. Fleet active: the living ship Fleet names for the player. Fleet naming none (a player
   waiting to be placed, dead with a lost ship, or spectating), or naming a ship that is dying,
   gives none, whatever ship the character stands on.
4. Fleet not active: the living ship the character is aboard, when its faction equals the
   character's. A boarder on an enemy ship never resolves to that ship.
5. Otherwise none.

"On the player's side": under a Fleet team match, the ship's team is the player's team.
Otherwise, once ships of more than one faction have been seen this mission, the ship's faction
is the character's. In a room where every ship has had one faction, every player is on every
ship's side, so a one-ship or co-op room never refuses. The last ship of a team match therefore
never resolves for the other team's players.

None means pending: ask again on `Changed` or on a timer.

A player's crew ship can change without any game event, when the multi-ship mod moves a player
between ships. Clients learn the move when the mod's room data reaches them, so for a moment
after a move two clients can disagree.

### Ship numbers

With Void Crew Fleet, a ship's number is its index in the ship list the host publishes when the
ships spawn, plus one. A lost ship keeps its index, so the others keep their numbers; an entry
whose id now names no player ship (a lost ship, or an id the room has since handed to another
object) is read as a lost ship. A list with the same id twice is not used. Without that list,
a ship's number is its rank by root PhotonView id among every ship seen this
mission. The room allocates those ids in sequence, so the rank is the same on every client
that saw every ship spawn. A lost ship keeps its place in the ranking.

### Binding to Void Crew Fleet

When the plugin `com.captainchunck.voidcrew.fleet` is loaded, the framework finds the static
class `VoidCrewFleet.FleetApi` in its assembly by reflection and reads `ApiVersion`. Fleet
raises that number when a member's meaning changes, so any value other than 1 leaves Fleet
unbound and every answer comes from game data. Otherwise it probes each member it uses by name
and signature: `int ShipOfPlayer(int)`, `int TeamOf(int)`, `int TeamOfShip(int)`,
`event Action ShipsChanged`, `event Action TeamsChanged`. A missing member is named once in the
log and its answer falls back to game data. Fleet's answers are used only while its plugin
component is enabled and its own `[General] Enabled` setting is on. Nothing references Fleet at
compile time.

## Rules for code in a room with several ships

Each rule names the mechanism that makes it hold and the check that catches a violation.

1. **Never read the player-ship getter for another player.** On every client the getter can
   return the viewing client's ship, so an effect placed through it lands on a different ship
   on each client, and the copy on the ship's simulating owner decides the outcome. Resolve the
   player's ship with `CrewShipOf` (or `AboardShipOf` for "where they stand"). The getter is
   right only for what the local screen shows. Check: two clients log the same ship number for
   the same player.
2. **Spawn networked objects through the game's own paths, from a registered asset GUID.** The
   game's spawn calls (the fabricator, the object factory's spawn by GUID, the ship loadout's
   initialiser) allocate the PhotonView id, cache the spawn for late joiners and register the
   object. A mod that allocates view ids itself has to repeat all three, and an object whose
   spawn was not cached does not exist for a player who joins after it. Check: a late joiner
   sees the object.
3. **Hold no live object references in a dynamic stat rule before it initialises.** The game
   deep-copies perk stat modifiers on every perk reapply, and the copier recurses through every
   field and clones MonoBehaviours, so a rule that holds a player, a ship or a stat collection
   drags that whole object graph into each copy. A multi-ship mod copies rules once per ship as
   well. Store identity (an actor number, a view id) and resolve the live object when the rule
   initialises against its collection. Check: deep-copy the modifier with the game's copier and
   walk the copy; it reaches no stat collection and no Unity object other than ScriptableObjects.
4. **Do not graft components under a ship at runtime when another mod identifies them by
   order.** Multi-ship and ship-editing mods identify parts such as airlocks, enhancement
   panels and build sockets by their index among the ship's children of that type. A component
   added on some clients, or at different times, shifts those indices and the clients stop
   agreeing on which part an index names. Check: compare the child count of that type on two
   clients after the graft.
5. **Register crafting data for every carryable.** The game's recycled-alloy count reads the
   crafting rules of every carryable on the player ship, with no null check, at session start
   on the host and at the end of every run. A carryable with no crafting definition throws
   there and breaks the end-of-run tally, in a one-ship room too. Check: the framework logs
   `carryable '<name>' (<guid>) is registered with no crafting data` once per offender.

## Log lines

```
Ectype: topology: <n> ship(s), provider=<single|fleet>[, <k> dying][, numbers from <VCF_Ships|view-id rank>].
Ectype: topology: Void Crew Fleet <version> bound, API v<n>[; missing <members>, answered from game data instead].
Ectype: topology: Fleet's VCF_Ships room property failed its shape check (<why>); ship numbers come from view-id rank instead.
Ectype: topology: Void Crew Fleet <version> has API v<n>; this framework reads v1, so answers come from game data alone.
```

The first line is written about two seconds after the ship count settles, at every mission
start and after every ship loss. A vanilla mission logs `1 ship(s), provider=single`.

Related: [multiplayer.md](../multiplayer.md) (rooms with more than one ship),
[roles.md](roles.md) (the `characterShip` target), [ship-arrivals.md](ship-arrivals.md).
