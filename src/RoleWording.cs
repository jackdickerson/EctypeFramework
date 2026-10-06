using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

using CG;
using CG.Input;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace Ectype
{
    /// <summary>
    /// Presentation only: the English string-table entries where the game names a playable
    /// role "Class" are rewritten to say "Role". Every word the player reads for a role lives in
    /// the game's Unity Localization string tables (no UXML literal, no C# literal), so the
    /// rewrite happens on the table entry itself and every consumer - LocalizedString lookups,
    /// the game's direct entry reads, its own string caches once cleared - sees the new text.
    ///
    /// The seam is Unity Localization's own load hook, LocalizedStringDatabase.TablePostprocessor:
    /// called once for each string table as it finishes loading, before anything reads it, and
    /// again for the fresh copy loaded after a language change. No Harmony patch. Only tables
    /// whose locale code starts with "en" are touched; other languages keep their own word.
    ///
    /// Keys are listed one by one rather than matched by pattern, so a ship class ("REIGN-Class
    /// Frigate", "Ship Class") or a codex line can never be caught.
    /// </summary>
    internal static class RoleWording
    {
        private struct Rule
        {
            public readonly string Table, Key, Game, Ours;
            public Rule(string table, string key, string game, string ours)
            { Table = table; Key = key; Game = game; Ours = ours; }
        }

        private const string Ability = "Class Ability", RoleAbility = "Role Ability";
        private const string Label = "Class: ", RoleLabel = "Role: ";

        private static readonly Rule[] Rules =
        {
            // Settings -> Controls: the ability keybind's name.
            new Rule("UI", "ui_settings_control_class_ability", Ability, RoleAbility),
            // Stat-mod lines on relic and item tooltips.
            new Rule("ContextInfo", "ContextInfo_ModDescription_ClassAbilityCooldown_h", Ability, RoleAbility),
            new Rule("ContextInfo", "ContextInfo_ModDescription_ClassAbilityDuration_h", Ability, RoleAbility),
            // Hero-perk tooltip bodies: "Gain the <X> Class Ability."
            new Rule("ContextInfo", "ContextInfo_PerkBuff_Engineer_ClassAbility_b", Ability, RoleAbility),
            new Rule("ContextInfo", "ContextInfo_PerkBuff_Engineer_ClassAbility_Superior_b", Ability, RoleAbility),
            new Rule("ContextInfo", "ContextInfo_PerkBuff_Gunner_ClassAbility_b", Ability, RoleAbility),
            new Rule("ContextInfo", "ContextInfo_PerkBuff_Gunner_ClassAbility_Superior_b", Ability, RoleAbility),
            new Rule("ContextInfo", "ContextInfo_PerkBuff_Pilot_ClassAbility_b", Ability, RoleAbility),
            new Rule("ContextInfo", "ContextInfo_PerkBuff_Pilot_ClassAbility_Superior_b", Ability, RoleAbility),
            new Rule("ContextInfo", "ContextInfo_PerkBuff_Scavenger_ClassAbility_b", Ability, RoleAbility),
            new Rule("ContextInfo", "ContextInfo_PerkBuff_Scavenger_ClassAbility_Superior_b", Ability, RoleAbility),
            // The hub's player-projection interactables: "Class: Engineer".
            new Rule("ContextInfo", "ContextInfo_PlayerProjection_SetClassEngineer_h", Label, RoleLabel),
            new Rule("ContextInfo", "ContextInfo_PlayerProjection_SetClassGunner_h", Label, RoleLabel),
            new Rule("ContextInfo", "ContextInfo_PlayerProjection_SetClassPilot_h", Label, RoleLabel),
            new Rule("ContextInfo", "ContextInfo_PlayerProjection_SetClassScavenger_h", Label, RoleLabel),
        };

        private static bool _installed;
        private static bool _hooked;
        private static string _status = "not installed";
        private static readonly HashSet<string> _reported = new HashSet<string>();

        internal static string Status { get { return _status; } }

        private static bool On
        {
            get { return EctypeConfig.RoleWording == null || EctypeConfig.RoleWording.Value; }
        }

        /// <summary>
        /// Puts the postprocessor on the string database. Plugin Awake can run before the
        /// game's localization settings asset is loaded, so a miss here is retried by the
        /// driver until it lands. Never touches LocalizationSettings.Instance before HasSettings
        /// is true: that getter creates an empty default settings object when none is found.
        /// </summary>
        internal static void Install()
        {
            if (_installed) return;
            if (!_hooked)
            {
                _hooked = true;
                EctypeConfig.RoleWording.SettingChanged += (s, e) => Reapply();
            }
            if (!LocalizationSettings.HasSettings) { _status = "waiting for localization settings"; return; }

            LocalizedStringDatabase db = LocalizationSettings.StringDatabase;
            if (db == null) { _status = "waiting for the string database"; return; }

            ITablePostprocessor current = db.TablePostprocessor;
            if (!(current is Postprocessor))
                db.TablePostprocessor = new Postprocessor(current);
            _installed = true;
            _status = "installed" + (current != null && !(current is Postprocessor)
                ? " (chained after " + current.GetType().Name + ")" : "");
            Plugin.Log.LogInfo("Ectype: role wording " + _status + ".");

            // Tables loaded before the hook went in were never offered to it.
            if (ForEachLoadedTable(on: On) > 0) RefreshGameCaches();
        }

        /// <summary>Retry hook for the driver; one bool read once installed.</summary>
        internal static void Poll()
        {
            if (_installed || EctypeConfig.Enabled == null || !EctypeConfig.Enabled.Value) return;
            Install();
        }

        /// <summary>The config switch flipped: rewrite or restore every loaded table. Screens
        /// already built keep their text until the game rebuilds them.</summary>
        private static void Reapply()
        {
            try
            {
                if (!_installed) return;
                int changed = ForEachLoadedTable(on: On);
                RefreshGameCaches();
                Plugin.Log.LogInfo("Ectype: role wording " + (On ? "on" : "off") + ", " + changed +
                                   " loaded entr" + (changed == 1 ? "y" : "ies") + " rewritten.");
            }
            catch (Exception e) { Plugin.Log.LogError("Ectype: role wording toggle failed: " + e); }
        }

        /// <summary>Rewrites (or with on=false restores) the listed entries of one table.
        /// Idempotent: an entry already in the wanted wording has nothing to replace.</summary>
        private static int Process(StringTable table, bool on)
        {
            if (table == null) return 0;
            string code = table.LocaleIdentifier.Code;
            if (code == null || !code.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return 0;
            string collection = table.TableCollectionName;

            int changed = 0;
            for (int i = 0; i < Rules.Length; i++)
            {
                Rule r = Rules[i];
                if (!string.Equals(r.Table, collection, StringComparison.Ordinal)) continue;

                StringTableEntry entry = table.GetEntry(r.Key);
                if (entry == null)
                {
                    if (_reported.Add("missing:" + r.Key))
                        Plugin.Log.LogWarning("Ectype: role wording: no entry '" + r.Key + "' in " +
                                              collection + " (" + code + ") - the game shows its " +
                                              "own text there (game update renamed the key?).");
                    continue;
                }

                string from = on ? r.Game : r.Ours, to = on ? r.Ours : r.Game;
                string value = entry.Value;
                if (string.IsNullOrEmpty(value) || value.IndexOf(from, StringComparison.Ordinal) < 0)
                {
                    if (Plugin.Verbose && _reported.Add("nomatch:" + r.Key + on))
                        Plugin.Log.LogInfo("Ectype: role wording: '" + r.Key + "' has no '" + from +
                                           "' to replace - left as is.");
                    continue;
                }
                entry.Value = value.Replace(from, to);
                changed++;
            }
            if (changed > 0 && Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: role wording: " + collection + " (" + code + "): " +
                                   changed + " entr" + (changed == 1 ? "y" : "ies") + " " +
                                   (on ? "rewritten" : "restored") + ".");
            return changed;
        }

        /// <summary>
        /// Every string table the database currently holds. The handle map is internal to Unity
        /// Localization, so it is read by reflection; a miss means a package update and costs
        /// only the catch-up (tables loaded from here on are still processed).
        /// </summary>
        private static int ForEachLoadedTable(bool on)
        {
            object ops = null;
            PropertyInfo p = typeof(LocalizedDatabase<StringTable, StringTableEntry>).GetProperty(
                "TableOperations", BindingFlags.Instance | BindingFlags.NonPublic);
            if (p != null) ops = p.GetValue(LocalizationSettings.StringDatabase, null);
            IDictionary map = ops as IDictionary;
            if (map == null)
            {
                if (_reported.Add("tableops"))
                    Plugin.Log.LogWarning("Ectype: role wording: cannot list loaded string tables " +
                                          "(Unity Localization update?) - tables already loaded " +
                                          "keep the game's text until the language changes.");
                return 0;
            }

            List<AsyncOperationHandle<StringTable>> handles = new List<AsyncOperationHandle<StringTable>>();
            foreach (object v in map.Values)
                if (v is AsyncOperationHandle<StringTable>) handles.Add((AsyncOperationHandle<StringTable>)v);

            int changed = 0;
            foreach (AsyncOperationHandle<StringTable> h in handles)
            {
                if (!h.IsValid()) continue;
                if (h.IsDone) changed += Process(h.Result, on);
                else h.Completed += done =>
                {
                    try { if (Process(done.Result, On) > 0) RefreshGameCaches(); }
                    catch (Exception e) { ReportOnce("late", e); }
                };
            }
            return changed;
        }

        /// <summary>
        /// The game memoises looked-up strings and caches the keybind names; it clears and
        /// rebuilds both on a language change, and the same calls are made here after a rewrite
        /// of a table it may already have read.
        /// </summary>
        private static void RefreshGameCaches()
        {
            try
            {
                LocalizationExtensions.LocalizationStringCache.Clear();
                LocalizationExtensions.LocalizedStringCache.Clear();
            }
            catch (Exception e) { ReportOnce("caches", e); }

            try
            {
                object input = Refl.GetStatic(typeof(ServiceBase<InputService>), "_instance");
                object keyBinding = Refl.Get(input, "_inputKeyBinding");
                if (keyBinding != null)
                    Refl.Call(keyBinding, "RebuildKeybindArray", Type.EmptyTypes);
            }
            catch (Exception e) { ReportOnce("keybinds", e); }
        }

        private static void ReportOnce(string what, Exception e)
        {
            if (_reported.Add("fail:" + what))
                Plugin.Log.LogError("Ectype: role wording (" + what + ") failed, reported once: " + e);
        }

        /// <summary>Runs any postprocessor the game had set, then the rewrite. Throws nothing
        /// into Unity Localization's load path.</summary>
        private sealed class Postprocessor : ITablePostprocessor
        {
            private readonly ITablePostprocessor _inner;
            public Postprocessor(ITablePostprocessor inner) { _inner = inner; }

            public void PostprocessTable(LocalizationTable table)
            {
                if (_inner != null) _inner.PostprocessTable(table);
                try { if (On) Process(table as StringTable, true); }
                catch (Exception e) { ReportOnce("table load", e); }
            }
        }
    }
}
