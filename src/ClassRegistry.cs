using System;
using System.Collections.Generic;
using System.Reflection;

using Gameplay.Perks;
using ResourceAssets;

namespace Ectype
{
    /// <summary>
    /// One registered class: manifest + owning assembly + everything the pipeline builds for
    /// it. Ordinals are allocated once for the whole set, sorted by classId, so every client
    /// in a room agrees on them regardless of plugin load order.
    /// </summary>
    internal class ClassRecord
    {
        public ClassManifest Manifest;
        public Assembly SourceAssembly;

        /// <summary>(EPerkTree) ordinal; also the injected PerkTreeVE's element name.</summary>
        public int Ordinal = -1;

        public GUIDUnion TreeGuid;
        public PerkTree Tree;
        public PerkTreeDef TreeDef;
        public bool TreeDefPublished;
        public bool ElementInjected;

        /// <summary>perk key -> asset GUID (hero included, key per manifest).</summary>
        public readonly Dictionary<string, GUIDUnion> PerkGuids =
            new Dictionary<string, GUIDUnion>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, PerkBuff> Perks =
            new Dictionary<string, PerkBuff>(StringComparer.OrdinalIgnoreCase);

        /// <summary>custom stat name -> registered stat id.</summary>
        public readonly Dictionary<string, int> CustomStats =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public bool Registered;
        public bool PermanentlyFailed;
        public int Attempts;
        public string Stage = "start";
        public string Status = "waiting for asset containers";

        public Func<object> AbilityFactory;      // consumed by AbilityWiring on spawn
        public Func<bool> UnlockPredicate;       // reserved: wired in a later work item

        /// <summary>Installed ability instances, keyed by the Opsive locomotion (typed as
        /// object, to avoid this file depending on Opsive types) they were installed onto.
        /// Every character this client sees gets one, own or networked, because Photon syncs
        /// ability state by array index. Entries are removed by the OnDestroy patch so a
        /// respawn reinstalls.</summary>
        public readonly Dictionary<object, ClassAbility> AbilityInstances =
            new Dictionary<object, ClassAbility>();
        public bool AbilityInstallFailed;

        /// <summary>The HUD slot reports itself once per class, not once per selection.</summary>
        public bool SlotLogged;
    }

    /// <summary>
    /// Discovery and the public code-surface. Class mods that are data-only never touch this
    /// type; mods with abilities or computed unlocks call the Register* methods at plugin
    /// Awake (they hard-depend on this plugin, so their Awake runs after ours).
    /// </summary>
    public static class ClassRegistry
    {
        internal static readonly List<ClassRecord> Classes = new List<ClassRecord>();

        /// <summary>The manifest perk at a banner row (0 = hero, else the wide perk on that row), or null.</summary>
        internal static PerkSpec BannerSpec(ClassRecord rec, int row)
        {
            if (row == 0) return rec.Manifest.hero;
            foreach (PerkSpec p in rec.Manifest.perks)
                if (p.row == row && p.wide) return p;
            return null;
        }

        /// <summary>The registered PerkBuff at a banner row, or null when the row has no perk,
        /// the perk has no key, or it did not register.</summary>
        internal static PerkBuff BannerPerk(ClassRecord rec, int row)
        {
            PerkSpec spec = BannerSpec(rec, row);
            if (spec == null || string.IsNullOrEmpty(spec.key)) return null;
            PerkBuff perk;
            return rec.Perks.TryGetValue(spec.key, out perk) ? perk : null;
        }
        private static readonly Dictionary<string, Func<object>> _pendingAbilityFactories =
            new Dictionary<string, Func<object>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Func<bool>> _pendingUnlockPredicates =
            new Dictionary<string, Func<bool>>(StringComparer.OrdinalIgnoreCase);

        internal static bool Discovered { get; private set; }
        internal static bool RegistrationClosed;

        // ------------------------------------------------------------------ public surface

