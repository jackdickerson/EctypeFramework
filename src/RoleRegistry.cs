using System;
using System.Collections.Generic;
using System.Reflection;

using Gameplay.Perks;
using ResourceAssets;

namespace Ectype
{
    /// <summary>
    /// One registered role: manifest + owning assembly + everything the pipeline builds for
    /// it. Ordinals are allocated once for the whole set, sorted by roleId, so every client
    /// in a room agrees on them regardless of plugin load order.
    /// </summary>
    internal class RoleRecord
    {
        public RoleManifest Manifest;
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

        /// <summary>The HUD slot reports itself once per role, not once per selection.</summary>
        public bool SlotLogged;
    }

    /// <summary>
    /// Discovery and the public code-surface. Role mods that are data-only never touch this
    /// type; mods with abilities or computed unlocks call the Register* methods at plugin
    /// Awake (they hard-depend on this plugin, so their Awake runs after ours).
    /// </summary>
    public static class RoleRegistry
    {
        internal static readonly List<RoleRecord> Roles = new List<RoleRecord>();

        /// <summary>The manifest perk at a banner row (0 = hero, else the wide perk on that row), or null.</summary>
        internal static PerkSpec BannerSpec(RoleRecord rec, int row)
        {
            if (row == 0) return rec.Manifest.hero;
            foreach (PerkSpec p in rec.Manifest.perks)
                if (p.row == row && p.wide) return p;
            return null;
        }

        /// <summary>The registered PerkBuff at a banner row, or null when the row has no perk,
        /// the perk has no key, or it did not register.</summary>
        internal static PerkBuff BannerPerk(RoleRecord rec, int row)
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
        /// Attach an ability factory to a manifest-declared role. The factory must return a
        /// ClassAbility subclass; it is called once per character spawn and the instance is
        /// installed with its perk identities pointed at the role's hero / row-5 / row-7
        /// banner perks. Call at plugin Awake.
        /// </summary>
        public static void RegisterAbilityFactory(string roleId, Func<object> factory)
        {
            if (string.IsNullOrEmpty(roleId) || factory == null) return;
            _pendingAbilityFactories[roleId] = factory;
            Plugin.Log.LogInfo("Ectype: ability factory registered for '" + roleId + "'.");
        }

        /// <summary>
        /// The asset GUID a perk registers under, as 32 hex characters. Derived from
        /// (roleId, perk key) exactly as registration derives it, so role code can find
        /// its own perks - in PlayerPerkLoader.PerkBuffLevels, say - without pinning a
        /// literal GUID in the manifest. A perk whose manifest pins an explicit "guid"
        /// is not covered: ask the manifest for that one.
        /// </summary>
        public static string PerkGuid(string roleId, string perkKey)
        {
            return Guids.PerkGuid(roleId, perkKey);
        }

        /// <summary>
        /// True while a role is registered and nothing suppresses it in the current room:
        /// neither the host's per-role toggle nor another active role's suppressRoles. The
        /// host's toggle travels as a room property applied on every client and the rest is
        /// manifest data, so every client in the room gives the same answer.
        /// </summary>
        public static bool IsAvailableInRoom(string roleId)
        {
            RoleRecord rec = Find(roleId);
            return rec != null && rec.Registered && !Suppression.IsRoleSuppressed(roleId);
        }

        /// <summary>Override the manifest's unlock criteria with code. Reserved surface.</summary>
        public static void RegisterUnlockPredicate(string roleId, Func<bool> predicate)
        {
            if (string.IsNullOrEmpty(roleId) || predicate == null) return;
            _pendingUnlockPredicates[roleId] = predicate;
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
                if (asm == typeof(RoleRegistry).Assembly) continue;

                foreach (RoleManifest m in ManifestLoader.LoadFrom(asm))
                {
                    RoleRecord dup = Find(m.roleId);
                    if (dup != null)
                    {
                        Plugin.Log.LogError("Ectype: duplicate roleId '" + m.roleId +
                                            "' in " + asm.GetName().Name + " (already " +
                                            "provided by " +
                                            dup.SourceAssembly.GetName().Name +
                                            ") - the duplicate is skipped.");
                        continue;
                    }
                    Roles.Add(new RoleRecord { Manifest = m, SourceAssembly = asm });
                }
            }

