using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

using CG.Client.UserData;
using Client.Player.Interactions;
using Gameplay.Perks;
using Gameplay.Utilities;
using ResourceAssets;

namespace Ectype
{
    /// <summary>
    /// The registration pipeline, run per discovered role. Staged per EctypeConfig, every
    /// step re-runnable and HasAsset/HasItem-guarded because registration bottoms out in a
    /// plain Dictionary.Add that throws on the second call. One role failing is one named
    /// error and one absent role; the rest of the set registers normally.
    /// </summary>
    internal static class EctypeAssets
    {
        public static string GlobalStatus = "waiting for asset containers";

        private static bool ReadyToRegister()
        {
            try
            {
                if (ResourceAssetContainerRegister.Instance == null) return false;
                if (RuntimeAssetsRegister.Instance == null) return false;
                if (PerkBuffContainer.Instance == null) return false;
                if (PerkTreeContainer.Instance == null) return false;
                if (DataTable<StatTable>.Instance == null) return false;
                if (DataTable<CsTagTable>.Instance == null) return false;
                if (UnlockContainer.Instance == null) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Fast-returns once every role is settled. Called from the driver's retry
        /// loop and from the PerkLoadout ctor prefix (the profile-load race).</summary>
        public static void EnsureRegistered()
        {
            if (EctypeConfig.Enabled == null || !EctypeConfig.Enabled.Value) return;
            // A half-applied patch set must never register content: the caller may itself be
            // one of the patches that survived a mid-PatchAll abort.
            if (!Plugin.PatchesApplied)
            { GlobalStatus = "inert: patching did not complete"; return; }
            if (!ReadyToRegister()) { GlobalStatus = "waiting for asset containers"; return; }

            // Before any role registers a perk: a perk effect may name one of these, and it
            // resolves while the perk asset is being built. Unconditional on the role set,
            // so the surface exists in an install with no role mods at all.
            try { FrameworkStats.EnsureRegistered(); }
            catch (Exception e)
            { Plugin.Log.LogError("Ectype: framework-owned stat registration failed: " + e); }

            RoleRegistry.DiscoverOnce();
#if ECTYPE_DEV
            DevTools.AfterDiscovery();
#endif
            try { Suppression.Resolve(); }
            catch (Exception e)
            { Plugin.Log.LogError("Ectype: suppression resolution failed: " + e); }

            int done = 0, failed = 0;
            foreach (RoleRecord rec in RoleRegistry.Roles)
            {
                if (rec.Registered) { done++; continue; }
                if (rec.PermanentlyFailed) { failed++; continue; }
                RegisterOne(rec);
                if (rec.Registered) done++;
            }

            try { Adoption.ResolveDeferredUnlocks(); }
            catch (Exception e)
            { Plugin.Log.LogError("Ectype: deferred unlock adoption failed: " + e); }

            GlobalStatus = RoleRegistry.Roles.Count == 0
                ? "no role manifests found"
                : done + "/" + RoleRegistry.Roles.Count + " roles registered" +
                  (failed > 0 ? " (" + failed + " failed)" : "");

            if (RoleRegistry.Discovered && done + failed == RoleRegistry.Roles.Count)
                CraftingAudit.Run();
        }

        private static int Stage()
        {
            return EctypeConfig.RegisterStage == null ? 9 : EctypeConfig.RegisterStage.Value;
        }

        /// <summary>DontDestroyOnLoad alone does not survive Resources.UnloadUnusedAssets on
        /// a scene transition; HideAndDontSave does.</summary>
        private static void Keep(UnityEngine.Object o)
        {
            if (o == null) return;
            o.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(o);
        }

        private static void RegisterOne(RoleRecord rec)
        {
            rec.Attempts++;
            try
            {
                RunStages(rec);
                rec.Registered = true;
                rec.Status = "registered (stage limit " + Stage() + "); " +
                             (rec.TreeDefPublished ? "tree def published as EPerkTree " +
                                                     rec.Ordinal
                                                   : "tree def not published");
                Plugin.Log.LogInfo("Ectype: role '" + rec.Manifest.roleName +
                                   "' registered. Tree " + rec.TreeGuid.AsHex() +
                                   " as EPerkTree " + rec.Ordinal + ", " +
                                   rec.PerkGuids.Count + " perk(s). " + rec.Status);
                Verify(rec);
            }
            catch (Exception e)
            {
                rec.Status = "attempt " + rec.Attempts + " failed at [" + rec.Stage + "]: " +
                             e.Message;
                if (rec.Attempts == 1 || rec.Attempts % 10 == 0)
                    Plugin.Log.LogWarning("Ectype: '" + rec.Manifest.roleName +
                                          "' registration " + rec.Status);
                if (rec.Attempts > 200)
                {
                    rec.PermanentlyFailed = true;
                    Plugin.Log.LogError("Ectype: giving up on '" + rec.Manifest.roleName +
                                        "'. Last error: " + e);
                }
            }
        }

        private static void RunStages(RoleRecord rec)
        {
            RoleManifest m = rec.Manifest;

            rec.Stage = "guids";
            rec.TreeGuid = new GUIDUnion(Guids.TreeGuid(m.roleId));

            // ---- stage 1: custom stats, then perk assets --------------------------------
            rec.Stage = "custom stats";
            foreach (CustomStatSpec spec in m.customStats)
                if (!rec.CustomStats.ContainsKey(spec.name))
                    rec.CustomStats[spec.name] = StatRegistry.RegisterCustomStat(
                        m.roleId, spec, IconStore.Get(rec.SourceAssembly, spec.icon));

            rec.Stage = "perk assets";
            BuildPerk(rec, m.hero, true);
            foreach (PerkSpec spec in m.perks) BuildPerk(rec, spec, false);
            if (Stage() < 2) return;

            // ---- stage 2: runtime assets (Required mask - a role changes what a run
            //      contains, and its GUIDs cross the wire in "PBL") -----------------------
            rec.Stage = "runtime assets";
            RuntimeAssetsRegister reg = RuntimeAssetsRegister.Instance;
            foreach (KeyValuePair<string, PerkBuff> kv in rec.Perks)
            {
                GUIDUnion guid = rec.PerkGuids[kv.Key];
                if (!reg.HasAsset(guid))
                    reg.RegisterAsset(guid, kv.Value, ModFlagPolicy.RequiredEffect,
                        new RuntimeAssetInfo
                        {
                            Name = kv.Value.name,
                            DisplayName = m.roleName + ": " + kv.Key
                        });
            }
            if (Stage() < 3) return;

            // ---- stages 3+4: context infos and perk defs --------------------------------
            rec.Stage = "perk defs";
            PerkBuffContainer perkContainer = PerkBuffContainer.Instance;
            List<PerkBuffDef> authoredPerks =
                Refl.GetField<List<PerkBuffDef>>(perkContainer, "AssetDescriptions", null);
            PublishPerkDef(rec, m.hero, perkContainer, authoredPerks);
            PublishUnlocks(rec, m.hero);
            if (Stage() < 4) return;
            foreach (PerkSpec spec in m.perks)
            {
                PublishPerkDef(rec, spec, perkContainer, authoredPerks);
                PublishUnlocks(rec, spec);
            }
            if (Stage() < 5) return;

            // ---- stage 5: the tree asset -------------------------------------------------
            rec.Stage = "tree asset";
            if (rec.Tree == null)
            {
                rec.Tree = ScriptableObject.CreateInstance<PerkTree>();
                rec.Tree.name = "Ectype_PerkTree_" + m.roleId;
                Keep(rec.Tree);
                rec.Tree.ContainerGuid = rec.TreeGuid;
                rec.Tree.Perks = new List<PerkBuffRef>();
                foreach (GUIDUnion guid in rec.PerkGuids.Values)
                    rec.Tree.Perks.Add(new PerkBuffRef(guid, "") { IsRuntime = true });
            }
            if (!reg.HasAsset(rec.TreeGuid))
                reg.RegisterAsset(rec.TreeGuid, rec.Tree, ModFlagPolicy.RequiredEffect,
                    new RuntimeAssetInfo
                    {
                        Name = rec.Tree.name,
                        DisplayName = m.roleName + " gene tree"
                    });
            if (Stage() < 6 ||
                EctypeConfig.PublishTreeDefs == null || !EctypeConfig.PublishTreeDefs.Value ||
                !InjectionPatchApplied())
            {
                rec.TreeDefPublished = false;
                return;
            }

            // ---- stage 6: the tree def ---------------------------------------------------
            rec.Stage = "tree def";
            if (rec.TreeDef == null)
            {
                rec.TreeDef = new PerkTreeDef();
                rec.TreeDef.Ref = new PerkTreeRef(rec.TreeGuid, "") { IsRuntime = true };
                ContextInfo treeCtx = ContextInfo.Create(
                    IconStore.GetRoleIcon(rec.SourceAssembly, m.roleIcon)
                        ?? IconFrom(m.roleIconDonor, m.roleId + " (role icon)")
                        ?? IconStore.Placeholder(false),
                    m.roleName, m.roleDesc ?? "");
                Keep(treeCtx);
                rec.TreeDef.ContextInfo = treeCtx;
                if (!Refl.SetFieldLogged(rec.TreeDef, "perkTree", (EPerkTree)rec.Ordinal))
                    throw new Exception("could not set PerkTreeDef.perkTree");
            }
            PerkTreeContainer treeContainer = PerkTreeContainer.Instance;
            if (!treeContainer.HasItem(rec.TreeGuid))
                treeContainer.RegisterRuntimeAsset(rec.TreeGuid, rec.TreeDef);
            List<PerkTreeDef> authoredTrees =
                Refl.GetField<List<PerkTreeDef>>(treeContainer, "AssetDescriptions", null);
            if (authoredTrees != null && !authoredTrees.Contains(rec.TreeDef))
                authoredTrees.Add(rec.TreeDef);
            rec.TreeDefPublished = true;
        }

        private static bool? _injectionPatchVerified;

        /// <summary>
        /// The stage-6 interlock: a published PerkTreeDef whose PerkTreeVE never gets injected
        /// kills both terminals, and shipping the prefix in this DLL guarantees nothing unless
        /// it actually APPLIED - so ask Harmony directly. Checked once; one named error on
        /// failure.
        /// </summary>
        private static bool InjectionPatchApplied()
        {
            if (_injectionPatchVerified.HasValue) return _injectionPatchVerified.Value;
            bool ok = false;
            try
            {
                System.Reflection.ConstructorInfo ctor =
                    typeof(UI.Token.PerkTreeController).GetConstructor(new Type[]
                    {
                        typeof(IReadOnlyCollection<PerkTreeDef>), typeof(VisualElement),
                        typeof(TokenTerminalEvents), typeof(ILocalizationProvider),
                        typeof(InteractionInfo), typeof(bool)
                    });
                HarmonyLib.Patches info = ctor != null ? Harmony.GetPatchInfo(ctor) : null;
                if (info != null && info.Prefixes != null)
                    foreach (Patch p in info.Prefixes)
                        if (p.owner == EctypePluginInfo.PLUGIN_GUID) { ok = true; break; }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: injection-patch verification threw (" +
                                    e.Message + ") - treating as not applied.");
            }
            _injectionPatchVerified = ok;
            if (!ok)
                Plugin.Log.LogError("Ectype: the PerkTreeController ctor prefix is not " +
                                    "applied - tree defs are not published this session. " +
                                    "Roles register but no tree renders.");
            return ok;
        }

        // ------------------------------------------------------------------------ builders

        private static void BuildPerk(RoleRecord rec, PerkSpec spec, bool hero)
        {
            GUIDUnion guid = new GUIDUnion(
                string.IsNullOrEmpty(spec.guid) ? Guids.PerkGuid(rec.Manifest.roleId, spec.key)
                                                : spec.guid);
            rec.PerkGuids[spec.key] = guid;
            if (rec.Perks.ContainsKey(spec.key)) return;   // re-entry

            PerkBuff perk = ScriptableObject.CreateInstance<PerkBuff>();
            perk.name = "Ectype_PerkBuff_" + rec.Manifest.roleId + "_" + spec.key;
            Keep(perk);
            // PerkBuff's ctor initialises none of its lists, and the reapply path indexes
            // Levels[level-1].Mods unchecked.
            perk.RequiredFor = new List<PerkBuffRef>();
            PerkBuffTarget target;
            TryParseTarget(spec.target, out target);   // validation refused an unknown one
            perk.Target = target;
            perk.DisplayType = hero ? PerkDisplayType.Hero
                                    : DisplayTypeFor(spec.row, spec.wide);
            perk.BuyingDisabled = false;
            perk.ContainerGuid = guid;
            perk.Row = spec.row;
            perk.Column = spec.col;
            perk.Levels = BuildLevels(rec, spec);
#if ECTYPE_DEV
            CharacterShipTestPerk.Apply(rec, spec, perk);
#endif
            rec.Perks[spec.key] = perk;
        }

        /// <summary>
        /// PerkBuffTarget is the routing that makes "when manning" work: Character goes to
        /// player.Stats, Seat to the manned module only, SeatShip to the ship's stat
        /// collection while seated in any chair. characterShip is value 4, which the perk
        /// loader's switch skips; CharacterShipRoute applies it to the ship's collection for
        /// every holder, seated or not. Manifest validation refuses an unrecognised name.
        /// </summary>
        internal static bool TryParseTarget(string s, out PerkBuffTarget target)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "seat": target = PerkBuffTarget.Seat; return true;
                case "seatship": target = PerkBuffTarget.SeatShip; return true;
                case "": case "character": target = PerkBuffTarget.Character; return true;
                case "charactership": target = CharacterShipRoute.CharacterShip; return true;
                default: target = PerkBuffTarget.Character; return false;
            }
        }

