using System;
using System.Collections.Generic;

using Gameplay.Tags;
using Gameplay.Utilities;

namespace Ectype
{
    /// <summary>
    /// Resolves manifest tag names to the live Gameplay.Tags.CsTag objects. The game matches
    /// tags by object reference, never by name or GUID, so a name must be resolved to the
    /// instance the game itself holds and a tag must never be constructed.
    /// </summary>
    internal static class TagRegistry
    {
        // Our own case-insensitive index over CsTagTable's backing list, because the game's
        // own TryGetTagByName lookup is case-SENSITIVE and every other manifest string match
        // in this framework folds case.
        private static Dictionary<string, CsTag> _byName;

        private static void EnsureIndex()
        {
            if (_byName != null) return;
            CsTagTable table = DataTable<CsTagTable>.Instance;
            if (table == null) throw new Exception("CsTagTable not loaded yet");
            List<CsTag> all = Refl.GetField<List<CsTag>>(table, "_allTags", null);
            if (all == null) throw new Exception("CsTagTable._allTags did not resolve");

            Dictionary<string, CsTag> map =
                new Dictionary<string, CsTag>(StringComparer.OrdinalIgnoreCase);
            foreach (CsTag tag in all)
            {
                if (tag == null || string.IsNullOrEmpty(tag.name)) continue;
                map[tag.name] = tag;
                // Mirror CsTagTable's own convenience alias: the "Tag_" prefix is optional.
                if (tag.name.Length > 4 && tag.name.StartsWith("Tag_", StringComparison.OrdinalIgnoreCase))
                    map[tag.name.Substring(4)] = tag;
            }
            _byName = map;
            Plugin.Log.LogInfo("Ectype: tag registry indexed " + all.Count + " CsTag asset(s).");
        }

        /// <summary>Unresolvable name -> named exception; caller decides whether that drops
        /// just the one tag (effect stays, narrower filter lost) or the whole effect.</summary>
        public static CsTag Resolve(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new Exception("empty tag name");
            EnsureIndex();
            CsTag tag;
            if (!_byName.TryGetValue(name, out tag) || tag == null)
                throw new Exception("unknown tag '" + name + "'");
            return tag;
        }

        /// <summary>Resolves a manifest tag-name list to CsTag[], skipping (and warning on)
        /// any name that does not resolve - the effect still registers, just with a narrower
        /// filter than authored. Returns null for an empty/absent list (ModTagConfiguration
        /// treats null and empty arrays the same as "no filter").</summary>
        public static CsTag[] ResolveList(List<string> names, string context)
        {
            if (names == null || names.Count == 0) return null;
            List<CsTag> resolved = new List<CsTag>(names.Count);
            foreach (string name in names)
            {
                try { resolved.Add(Resolve(name)); }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Ectype: " + context + ": tag '" + name +
                                          "' did not resolve (" + e.Message + ") - dropped " +
                                          "from this effect's filter.");
                }
            }
            return resolved.Count > 0 ? resolved.ToArray() : null;
        }

        public static InclusionCriteria ResolveCriteria(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "or": return InclusionCriteria.Or;
                case "": case "and": return InclusionCriteria.And;
                default:
                    Plugin.Log.LogWarning("Ectype: unknown tag criteria '" + s +
                                          "' - using 'and'.");
                    return InclusionCriteria.And;
            }
        }

        /// <summary>Builds a ModTagConfiguration from an EffectSpec's tag fields, or null if
        /// the effect declares no tag filtering at all (StatMod's 2-arg ctor already gives an
        /// empty-but-non-null ModTagConfiguration, which is the "no filter" vanilla shape).</summary>
        public static ModTagConfiguration Build(EffectSpec fx, string context)
        {
            CsTag[] required = ResolveList(fx.requiredTags, context);
            CsTag[] requiredLocal = ResolveList(fx.requiredLocalTags, context);
            CsTag[] disabling = ResolveList(fx.disablingTags, context);
            CsTag[] toAdd = ResolveList(fx.tagsToAdd, context);
            if (required == null && requiredLocal == null && disabling == null && toAdd == null)
                return null;

            return new ModTagConfiguration
            {
                RequiredTags = required,
                RequiredTagCriteria = ResolveCriteria(fx.requiredTagCriteria),
                RequiredLocalTags = requiredLocal,
                RequiredLocalTagCriteria = ResolveCriteria(fx.requiredLocalTagCriteria),
                DisablingTags = disabling,
                TagsToAdd = toAdd,
            };
        }
    }
}
