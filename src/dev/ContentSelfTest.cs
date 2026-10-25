using System;
using System.Collections.Generic;

using ResourceAssets;

namespace Ectype
{
    /// <summary>
    /// Dev build self-test of the content registry, run once at load. Offline: no container
    /// is touched and nothing is registered with the game.
    ///
    /// - Ordered insert: a list of authored entries in arbitrary order, then the same set of
    ///   runtime entries inserted in several different orders. Every result must be identical,
    ///   keep the authored entries in place, and hold the runtime tail in ordinal key order; a
    ///   repeated insert must be refused.
    /// - Refusal: a carryable, and a component carryable, without crafting data must be
    ///   refused before anything else is checked, with a reason that names the GUID; a weapon
    ///   module or module marked as a component must be refused, naming the GUID.
    /// - Registry: an entry added under a GUID reserved for this test must be readable through
    ///   TryGet and All as a copy, raise Changed once, and be gone after removal.
    /// </summary>
    internal static class ContentSelfTest
    {
        private sealed class Row
        {
            public string Key;
            public bool Runtime;
            public override string ToString() { return (Runtime ? "r:" : "a:") + Key; }
        }

        private const string TestGuid = "ec7e57000000000000000000000000c1";

        internal static void Run()
        {
            List<string> failures = new List<string>();
            try { OrderedInsert(failures); }
            catch (Exception e) { failures.Add("ordered insert threw: " + e.Message); }
            try { Refusal(failures); }
            catch (Exception e) { failures.Add("refusal threw: " + e.Message); }
            try { Registry(failures); }
            catch (Exception e) { failures.Add("registry threw: " + e.Message); }

            if (failures.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: content registry passed (ordered " +
                                   "insert over 4 publish orders, crafting-data and component refusals, " +
                                   "TryGet/All/Changed).");
            else
                Plugin.Log.LogError("Ectype: dev self-test: content registry FAILED: " +
                                    string.Join("; ", failures.ToArray()));
        }

        private static void OrderedInsert(List<string> failures)
        {
            string[] authored = { "f0", "0a", "9c" };
            string[] runtime =
            {
                "6b1f", "00ff", "a3c0", "6b10", "ffff", "1234", "a3bf",
            };
            List<string[]> orders = new List<string[]>
            {
                runtime,
                Reversed(runtime),
                new[] { "a3c0", "1234", "ffff", "00ff", "6b10", "a3bf", "6b1f" },
                Shuffled(runtime, 1234),
            };

            string expected = null;
            foreach (string[] order in orders)
            {
                List<Row> list = new List<Row>();
                foreach (string k in authored) list.Add(new Row { Key = k });
                foreach (string k in order)
                    if (AssetDescriptionOrder.Insert(list, new Row { Key = k, Runtime = true },
                                                     r => r.Key, r => r.Runtime) < 0)
                        failures.Add("insert of " + k + " was refused");

                Row again = new Row { Key = order[0], Runtime = true };
                if (AssetDescriptionOrder.Insert(list, again, r => r.Key, r => r.Runtime) >= 0)
                    failures.Add("a second entry with key " + order[0] + " was inserted");
                Row same = list[list.Count - 1];
                if (AssetDescriptionOrder.Insert(list, same, r => r.Key, r => r.Runtime) >= 0)
                    failures.Add("the same entry was inserted twice");

                for (int i = 0; i < authored.Length; i++)
                    if (list[i].Runtime || list[i].Key != authored[i])
                        failures.Add("authored entry " + i + " moved");
                for (int i = authored.Length + 1; i < list.Count; i++)
                    if (string.CompareOrdinal(list[i - 1].Key, list[i].Key) >= 0)
                        failures.Add("runtime tail out of order at " + i);

                string got = Join(list);
                if (expected == null) expected = got;
                else if (got != expected)
                    failures.Add("order [" + string.Join(",", order) + "] gave " + got +
                                 ", expected " + expected);
            }
        }

        private static void Refusal(List<string> failures)
        {
            GUIDUnion guid = new GUIDUnion(TestGuid);
            foreach (bool component in new[] { false, true })
            {
                ModContentEntry entry = new ModContentEntry
                {
                    Owner = EctypePluginInfo.PLUGIN_GUID,
                    DisplayName = "Self-test item",
                    IsComponent = component,
                };
                // No asset either: the crafting check must come first.
                string why = RuntimeContent.Validate(ContentKind.Carryable, guid,
                                                     new ContentAssets(), entry);
                string label = component ? "component carryable" : "carryable";
                if (why == null)
                    failures.Add(label + " without crafting data was accepted");
                else if (why.IndexOf(TestGuid, StringComparison.OrdinalIgnoreCase) < 0 ||
                         why.IndexOf("crafting", StringComparison.Ordinal) < 0)
                    failures.Add(label + " refusal does not name the GUID and crafting: " + why);
            }

            foreach (ContentKind kind in new[] { ContentKind.WeaponModule, ContentKind.Module })
            {
                string why = RuntimeContent.Validate(kind, guid,
                    new ContentAssets { Crafting = new CraftingRules() },
                    new ModContentEntry
                    {
                        Owner = EctypePluginInfo.PLUGIN_GUID,
                        DisplayName = "Self-test item",
                        IsComponent = true,
                    });
                if (why == null)
                    failures.Add(kind + " marked IsComponent was accepted");
                else if (why.IndexOf(TestGuid, StringComparison.OrdinalIgnoreCase) < 0 ||
                         why.IndexOf("IsComponent", StringComparison.Ordinal) < 0)
                    failures.Add(kind + " component refusal does not name the GUID: " + why);
            }
        }

        private static void Registry(List<string> failures)
        {
            GUIDUnion guid = new GUIDUnion(TestGuid);
            int raised = 0;
            Action count = () => raised++;
            ModContent.Changed += count;
            try
            {
                ModContent.Add(new ModContentEntry
                {
                    Guid = guid,
                    Kind = ContentKind.Carryable.ToString(),
                    Owner = EctypePluginInfo.PLUGIN_GUID,
                    DisplayName = "Self-test item",
                    Category = "Other_Basic",
                    Tier = 2,
                    IsComponent = true,
                    PvpLegal = false,
                    IconHint = "none",
                });
                if (raised != 1) failures.Add("Changed raised " + raised + " times on add");

                ModContentEntry got;
                if (!ModContent.TryGet(guid, out got) || got == null)
                    failures.Add("TryGet missed the entry");
                else
                {
                    if (got.DisplayName != "Self-test item" || got.Tier != 2 || !got.IsComponent ||
                        got.PvpLegal || got.Kind != "Carryable")
                        failures.Add("TryGet returned other field values");
                    if (got.AvailableInRoom == null || !got.AvailableInRoom())
                        failures.Add("a null AvailableInRoom was not stored as always");
                    got.DisplayName = "changed";
                    ModContentEntry again;
                    ModContent.TryGet(guid, out again);
                    if (again == null || again.DisplayName != "Self-test item")
                        failures.Add("writing a returned entry changed the registry");
                }

                bool listed = false;
                foreach (ModContentEntry e in ModContent.All)
                    if (e.Guid.Equals(guid)) listed = true;
                if (!listed) failures.Add("All does not list the entry");

                if (!ModContent.Remove(guid)) failures.Add("Remove missed the entry");
                if (raised != 2) failures.Add("Changed raised " + raised + " times after removal");
                ModContentEntry gone;
                if (ModContent.TryGet(guid, out gone)) failures.Add("TryGet found a removed entry");
            }
            finally
            {
                ModContent.Changed -= count;
                ModContent.Remove(guid);
            }
        }

        private static string[] Reversed(string[] a)
        {
            string[] r = (string[])a.Clone();
            Array.Reverse(r);
            return r;
        }

        private static string[] Shuffled(string[] a, int seed)
        {
            string[] r = (string[])a.Clone();
            Random rng = new Random(seed);
            for (int i = r.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                string t = r[i]; r[i] = r[j]; r[j] = t;
            }
            return r;
        }

        private static string Join(List<Row> list)
        {
            List<string> parts = new List<string>(list.Count);
            foreach (Row r in list) parts.Add(r.ToString());
            return string.Join(",", parts.ToArray());
        }
    }
}
