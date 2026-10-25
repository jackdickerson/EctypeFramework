![](https://img.shields.io/badge/Game%20Version-[GameVersion]-111111?style=flat&labelColor=24292f&color=111111)
[![](https://img.shields.io/discord/1180651062550593536.svg?&logo=discord&logoColor=ffffff&style=flat&label=Discord&labelColor=24292f&color=111111)](https://discord.gg/g2u5wpbMGu "Void Crew Modding Discord")

# [UserModName]

Version [ModVersion]  
For Game Version [GameVersion]  
Developed by Jack  
Some AI tooling used  
Requires: [Dependencies]

---------------------

### 💡 Function(s)

- A dependency library. On its own it adds no gameplay: install it when another mod lists it as required.
- Lets other mods add **playable roles** (a perk tree plus an optional role ability) to the Fabricator's gene-tree screens, which the base game limits to four trees.
- Provides shared services to dependent mods: loot-table registration for modded weapons, timed enemy and crew statuses, field-backed stats that perks can modify, a HUD ability bar and ship arrivals from the void.
- Keeps your client from crashing when a run uses a session mutator whose mod you do not have installed.

### 🎮 Client Usage

- Nothing to operate. Install it alongside the mods that need it.
- Settings, if any, are under **F5 → Mod Settings → Ectype Framework**.

### 👥 Multiplayer Functionality

- ⚠️ All players
  - **Everyone in the room needs the same build of this mod**, and the same build of every mod that depends on it. A mismatched version bounces the joiner.
  - Install new versions on every crewmate's machine at the same time.

### 🛠️ For modders

- A role mod is a JSON manifest, icons and optional code callbacks; the framework does the registration.
- The package includes `EctypeFramework.xml`, the API documentation your editor reads when you reference the DLL.

---------------------

## 🔧 Install Instructions - **Install following the normal BepInEx procedure.**

Ensure that you have [BepInEx 5](https://thunderstore.io/c/void-crew/p/BepInEx/BepInExPack/) (stable version 5 **MONO**) installed. [VoidManager](https://thunderstore.io/c/void-crew/p/NihilityShift/VoidManager/) is optional: with it, the framework has an F5 status page.

#### ✔️ Mod installation - **Unzip the contents into the BepInEx plugin directory**

Drag and drop `EctypeFramework.dll` into `Void Crew\BepInEx\plugins`, or install through r2modman / Gale.
