# Engine notes

The engine facts this framework was built on are not framework documentation. They are
facts about Void Crew, Harmony, Unity UI Toolkit, Opsive and Photon that hold for any mod
against this game, so they live in the `vcm` plugin's reference set, where every skill
already loads them. This file is the map from the subject to its home there.

Anything below that names a `vcm` path is canonical at that path. Do not restate it here:
two copies of an engine fact drift, and the copy a reader has open is the one that is
wrong.

| Subject | Where it lives |
| --- | --- |
| Harmony: patch binding, partial `PatchAll` application, going inert on a failed pass | `vcm/common/discipline.md` §3, §5 |
| Harmony: resolving the member you meant to patch - `TargetMethods`, overload ambiguity, when a miss is allowed | `vcm/common/discipline.md` §5a |
| Vanilla crash sites a modded fifth tree walks into | `vcm/references/pitfalls.md` - the perk-tagged entries, notably #1, #23 and #87 |
| Runtime asset registration: containers, re-runnability, surviving a scene transition | `vcm/references/runtime-assets-and-custom-items.md` |
| Unlocks, the fabricator and criteria that cannot be repointed | `vcm/references/runtime-assets-and-custom-items.md` §5 |
| Deterministic identity across clients: derived GUIDs, ordinal allocation | `vcm/references/runtime-assets-and-custom-items.md` §10, §10a |
| Stats and modifiers: the value formula, collection trees, ordering guarantees, id aliasing | `vcm/references/stats-and-modifiers.md` §1–§4b |
| Registering a stat id of your own; the silent no-op | `vcm/references/stats-and-modifiers.md` §7 |
| Modifier classes, reading one back, instance sharing | `vcm/references/stats-and-modifiers.md` §8 |
| Wrapping a raw field as a stat, and the two-owner failure | `vcm/references/stats-and-modifiers.md` §6, `vcm/references/pitfalls.md` #86 |
| Tags: reference matching, case sensitivity, the empty filter | `vcm/references/runtime-assets-and-custom-items.md` §8a |
| Perks and gene trees: the grid, display types, perk targets, hiding a tree | `vcm/workflows/roles-and-perks.md` |
| Opsive abilities: start types, the replication index problem, replicating by key | `vcm/references/pitfalls.md` #85, `vcm/workflows/roles-and-perks.md` |
| UI Toolkit: no parser, no debugger, what a dump must carry | `vcm/references/ui-toolkit.md` §5, §6 |
| UI Toolkit: transforms, layout resolution, geometry watching | `vcm/references/ui-toolkit.md` §6c, §6c-i, §6e |
| UI Toolkit: sprites in backgrounds and the nine-slice trap | `vcm/references/ui-toolkit.md` §6c-ii |
| UI Toolkit: the game's compiled stylesheets, reading and cloning them | `vcm/references/ui-toolkit.md` §5b and its subsections |
| UI Toolkit: injecting into a game document, picking mode, the HUD slot | `vcm/references/ui-toolkit.md` §3, §5a; `vcm/references/pitfalls.md` #7, #16 |
| UI Toolkit: `ScrollView` in an authored layout | `vcm/references/ui-toolkit.md` §6e, `vcm/references/pitfalls.md` #51 |
| Effects and statuses on space objects, and object markers | `vcm/references/architecture.md` §15, `vcm:enemy-statuses` |
| Cloning stock prefabs for a visual | `vcm/references/kitbashing-assets.md` |
| Input, dev keys and the tuning-file layer | `vcm/references/input-and-controls.md`, `vcm/workflows/tuning-knobs.md` |
| Iterating on UI you cannot see, including the capture loop | `vcm/agents/void-crew-ui.md`, `vcm/workflows/editing-uxml.md` |

`vcm/` is the source; installing copies it into a cache, so a change there is not live
until the version in `vcm/.claude-plugin/plugin.json` is bumped and the plugin updated.
`/vcm:update` runs that loop.

## What stays here

Framework behaviour - what this DLL does, the contract it offers, its config keys and its
manifest format - is in `README.md` and `docs/` beside it. The rule for deciding: if the
fact would still be true with this framework uninstalled, it belongs in `vcm`.
