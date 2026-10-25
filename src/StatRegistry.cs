using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

using Client.Player.Interactions;
using Gameplay.Utilities;

namespace Ectype
{
    /// <summary>
    /// The stat alias registry. Vanilla aliases are the game's own StatType names, enumerated
    /// by reflection over StatType's static fields - the FULL vanilla surface, not just what
    /// vanilla perks use, and it tracks game updates for free. A raw int id is accepted as
    /// the escape hatch. Custom stats declared in a manifest register a StatStructure into
    /// StatTable's lookup so tooltips render them by name; their ids derive deterministically
    /// from (roleId, statName). A custom stat is inert until code reads it - logged so a
    /// declared-but-unconsumed stat is visible.
    /// </summary>
    internal static class StatRegistry
    {
        /// <summary>True when the collection or any child collection registers stat <paramref name="id"/>.</summary>
        internal static bool CollectionHasStat(StatCollection col, int id)
        {
            return CollectionHasStat(col, id, 0);
        }

        // Depth-capped so a malformed (cyclic) collection graph cannot recurse forever.
        private static bool CollectionHasStat(StatCollection col, int id, int depth)
        {
            if (col == null || depth > 32) return false;
            if (col.Stats != null && col.Stats.ContainsKey(id)) return true;
            if (col.ChildCollections != null)
                foreach (StatTagCollection child in col.ChildCollections)
                    if (CollectionHasStat(child, id, depth + 1)) return true;
            return false;
        }

        private static Dictionary<string, int> _vanilla;          // alias -> id
        private static readonly Dictionary<int, string> _customOwners =
            new Dictionary<int, string>();                        // id -> "roleId/name"
        private static readonly Dictionary<int, string> _customOrigins =
            new Dictionary<int, string>();                        // id -> who declared it

        public static int VanillaCount
        {
            get { EnsureVanilla(); return _vanilla.Count; }
        }

        /// <summary>Reverse lookup for dumps: vanilla name, custom "roleId/name", or null.</summary>
        public static string NameOf(int id)
        {
            EnsureVanilla();
            foreach (KeyValuePair<string, int> kv in _vanilla)
                if (kv.Value == id) return kv.Key;
            string custom;
            return _customOwners.TryGetValue(id, out custom) ? custom : null;
        }

        private static void EnsureVanilla()
        {
            if (_vanilla != null) return;
            Dictionary<string, int> map =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (FieldInfo f in typeof(StatType).GetFields(
                         BindingFlags.Public | BindingFlags.Static))
            {
                if (!typeof(StatType).IsAssignableFrom(f.FieldType)) continue;
                StatType st = f.GetValue(null) as StatType;
                if (st == null) continue;
                map[st.Name] = st.Id;
            }
            _vanilla = map;
            Plugin.Log.LogInfo("Ectype: stat registry holds " + map.Count +
                               " vanilla stat aliases.");
        }

        /// <summary>
        /// Resolve a manifest "stat" string against, in order: the role's own custom stats,
        /// the framework-owned stats, a stat declared in a mod's stats file by its qualified
        /// "owner/name", the vanilla StatType names, a raw int literal. Throws
        /// with the failing name - the caller turns that into one named error and an empty
        /// level, never a broken perk. That order is what keeps a role from being broken by
        /// a name the framework or the game adds later.
        /// </summary>
        public static int Resolve(string stat, Dictionary<string, int> customStats)
        {
            if (string.IsNullOrEmpty(stat)) throw new Exception("effect names no stat");
            int id;
            if (customStats != null && customStats.TryGetValue(stat, out id)) return id;
            if (FrameworkStats.TryGet(stat, out id)) return id;
            if (ModStats.TryGetDeclaredId(stat, out id)) return id;
            EnsureVanilla();
            if (_vanilla.TryGetValue(stat, out id)) return id;
            if (int.TryParse(stat, out id)) return id;
            throw new Exception("unknown stat '" + stat + "' (not a StatType name, not a " +
                                "framework-owned stat, not a custom stat of this role, not " +
                                "a declared owner/name, not an int id)");
        }

        public static ModifierType ResolveModifierType(string type)
        {
            switch ((type ?? "").Trim().ToLowerInvariant())
            {
                case "percent": return ModifierType.AdditiveMultiplier;
                case "scalar": return ModifierType.ExponentialMultiplier;
                case "flat": return ModifierType.PrimaryAddend;
                case "flat2": return ModifierType.SecondaryAddend;
            }
            ModifierType parsed;
            if (Enum.TryParse(type, true, out parsed)) return parsed;
            throw new Exception("unknown modifier type '" + type + "' (percent, scalar, " +
                                "flat, flat2, or a ModifierType name)");
        }

        /// <summary>
        /// Register one custom stat into the live StatTable (LUT and authored list, so a
        /// lookup rebuild keeps it). Re-runnable; collision with a different owner, or with the
        /// same owner and name declared by someone else, is a named error. Returns the derived
        /// id. declaredBy names who declares it, for the collision message: null for a role
        /// manifest or the framework, the file for a mod stats file.
        /// </summary>
        public static int RegisterCustomStat(string roleId, CustomStatSpec spec, Sprite icon,
                                             string declaredBy = null)
        {
            StatTable table = DataTable<StatTable>.Instance;
            if (table == null) throw new Exception("StatTable not loaded yet");

            int id = Guids.StatId(roleId, spec.name);
            string owner = roleId + "/" + spec.name;

            string existingOwner;
            if (_customOwners.TryGetValue(id, out existingOwner))
            {
                string existingBy;
                _customOrigins.TryGetValue(id, out existingBy);
                if (existingOwner == owner && existingBy == declaredBy) return id;   // re-entry
                if (existingOwner == owner)
                    throw new Exception("custom stat " + owner + " is declared by " +
                                        (declaredBy ?? "a role manifest") + " and by " +
                                        (existingBy ?? "a role manifest"));
                throw new Exception("custom stat id collision: " + owner + " and " +
                                    existingOwner + " derive the same id " + id);
            }
            EnsureVanilla();
            if (table.StatLUT.ContainsKey(id))
                throw new Exception("custom stat " + owner + " derived id " + id +
                                    " already present in StatTable");

            StatStructure structure = new StatStructure
            {
                ModType = id,
                InverseBuffValue = spec.lowerIsBetter,
                DisplayType = ParseDisplayType(spec.displayType),
                ContextInfo = ContextInfo.Create(
                    icon,
                    string.IsNullOrEmpty(spec.displayName) ? spec.name : spec.displayName,
                    spec.desc ?? "")
            };
            structure.ContextInfo.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(structure.ContextInfo);

            table.StatLUT[id] = structure;
            List<StatStructure> authored =
                Refl.GetField<List<StatStructure>>(table, "statStructures", null);
            if (authored != null && !authored.Contains(structure)) authored.Add(structure);

            _customOwners[id] = owner;
            _customOrigins[id] = declaredBy;
            if (Plugin.Verbose) Plugin.Log.LogInfo("Ectype: custom stat '" + owner + "' registered as id " + id +
                                                   ".");
            return id;
        }

        private static StatStructure.EStatDisplayType ParseDisplayType(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "value": return StatStructure.EStatDisplayType.Value;
                case "bool": return StatStructure.EStatDisplayType.Bool;
                default: return StatStructure.EStatDisplayType.Percentage;
            }
        }
    }
}
