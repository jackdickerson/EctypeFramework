## 0.22.0

Rooms with more than one player ship, such as Void Crew Fleet's: perks that buff "the ship" now buff each player's own ship on every client, enemy-shot slowing works per ship, and a new event fires once when a whole fleet arrives from a jump. Timed stat effects survive being copied by other mods, a jump from one ship's inertia zone into another's ends channelled abilities, and spawns are lighter in large rooms.

## 0.21.0

VoidManager is now optional: the framework runs without it, and with it installed the F5 page is unchanged. Players with and without VoidManager can share a room, unless the host runs VoidManager together with a mod that requires it of everyone. The framework does not block progression or achievements: rooms it hosts are still tagged modded, and with VoidManager installed VoidManager's own policy applies. Multiplayer messages use the framework's own network channel, and a player whose framework cannot talk to the host's is refused at join with the host's framework version on the rejection screen.

## 0.20.1

Mod matching now belongs to the Void Crew Mod Launcher: the framework no longer checks other players' builds, re-sends mod lists on a host change, or offers a host-only per-role switch. Suppression declared in a role's manifest is unchanged.

## 0.20.0

First Thunderstore release. Since the tester build: how-to toasts through the game's own hint system for other mods to use, a cancel policy for role abilities, role icons fitted to their glyph so they read at small sizes, and an ability replicated to a crewmate who has just joined waits until their character is ready instead of failing.

## 0.19.15

Pre-release build for testers. Lets other mods add playable roles from a JSON manifest, and provides loot-table registration, enemy and character statuses, field-backed stats, a HUD ability bar and mutator-list safety for clients missing a session mutator. The package includes the API documentation file EctypeFramework.xml beside the DLL.