            // Ordinal comparison, never culture-sensitive: culture must not enter into
            // cross-client agreement.
            Roles.Sort((a, b) => string.CompareOrdinal(a.Manifest.roleId, b.Manifest.roleId));
            for (int i = 0; i < Roles.Count; i++)
                Roles[i].Ordinal = EctypePluginInfo.FIRST_TREE_ORDINAL + i;

            foreach (RoleRecord rec in Roles)
            {
                Func<object> f;
                if (_pendingAbilityFactories.TryGetValue(rec.Manifest.roleId, out f))
                    rec.AbilityFactory = f;
                Func<bool> u;
                if (_pendingUnlockPredicates.TryGetValue(rec.Manifest.roleId, out u))
                    rec.UnlockPredicate = u;
                foreach (string hex in rec.Manifest.suppress)
                    Suppression.Request(hex, rec.Manifest.roleId);
                foreach (string id in rec.Manifest.suppressRoles)
                {
                    if (string.Equals(id, rec.Manifest.roleId, StringComparison.OrdinalIgnoreCase))
                        Plugin.Log.LogError("Ectype: role '" + rec.Manifest.roleId +
                                            "' lists itself in suppressRoles - entry ignored.");
                    else if (Find(id) == null)
                        Plugin.Log.LogWarning("Ectype: role '" + rec.Manifest.roleId +
                                              "' suppresses role '" + id + "', which is not " +
                                              "installed - entry ignored.");
                }
            }

            Discovered = true;
            Plugin.Log.LogInfo("Ectype: discovered " + Roles.Count + " role(s)" +
                               (Roles.Count > 0 ? ": " + Summary() : "."));
            WarnOnAccentCollisions();
        }

        /// <summary>
        /// Independently-authored role mods cannot coordinate accents, so the framework is
        /// the only place this lint can live. Componentwise distance under 90/765 reads as
        /// the same color at gene-tree scale.
        /// </summary>
        private static void WarnOnAccentCollisions()
        {
            for (int i = 0; i < Roles.Count; i++)
            {
                UnityEngine.Color a;
                if (string.IsNullOrEmpty(Roles[i].Manifest.color) ||
                    !UnityEngine.ColorUtility.TryParseHtmlString(Roles[i].Manifest.color, out a))
                    continue;
                for (int j = i + 1; j < Roles.Count; j++)
                {
                    UnityEngine.Color b;
                    if (string.IsNullOrEmpty(Roles[j].Manifest.color) ||
                        !UnityEngine.ColorUtility.TryParseHtmlString(Roles[j].Manifest.color, out b))
                        continue;
                    int dist = (int)(255f * (System.Math.Abs(a.r - b.r) +
                                             System.Math.Abs(a.g - b.g) +
                                             System.Math.Abs(a.b - b.b)));
                    if (dist < 90)
                        Plugin.Log.LogWarning("Ectype: roles '" +
                            Roles[i].Manifest.roleName + "' (" + Roles[i].Manifest.color +
                            ") and '" + Roles[j].Manifest.roleName + "' (" +
                            Roles[j].Manifest.color + ") have nearly identical accent " +
                            "colors (distance " + dist + " < 90) - the trees will be hard " +
                            "to tell apart.");
                }
            }
        }

        internal static RoleRecord Find(string roleId)
        {
            foreach (RoleRecord rec in Roles)
                if (string.Equals(rec.Manifest.roleId, roleId,
                                  StringComparison.OrdinalIgnoreCase)) return rec;
            return null;
        }

        internal static bool IsOurGuid(GUIDUnion g)
        {
            if (g.IsNull) return false;
            foreach (RoleRecord rec in Roles)
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
            foreach (RoleRecord rec in Roles)
                parts.Add(rec.Manifest.roleName + " (tree " + rec.Ordinal + ", " +
                          rec.SourceAssembly.GetName().Name + ")");
            return string.Join("; ", parts.ToArray());
        }
    }
}
