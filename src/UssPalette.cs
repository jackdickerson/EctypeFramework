using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ectype
{
    /// <summary>
    /// Adds the framework's named colors TO THE USS, so class-coloring runs through the
    /// game's own mechanism (EnableInClassList + cascade) instead of inline writes.
    ///
    /// For each registered class this clones the donor role's compiled StyleSheet, keeps
    /// only the donor-role rules, rewrites their selector parts to the framework class's
    /// role string, and recolors the entries where the donor role differs from a second
    /// vanilla role. From then on the game's own class toggle is the whole mechanism.
    /// </summary>
    internal static class UssPalette
    {
        private const string DONOR = "loadout-role__gunner";
        private const string COMPARE = "loadout-role__pilot";

        /// <summary>The game's color dictionary is USS custom properties: role rules read
        /// `var(--ui-color__perk-gunner)`, and theme sheets define those variables.</summary>
        private const string DONOR_VAR = "--ui-color__perk-gunner";

        private static List<StyleSheet> _donorSheets;
        private static bool _warnedNoDonor;
        private static List<StyleSheet> _definerSheets;
        private static bool _warnedNoDefiner;
        private static readonly Dictionary<string, StyleSheet> _clones =
            new Dictionary<string, StyleSheet>();   // donorInstanceId|classId -> patched clone
        private static readonly Dictionary<string, StyleSheet> _defClones =
            new Dictionary<string, StyleSheet>();   // definerInstanceId|classId -> def clone

        private static readonly object _classPartType = ResolveClassPartType();

        public static string RoleClass(ClassRecord rec)
        {
            // Mirrors the game's own class construction; an unnamed enum value stringifies
            // to its number, so tree ordinal 4 (role 5) yields "loadout-role__5".
            return "loadout-role__" + ((EPlayerRole)(rec.Ordinal + 1)).AsStyleString();
        }

        /// <summary>The framework class's entry in the game's named-color dictionary,
        /// mirroring the vanilla naming scheme.</summary>
        public static string VarFor(ClassRecord rec)
        {
            return "--ui-color__perk-" + ((EPlayerRole)(rec.Ordinal + 1)).AsStyleString();
        }

        /// <summary>Attach every registered class's patched sheet to the panel owning
        /// <paramref name="anywhere"/>. Idempotent; retries donor discovery until found.</summary>
        public static void EnsureInstalled(VisualElement anywhere)
        {
            try
            {
                VisualElement root = anywhere;
                while (root.parent != null) root = root.parent;

                foreach (ClassRecord rec in ClassRegistry.Classes)
                {
                    Color accent;
                    if (string.IsNullOrEmpty(rec.Manifest.color) ||
                        !ColorUtility.TryParseHtmlString(rec.Manifest.color, out accent))
                        continue;
                    // The dictionary entry: define --ui-color__perk-<role> = accent.
                    foreach (StyleSheet definer in DefinerSheets())
                    {
                        StyleSheet def = GetOrBuild(_defClones, definer, rec,
                            () => BuildDefinitionClone(definer, rec, accent));
                        if (def != null && !root.styleSheets.Contains(def))
                            root.styleSheets.Add(def);
                    }
                    // The consumers: cloned role rules reading var(<our variable>).
                    foreach (StyleSheet donor in DonorSheets())
                    {
                        StyleSheet clone = GetOrBuild(_clones, donor, rec,
                            () => BuildPatchedClone(donor, rec, accent));
                        if (clone != null && !root.styleSheets.Contains(clone))
                            root.styleSheets.Add(clone);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: USS palette install failed: " + e);
            }
        }

        // ------------------------------------------------------------------- donor lookup

        /// <summary>
        /// EVERY sheet that consumes the donor role's styling - by reading the donor variable
        /// in a rule value, or by keying a selector on a gunner token - needs a per-class
        /// clone, so this censuses them rather than naming the ones already known.
        /// </summary>
        private static List<StyleSheet> DonorSheets()
        {
            if (_donorSheets != null) return _donorSheets;
            List<StyleSheet> found = new List<StyleSheet>();
            foreach (StyleSheet sheet in Resources.FindObjectsOfTypeAll<StyleSheet>())
            {
                try
                {
                    if (SheetConsumesDonor(sheet)) found.Add(sheet);
                }
                catch { /* skip malformed sheet */ }
            }
            if (found.Count > 0)
            {
                _donorSheets = found;
                List<string> names = new List<string>();
                foreach (StyleSheet s in found) names.Add(s.name);
                Plugin.Log.LogInfo("Ectype: USS consumer census for the gunner role (" +
                                   found.Count + " sheet(s)): " +
                                   string.Join(", ", names.ToArray()) + ".");
                return _donorSheets;
            }
            if (!_warnedNoDonor)
            {
                _warnedNoDonor = true;
                Plugin.Log.LogWarning("Ectype: no loaded StyleSheet consumes the gunner " +
                                      "role yet - framework role styling is inert until " +
                                      "one appears (rechecked on every update).");
            }
            return new List<StyleSheet>();
        }

        private static bool SheetConsumesDonor(StyleSheet sheet)
        {
            string[] strings = Traverse.Create(sheet).Field("strings").GetValue<string[]>();
            if (strings != null)
                foreach (string s in strings)
                    if (s == DONOR_VAR) return true;
            IEnumerable selectors = Traverse.Create(sheet).Property("complexSelectors")
                                            .GetValue() as IEnumerable;
            if (selectors == null) return false;
            foreach (object cs in selectors)
                if (SelectorHasGunnerToken(cs)) return true;
            return false;
        }

        private static bool SelectorHasGunnerToken(object cs)
        {
            foreach (GunnerPart unused in GunnerParts(cs)) return true;
            return false;
        }

        /// <summary>One selector part whose value carries a gunner token. <c>Part</c> is a
        /// boxed struct copy: write it back with <c>Parts.SetValue(Part, Index)</c>.</summary>
        private struct GunnerPart
        {
            public Array Parts;
            public int Index;
            public object Part;
            public Traverse Tp;
            public string Type;
            public string Value;
        }

        /// <summary>Every non-pseudo-class, non-wildcard selector part of <paramref name="cs"/>
        /// whose value contains "gunner" (case-insensitive).</summary>
        private static IEnumerable<GunnerPart> GunnerParts(object cs)
        {
            IEnumerable sels = SelectorsOf(cs);
            if (sels == null) yield break;
            foreach (object sel in sels)
            {
                Array parts = Traverse.Create(sel).Field("m_Parts").GetValue() as Array;
                if (parts == null) continue;
                for (int i = 0; i < parts.Length; i++)
                {
                    object part = parts.GetValue(i);
                    Traverse tp = Traverse.Create(part);
                    string type = tp.Field("m_Type").GetValue().ToString();
                    if (type == "PseudoClass" || type == "Wildcard") continue;
                    string value = (string)tp.Field("m_Value").GetValue();
                    if (value == null ||
                        value.IndexOf("gunner", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    yield return new GunnerPart
                    {
                        Parts = parts, Index = i, Part = part, Tp = tp, Type = type, Value = value,
                    };
                }
            }
        }

        /// <summary>Rewrite a gunner token for the framework class, respecting what the
        /// token names: selectors matching ELEMENT identity (ID/Type parts - e.g. the tree
        /// element named "Gunner") become the tree ordinal ("4"); class parts (role-derived
        /// - "loadout-role__gunner") become the role style string ("5").</summary>
        private static string SwapToken(string value, string partType, ClassRecord rec)
        {
            string replacement = partType == "ID" || partType == "Type"
                ? rec.Ordinal.ToString()
                : ((EPlayerRole)(rec.Ordinal + 1)).AsStyleString();
            return ReplaceIgnoreCase(value, "gunner", replacement);
        }

        private static string ReplaceIgnoreCase(string s, string token, string replacement)
        {
            int idx = s.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            return idx < 0 ? s
                : s.Substring(0, idx) + replacement + s.Substring(idx + token.Length);
        }

        // ------------------------------------------------------------------ clone builder

        private static StyleSheet GetOrBuild(Dictionary<string, StyleSheet> cache,
                                             StyleSheet source, ClassRecord rec,
                                             Func<StyleSheet> build)
        {
            string key = source.GetInstanceID() + "|" + rec.Manifest.classId;
            StyleSheet cached;
            if (cache.TryGetValue(key, out cached)) return cached;

            StyleSheet built = null;
            try
            {
                built = build();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: patching sheet '" + source.name + "' for '" +
                                    rec.Manifest.className + "' failed: " + e);
            }
            cache[key] = built;   // cache failures too - one error, not one per update
            return built;
        }

        private static List<StyleSheet> DefinerSheets()
        {
            if (_definerSheets != null) return _definerSheets;
            List<StyleSheet> found = new List<StyleSheet>();
            foreach (StyleSheet sheet in Resources.FindObjectsOfTypeAll<StyleSheet>())
            {
                try
                {
                    if (ScanGunnerVarDefinitions(sheet).Count > 0) found.Add(sheet);
                }
                catch { }
            }
            if (found.Count > 0)
            {
                _definerSheets = found;
                List<string> names = new List<string>();
                foreach (StyleSheet s in found) names.Add(s.name);
                Plugin.Log.LogInfo("Ectype: USS variable definer sheet(s) for '" + DONOR_VAR +
                                   "': " + string.Join(", ", names.ToArray()) + ".");
                return _definerSheets;
            }
            if (!_warnedNoDefiner)
            {
                _warnedNoDefiner = true;
                Plugin.Log.LogWarning("Ectype: no loaded StyleSheet defines '" + DONOR_VAR +
                                      "' yet - framework role colors resolve to nothing " +
                                      "until one appears (rechecked on every update).");
            }
            return new List<StyleSheet>();
        }

        /// <summary>Every custom-property definition whose name carries the gunner token:
        /// (selectorIndex, ruleIndex, propIndex, varName, firstColor). The theme is a FAMILY
        /// of variables, and every member needs a counterpart or its consumers keep the
        /// donor hue.</summary>
        private static List<object[]> ScanGunnerVarDefinitions(StyleSheet sheet)
        {
            List<object[]> found = new List<object[]>();
            Array selectors = Traverse.Create(sheet).Property("complexSelectors")
                                      .GetValue() as Array;
            Array rules = Traverse.Create(sheet).Property("rules").GetValue() as Array;
            Color[] colors = Traverse.Create(sheet).Field("colors").GetValue<Color[]>();
            if (selectors == null || rules == null || colors == null) return found;
            for (int s = 0; s < selectors.Length; s++)
            {
                int ruleIndex = Traverse.Create(selectors.GetValue(s)).Field("ruleIndex")
                                        .GetValue<int>();
                if (ruleIndex >= rules.Length) continue;
                Array props = Traverse.Create(rules.GetValue(ruleIndex))
                                      .Field("m_Properties").GetValue() as Array;
                for (int p = 0; props != null && p < props.Length; p++)
                {
                    string name = Traverse.Create(props.GetValue(p)).Field("m_Name")
                                          .GetValue<string>();
                    if (name == null || !name.StartsWith("--") ||
                        name.IndexOf("gunner", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    Color? c = FirstColorOf(props.GetValue(p), colors);
                    if (c.HasValue)
                        found.Add(new object[] { s, ruleIndex, p, name, c.Value });
                }
            }
            return found;
        }

        private static Color? FirstColorOf(object prop, Color[] colors)
        {
            Array vals = Traverse.Create(prop).Field("m_Values").GetValue() as Array;
            for (int v = 0; vals != null && v < vals.Length; v++)
            {
                Traverse h = Traverse.Create(vals.GetValue(v));
                if (h.Field("m_ValueType").GetValue().ToString() != "Color") continue;
                int idx = h.Field("valueIndex").GetValue<int>();
                if (idx < colors.Length) return colors[idx];
            }
            return null;
        }

        /// <summary>A family variant of the accent: apply the same HSV relationship the
        /// donor variant has to the donor base ("darker red" becomes "darker accent"),
        /// keeping the variant's alpha structure.</summary>
        private static Color DeriveVariant(Color accent, Color donorBase, Color donorVariant)
        {
            float hb, sb, vb, hv, sv, vv, ha, sa, va;
            Color.RGBToHSV(donorBase, out hb, out sb, out vb);
            Color.RGBToHSV(donorVariant, out hv, out sv, out vv);
            Color.RGBToHSV(accent, out ha, out sa, out va);
            float h = Mathf.Repeat(ha + (hv - hb), 1f);
            float s = Mathf.Clamp01(sb > 0.001f ? sa * (sv / sb) : sv);
            float v = Mathf.Clamp01(vb > 0.001f ? va * (vv / vb) : vv);
            Color c = Color.HSVToRGB(h, s, v);
            c.a = donorVariant.a;
            return c;
        }

        /// <summary>
        /// The dictionary entries: clone the theme sheet, keep only the selectors owning
        /// gunner-family variable definitions, strip their rules down to those definitions,
        /// rename each to the framework class's family and set colors to the accent (base)
        /// or derived variants. The game's own var() resolution does everything else.
        /// </summary>
        private static StyleSheet BuildDefinitionClone(StyleSheet definer, ClassRecord rec,
                                                       Color accent)
        {
            List<object[]> defs = ScanGunnerVarDefinitions(definer);
            if (defs.Count == 0) throw new Exception("gunner-family definitions vanished " +
                                                     "from " + definer.name);
            Color donorBase = accent;
            foreach (object[] d in defs)
                if ((string)d[3] == DONOR_VAR) { donorBase = (Color)d[4]; break; }

            StyleSheet clone = UnityEngine.Object.Instantiate(definer);
            clone.name = definer.name + "-ectype-var-" + rec.Ordinal;
            clone.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(clone);

            Array selectors = Traverse.Create(clone).Property("complexSelectors")
                                      .GetValue() as Array;
            Array rules = Traverse.Create(clone).Property("rules").GetValue() as Array;
            Color[] colors = Traverse.Create(clone).Field("colors").GetValue<Color[]>();
            string roleToken = ((EPlayerRole)(rec.Ordinal + 1)).AsStyleString();

            // Group per rule, rename + recolor, then shrink each owning rule to only our
            // renamed definitions - the theme rule defines the whole vanilla palette, and
            // none of the rest may survive in the clone.
            Dictionary<int, List<object>> perRule = new Dictionary<int, List<object>>();
            Dictionary<int, object> selectorOfRule = new Dictionary<int, object>();
            List<string> defined = new List<string>();
            foreach (object[] d in defs)
            {
                int selIdx = (int)d[0], ruleIdx = (int)d[1], propIdx = (int)d[2];
                string donorName = (string)d[3];
                Color donorVariant = (Color)d[4];

                object rule = rules.GetValue(ruleIdx);
                Array props = Traverse.Create(rule).Field("m_Properties").GetValue() as Array;
                object prop = props.GetValue(propIdx);
                string ourName = ReplaceIgnoreCase(donorName, "gunner", roleToken);
                Traverse.Create(prop).Field("m_Name").SetValue(ourName);

                Color target = donorName == DONOR_VAR
                    ? accent : DeriveVariant(accent, donorBase, donorVariant);
                Array vals = Traverse.Create(prop).Field("m_Values").GetValue() as Array;
                for (int v = 0; vals != null && v < vals.Length; v++)
                {
                    Traverse h = Traverse.Create(vals.GetValue(v));
                    if (h.Field("m_ValueType").GetValue().ToString() != "Color") continue;
                    int idx = h.Field("valueIndex").GetValue<int>();
                    if (idx < colors.Length) colors[idx] = target;
                }

                List<object> list;
                if (!perRule.TryGetValue(ruleIdx, out list))
                    perRule[ruleIdx] = list = new List<object>();
                list.Add(prop);
                selectorOfRule[ruleIdx] = selectors.GetValue(selIdx);
                defined.Add(ourName);
            }

            List<object> keptSelectors = new List<object>();
            foreach (KeyValuePair<int, List<object>> kv in perRule)
            {
                object rule = rules.GetValue(kv.Key);
                Array lone = Array.CreateInstance(kv.Value[0].GetType(), kv.Value.Count);
                for (int i = 0; i < kv.Value.Count; i++) lone.SetValue(kv.Value[i], i);
                Traverse.Create(rule).Field("m_Properties").SetValue(lone);
                keptSelectors.Add(selectorOfRule[kv.Key]);
            }

            Array kept = Array.CreateInstance(selectors.GetType().GetElementType(),
                                              keptSelectors.Count);
            for (int i = 0; i < keptSelectors.Count; i++) kept.SetValue(keptSelectors[i], i);
            Traverse.Create(clone).Property("complexSelectors").SetValue(kept);
            clone.contentHash = unchecked(definer.contentHash * 397 ^
                                          rec.Manifest.classId.GetHashCode());

            if (Plugin.Verbose) Plugin.Log.LogInfo("Ectype: USS variable family for " + rec.Manifest.className +
                                                   " from " + definer.name + " (" + defs.Count + " member(s)): " +
                                                   string.Join(", ", defined.ToArray()) + ".");
            return clone;
        }

        private static StyleSheet BuildPatchedClone(StyleSheet donor, ClassRecord rec,
                                                    Color accent)
        {
            StyleSheet clone = UnityEngine.Object.Instantiate(donor);
            clone.name = donor.name + "-ectype-" + rec.Ordinal;
            clone.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(clone);

            Array cloneSelectors = Traverse.Create(clone).Property("complexSelectors")
                                           .GetValue() as Array;
            Array donorSelectors = Traverse.Create(donor).Property("complexSelectors")
                                           .GetValue() as Array;
            Array cloneRules = Traverse.Create(clone).Property("rules").GetValue() as Array;
            Array donorRules = Traverse.Create(donor).Property("rules").GetValue() as Array;
            Color[] donorColors = Traverse.Create(donor).Field("colors").GetValue<Color[]>();
            Color[] cloneColors = Traverse.Create(clone).Field("colors").GetValue<Color[]>();
            if (cloneSelectors == null || cloneRules == null || donorColors == null)
                throw new Exception("sheet internals not in expected shape (Unity update?)");

            List<object> kept = new List<object>();
            List<string> renames = new List<string>();
            int recolored = 0, unmatched = 0;

            for (int i = 0; i < cloneSelectors.Length; i++)
            {
                object cs = cloneSelectors.GetValue(i);
                bool byToken = SelectorHasGunnerToken(cs);
                bool byVar = RuleConsumesVar(donorRules, cs, donor);
                if (!byToken && !byVar) continue;

                if (byToken) RenameGunnerParts(cs, rec, renames);
                kept.Add(cs);

                // Recolor direct color values: find the donor sheet's matching selector for
                // the compare role; color handles whose table values differ between the two
                // roles are the role hue. Identical values are shared chrome and stay.
                object donorCs = donorSelectors.GetValue(i);
                object compareCs = FindCounterpart(donorSelectors, donorCs);
                if (compareCs == null) { if (byToken) unmatched++; continue; }

                object donorRule = donorRules.GetValue(
                    Traverse.Create(donorCs).Field("ruleIndex").GetValue<int>());
                object compareRule = donorRules.GetValue(
                    Traverse.Create(compareCs).Field("ruleIndex").GetValue<int>());
                recolored += RecolorRule(donorRule, compareRule,
                                         donorColors, cloneColors, accent);
            }

            if (kept.Count == 0)
                throw new Exception("no gunner-consuming selectors found in " + donor.name);

            // The role rules read the gunner-family variables, so EVERY family reference in
            // the clone is repointed: renaming only the base variable leaves
            // variant-consuming surfaces in the donor hue.
            int varRenames = 0;
            string roleToken = ((EPlayerRole)(rec.Ordinal + 1)).AsStyleString();
            string[] cloneStrings = Traverse.Create(clone).Field("strings")
                                            .GetValue<string[]>();
            for (int i = 0; cloneStrings != null && i < cloneStrings.Length; i++)
                if (cloneStrings[i] != null && cloneStrings[i].StartsWith("--") &&
                    cloneStrings[i].IndexOf("gunner", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    cloneStrings[i] = ReplaceIgnoreCase(cloneStrings[i], "gunner", roleToken);
                    varRenames++;
                }

            Array keptArray = Array.CreateInstance(cloneSelectors.GetType().GetElementType(),
                                                   kept.Count);
            for (int i = 0; i < kept.Count; i++) keptArray.SetValue(kept[i], i);
            // Property assignment runs the sheet's own SetupReferences(), rebuilding its
            // selector matching tables for the renamed parts.
            Traverse.Create(clone).Property("complexSelectors").SetValue(keptArray);
            clone.contentHash = unchecked(donor.contentHash * 397 ^
                                          rec.Manifest.classId.GetHashCode());

            int iconSwaps = SwapRoleIcon(clone, rec);

            if (Plugin.Verbose) Plugin.Log.LogInfo("Ectype: USS palette for " + rec.Manifest.className + " from " +
                                                   donor.name + ": " + kept.Count + " rule(s), " + varRenames +
                                                   " var reference(s) -> " + VarFor(rec) + ", " + recolored +
                                                   " direct color value(s) set" +
                                                   (iconSwaps > 0 ? ", " + iconSwaps + " role icon(s) swapped" : "") +
                                                   (unmatched > 0 ? ", " + unmatched + " without a compare " +
                                                                    "counterpart" : "") +
                                                   (renames.Count > 0 ? "; selector renames: " +
                                                        string.Join(", ", renames.ToArray()) : "") + ".");
            if (recolored == 0 && varRenames == 0)
                DumpRoleRules(donor, donorSelectors, donorRules);
            return clone;
        }

        /// <summary>
        /// The role icon on the crew HUD and the lobby list is a background image set by the
        /// role's USS rule, so the cloned gunner rule would draw the gunner's glyph. Every
        /// gunner image the clone references is repointed at the class's own role icon, or
        /// at the framework placeholder when the class ships none. The clone owns its asset
        /// table, so the donor sheet is untouched.
        /// </summary>
        private static int SwapRoleIcon(StyleSheet clone, ClassRecord rec)
        {
            UnityEngine.Object[] assets =
                Traverse.Create(clone).Field("assets").GetValue<UnityEngine.Object[]>();
            if (assets == null) return 0;
            Sprite own = null;
            int swapped = 0;
            for (int i = 0; i < assets.Length; i++)
            {
                UnityEngine.Object a = assets[i];
                if (a == null || !(a is Sprite || a is Texture2D) ||
                    a.name.IndexOf("gunner", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (own == null) own = RoleIconSprite(rec);
                if (own == null) return swapped;
                assets[i] = a is Sprite ? (UnityEngine.Object)own : own.texture;
                swapped++;
            }
            if (swapped > 0)
                Plugin.Log.LogInfo("Ectype: " + rec.Manifest.className + " role icon set in " +
                                   clone.name + " (" + swapped + " image reference(s)).");
            return swapped;
        }

        private static Sprite RoleIconSprite(ClassRecord rec)
        {
            Sprite s = IconStore.Get(rec.SourceAssembly, rec.Manifest.roleIcon);
            if (s != null) return s;
            Plugin.Log.LogInfo("Ectype: " + rec.Manifest.className + " ships no role icon - " +
                               "the crew HUD and lobby show the placeholder.");
            return IconStore.Placeholder(false);
        }

        // ------------------------------------------------------------ selector plumbing

        private static object ResolveClassPartType()
        {
            Type t = typeof(StyleSheet).Assembly
                .GetType("UnityEngine.UIElements.StyleSelectorType");
            return t != null ? Enum.Parse(t, "Class") : null;
        }

        private static IEnumerable SelectorsOf(object complexSelector)
        {
            return Traverse.Create(complexSelector).Field("m_Selectors").GetValue()
                   as IEnumerable;
        }

        private static bool ComplexSelectorHasClass(object cs, string className)
        {
            IEnumerable sels = SelectorsOf(cs);
            if (sels == null) return false;
            foreach (object sel in sels)
            {
                Array parts = Traverse.Create(sel).Field("m_Parts").GetValue() as Array;
                if (parts == null) continue;
                for (int i = 0; i < parts.Length; i++)
                {
                    object part = parts.GetValue(i);
                    if (Equals(Traverse.Create(part).Field("m_Type").GetValue(),
                               _classPartType) &&
                        (string)Traverse.Create(part).Field("m_Value").GetValue() == className)
                        return true;
                }
            }
            return false;
        }

        /// <summary>Does the rule this selector targets read the donor variable?</summary>
        private static bool RuleConsumesVar(Array rules, object cs, StyleSheet sheet)
        {
            int ruleIndex = Traverse.Create(cs).Field("ruleIndex").GetValue<int>();
            if (ruleIndex >= rules.Length) return false;
            string[] strings = Traverse.Create(sheet).Field("strings").GetValue<string[]>();
            if (strings == null) return false;
            Array props = Traverse.Create(rules.GetValue(ruleIndex)).Field("m_Properties")
                                  .GetValue() as Array;
            for (int p = 0; props != null && p < props.Length; p++)
            {
                Array vals = Traverse.Create(props.GetValue(p)).Field("m_Values")
                                     .GetValue() as Array;
                for (int v = 0; vals != null && v < vals.Length; v++)
                {
                    Traverse h = Traverse.Create(vals.GetValue(v));
                    if (h.Field("m_ValueType").GetValue().ToString() != "Variable") continue;
                    int idx = h.Field("valueIndex").GetValue<int>();
                    if (idx < strings.Length && strings[idx] == DONOR_VAR) return true;
                }
            }
            return false;
        }

        private static void RenameGunnerParts(object cs, ClassRecord rec, List<string> renames)
        {
            foreach (GunnerPart g in GunnerParts(cs))
            {
                string renamed = SwapToken(g.Value, g.Type, rec);
                g.Tp.Field("m_Value").SetValue(renamed);
                g.Parts.SetValue(g.Part, g.Index);   // write the boxed copy back
                string note = g.Type + ":" + g.Value + "->" + renamed;
                if (!renames.Contains(note)) renames.Add(note);
            }
        }

        /// <summary>The selector identical to <paramref name="reference"/> except with
        /// every gunner token swapped to the pilot equivalent.</summary>
        private static object FindCounterpart(Array all, object reference)
        {
            for (int i = 0; i < all.Length; i++)
            {
                object candidate = all.GetValue(i);
                if (!ReferenceEquals(candidate, reference) &&
                    SelectorsEqualModulo(reference, candidate))
                    return candidate;
            }
            return null;
        }

        private static bool SelectorsEqualModulo(object a, object b)
        {
            List<object> sa = ToList(SelectorsOf(a)), sb = ToList(SelectorsOf(b));
            if (sa.Count != sb.Count) return false;
            bool anySwap = false;
            for (int i = 0; i < sa.Count; i++)
            {
                Array pa = Traverse.Create(sa[i]).Field("m_Parts").GetValue() as Array;
                Array pb = Traverse.Create(sb[i]).Field("m_Parts").GetValue() as Array;
                if (pa == null || pb == null || pa.Length != pb.Length) return false;
                for (int j = 0; j < pa.Length; j++)
                {
                    Traverse ta = Traverse.Create(pa.GetValue(j));
                    Traverse tb = Traverse.Create(pb.GetValue(j));
                    if (!Equals(ta.Field("m_Type").GetValue(), tb.Field("m_Type").GetValue()))
                        return false;
                    string va = (string)ta.Field("m_Value").GetValue();
                    string vb = (string)tb.Field("m_Value").GetValue();
                    if (va == vb) continue;
                    if (va != null && vb != null &&
                        ReplaceIgnoreCase(va, "gunner", "pilot") == vb)
                    { anySwap = true; continue; }
                    if (va != null && vb != null &&
                        ReplaceIgnoreCase(va, "gunner", "Pilot") == vb)
                    { anySwap = true; continue; }
                    return false;
                }
            }
            return anySwap;
        }

        private static List<object> ToList(IEnumerable e)
        {
            List<object> list = new List<object>();
            if (e != null) foreach (object o in e) list.Add(o);
            return list;
        }

        // ------------------------------------------------------------------- diagnostics

        /// <summary>When the recolor finds nothing, print every role rule's properties and
        /// resolved values so the data shape is named instead of guessed.</summary>
        private static void DumpRoleRules(StyleSheet sheet, Array selectors, Array rules)
        {
            try
            {
                var sb = new System.Text.StringBuilder("Ectype: role-rule dump of " +
                                                       sheet.name + ":");
                Color[] colors = Traverse.Create(sheet).Field("colors").GetValue<Color[]>();
                float[] floats = Traverse.Create(sheet).Field("floats").GetValue<float[]>();
                string[] strings = Traverse.Create(sheet).Field("strings").GetValue<string[]>();
                UnityEngine.Object[] assets =
                    Traverse.Create(sheet).Field("assets").GetValue<UnityEngine.Object[]>();

                for (int i = 0; i < selectors.Length; i++)
                {
                    object cs = selectors.GetValue(i);
                    string cls = null;
                    foreach (string role in new[] { "gunner", "pilot", "engineer", "scavenger" })
                        if (ComplexSelectorHasClass(cs, "loadout-role__" + role))
                        { cls = role; break; }
                    if (cls == null) continue;

                    object rule = rules.GetValue(
                        Traverse.Create(cs).Field("ruleIndex").GetValue<int>());
                    sb.Append("\n  ." + cls + ":");
                    Array props = Traverse.Create(rule).Field("m_Properties").GetValue() as Array;
                    if (props == null) continue;
                    for (int p = 0; p < props.Length; p++)
                    {
                        object prop = props.GetValue(p);
                        sb.Append(" ")
                          .Append(Traverse.Create(prop).Field("m_Name").GetValue<string>())
                          .Append("=[");
                        Array vals = Traverse.Create(prop).Field("m_Values").GetValue() as Array;
                        for (int v = 0; vals != null && v < vals.Length; v++)
                        {
                            Traverse h = Traverse.Create(vals.GetValue(v));
                            string t = h.Field("m_ValueType").GetValue().ToString();
                            int idx = h.Field("valueIndex").GetValue<int>();
                            string val = "?";
                            try
                            {
                                switch (t)
                                {
                                    case "Color":
                                        val = "#" + ColorUtility.ToHtmlStringRGBA(colors[idx]);
                                        break;
                                    case "Float": val = floats[idx].ToString(); break;
                                    case "Dimension": val = "dim" + idx; break;
                                    case "AssetReference":
                                    case "ScalableImage":
                                        val = assets != null && idx < assets.Length &&
                                              assets[idx] != null ? assets[idx].name : "<null>";
                                        break;
                                    case "Keyword": val = "kw" + idx; break;
                                    default:
                                        val = strings != null && idx < strings.Length
                                              ? strings[idx] : "s" + idx;
                                        break;
                                }
                            }
                            catch { }
                            sb.Append(v > 0 ? " " : "").Append(t).Append(":").Append(val);
                        }
                        sb.Append("]");
                    }
                }
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: role-rule dump failed: " + e.Message);
            }
        }

        // ---------------------------------------------------------------- rule recoloring

        private static int RecolorRule(object donorRule, object compareRule,
                                       Color[] donorColors, Color[] cloneColors, Color accent)
        {
            int recolored = 0;
            Array donorProps = Traverse.Create(donorRule).Field("m_Properties").GetValue() as Array;
            Array compareProps = Traverse.Create(compareRule).Field("m_Properties").GetValue() as Array;
            if (donorProps == null || compareProps == null) return 0;

            for (int i = 0; i < donorProps.Length; i++)
            {
                object dProp = donorProps.GetValue(i);
                string name = Traverse.Create(dProp).Field("m_Name").GetValue<string>();
                object cProp = null;
                for (int j = 0; j < compareProps.Length; j++)
                    if (Traverse.Create(compareProps.GetValue(j)).Field("m_Name")
                                .GetValue<string>() == name)
                    { cProp = compareProps.GetValue(j); break; }
                if (cProp == null) continue;

                Array dVals = Traverse.Create(dProp).Field("m_Values").GetValue() as Array;
                Array cVals = Traverse.Create(cProp).Field("m_Values").GetValue() as Array;
                if (dVals == null || cVals == null) continue;
                int n = Math.Min(dVals.Length, cVals.Length);
                for (int k = 0; k < n; k++)
                {
                    Traverse dh = Traverse.Create(dVals.GetValue(k));
                    Traverse ch = Traverse.Create(cVals.GetValue(k));
                    if (dh.Field("m_ValueType").GetValue().ToString() != "Color" ||
                        ch.Field("m_ValueType").GetValue().ToString() != "Color") continue;
                    int dIdx = dh.Field("valueIndex").GetValue<int>();
                    int cIdx = ch.Field("valueIndex").GetValue<int>();
                    if (dIdx < donorColors.Length && cIdx < donorColors.Length &&
                        donorColors[dIdx] != donorColors[cIdx] &&
                        dIdx < cloneColors.Length)
                    {
                        cloneColors[dIdx] = accent;
                        recolored++;
                    }
                }
            }
            return recolored;
        }
    }
}