        /// <summary>
        /// Attach an ability factory to a manifest-declared class. The factory must return a
        /// ClassAbility subclass; it is called once per character spawn and the instance is
        /// installed with its perk identities pointed at the class's hero / row-5 / row-7
        /// banner perks. Call at plugin Awake.
        /// </summary>
        public static void RegisterAbilityFactory(string classId, Func<object> factory)
        {
            if (string.IsNullOrEmpty(classId) || factory == null) return;
            _pendingAbilityFactories[classId] = factory;
            Plugin.Log.LogInfo("Ectype: ability factory registered for '" + classId + "'.");
        }

        /// <summary>
        /// The asset GUID a perk registers under, as 32 hex characters. Derived from
        /// (classId, perk key) exactly as registration derives it, so class code can find
        /// its own perks - in PlayerPerkLoader.PerkBuffLevels, say - without pinning a
        /// literal GUID in the manifest. A perk whose manifest pins an explicit "guid"
        /// is not covered: ask the manifest for that one.
        /// </summary>
        public static string PerkGuid(string classId, string perkKey)
        {
            return Guids.PerkGuid(classId, perkKey);
        }

        /// <summary>
        /// True while a class is registered and nothing suppresses it in the current room:
        /// neither the host's per-class toggle nor another active class's suppressClasses. The
        /// host's toggle travels as a room property applied on every client and the rest is
        /// manifest data, so every client in the room gives the same answer.
        /// </summary>
        public static bool IsAvailableInRoom(string classId)
        {
            ClassRecord rec = Find(classId);
            return rec != null && rec.Registered && !Suppression.IsClassSuppressed(classId);
        }

        /// <summary>Override the manifest's unlock criteria with code. Reserved surface.</summary>
        public static void RegisterUnlockPredicate(string classId, Func<bool> predicate)
        {
            if (string.IsNullOrEmpty(classId) || predicate == null) return;
            _pendingUnlockPredicates[classId] = predicate;
        }

        // ---------------------------------------------------------------------- discovery

        /// <summary>
        /// Scan every loaded BepInEx plugin assembly for embedded manifests, then allocate
        /// ordinals over the full sorted set. Runs once: a manifest appearing after this would
        /// get a different ordinal on different clients, hence RegistrationClosed.
        /// </summary>
        internal static void DiscoverOnce()
        {
            if (Discovered) return;
            if (RegistrationClosed)
            {
                Plugin.Log.LogError("Ectype: discovery requested after registration closed " +
                                    "(first terminal build) - refused.");
                return;
            }

            HashSet<Assembly> seen = new HashSet<Assembly>();
            foreach (KeyValuePair<string, BepInEx.PluginInfo> kv in
                     BepInEx.Bootstrap.Chainloader.PluginInfos)
            {
                BepInEx.PluginInfo info = kv.Value;
                if (info == null || info.Instance == null) continue;
                Assembly asm = info.Instance.GetType().Assembly;
                if (!seen.Add(asm)) continue;
                if (asm == typeof(ClassRegistry).Assembly) continue;

                foreach (ClassManifest m in ManifestLoader.LoadFrom(asm))
                {
                    ClassRecord dup = Find(m.classId);
                    if (dup != null)
                    {
                        Plugin.Log.LogError("Ectype: duplicate classId '" + m.classId +
                                            "' in " + asm.GetName().Name + " (already " +
                                            "provided by " +
                                            dup.SourceAssembly.GetName().Name +
                                            ") - the duplicate is skipped.");
                        continue;
                    }
                    Classes.Add(new ClassRecord { Manifest = m, SourceAssembly = asm });
                }
            }

            // Ordinal comparison, never culture-sensitive: culture must not enter into
            // cross-client agreement.
            Classes.Sort((a, b) => string.CompareOrdinal(a.Manifest.classId, b.Manifest.classId));
            for (int i = 0; i < Classes.Count; i++)
                Classes[i].Ordinal = EctypePluginInfo.FIRST_TREE_ORDINAL + i;

            foreach (ClassRecord rec in Classes)
            {
                Func<object> f;
                if (_pendingAbilityFactories.TryGetValue(rec.Manifest.classId, out f))
                    rec.AbilityFactory = f;
                Func<bool> u;
                if (_pendingUnlockPredicates.TryGetValue(rec.Manifest.classId, out u))
                    rec.UnlockPredicate = u;
                foreach (string hex in rec.Manifest.suppress)
                    Suppression.Request(hex, rec.Manifest.classId);
                foreach (string id in rec.Manifest.suppressClasses)
                {
                    if (string.Equals(id, rec.Manifest.classId, StringComparison.OrdinalIgnoreCase))
                        Plugin.Log.LogError("Ectype: class '" + rec.Manifest.classId +
                                            "' lists itself in suppressClasses - entry ignored.");
                    else if (Find(id) == null)
                        Plugin.Log.LogWarning("Ectype: class '" + rec.Manifest.classId +
                                              "' suppresses class '" + id + "', which is not " +
                                              "installed - entry ignored.");
                }
            }

            Discovered = true;
            Plugin.Log.LogInfo("Ectype: discovered " + Classes.Count + " class(es)" +
                               (Classes.Count > 0 ? ": " + Summary() : "."));
            WarnOnAccentCollisions();
        }

