# Configuration

The framework's settings live in `BepInEx/config/jack.voidcrew.ectype.cfg`, created on first
launch.

| Key | Default | Meaning |
| --- | --- | --- |
| `[General] Enabled` | `true` | Master switch. Off means no patches, no discovery and no registration: the framework is inert. |
| `[General] RegisterStage` | `9` | Bisection aid. Stops each role's registration after stage N: 1 custom stats and perk assets built, 2 runtime assets registered, 3 context infos, 4 perk defs published, 5 tree asset registered, 6 tree def published. 9 runs everything. |
| `[General] PublishTreeDefs` | `true` | Emergency brake on stage 6. A published tree def whose `PerkTreeVE` element failed to inject is filtered out of the terminal build so the terminal survives. Set false to keep every tree def unpublished: roles register but no tree renders. |
| `[Debug] VerboseLogging` | `false` | Also runs the layout watcher, which logs one warning if a terminal's tree row has no width 20 s after a build. Logs per-character, per-switch and per-open events: ability installs and adoptions, role-ability channel ends, ability-display slot changes, HUD bar button register, build, remove and layout, stat value changes, gene-tree injection and scroller installs, per-perk unlock and USS palette lines. Off leaves warnings, errors and the one-time startup summaries. Role mods read the same key. |
| `[Presentation] EnableScroll` | `true` | Wraps the tree row in the horizontal scroller when more than four trees survive (registered roles minus suppressed vanilla trees). Off lets the extra trees overflow off-layout. With four or fewer the layout is never touched. |
| `[Presentation] ScrollExcludeDocuments` | (empty) | Comma-separated, case-insensitive substrings. A document whose `UIDocument` name, GameObject name or visual-tree-asset name matches any of them keeps its authored layout, with no scroller. The layout-failed warning (needs `VerboseLogging`) prints each document's names. |
| `[Presentation] RoleWording` | `true` | Where the game's English text calls a playable role a "Class", it says "Role": the ability keybind in Settings -> Controls, the ability cooldown and duration lines on item tooltips, the stock hero-perk descriptions and the hub's "Class: <role>" projection headers. Display text only, and other languages are untouched. Off restores the game's wording; a screen already open keeps its text until the game rebuilds it. |
| `[EnemyStatuses] Markers` | `true` | Countdown markers over objects carrying a status. Off runs statuses markerless. Each marker is a live UI entity the game's viewer repositions every frame, hence the cap. |
| `[EnemyStatuses] MarkerCap` | `10` | Most status markers alive at once, across all statuses and targets. Past the cap a status runs without a marker until one frees up. |
| `[EnemyStatuses] Vfx` | `true` | Attached status visuals. Off runs statuses without them. |
| `[EnemyStatuses] VfxCap` | `8` | Most attached status visuals alive at once, across all statuses and targets. |

## The F5 page

F5 -> Ectype Framework shows the registration pipeline status for each role, the vanilla stat
count and the trees and roles suppression removed. In a release build the page has no
controls.

A dev tools build adds a `[Dev]` config section and, with VoidManager's debug mode on, a
developer section on this page. A release build has neither. See
[CONTRIBUTING.md](../../CONTRIBUTING.md#dev-tools-build).
