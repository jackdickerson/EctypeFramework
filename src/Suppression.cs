using System;
using System.Collections.Generic;

using Gameplay.Perks;
using ResourceAssets;

namespace Ectype
{
    /// <summary>
    /// Manifest-declared suppression: a class mod may list vanilla perk-tree GUIDs whose
    /// trees should not exist while it is installed - the "this class replaces Gunner"
    /// mechanism. The union across every installed class mod applies process-wide.
    ///
    /// What suppression means, and where each part is enforced:
    /// - the tree's PerkTreeDef is filtered out of every PerkTreeController build and its
    ///   PerkTreeVE removed from the document, so neither terminal renders it (TreeInjector);
    /// - the tree is skipped by the role tally, so perks held in it stop making a player
    ///   count as that role (Patches: GetPlayerRole);
    /// - its perks are scrubbed from saved loadouts at load and before reapply - vanilla's
    ///   own degrade semantics, so the spent points return as unspent on the next profile
    ///   write (Patches: PerkLoadout ctor, ReApplyPerks).
    ///
    /// Hiding a tree orphans the payloads the game keys to its perks from code; Adoption
    /// re-homes them onto the suppressing class, from the perk -> adopter map this type owns.
    ///
    /// Entries that do not name a vanilla tree are ignored with a named warning: a single
    /// perk (removal is stock-tree edit surface, not suppression), a framework class
    /// (uninstall the mod instead), or nothing at all (a game update moved it). An ignored
    /// entry never fails the class - suppression degrades to "tree still present".
    /// </summary>
    internal static class Suppression
    {
        /// <summary>normalized 32-hex -> requesting classId (first requester wins the credit).</summary>
        private static readonly Dictionary<string, string> _requested =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> _trees =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _perks =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>normalized perk 32-hex -> the classId that suppressed the tree holding
        /// it. Suppressing a tree orphans the payloads the game keyed to its perks (the
        /// class ability, fabricator unlocks); this is the map Adoption re-homes them by.</summary>
        private static readonly Dictionary<string, string> _perkAdopter =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _scrubReported =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Element names ("Gunner", ...) of suppressed trees, for removal from the
        /// terminal documents. The game names each tree's PerkTreeVE by its EPerkTree.</summary>
        internal static readonly List<string> TreeElementNames = new List<string>();

        /// <summary>classIds suppressed by another active class's suppressClasses. Host
        /// suppression lives in HostSuppression; IsClassSuppressed is the union.</summary>
        private static readonly HashSet<string> _classSuppressed =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool _resolved;
        public static string Status = "not resolved yet";

        public static bool HasSuppressedTrees { get { return _resolved && _trees.Count > 0; } }

        /// <summary>True once Resolve has settled. Until then "not suppressed" means "not
        /// known yet", which is a different answer and callers that act on it must wait.</summary>
        public static bool Resolved { get { return _resolved; } }

        /// <summary>Called at discovery for each manifest suppress entry (already normalized
        /// to 32-hex by manifest validation).</summary>
        public static void Request(string hex, string classId)
        {
            if (string.IsNullOrEmpty(hex)) return;
            if (!_requested.ContainsKey(hex)) _requested[hex] = classId;
        }

        /// <summary>
        /// Container-gated and re-runnable; settles once the perk-tree refs are populated,
        /// which the registration retry loop reaches long before the first terminal build
        /// consumes it. Deriving more than once is Rederive's job, not this gate's.
        /// </summary>
        public static void Resolve()
        {
            if (_resolved) return;
            if (_requested.Count == 0 && !AnyClassRequests())
            {
                _resolved = true;
                Status = "nothing to suppress";
                return;
            }

            PerkTreeContainer tc = PerkTreeContainer.Instance;
            PerkBuffContainer pc = PerkBuffContainer.Instance;
            if (tc == null || pc == null) return;
            bool populated = false;
            foreach (PerkTreeRef r in tc.GetPerkRefs()) { if (r != null) { populated = true; break; } }
            if (!populated) return;

            Rederive(tc, pc);
            _resolved = true;
        }

        /// <summary>
        /// Called by HostSuppression whenever the host-suppressed class set changes, so a
        /// vanilla tree that class X's manifest suppresses stops being suppressed while X
        /// itself is host-suppressed, unless some other still-active class also claims it.
        /// No-ops before the first Resolve(), which reads current HostSuppression state itself.
        /// </summary>
        public static void OnHostSuppressionChanged()
        {
            if (!_resolved) return;
            PerkTreeContainer tc = PerkTreeContainer.Instance;
            PerkBuffContainer pc = PerkBuffContainer.Instance;
            if (tc == null || pc == null) return;
            Rederive(tc, pc);
        }

