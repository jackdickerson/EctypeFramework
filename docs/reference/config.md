# Configuration

The framework's settings live in `BepInEx/config/jack.voidcrew.ectype.cfg`, created on first
launch.

| Key | Default | Meaning |
| --- | --- | --- |
| `[General] Enabled` | `true` | Master switch. Off means no patches, no discovery and no registration: the framework is inert. |
| `[General] RegisterStage` | `9` | Bisection aid. Stops each class's registration after stage N: 1 custom stats and perk assets built, 2 runtime assets registered, 3 context infos, 4 perk defs published, 5 tree asset registered, 6 tree def published. 9 runs everything. |
| `[General] PublishTreeDefs` | `true` | Emergency brake on stage 6. A published tree def whose `PerkTreeVE` element failed to inject is filtered out of the terminal build so the terminal survives. Set false to keep every tree def unpublished: classes register but no tree renders. |
| `[Debug] VerboseLogging` | `false` | Logs per-character, per-switch and per-open events: ability installs and adoptions, ability-display slot changes, HUD bar button register, build, remove and layout, stat value changes, gene-tree injection and scroller installs, per-perk unlock and USS palette lines. Off leaves warnings, errors and the one-time startup summaries. Class mods read the same key. |
| `[Presentation] EnableScroll` | `true` | Wraps the tree row in the horizontal scroller when more than four trees survive (registered classes minus suppressed vanilla trees). Off lets the extra trees overflow off-layout. With four or fewer the layout is never touched. |
| `[Presentation] ScrollExcludeDocuments` | (empty) | Comma-separated, case-insensitive substrings. A document whose `UIDocument` name, GameObject name or visual-tree-asset name matches any of them keeps its authored layout, with no scroller. The geometry log prints each document's names. |
| `[Presentation] GeometryLog` | `true` | Watches each terminal document's tree row after every terminal build, sampling twice a second. A row that is attached and shown but still has no width after 20 s logs a warning with the full geometry dump. The dump of a healthy row is logged at first layout only with `VerboseLogging`. |
| `[ClassSuppression] Suppress_<classId>` | `false` | Host only, one per discovered class. Makes that class unavailable to everyone in the room. See [classes.md](classes.md#host-driven-suppression). |
| `[EnemyStatuses] Markers` | `true` | Countdown markers over objects carrying a status. Off runs statuses markerless. Each marker is a live UI entity the game's viewer repositions every frame, hence the cap. |
| `[EnemyStatuses] MarkerCap` | `10` | Most status markers alive at once, across all statuses and targets. Past the cap a status runs without a marker until one frees up. |
| `[EnemyStatuses] Vfx` | `true` | Attached status visuals. Off runs statuses without them. |
| `[EnemyStatuses] VfxCap` | `8` | Most attached status visuals alive at once, across all statuses and targets. |

## The F5 page

F5 -> Ectype Framework shows the registration pipeline status for each class, the vanilla stat
count, and the per-class suppress toggles. The toggles act only for the host.
