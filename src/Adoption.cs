using System;
using System.Collections.Generic;
using System.Reflection;

using CG.Client.UserData;
using Gameplay.Perks;
using Gameplay.Utilities;
using HarmonyLib;
using Opsive.UltimateCharacterController.Character;
using Opsive.UltimateCharacterController.Character.Abilities;
using ResourceAssets;

namespace Ectype
{
    /// <summary>
    /// Re-homes the two payloads the game keys to a suppressed tree's perks from code - the
    /// class ability's selection check, and any fabricator unlock with PlayerPerk criteria -
    /// onto the class that requested the suppression, by repointing the perk identity the
    /// game already consults. Nothing is reimplemented; only the answer to "which perk
    /// grants this" changes.
    ///
    /// Never touched: a payload whose perk is not suppressed, and a class that ships its own
    /// ability factory (its ability wins, and the vanilla one is left orphaned).
    /// </summary>
    internal static class Adoption
    {
        // ------------------------------------------------------------------- class ability

        private static readonly MethodInfo VerifyM =
            AccessTools.Method(typeof(ClassAbility), "VerifyIfIsSelectedAbility");

        /// <summary>Ability instances already repointed on the current character.</summary>
        private static readonly HashSet<ClassAbility> _abilityDone =
            new HashSet<ClassAbility>();

        private static bool _loggedVerifyMissing;

        /// <summary>The local character's locomotion, so the sweep can be re-run from the
        /// perk-reapply path. A MonoBehaviour, so Unity null semantics do apply here and a
        /// despawned character reads as null - unlike the abilities hanging off it.</summary>
        private static UltimateCharacterLocomotion _localLoco;

        /// <summary>Read-only access for the selection pass after a host-suppression change,
        /// which needs the local character's abilities.</summary>
        internal static UltimateCharacterLocomotion LocalLoco { get { return _localLoco; } }

        /// <summary>
        /// Repoint every vanilla ability whose AbilityPerk sits in a suppressed tree at the
        /// suppressing class's banner perks, then re-run the selection check the ability
        /// already ran (with the old perk) during its own Start.
        ///
        /// Re-running the game's own check rather than force-selecting is what makes this
        /// safe: it reads the profile and calls SetAsSelectedAbility either way, so it binds
        /// input and re-enables the Opsive ability on a match and deselects cleanly on a
        /// non-match.
        /// </summary>
        internal static void AdoptAbilities(UltimateCharacterLocomotion loco)
        {
            if (loco == null) return;
            _localLoco = loco;
            if (!Suppression.HasSuppressedTrees) return;
            Ability[] abilities = loco.Abilities;
            if (abilities == null) return;

            foreach (Ability a in abilities)
            {
                ClassAbility ca = a as ClassAbility;
                if (ca == null || ca is EctypeClassAbility) continue;
                if (_abilityDone.Contains(ca)) continue;

                PerkBuff current = Refl.GetField<PerkBuff>(ca, "AbilityPerk", null);
                if (current == null) continue;
                ClassRecord rec = Suppression.AdopterOf(current.ContainerGuid);
                if (rec == null || !rec.Registered) continue;

                // A class that brought its own ability does not want the vanilla one.
                if (rec.AbilityFactory != null)
                {
                    _abilityDone.Add(ca);
                    if (Plugin.Verbose) Plugin.Log.LogInfo("Ectype: '" + rec.Manifest.classId + "' suppresses " +
                                                           "the tree behind " + ca.GetType().Name + " but ships " +
                                                           "its own ability - the vanilla one is left unselected.");
                    continue;
                }

                PerkBuff hero = ClassRegistry.BannerPerk(rec, 0);
                if (hero == null) continue;   // registration not finished; a later Start retries
                PerkBuff capstone = ClassRegistry.BannerPerk(rec, 5) ?? hero;
                PerkBuff superior = ClassRegistry.BannerPerk(rec, 7) ?? hero;

                // All three or none: IsSuperiorVersion and IsCapstonePerkSelected dereference
                // their field unconditionally, so a half-applied repoint NREs inside game code.
                if (!(Refl.SetFieldLogged(ca, "AbilityPerk", hero)
                    & Refl.SetFieldLogged(ca, "CapstonePerk", capstone)
                    & Refl.SetFieldLogged(ca, "SuperiorAbilityPerk", superior)))
                {
                    _abilityDone.Add(ca);   // do not retry a rename every spawn
                    Plugin.Log.LogError("Ectype: could not repoint " + ca.GetType().Name +
                                        " onto '" + rec.Manifest.classId + "' (field rename? " +
                                        "see warnings above) - that class has no active ability.");
                    continue;
                }

                _abilityDone.Add(ca);
                TakeOverCapstonePayload(ca, rec);

                if (VerifyM == null)
                {
                    if (!_loggedVerifyMissing)
                    {
                        _loggedVerifyMissing = true;
                        Plugin.Log.LogError("Ectype: ClassAbility.VerifyIfIsSelectedAbility " +
                                            "not found - game update? Adopted abilities will " +
                                            "not select until the next loadout change.");
                    }
                }
                else
                {
                    try { VerifyM.Invoke(ca, null); }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError("Ectype: re-running the selection check for " +
                                            ca.GetType().Name + " after adoption failed: " + e);
                    }
                }

                if (Plugin.Verbose) Plugin.Log.LogInfo("Ectype: '" + rec.Manifest.classId + "' adopted the " +
                                                       "vanilla " + ca.GetType().Name + " (its perk tree is " +
                                                       "suppressed); ability now keyed to perk '" +
                                                       HeroKey(rec) + "'.");
            }
        }