        /// <summary>
        /// Rebuilds _trees / _perks / _perkAdopter / TreeElementNames from _requested (the
        /// manifest-declared vanilla-tree suppressions, skipping any whose requesting class
        /// is currently host-suppressed) plus every host-suppressed ectype class's own tree
        /// and perk GUIDs, folded into the SAME sets so every consumer treats the two cases
        /// alike. An ectype class's own perks get no adopter entry - there is no replacement
        /// class to re-home onto, so those payloads go dead.
        /// </summary>
        private static void Rederive(PerkTreeContainer tc, PerkBuffContainer pc)
        {
            _trees.Clear();
            _perks.Clear();
            _perkAdopter.Clear();
            TreeElementNames.Clear();
            DeriveClassSuppression();

            List<string> suppressedNames = new List<string>();
            int ignored = 0;
            foreach (KeyValuePair<string, string> kv in _requested)
            {
                if (IsClassSuppressed(kv.Value)) continue;
                GUIDUnion guid = new GUIDUnion(kv.Key);
                if (ClassRegistry.IsOurGuid(guid))
                {
                    ignored++;
                    Plugin.Log.LogWarning("Ectype: suppress entry " + kv.Key + " (from '" +
                                          kv.Value + "') names a framework-registered class " +
                                          "asset - name its classId in suppressClasses " +
                                          "instead. Entry ignored.");
                    continue;
                }
                if (tc.HasItem(guid))
                {
                    PerkTreeDef def = tc.GetAssetDefById(guid, true);
                    if (def == null || def.Asset == null || def.Asset.Perks == null)
                    {
                        ignored++;
                        Plugin.Log.LogWarning("Ectype: suppress entry " + kv.Key + " (from '" +
                                              kv.Value + "') is a tree whose def or asset " +
                                              "did not resolve - entry ignored.");
                        continue;
                    }
                    string treeName = Refl.GetField(def, "perkTree", (EPerkTree)0).ToString();
                    _trees.Add(kv.Key);
                    TreeElementNames.Add(treeName);
                    int perkCount = 0;
                    foreach (PerkBuffRef pref in def.Asset.Perks)
                    {
                        if (pref == null || pref.AssetGuid.IsNull) continue;
                        string phex = pref.AssetGuid.AsHex();
                        _perks.Add(phex);
                        _perkAdopter[phex] = kv.Value;
                        perkCount++;
                    }
                    suppressedNames.Add(treeName);
                    Plugin.Log.LogInfo("Ectype: suppressing vanilla tree '" + treeName +
                                       "' (" + kv.Key + ", " + perkCount + " perks) at the " +
                                       "request of '" + kv.Value + "'.");
                }
                else if (pc.HasItem(guid))
                {
                    ignored++;
                    Plugin.Log.LogWarning("Ectype: suppress entry " + kv.Key + " (from '" +
                                          kv.Value + "') names a single perk; only whole trees " +
                                          "can be suppressed. Entry ignored.");
                }
                else
                {
                    ignored++;
                    Plugin.Log.LogWarning("Ectype: suppress entry " + kv.Key + " (from '" +
                                          kv.Value + "') resolves to nothing in this game " +
                                          "version - entry ignored, nothing suppressed.");
                }
            }

            // GUIDs come from the manifest, not the record: this can run before the class
            // has registered, while rec.TreeGuid and rec.PerkGuids are still empty.
            int hostSuppressedTrees = 0;
            foreach (ClassRecord rec in ClassRegistry.Classes)
            {
                ClassManifest m = rec.Manifest;
                if (!IsClassSuppressed(m.classId)) continue;
                _trees.Add(new GUIDUnion(Guids.TreeGuid(m.classId)).AsHex());
                hostSuppressedTrees++;
                if (m.hero != null) _perks.Add(PerkHex(m.classId, m.hero));
                foreach (PerkSpec p in m.perks)
                    if (p != null) _perks.Add(PerkHex(m.classId, p));
            }

            // The two populations are reported separately, so the count is over the named
            // trees only - _trees also holds the host-suppressed ones the suffix covers.
            string ignoredSuffix = ignored > 0 ? " (" + ignored + " ignored)" : "";
            string hostSuffix = hostSuppressedTrees > 0
                ? hostSuppressedTrees + " suppressed class(es)" : "";
            if (_trees.Count == 0)
                Status = "no trees suppressed" +
                         (ignored > 0 ? " (" + ignored + " entr" +
                                        (ignored == 1 ? "y" : "ies") + " ignored)" : "");
            else if (suppressedNames.Count == 0)
                Status = hostSuffix + ignoredSuffix;
            else
                Status = suppressedNames.Count + " tree(s) suppressed: " +
                         string.Join(", ", suppressedNames.ToArray()) +
                         (hostSuffix.Length > 0 ? " + " + hostSuffix : "") +
                         ignoredSuffix;
        }