        internal const string KnownTargets = "character, seat, seatShip, characterShip";

        /// <summary>A target's authored name; value 4 has no enum member to print.</summary>
        internal static string TargetName(PerkBuffTarget target)
        {
            return target == CharacterShipRoute.CharacterShip
                ? CharacterShipRoute.CharacterShipName : target.ToString();
        }

        /// <summary>Rows of the stock grid that hold a single full-width cell, other than the
        /// row-0 hero: 2 (the wide perk), 5 (capstone) and 7 (superior).</summary>
        internal static bool IsBannerRow(int row) { return row == 2 || row == 5 || row == 7; }

        internal const string BannerRowList = "2, 5 and 7";

        /// <summary>Authored convention (confirmed by the grid dump): the display type follows
        /// the cell. Row 2 is Wide, rows 5 and 7 are Hero, every other cell Normal. Validation
        /// guarantees <paramref name="wide"/> is true exactly on the banner rows.</summary>
        private static PerkDisplayType DisplayTypeFor(int row, bool wide)
        {
            if (!wide) return PerkDisplayType.Normal;
            return row == 2 ? PerkDisplayType.Wide : PerkDisplayType.Hero;
        }

        /// <summary>"kind" picks between the only two concrete PrimitiveModifier&lt;T&gt;
        /// classes, FloatModifier and IntModifier; an unknown value degrades to float with a
        /// warning rather than failing the whole effect.</summary>
        private static PrimitiveModifier BuildModifier(EffectSpec fx, ModifierType type)
        {
            switch ((fx.kind ?? "float").Trim().ToLowerInvariant())
            {
                case "": case "float": return new FloatModifier(fx.value, type);
                case "int": return new IntModifier((int)fx.value, type);
                default:
                    Plugin.Log.LogWarning("Ectype: unknown effect kind '" + fx.kind +
                                          "' on stat '" + fx.stat + "' - using float.");
                    return new FloatModifier(fx.value, type);
            }
        }

