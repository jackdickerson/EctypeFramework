using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace Ectype
{
    /// <summary>
    /// The v2 role manifest: a JSON document embedded in a role mod's assembly under a
    /// resource name ending in "ectype-role.json". Everything a data-only role needs; code
    /// callbacks (ability factories, unlock predicates) register separately through
    /// RoleRegistry.
    /// </summary>
    public class RoleManifest
    {
        public int contractVersion;

        /// <summary>The oldest framework version this role works with ("0.18.0"), stamped by
        /// the packer from the features the role uses. A newer requirement than this build
        /// rejects the role by name. Absent = no minimum.</summary>
        public string minFrameworkVersion;

        /// <summary>Namespaced stable id, conventionally the same string as the plugin GUID
        /// (e.g. "jack.voidcrew.ectype.testrole"). Seed for every derived GUID and the
        /// ordinal sort key across all role mods - never rename after shipping.</summary>
        public string roleId;

        public string roleName;
        public string roleDesc;

        /// <summary>Icon key for the role icon the crew HUD and lobby draw for this role, and
        /// the tree's own icon: an embedded PNG resource name (without extension) in the same
        /// assembly. Optional; the HUD falls back to the placeholder, the tree to the donor.</summary>
        public string roleIcon;

        /// <summary>PerkBuffContainer GUID whose ContextInfo icon the ROLE borrows when
        /// "roleIcon" names nothing loadable. Same borrow-by-reference mechanism as a perk's
        /// iconDonor; for a role cloned from a stock one, the stock hero perk's GUID is the
        /// icon the original role shows.</summary>
        public string roleIconDonor;

        /// <summary>The role accent color as an HTML hex string ("#3FA7A0"), applied inline
        /// because a stock tree's tint comes from name-keyed USS selectors that no longer
        /// match. Omitted or unparseable → the tree renders untinted (white) with a warning;
        /// a color whose R+G+B sum is 700+ (of 765) is warned as too close to white.</summary>
        public string color;

        /// <summary>The row-0 hero perk. Its DisplayType.Hero at row 0 is what makes the
        /// tree a role.</summary>
        public PerkSpec hero;

        public List<PerkSpec> perks = new List<PerkSpec>();

        public List<CustomStatSpec> customStats = new List<CustomStatSpec>();

        /// <summary>Vanilla perk-tree GUIDs whose trees should not exist while this role is
        /// installed - the "this role replaces Gunner" mechanism. Validation normalizes the
        /// entries to 32-hex; resolution and enforcement live in Suppression.</summary>
        public List<string> suppress = new List<string>();

        /// <summary>roleIds of other framework roles whose trees should not exist while
        /// this role is installed and not itself suppressed. Keyed on roleId, the unique
        /// and stable identity; roleName is display text. Resolution lives in
        /// Suppression.</summary>
        public List<string> suppressRoles = new List<string>();
    }

    public class PerkSpec
    {
        /// <summary>Stable key within the role; the perk GUID derives from
        /// (roleId, key) unless "guid" pins one explicitly.</summary>
        public string key;
        public string guid;

        public string name;
        public string desc;
        public string icon;

        /// <summary>PerkBuffContainer GUID (32 hex, dashes allowed) whose ContextInfo icon
        /// this perk borrows when "icon" names nothing loadable - shared by reference, never
        /// copied. Falls through to the framework's square or wide placeholder
        /// when unset or unresolvable.</summary>
        public string iconDonor;

        public int row;
        public int col;
        public bool wide;

        /// <summary>Where the effects land: "character" (always-on, on the player),
        /// "seat" (the module being manned, applied on sit / removed on stand), or
        /// "seatShip" (the ship's stat collection while seated in any chair - the
        /// all-installed-weapons pattern), or "characterShip" (the ship's stat collection for
        /// every holder, seated or not), "characterAboard" (the ship the holder is aboard, when
        /// it is on the holder's side), "characterFleet" (every living ship on the holder's
        /// side), "seatFleet" (while seated, every living ship on the seat's side). Default:
        /// character. An unrecognised value skips the perk with a named error.</summary>
        public string target;

        /// <summary>How an amount is shared among the ships a characterFleet or seatFleet perk
        /// lands on: "each" (default, every ship gets the full amount) or "split" (divided
        /// evenly; flat and percent float effects only). An unrecognised value skips the
        /// perk with a named error.</summary>
        public string distribution;

        /// <summary>The parsed routing, set by validation.</summary>
        internal PerkScope Scope;

        /// <summary>One entry per level; each entry is that level's effect list. A missing or
        /// short list pads with empty levels (they cost a point and do nothing - logged).</summary>
        public List<List<EffectSpec>> mods = new List<List<EffectSpec>>();

        /// <summary>Level count. Defaults to mods.Count, minimum 1.</summary>
        public int levels;

        /// <summary>Item GUIDs (32 hex, dashes allowed) this perk unlocks in the Ship's
        /// Fabricator once bought at any level, via the same UnlockCriteriaType.PlayerPerk
        /// mechanism every "Inscribe X"/"Fabricate X" perk uses. An item that already has an
        /// UnlockItemDef is skipped with a named warning; existing criteria cannot be
        /// repointed.</summary>
        public List<string> unlocks;

        /// <summary>Declares that an empty level on THIS perk is intentional - an ability perk
        /// (payload is code, via RegisterAbilityFactory) or an unlock-only perk (payload is
        /// "unlocks" above). Silences the "EMPTY StatMods" warning for this perk's empty
        /// levels only.</summary>
        public bool emptyOk;
    }

    public class EffectSpec
    {
        /// <summary>A StatType name ("Accuracy", "EnginePower"), a custom stat declared in
        /// this manifest, or a raw int id as the escape hatch.</summary>
        public string stat;

        /// <summary>"percent" (+X%, AdditiveMultiplier), "scalar" (xN, ExponentialMultiplier),
        /// "flat" (PrimaryAddend), "flat2" (SecondaryAddend), or an exact ModifierType name.</summary>
        public string type;

        /// <summary>percent: 0.25 means +25%. scalar: 0.25 means x1.25. flat: the addend.
        /// For "kind": "int" this is truncated to an int (JSON numbers parse as float either
        /// way, so 1 and 1.0 both work).</summary>
        public float value;

        /// <summary>"float" (default, FloatModifier) or "int" (IntModifier) - which concrete
        /// PrimitiveModifier&lt;T&gt; this effect builds. There is no bool modifier; a
        /// conceptually-boolean stat (JetpackEnhanced) is an int amount of 1.</summary>
        public string kind;

        /// <summary>Tag names this effect requires present, evaluated by requiredTagCriteria
        /// ("and"/"or", default "and"). Resolved against the live CsTagTable by name; an
        /// unresolvable name is a named error, not a silently-inert filter.</summary>
        public List<string> requiredTags;
        public string requiredTagCriteria;

        /// <summary>Same shape as requiredTags/requiredTagCriteria, but for
        /// ModTagConfiguration.RequiredLocalTags (no vanilla perk in the four fixtures uses
        /// this; exposed because the struct is otherwise plain serializable data).</summary>
        public List<string> requiredLocalTags;
        public string requiredLocalTagCriteria;

        /// <summary>ModTagConfiguration.DisablingTags: any tag present disables the mod.</summary>
        public List<string> disablingTags;

        /// <summary>ModTagConfiguration.TagsToAdd: tags this mod adds while active.</summary>
        public List<string> tagsToAdd;
    }

    public class CustomStatSpec
    {
        public string name;
        public string displayName;
        public string desc;
        public string icon;
        public bool lowerIsBetter;

        /// <summary>"value", "percentage" (default) or "bool" - how the stat renders in
        /// tooltips.</summary>
        public string displayType;
    }

    internal static class ManifestLoader
    {
        /// <summary>
        /// All manifests embedded in one assembly. Parse errors are logged by resource name
        /// and skipped - one bad manifest never takes down another role.
        /// </summary>
        public static List<RoleManifest> LoadFrom(Assembly asm)
        {
            List<RoleManifest> found = new List<RoleManifest>();
            string[] names;
            try { names = asm.GetManifestResourceNames(); }
            catch { return found; }

            foreach (string res in names)
            {
                if (!res.EndsWith(EctypePluginInfo.MANIFEST_SUFFIX,
                                  StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    string json;
                    using (Stream st = asm.GetManifestResourceStream(res))
                    using (StreamReader rd = new StreamReader(st))
                        json = rd.ReadToEnd();

                    RoleManifest m = JsonConvert.DeserializeObject<RoleManifest>(json);
                    string problem = Validate(m);
                    if (problem != null)
                    {
                        Plugin.Log.LogError("Ectype: manifest '" + res + "' in " +
                                            asm.GetName().Name + " rejected: " + problem);
                        continue;
                    }
                    found.Add(m);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Ectype: manifest '" + res + "' in " +
                                        asm.GetName().Name + " failed to parse: " + e.Message);
                }
            }
            return found;
        }

        private static string Validate(RoleManifest m)
        {
            if (m == null) return "empty document";
            string versionProblem = CheckFrameworkVersion(m.minFrameworkVersion);
            if (versionProblem != null) return versionProblem;
            if (m.contractVersion != EctypePluginInfo.CONTRACT_VERSION)
                return "contractVersion " + m.contractVersion + " (this framework speaks " +
                       EctypePluginInfo.CONTRACT_VERSION + ")";
            if (string.IsNullOrEmpty(m.roleId)) return "no roleId";
            if (string.IsNullOrEmpty(m.roleName)) return "no roleName";
            if (m.hero == null) return "no hero perk";
            if (m.perks == null) m.perks = new List<PerkSpec>();
            if (m.customStats == null) m.customStats = new List<CustomStatSpec>();

            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            m.hero.key = string.IsNullOrEmpty(m.hero.key) ? "hero" : m.hero.key;
            m.hero.row = 0; m.hero.col = 0; m.hero.wide = true;
            keys.Add(m.hero.key);
            foreach (PerkSpec p in m.perks)
            {
                if (p == null) return "null perk entry";
                if (string.IsNullOrEmpty(p.key)) return "a perk has no key";
                if (!keys.Add(p.key)) return "duplicate perk key '" + p.key + "'";
                if (string.IsNullOrEmpty(p.name)) return "perk '" + p.key + "' has no name";
                if (p.row < 1) return "perk '" + p.key + "' has row " + p.row +
                                      " (row 0 is the hero banner)";
                if (EctypeAssets.IsBannerRow(p.row) != p.wide)
                    return p.wide
                        ? "perk '" + p.key + "' on row " + p.row + " has wide: true, but only " +
                          "rows " + EctypeAssets.BannerRowList + " are wide cells"
                        : "perk '" + p.key + "' on row " + p.row + " is a wide cell and must " +
                          "declare wide: true";
                if (p.mods == null) p.mods = new List<List<EffectSpec>>();
                string unlocksProblem = NormalizeGuidList(p.unlocks);
                if (unlocksProblem != null)
                    return "perk '" + p.key + "' unlocks entry " + unlocksProblem;
            }
            // Targets, distributions and conditions fail closed: one this build does not know
            // would otherwise register as a perk whose effects land somewhere nobody authored.
            string heroProblem = PerkScopes.Parse(m.hero, out m.hero.Scope);
            if (heroProblem != null) return "hero perk has " + heroProblem;
            m.perks.RemoveAll(p =>
            {
                string problem = PerkScopes.Parse(p, out p.Scope);
                if (problem == null) return false;
                Plugin.Log.LogError("Ectype: role '" + m.roleName + "' perk '" + p.key +
                                    "' skipped: " + problem + ".");
                return true;
            });
            string heroUnlocksProblem = NormalizeGuidList(m.hero.unlocks);
            if (heroUnlocksProblem != null) return "hero unlocks entry " + heroUnlocksProblem;
            foreach (CustomStatSpec s in m.customStats)
            {
                if (s == null || string.IsNullOrEmpty(s.name)) return "a custom stat has no name";
            }
            if (m.suppress == null) m.suppress = new List<string>();
            string suppressProblem = NormalizeGuidList(m.suppress);
            if (suppressProblem != null) return "suppress entry " + suppressProblem;
            List<string> roles = new List<string>();
            foreach (string id in m.suppressRoles ?? new List<string>())
            {
                string t = (id ?? "").Trim();
                if (t.Length > 0) roles.Add(t);
            }
            m.suppressRoles = roles;
            WarnOnPaleColor(m);
            return null;
        }

        /// <summary>A role packed against a newer framework can name a target or field this
        /// build does not know; it is refused by name rather than half-registered.</summary>
        private static string CheckFrameworkVersion(string min)
        {
            if (string.IsNullOrEmpty(min)) return null;
            Version need, have;
            if (!Version.TryParse(min.Trim(), out need))
                return "minFrameworkVersion '" + min + "' is not a version (expected " +
                       "\"major.minor.patch\")";
            if (Version.TryParse(EctypePluginInfo.PLUGIN_VERSION, out have) && need > have)
                return "needs Ectype Framework " + min.Trim() + " or newer (this is " +
                       EctypePluginInfo.PLUGIN_VERSION + ")";
            return null;
        }

        /// <summary>Normalizes a list of manifest GUID strings to 32-hex in place. Returns
        /// null if every entry is valid, else the first bad entry quoted for the caller to
        /// prefix with the field name. A null/empty list is valid (no-op).</summary>
        private static string NormalizeGuidList(List<string> guids)
        {
            if (guids == null) return null;
            for (int i = 0; i < guids.Count; i++)
            {
                string norm = (guids[i] ?? "").Trim().Replace("-", "").ToLowerInvariant();
                if (norm.Length != 32 || !IsHex(norm))
                    return "'" + guids[i] + "' is not a GUID (32 hex chars, dashes allowed)";
                guids[i] = norm;
            }
            return null;
        }

        private static bool IsHex(string s)
        {
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        /// <summary>Non-fatal color lint: missing/unparseable renders white; near-white is
        /// indistinguishable from the broken state and from other pale roles.</summary>
        private static void WarnOnPaleColor(RoleManifest m)
        {
            if (string.IsNullOrEmpty(m.color))
            {
                Plugin.Log.LogWarning("Ectype: role '" + m.roleName + "' declares no " +
                                      "color - its tree will render untinted (white).");
                return;
            }
            UnityEngine.Color c;
            if (!UnityEngine.ColorUtility.TryParseHtmlString(m.color, out c))
            {
                Plugin.Log.LogWarning("Ectype: role '" + m.roleName + "' color '" +
                                      m.color + "' is not parseable (expected \"#RRGGBB\") - " +
                                      "the tree will render untinted (white).");
                return;
            }
            int sum = (int)(c.r * 255f) + (int)(c.g * 255f) + (int)(c.b * 255f);
            if (sum >= 700)
                Plugin.Log.LogWarning("Ectype: role '" + m.roleName + "' color " + m.color +
                                      " is too close to white (R+G+B = " + sum +
                                      " >= 700) - it will be hard to tell from an " +
                                      "untinted tree.");
        }
    }
}