        /// <summary>
        /// Independently-authored class mods cannot coordinate accents, so the framework is
        /// the only place this lint can live. Componentwise distance under 90/765 reads as
        /// the same color at gene-tree scale.
        /// </summary>
        private static void WarnOnAccentCollisions()
        {
            for (int i = 0; i < Classes.Count; i++)
            {
                UnityEngine.Color a;
                if (string.IsNullOrEmpty(Classes[i].Manifest.color) ||
                    !UnityEngine.ColorUtility.TryParseHtmlString(Classes[i].Manifest.color, out a))
                    continue;
                for (int j = i + 1; j < Classes.Count; j++)
                {
                    UnityEngine.Color b;
                    if (string.IsNullOrEmpty(Classes[j].Manifest.color) ||
                        !UnityEngine.ColorUtility.TryParseHtmlString(Classes[j].Manifest.color, out b))
                        continue;
                    int dist = (int)(255f * (System.Math.Abs(a.r - b.r) +
                                             System.Math.Abs(a.g - b.g) +
                                             System.Math.Abs(a.b - b.b)));
                    if (dist < 90)
                        Plugin.Log.LogWarning("Ectype: classes '" +
                            Classes[i].Manifest.className + "' (" + Classes[i].Manifest.color +
                            ") and '" + Classes[j].Manifest.className + "' (" +
                            Classes[j].Manifest.color + ") have nearly identical accent " +
                            "colors (distance " + dist + " < 90) - the trees will be hard " +
                            "to tell apart.");
                }
            }
        }

        internal static ClassRecord Find(string classId)
        {
            foreach (ClassRecord rec in Classes)
                if (string.Equals(rec.Manifest.classId, classId,
                                  StringComparison.OrdinalIgnoreCase)) return rec;
            return null;
        }

        internal static bool IsOurGuid(GUIDUnion g)
        {
            if (g.IsNull) return false;
            foreach (ClassRecord rec in Classes)
            {
                if (g == rec.TreeGuid) return true;
                foreach (GUIDUnion pg in rec.PerkGuids.Values)
                    if (g == pg) return true;
            }
            return false;
        }

        internal static string Summary()
        {
            List<string> parts = new List<string>();
            foreach (ClassRecord rec in Classes)
                parts.Add(rec.Manifest.className + " (tree " + rec.Ordinal + ", " +
                          rec.SourceAssembly.GetName().Name + ")");
            return string.Join("; ", parts.ToArray());
        }
    }
}
