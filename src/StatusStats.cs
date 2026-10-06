using System;
using System.Collections.Generic;

using Gameplay.Utilities;

namespace Ectype
{
    /// <summary>One stat modifier a status def carries, resolved to a stat id on first use.</summary>
    internal sealed class StatusStatEntry
    {
        public string Alias;
        public ModifierType Type;
        public float Amount;
        public bool IsInt;
        public int ResolvedId = int.MinValue;   // MinValue = unresolved, MinValue+1 = failed
    }

    /// <summary>Stat-entry handling shared by character and enemy statuses.</summary>
    internal static class StatusStats
    {
        internal const int Failed = int.MinValue + 1;

        internal static void Add(List<StatusStatEntry> entries, string alias, string modifierType,
                                 float amount, bool isInt)
        {
            entries.Add(new StatusStatEntry
            {
                Alias = alias,
                Type = StatRegistry.ResolveModifierType(modifierType),
                Amount = amount,
                IsInt = isInt,
            });
        }

        /// <summary>Resolves once and caches; a failure is logged once and cached as <see cref="Failed"/>.</summary>
        internal static int Resolve(StatusStatEntry entry, string label)
        {
            if (entry.ResolvedId != int.MinValue) return entry.ResolvedId;
            try
            {
                entry.ResolvedId = StatRegistry.Resolve(entry.Alias, null);
            }
            catch (Exception e)
            {
                entry.ResolvedId = Failed;
                Plugin.Log.LogError("Ectype: " + label + " stat '" + entry.Alias +
                                    "' skipped: " + e.Message);
            }
            return entry.ResolvedId;
        }

        /// <summary>Fresh StatMod instances per call: the game keys removal on the modifier's
        /// Source field, which applying overwrites, so instances can never be shared between
        /// targets or stacks. <paramref name="anyMatched"/> is true when at least one id is
        /// registered on <paramref name="target"/>.</summary>
        internal static List<StatMod> BuildMods(List<StatusStatEntry> entries, float magnitude,
                                                StatCollection target, string label,
                                                out bool anyMatched)
        {
            List<StatMod> mods = new List<StatMod>();
            anyMatched = false;
            foreach (StatusStatEntry entry in entries)
            {
                int id = Resolve(entry, label);
                if (id == Failed) continue;   // already logged
                PrimitiveModifier mod = entry.IsInt
                    ? (PrimitiveModifier)new IntModifier(
                          (int)Math.Round(entry.Amount * magnitude), entry.Type)
                    : new FloatModifier(entry.Amount * magnitude, entry.Type);
                mods.Add(new StatMod(mod, id));
                if (StatRegistry.CollectionHasStat(target, id)) anyMatched = true;
            }
            return mods;
        }

        /// <summary>Runs a mod callback; the first throw per status key is logged, later ones are swallowed.</summary>
        internal static void SafeCb(Action a, Func<string, bool> warnOnce, string label, string key)
        {
            try { a(); }
            catch (Exception e)
            {
                if (warnOnce("cbfail:" + key))
                    Plugin.Log.LogError("Ectype: " + label + " '" + key + "' callback threw " +
                                        "(suppressing further reports): " + e);
            }
        }
    }
}