        /// <summary>
        /// Re-run the sweep on the known local character, so a class whose registration was
        /// still unfinished at install time gets its ability on the next loadout change
        /// rather than on a respawn.
        /// </summary>
        internal static void ReSweep()
        {
            if (_localLoco == null) return;   // MonoBehaviour: also true once destroyed
            AdoptAbilities(_localLoco);
        }

        /// <summary>Forget instances belonging to a despawned character. Opsive abilities are
        /// plain objects, so a stale entry would keep a dead instance alive and skip the
        /// repoint on the next character.</summary>
        internal static void ForgetAbility(ClassAbility ability)
        {
            if (ability != null) _abilityDone.Remove(ability);
        }

        /// <summary>
        /// A vanilla ability carries its capstone payload as CapstonePerkMods, deep-copied
        /// from the shared config at Awake and authored for the perk the ability shipped
        /// with - which adoption has just changed. The rule: a class that authors effects on
        /// its capstone banner owns the payload and the ability's own is dropped for this
        /// instance; a class that leaves that banner empty keeps the vanilla payload, which
        /// is what makes a verbatim recreation faithful.
        ///
        /// The log line is the only record of what that payload contained - it is asset data,
        /// absent from every dump, and this is the moment the game has it resolved.
        /// </summary>
        private static void TakeOverCapstonePayload(ClassAbility ca, ClassRecord rec)
        {
            try
            {
                PerkSpec banner = ClassRegistry.BannerSpec(rec, 5);
                if (banner == null || !HasEffects(banner)) return;
                if (ca.CapstonePerkMods == null || ca.CapstonePerkMods.Count == 0) return;

                List<string> dropped = new List<string>();
                foreach (StatMod m in ca.CapstonePerkMods)
                {
                    if (m == null) continue;
                    string statName = StatRegistry.NameOf(m.Type) ?? m.Type.ToString();
                    FloatModifier fm = m.Mod as FloatModifier;
                    IntModifier im = m.Mod as IntModifier;
                    dropped.Add(statName + " " +
                                (m.Mod != null ? m.Mod.Type.ToString() : "?") + " " +
                                (fm != null ? fm.Amount.ToString("0.###")
                                            : im != null ? im.Amount.ToString() : "?"));
                }
                ca.CapstonePerkMods.Clear();

                if (Plugin.Verbose) Plugin.Log.LogInfo("Ectype: '" + rec.Manifest.classId + "' authors its own capstone " +
                                                       "perk '" + banner.key + "'; dropped the adopted " +
                                                       ca.GetType().Name + " capstone payload: " +
                                                       string.Join(", ", dropped.ToArray()) + ".");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: deciding the capstone payload for " +
                                    ca.GetType().Name + " failed - the vanilla payload stays, " +
                                    "and it may stack with the class's authored effects: " + e);
            }
        }

        private static bool HasEffects(PerkSpec spec)
        {
            if (spec.mods == null) return false;
            foreach (List<EffectSpec> level in spec.mods)
                if (level != null && level.Count > 0) return true;
            return false;
        }



        private static string HeroKey(ClassRecord rec)
        {
            return rec.Manifest.hero != null ? rec.Manifest.hero.key : "(none)";
        }

        // ---------------------------------------------------------------- fabricator unlock

        /// <summary>
        /// Repoint a vanilla PlayerPerk unlock at one of our perks, when the perk it names
        /// today belongs to a tree this same class suppressed - the safety property, since a
        /// class can only take over unlocks its own suppression broke. Returns false for an
        /// unlock claimed by anything else, and also while suppression has not resolved,
        /// which is why the caller defers rather than warning on the first false.
        /// </summary>
        internal static bool TryAdoptUnlock(ClassRecord rec, string perkKey,
                                            GUIDUnion itemGuid, GUIDUnion ourPerkGuid)
        {
            UnlockItemDef def = FindPlayerPerkUnlock(itemGuid);
            if (def == null) return false;

            UnlockOptions opts = def.UnlockOptions;
            if (opts.RequiredPerk == null || opts.RequiredPerk.AssetGuid.IsNull) return false;

            ClassRecord adopter = Suppression.AdopterOf(opts.RequiredPerk.AssetGuid);
            // Any class whose own suppression orphaned the unlock may take it, not only the
            // first requester of the tree (CoSuppression).
            if (!ReferenceEquals(adopter, rec) &&
                !CoSuppression.Suppresses(rec, opts.RequiredPerk.AssetGuid)) return false;

            string wasHex = opts.RequiredPerk.AssetGuid.AsHex();
            // UnlockOptions is a struct behind a property - mutate a copy and assign back,
            // or the write lands on a temporary and silently does nothing.
            opts.RequiredPerk = new PerkBuffRef(ourPerkGuid, "") { IsRuntime = true };
            def.UnlockOptions = opts;

            if (Plugin.Verbose) Plugin.Log.LogInfo("Ectype: '" + rec.Manifest.className + "' perk '" + perkKey +
                                                   "' adopted the vanilla unlock of item " + itemGuid.AsHex() +
                                                   " (was gated on suppressed perk " + wasHex + ").");
            return true;
        }

        /// <summary>
        /// An unlock whose adoption could not be decided yet, because suppression waits for
        /// the perk-tree refs to populate while registration waits only for the asset
        /// containers, so a class can finish registering first and never revisit the question.
        /// </summary>
        private struct PendingUnlock
        {
            public ClassRecord Rec;
            public string PerkKey;
            public GUIDUnion Item;
            public GUIDUnion OurPerk;
        }

        private static readonly List<PendingUnlock> _pendingUnlocks = new List<PendingUnlock>();

        internal static void DeferUnlock(ClassRecord rec, string perkKey,
                                         GUIDUnion itemGuid, GUIDUnion ourPerkGuid)
        {
            _pendingUnlocks.Add(new PendingUnlock
            {
                Rec = rec, PerkKey = perkKey, Item = itemGuid, OurPerk = ourPerkGuid,
            });
        }

        /// <summary>
        /// Decide every deferred unlock, once suppression can answer. Called from the
        /// registration driver, so it runs on the same retry cadence everything else does;
        /// the list is empty on all but the first few passes.
        /// </summary>
        internal static void ResolveDeferredUnlocks()
        {
            if (_pendingUnlocks.Count == 0 || !Suppression.Resolved) return;
            foreach (PendingUnlock p in _pendingUnlocks)
            {
                if (TryAdoptUnlock(p.Rec, p.PerkKey, p.Item, p.OurPerk)) continue;
                Plugin.Log.LogWarning("Ectype: '" + p.Rec.Manifest.className + "' perk '" +
                                      p.PerkKey + "' unlocks item " + p.Item.AsHex() +
                                      ", which already has an UnlockItemDef from vanilla or " +
                                      "another mod. Skipped: this perk grants no fabricator " +
                                      "unlock this session.");
            }
            _pendingUnlocks.Clear();
        }

        /// <summary>
        /// The item's unlock def, looked up through the same enumeration the fabricator uses
        /// (UnlockContainer.GetItemsLockedBy over the PlayerPerk criteria), so anything this
        /// finds is by construction something the fabricator will re-read.
        /// </summary>
        private static UnlockItemDef FindPlayerPerkUnlock(GUIDUnion itemGuid)
        {
            UnlockContainer container = UnlockContainer.Instance;
            if (container == null) return null;
            try
            {
                List<UnlockItemDef> defs =
                    container.GetItemsLockedBy(UnlockCriteriaType.PlayerPerk);
                if (defs == null) return null;
                foreach (UnlockItemDef d in defs)
                    if (d != null && !d.AssetGuid.IsNull && d.AssetGuid.Equals(itemGuid))
                        return d;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: enumerating PlayerPerk unlocks failed: " +
                                      e.Message);
            }
            return null;
        }
    }
}