        private static List<PerkBuffLevel> BuildLevels(RoleRecord rec, PerkSpec spec)
        {
            int count = Math.Max(1, Math.Max(spec.levels, spec.mods.Count));
            List<PerkBuffLevel> levels = new List<PerkBuffLevel>();
            for (int i = 0; i < count; i++)
            {
                List<StatMod> mods = new List<StatMod>();
                if (i < spec.mods.Count && spec.mods[i] != null)
                {
                    foreach (EffectSpec fx in spec.mods[i])
                    {
                        try
                        {
                            int statId = StatRegistry.Resolve(fx.stat, rec.CustomStats);
                            ModifierType type = StatRegistry.ResolveModifierType(fx.type);
                            string ctx = rec.Manifest.roleName + "/" + spec.key + " L" + (i + 1);
                            Gameplay.Tags.ModTagConfiguration tags = TagRegistry.Build(fx, ctx);
                            PrimitiveModifier mod = BuildModifier(fx, type);
                            mods.Add(tags != null ? new StatMod(mod, statId, tags)
                                                  : new StatMod(mod, statId));
                        }
                        catch (Exception e)
                        {
                            Plugin.Log.LogError("Ectype: '" + rec.Manifest.roleName + "' perk '" +
                                                spec.key + "' level " + (i + 1) +
                                                " effect skipped: " + e.Message);
                        }
                    }
                }
                if (mods.Count == 0 && !spec.emptyOk)
                    Plugin.Log.LogWarning("Ectype: '" + rec.Manifest.roleName + "' perk '" +
                                          spec.key + "' level " + (i + 1) + " has EMPTY " +
                                          "StatMods - it costs a point and does nothing.");
                levels.Add(new PerkBuffLevel { Mods = mods });
            }
            return levels;
        }

