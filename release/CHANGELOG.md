## 0.30.0

Mods can add mutators to the game's Mutators menu that change run-wide numbers, such as how many hostiles spawn. Select one before a run, or have a mission force it on, and every player gets the same numbers, late joiners included. Players need the mod that adds a mutator to join a host who has it installed.

## 0.28.0

Mods can put timed effects on other players, such as a slow from a crewmate or an enemy. The affected player's own game checks each one first against the mod's rules (who sent it, how close they are, whether they can see each other, how often they send), so a modified client cannot apply them from afar, through walls or in a flood. Other players can see these effects. Effects on a player now end when that player dies. Players need this version to join a host running it.

## 0.27.0

Mods can publish new weapons, modules and items through the framework. Item catalogs such as ship customizers list published items with no change of their own, every player in a room holds them in the same order, and an item without a recycle value is refused instead of breaking the end-of-run alloy count.

## 0.26.0

Mods can declare their own stats in a data file. Each shows in tooltips like the game's own stats and can be read by mods that do not use the framework. The framework adds melee damage, cooldown and reach, anti-personnel damage and a hostile spawn scale for mods to adjust, and run-wide stats the host decides reach every player, late joiners included.

## 0.25.0

Mods can let the host decide settings for the whole crew. The host's values reach every player at the start of each session, late joiners included, so everyone plays the same run, and a resumed saved run keeps the values it started with.

## 0.24.0

Mods can stop players using seats, the helm, turrets, terminals, doors, switches and items by their own rules, such as keeping the helm for the host. A refused player sees a short warning instead of a prompt, and the game that owns the part refuses the request too, so a modified client cannot get around the rule. With no mod using it nothing changes.

## 0.23.0

Status effects that mods put on ships can be limited to enemies or to allies, and for those every player's game checks who applied or removed one, so a modified client cannot place or strip them on the wrong side. Effects a mod leaves open to everyone stay open. Rapid-fire weapons that apply a status on every hit send far fewer network messages in large rooms. When too many status markers are on screen, the ones nearest your ship are kept. Players need this version to join a host running it.

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