        /// <summary>True when the host suppressed this class, or a class that is not itself
        /// suppressed lists it in suppressClasses. The manifest half is known only once
        /// Resolve has run.</summary>
        public static bool IsClassSuppressed(string classId)
        {
            if (string.IsNullOrEmpty(classId)) return false;
            return HostSuppression.IsClassSuppressed(classId) || _classSuppressed.Contains(classId);
        }

        private static bool AnyClassRequests()
        {
            foreach (ClassRecord rec in ClassRegistry.Classes)
                if (rec.Manifest.suppressClasses.Count > 0) return true;
            return false;
        }

        private static string PerkHex(string classId, PerkSpec spec)
        {
            return new GUIDUnion(string.IsNullOrEmpty(spec.guid)
                ? Guids.PerkGuid(classId, spec.key) : spec.guid).AsHex();
        }

        /// <summary>
        /// A class is suppressed when the host suppressed it, or when a class that is itself
        /// active lists it. Each answer depends on its requesters' answers, so it is resolved
        /// depth-first over the request graph in classId order. A cycle (A lists B, B lists
        /// A) is broken by treating the class re-entered first as active; the walk order is
        /// the sorted class list, so every client breaks it the same way.
        /// </summary>
        private static void DeriveClassSuppression()
        {
            _classSuppressed.Clear();
            Dictionary<string, bool> state =
                new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ClassRecord rec in ClassRegistry.Classes)
                if (ClassSuppressedBy(rec.Manifest.classId, state, visiting))
                    _classSuppressed.Add(rec.Manifest.classId);
        }

        private static bool ClassSuppressedBy(string id, Dictionary<string, bool> state,
                                              HashSet<string> visiting)
        {
            bool known;
            if (state.TryGetValue(id, out known)) return known;
            if (HostSuppression.IsClassSuppressed(id)) { state[id] = true; return true; }
            if (!visiting.Add(id))
            {
                Plugin.Log.LogWarning("Ectype: suppressClasses cycle through '" + id +
                                      "' - treated as active.");
                return false;
            }
            bool result = false;
            foreach (ClassRecord other in ClassRegistry.Classes)
            {
                string by = other.Manifest.classId;
                if (string.Equals(by, id, StringComparison.OrdinalIgnoreCase)) continue;
                bool lists = false;
                foreach (string t in other.Manifest.suppressClasses)
                    if (string.Equals(t, id, StringComparison.OrdinalIgnoreCase)) { lists = true; break; }
                if (!lists || ClassSuppressedBy(by, state, visiting)) continue;
                result = true;
                Plugin.Log.LogInfo("Ectype: suppressing class '" + id + "' at the request of '" +
                                   by + "'.");
                break;
            }
            visiting.Remove(id);
            state[id] = result;
            return result;
        }

        public static bool IsSuppressedTree(GUIDUnion g)
        {
            return _resolved && _trees.Count > 0 && !g.IsNull && _trees.Contains(g.AsHex());
        }

        public static bool IsSuppressedPerk(GUIDUnion g)
        {
            return _resolved && _perks.Count > 0 && !g.IsNull && _perks.Contains(g.AsHex());
        }

        /// <summary>
        /// True for a perk of a suppressed vanilla tree - the only suppressed perks the
        /// load-time scrub removes from a saved loadout. A suppressed framework class's perks
        /// stay in the save, because that suppression can lift mid-session; SuppressionOverlay
        /// hides them instead.
        /// </summary>
        public static bool IsScrubbedPerk(GUIDUnion g)
        {
            return _resolved && _perkAdopter.Count > 0 && !g.IsNull &&
                   _perkAdopter.ContainsKey(g.AsHex());
        }

        /// <summary>
        /// The class that suppressed the tree this perk belonged to, or null. Adoption's
        /// entry point: a payload the game still keys to this perk now belongs to that class.
        /// </summary>
        public static ClassRecord AdopterOf(GUIDUnion perkGuid)
        {
            if (!_resolved || _perkAdopter.Count == 0 || perkGuid.IsNull) return null;
            string classId;
            if (!_perkAdopter.TryGetValue(perkGuid.AsHex(), out classId)) return null;
            return ClassRegistry.Find(classId);
        }

        /// <summary>Log-once helper for the scrub paths; true the first time a GUID is seen.</summary>
        public static bool ReportScrub(string hex)
        {
            return _scrubReported.Add(hex);
        }
    }
}