        private static void PublishPerkDef(RoleRecord rec, PerkSpec spec,
                                           PerkBuffContainer container,
                                           List<PerkBuffDef> authored)
        {
            GUIDUnion guid = rec.PerkGuids[spec.key];
            if (container.HasItem(guid)) return;   // re-entry

            // ContextInfo.Create, never a hand-built ContextInfo: those NRE in their text
            // setters. Icon: embedded PNG, else this perk's own donor, else the
            // framework placeholder - wide for a Wide-display cell, square everywhere else.
            ContextInfo ctx = ContextInfo.Create(
                IconStore.Get(rec.SourceAssembly, spec.icon)
                    ?? IconFrom(spec.iconDonor, spec.key)
                    ?? IconStore.Placeholder(
                           DisplayTypeFor(spec.row, spec.wide) == PerkDisplayType.Wide),
                spec.name, spec.desc ?? "");
            Keep(ctx);

            PerkBuffDef def = new PerkBuffDef();
            def.Ref = new PerkBuffRef(guid, "") { IsRuntime = true };
            // A plain field write on PerkBuffDef, not the recursive
            // PurchasableResourceAssetDef.set_ContextInfo trap.
            def.ContextInfo = ctx;
            container.RegisterRuntimeAsset(guid, def);
            // GetAssetDefBySelfRef scans only AssetDescriptions, so append there too
            // (in-memory only).
            if (authored != null && !authored.Contains(def)) authored.Add(def);
        }

