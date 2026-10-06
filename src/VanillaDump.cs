using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

using Client.Player.Interactions;
using Gameplay.Perks;
using Gameplay.Tags;
using Gameplay.Utilities;
using ResourceAssets;

namespace Ectype
{
    /// <summary>
    /// Writes every VANILLA class tree - perk names, tooltip text, grid positions, display
    /// types, targets, and each level's actual StatMods (stat id + name, modifier type,
    /// amount, dynamic condition/value type names, tag configuration) - to
    /// BepInEx\EctypeDump\vanilla-classes.json, once per launch, as soon as the containers
    /// are up. This is the ground truth for authoring a manifest that recreates a vanilla
    /// class verbatim, and a regression probe for what a game update changed.
    /// </summary>
    internal static class VanillaDump
    {
        private static bool _done;
        private static int _attempts;

        public static void EnsureDumped()
        {
            if (_done) return;
            try
            {
                PerkTreeContainer tc = PerkTreeContainer.Instance;
                PerkBuffContainer pc = PerkBuffContainer.Instance;
                if (tc == null || pc == null) return;

                JArray trees = new JArray();
                foreach (PerkTreeRef tref in tc.GetPerkRefs())
                {
                    if (tref == null || ClassRegistry.IsOurGuid(tref.AssetGuid)) continue;
                    PerkTreeDef def = tc.GetAssetDefById(tref.AssetGuid, true);
                    if (def == null || def.Asset == null || def.Asset.Perks == null) continue;
                    trees.Add(DumpTree(tref, def, pc));
                }
                if (trees.Count == 0) return;   // refs not populated yet - retry next tick

                string dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "EctypeDump");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "vanilla-classes.json");
                File.WriteAllText(path, new JObject
                {
                    ["game"] = UnityEngine.Application.version,
                    ["trees"] = trees
                }.ToString());
                _done = true;
                Plugin.Log.LogInfo("Ectype: vanilla class dump written to " + path + " (" +
                                   trees.Count + " trees).");
            }
            catch (Exception e)
            {
                _attempts++;
                if (_attempts == 1 || _attempts >= 20)
                    Plugin.Log.LogWarning("Ectype: vanilla dump attempt " + _attempts +
                                          " failed: " + e.Message);
                if (_attempts >= 20) _done = true;   // stop retrying, keep the log honest
            }
        }

        private static JObject DumpTree(PerkTreeRef tref, PerkTreeDef def, PerkBuffContainer pc)
        {
            ContextInfo treeCtx = def.ContextInfo as ContextInfo;
            JArray perks = new JArray();
            foreach (PerkBuffRef pref in def.Asset.Perks)
            {
                if (pref == null) continue;
                PerkBuffDef pdef;
                pc.TryGetByGuid(pref.AssetGuid, out pdef);
                PerkBuff perk = pdef != null ? pdef.Asset : null;
                if (perk == null) continue;
                perks.Add(DumpPerk(pref, pdef, perk));
            }
            return new JObject
            {
                ["tree"] = Refl.GetField(def, "perkTree", (EPerkTree)0).ToString(),
                ["guid"] = tref.AssetGuid.AsHex(),
                ["header"] = treeCtx != null ? treeCtx.HeaderText : null,
                ["body"] = treeCtx != null ? treeCtx.BodyText : null,
                ["perks"] = perks
            };
        }

        private static JObject DumpPerk(PerkBuffRef pref, PerkBuffDef pdef, PerkBuff perk)
        {
            ContextInfo ctx = pdef.ContextInfo as ContextInfo;
            JArray levels = new JArray();
            if (perk.Levels != null)
                foreach (PerkBuffLevel level in perk.Levels)
                {
                    JArray mods = new JArray();
                    if (level != null && level.Mods != null)
                        foreach (StatMod mod in level.Mods)
                            if (mod != null) mods.Add(DumpMod(mod));
                    levels.Add(mods);
                }
            return new JObject
            {
                ["guid"] = pref.AssetGuid.AsHex(),
                ["header"] = ctx != null ? ctx.HeaderText : null,
                ["body"] = ctx != null ? ctx.BodyText : null,
                // The sprite's asset name: only the live ContextInfo knows which icon a
                // perk uses, so an offline tool has no other source for it.
                ["icon"] = (ctx != null && ctx.Icon != null) ? ctx.Icon.name : null,
                ["row"] = perk.Row,
                ["col"] = perk.Column,
                ["displayType"] = perk.DisplayType.ToString(),
                ["target"] = EctypeAssets.TargetName(perk.Target),
                ["buyingDisabled"] = perk.BuyingDisabled,
                ["levels"] = levels
            };
        }

        private static JObject DumpMod(StatMod mod)
        {
            // Amount lives on PrimitiveModifier<T> and T varies (float, int, bool...):
            // read it untyped and convert, or int-typed modifiers dump as null.
            JToken amount = null;
            if (mod.Mod != null)
            {
                var f = HarmonyLib.AccessTools.Field(mod.Mod.GetType(), "Amount");
                object raw = f != null ? f.GetValue(mod.Mod) : null;
                if (raw is float) amount = (float)raw;
                else if (raw is int) amount = (int)raw;
                else if (raw is bool) amount = (bool)raw;
                else if (raw != null) amount = raw.ToString();
            }

            JObject o = new JObject
            {
                ["statId"] = mod.Type,
                ["stat"] = StatRegistry.NameOf(mod.Type),
                ["modifierType"] = mod.Mod != null ? mod.Mod.Type.ToString() : null,
                ["modifierClass"] = mod.Mod != null ? mod.Mod.GetType().Name : null,
                ["amount"] = amount
            };
            if (mod.DynamicCondition != null)
            {
                o["dynamicCondition"] = mod.DynamicCondition.GetType().Name;
                o["invertCondition"] = mod.DynamicCondition.InvertApplication;
            }
            if (mod.DynamicValue != null)
                o["dynamicValue"] = mod.DynamicValue.GetType().Name;
            ModTagConfiguration tags = mod.TagConfiguration;
            if (tags != null)
            {
                JArray req = TagNames(tags.RequiredTags);
                JArray local = TagNames(tags.RequiredLocalTags);
                JArray disabling = TagNames(tags.DisablingTags);
                JArray add = TagNames(tags.TagsToAdd);
                if (req.Count > 0)
                {
                    o["requiredTags"] = req;
                    o["requiredTagCriteria"] = tags.RequiredTagCriteria.ToString();
                }
                if (local.Count > 0)
                {
                    o["requiredLocalTags"] = local;
                    o["requiredLocalTagCriteria"] = tags.RequiredLocalTagCriteria.ToString();
                }
                if (disabling.Count > 0) o["disablingTags"] = disabling;
                if (add.Count > 0) o["tagsToAdd"] = add;
            }
            return o;
        }

        private static JArray TagNames(CsTag[] tags)
        {
            JArray a = new JArray();
            if (tags != null)
                foreach (CsTag t in tags)
                    a.Add(t != null ? t.name : "<null>");
            return a;
        }
    }
}