        // hex item guid -> "role / perk" that first listed it this session, so a retry into
        // this stage neither re-registers nor re-warns every ~2s.
        private static readonly Dictionary<string, string> _unlockAttempted =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // "hex|role|perk" claimants already warned about.
        private static readonly HashSet<string> _unlockDuplicateWarned =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Registers an UnlockItemDef per manifest "unlocks" entry, gated on this perk's own
        /// GUID. That is the entire mechanism: FabricatorModule already walks every
        /// PlayerPerk-criteria UnlockItemDef itself, so no event hook is needed here. An item
        /// guid already claimed by another UnlockItemDef is skipped with one named warning -
        /// the perk registers normally, it simply grants no unlock.
        /// </summary>
        private static void PublishUnlocks(RoleRecord rec, PerkSpec spec)
        {
            if (spec.unlocks == null || spec.unlocks.Count == 0) return;
            UnlockContainer container = UnlockContainer.Instance;
            if (container == null) return;   // retried next pass
            GUIDUnion perkGuid;
            if (!rec.PerkGuids.TryGetValue(spec.key, out perkGuid)) return;

            foreach (string hex in spec.unlocks)   // already normalized 32-hex by ManifestLoader
            {
                string claimant = "'" + rec.Manifest.roleName + "' perk '" + spec.key + "'";
                string holder;
                if (_unlockAttempted.TryGetValue(hex, out holder))
                {
                    // The same perk re-entering on a retry is silent; a different perk
                    // listing an item already claimed this session is warned once.
                    if (holder != claimant && _unlockDuplicateWarned.Add(hex + "|" + claimant))
                        Plugin.Log.LogWarning("Ectype: " + claimant + " unlocks item " + hex +
                                              ", which another role's perk (" + holder +
                                              ") already claimed this session. Skipped: this " +
                                              "perk grants no fabricator unlock for it.");
                    continue;
                }
                _unlockAttempted[hex] = claimant;

                GUIDUnion itemGuid = new GUIDUnion(hex);
                if (container.HasItem(itemGuid))
                {
                    // A verbatim replacement role claims the same items as the tree it
                    // replaced, so take the claim over when suppression killed its old owner.
                    if (Adoption.TryAdoptUnlock(rec, spec.key, itemGuid, perkGuid)) continue;
                    // Not "someone else owns it" until suppression has resolved, and this
                    // role registers only once - so defer rather than decide now.
                    Adoption.DeferUnlock(rec, spec.key, itemGuid, perkGuid);
                    continue;
                }

                UnlockItemDef def = new UnlockItemDef();
                def.Ref = new UnlockItemRef(itemGuid, "") { IsRuntime = true };
                def.UnlockOptions = new UnlockOptions
                {
                    UnlockCriteria = UnlockCriteriaType.PlayerPerk,
                    RequiredPerk = new PerkBuffRef(perkGuid, "") { IsRuntime = true },
                };
                container.RegisterRuntimeAsset(itemGuid, def);
                if (Plugin.Verbose) Plugin.Log.LogInfo("Ectype: '" + rec.Manifest.roleName + "' perk '" +
                                                       spec.key + "' registered a PlayerPerk unlock for item " +
                                                       hex + ".");
            }
        }

        /// <summary>
        /// Borrows the ContextInfo icon off any perk already in the container, by GUID -
        /// shared by reference, nothing copied. Returns null with a named warning when the
        /// GUID names nothing, so the caller falls through to the global donor rather than a
        /// blank cell.
        /// </summary>
        private static Sprite IconFrom(string guidHex, string perkKey)
        {
            if (string.IsNullOrEmpty(guidHex)) return null;
            try
            {
                PerkBuffDef d;
                if (PerkBuffContainer.Instance.TryGetByGuid(new GUIDUnion(guidHex), out d) &&
                    d != null && d.ContextInfo != null)
                {
                    Sprite s = ((ContextInfo)d.ContextInfo).Icon;
                    if (s != null) return s;
                }
                Plugin.Log.LogWarning("Ectype: perk '" + perkKey + "' names iconDonor " +
                                      guidHex + ", which resolves to no perk icon - " +
                                      "falling back to the placeholder.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: perk '" + perkKey + "' iconDonor lookup " +
                                      "failed (" + e.Message + ")");
            }
            return null;
        }

        /// <summary>
        /// Walks exactly what the tree build will dereference, so a silent misregistration
        /// surfaces as a named error at registration time instead of a skipped cell.
        /// </summary>
        private static void Verify(RoleRecord rec)
        {
            try
            {
                foreach (KeyValuePair<string, GUIDUnion> kv in rec.PerkGuids)
                {
                    PerkBuffDef check;
                    if (!PerkBuffContainer.Instance.TryGetByGuid(kv.Value, out check) ||
                        check == null)
                    {
                        Plugin.Log.LogError("Ectype VERIFY '" + rec.Manifest.roleName +
                                            "': perk '" + kv.Key + "' does not resolve by GUID.");
                        return;
                    }
                    if (check.Asset == null)
                    {
                        Plugin.Log.LogError("Ectype VERIFY '" + rec.Manifest.roleName +
                                            "': perk '" + kv.Key + "' def.Asset is null - " +
                                            "ref not IsRuntime, or the asset was collected.");
                        return;
                    }
                }
                Plugin.Log.LogInfo("Ectype VERIFY '" + rec.Manifest.roleName + "': ok. " +
                                   rec.PerkGuids.Count + " perk(s) resolve; tree asset " +
                                   (rec.Tree != null ? "built" : "MISSING") + "; tree def " +
                                   (rec.TreeDefPublished ? "published" : "not published") + ".");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype VERIFY threw: " + e);
            }
        }
    }
}
