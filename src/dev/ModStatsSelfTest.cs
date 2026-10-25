using System;
using System.Collections.Generic;

using Gameplay.Utilities;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// Dev build self-test for mod-declared stats and the session collection.
    ///
    /// Offline, on the driver's first frame:
    /// - Id formula: ModStats.Id("fleet", "HackSeconds") is 714605015, the vector the docs print.
    /// - Data file: a good file yields its three stats with kind, default, collection and display
    ///   read back; a second file declaring one of them again, and one declaring a case variant
    ///   of it, are each refused with an error naming both files; bad owner charset, bad name
    ///   charset, a reserved owner, a factor whose default is not 1 and a field with no default
    ///   are each refused.
    /// - Session collection: a host collection of a factor and a field takes a 50% contribution,
    ///   composes to the published values, a client collection rebuilt from those values reads
    ///   the same numbers (a stat the client does not declare included), withdrawal and disposal
    ///   leave no modifiers behind.
    /// Once the framework's stats have registered: the five generic stats sit at their documented
    /// ids.
    /// </summary>
    internal static class ModStatsSelfTest
    {
        private static bool _offlineDone;
        private static bool _registeredDone;
        private static float _nextTry;

        private static readonly KeyValuePair<string, int>[] GenericIds =
        {
            new KeyValuePair<string, int>(FrameworkStats.MeleeDamage, 544200806),
            new KeyValuePair<string, int>(FrameworkStats.MeleeCooldown, 578599244),
            new KeyValuePair<string, int>(FrameworkStats.MeleeReach, 650876603),
            new KeyValuePair<string, int>(FrameworkStats.AntiPersonnelDamage, 568581206),
            new KeyValuePair<string, int>(FrameworkStats.HostileSpawnScale, 696758852),
        };

        public static void Poll()
        {
            if (!Plugin.PatchesApplied) return;
            if (!_offlineDone)
            {
                _offlineDone = true;
                Offline();
            }
            if (_registeredDone || Time.unscaledTime < _nextTry) return;
            _nextTry = Time.unscaledTime + 5f;
            if (FrameworkStats.Id(FrameworkStats.MeleeDamage) == 0) return;
            _registeredDone = true;
            Registered();
        }

        private static void Offline()
        {
            List<string> failures = new List<string>();
            try { IdVector(failures); }
            catch (Exception e) { failures.Add("id formula threw: " + e.Message); }
            try { DataFile(failures); }
            catch (Exception e) { failures.Add("data file threw: " + e.Message); }
            try { Session(failures); }
            catch (Exception e) { failures.Add("session collection threw: " + e.Message); }

            if (failures.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: mod stats passed (id formula; data file parse, " +
                                   "duplicate and charset checks; session collection contribute, publish, " +
                                   "rebuild and dispose).");
            else
                Plugin.Log.LogError("Ectype: dev self-test: mod stats FAILED: " + string.Join("; ", failures.ToArray()));
        }

        private static void IdVector(List<string> failures)
        {
            int id = ModStats.Id("fleet", "HackSeconds");
            if (id != 714605015) failures.Add("ModStats.Id(fleet, HackSeconds) is " + id + ", expected 714605015");
            if (ModStats.Id("fleet/HackSeconds") != id) failures.Add("ModStats.Id(\"fleet/HackSeconds\") differs");
        }

        private const string Good =
            "{ \"owner\": \"selftest\", \"stats\": [" +
            " { \"name\": \"HackSeconds\", \"kind\": \"field\", \"default\": 60, \"collection\": \"character\"," +
            "   \"display\": \"Hack Time\", \"tooltip\": \"Seconds to hack.\", \"lowerIsBetter\": true }," +
            " { \"name\": \"HackResistance\", \"kind\": \"factor\", \"collection\": \"ship\" }," +
            " { \"name\": \"SupplyDropInterval\", \"kind\": \"factor\", \"default\": 1, \"collection\": \"session\" } ] }";

        private static void DataFile(List<string> failures)
        {
            List<string> errors = new List<string>(), warnings = new List<string>();
            List<ModStatDecl> good = ModStats.Parse(Good, "good.ectype-stats.json", errors, warnings);
            if (errors.Count > 0 || warnings.Count > 0)
                failures.Add("good file reported: " + string.Join(" | ", errors.ToArray()) +
                             string.Join(" | ", warnings.ToArray()));
            if (good.Count != 3)
            {
                failures.Add("good file yielded " + good.Count + " stats, expected 3");
                return;
            }
            ModStatDecl hack = good[0];
            if (hack.Id != ModStats.Id("selftest", "HackSeconds") || hack.IsFactor || hack.Default != 60f ||
                hack.Collection != StatCollectionKind.Character || hack.Display != "Hack Time" || !hack.LowerIsBetter ||
                hack.DisplayType != "value")
                failures.Add("good file: HackSeconds read back wrong");
            if (!good[1].IsFactor || good[1].Default != 1f || good[1].Collection != StatCollectionKind.Ship ||
                good[1].DisplayType != "percentage")
                failures.Add("good file: HackResistance read back wrong");
            if (good[2].Collection != StatCollectionKind.Session) failures.Add("good file: SupplyDropInterval collection wrong");

            // Duplicates: same id from a second file, and a case variant of the qualified name.
            Dictionary<int, ModStatDecl> byId = new Dictionary<int, ModStatDecl>();
            Dictionary<string, ModStatDecl> byName = new Dictionary<string, ModStatDecl>(StringComparer.OrdinalIgnoreCase);
            ModStats.Merge(good, byId, byName, errors);
            List<ModStatDecl> again = ModStats.Parse(
                "{ \"owner\": \"selftest\", \"stats\": [ { \"name\": \"HackSeconds\", \"kind\": \"factor\", \"collection\": \"ship\" }," +
                " { \"name\": \"hackseconds\", \"kind\": \"factor\", \"collection\": \"ship\" } ] }",
                "dup.ectype-stats.json", errors, warnings);
            List<ModStatDecl> admitted = ModStats.Merge(again, byId, byName, errors);
            int named = 0;
            foreach (string e in errors)
                if (e.Contains("dup.ectype-stats.json") && e.Contains("good.ectype-stats.json")) named++;
            if (admitted.Count != 0 || named != 2)
                failures.Add("duplicates: admitted " + admitted.Count + ", errors naming both files " + named +
                             " (expected 0 and 2)");

            Refused(failures, "{ \"owner\": \"Fleet!\", \"stats\": [ { \"name\": \"A\", \"kind\": \"factor\", \"collection\": \"ship\" } ] }",
                    "bad owner charset", "must be lower-case");
            Refused(failures, "{ \"owner\": \"fleet\", \"stats\": [ { \"name\": \"Hack Seconds\", \"kind\": \"factor\", \"collection\": \"ship\" } ] }",
                    "bad name charset", "must start with a letter");
            Refused(failures, "{ \"owner\": \"framework\", \"stats\": [ { \"name\": \"A\", \"kind\": \"factor\", \"collection\": \"ship\" } ] }",
                    "reserved owner", "reserved");
            Refused(failures, "{ \"owner\": \"ectype\", \"stats\": [ { \"name\": \"A\", \"kind\": \"factor\", \"collection\": \"ship\" } ] }",
                    "framework owner", "reserved");
            Refused(failures, "{ \"owner\": \"fleet\", \"stats\": [ { \"name\": \"A\", \"kind\": \"factor\", \"default\": 2, \"collection\": \"ship\" } ] }",
                    "factor default", "a factor's \"default\" is 1");
            Refused(failures, "{ \"owner\": \"fleet\", \"stats\": [ { \"name\": \"A\", \"kind\": \"field\", \"collection\": \"ship\" } ] }",
                    "field without default", "needs a \"default\"");
        }

        private static void Refused(List<string> failures, string json, string what, string expect)
        {
            List<string> errors = new List<string>(), warnings = new List<string>();
            List<ModStatDecl> got = ModStats.Parse(json, "bad.ectype-stats.json", errors, warnings);
            bool named = false;
            foreach (string e in errors) if (e.Contains(expect)) named = true;
            if (got.Count != 0 || !named)
                failures.Add(what + ": " + got.Count + " stat(s) admitted, errors: " + string.Join(" | ", errors.ToArray()));
        }

        private sealed class TestSource : IModifierSource { }

        private static void Session(List<string> failures)
        {
            ModStatDecl factor = new ModStatDecl
            {
                Owner = "selftest", Name = "Scale", Id = ModStats.Id("selftest", "Scale"), IsFactor = true,
                Default = 1f, Collection = StatCollectionKind.Session, DeclaredBy = "self-test",
            };
            ModStatDecl field = new ModStatDecl
            {
                Owner = "selftest", Name = "Interval", Id = ModStats.Id("selftest", "Interval"), IsFactor = false,
                Default = 300f, Collection = StatCollectionKind.Session, DeclaredBy = "self-test",
            };
            int hostOnly = ModStats.Id("selftest", "HostOnly");

            StatTagCollection host = SessionStats.NewCollection(new List<ModStatDecl> { factor, field });
            ModifiableFloat extra = new ModifiableFloat(7f);
            host.RegisterStat(hostOnly, ref extra);
            TestSource src = new TestSource();
            if (!SessionStats.ContributeTo(host, factor.Id, new FloatModifier(0.5f, ModifierType.AdditiveMultiplier), src))
                failures.Add("contribute to a session stat refused");
            if (SessionStats.ContributeTo(host, ModStats.Id("selftest", "Absent"), new FloatModifier(1f, ModifierType.PrimaryAddend), src))
                failures.Add("contribute to an absent stat accepted");
            Dictionary<string, float> published = SessionStats.Compose(host);
            float scale = Read(published, factor.Id), interval = Read(published, field.Id);
            if (!Near(scale, 1.5f) || !Near(interval, 300f) || !Near(Read(published, hostOnly), 7f))
                failures.Add("host composed " + scale + " / " + interval + ", expected 1.5 / 300 / 7");

            StatTagCollection client = SessionStats.NewCollection(new List<ModStatDecl> { factor, field });
            SessionStats.ApplyHostValues(client, published);
            float cScale, cInterval, cExtra;
            ModStats.TryGet(client, "selftest/Scale", out cScale);
            ModStats.TryGet(client, "selftest/Interval", out cInterval);
            bool hasExtra = ModStats.TryGet(client, "selftest/HostOnly", out cExtra);
            if (!Near(cScale, 1.5f) || !Near(cInterval, 300f) || !hasExtra || !Near(cExtra, 7f))
                failures.Add("client rebuilt " + cScale + " / " + cInterval + " / " + cExtra + ", expected 1.5 / 300 / 7");

            host.RemoveModifier(src);
            float after;
            ModStats.TryGet(host, "selftest/Scale", out after);
            if (!Near(after, 1f)) failures.Add("after withdrawal the host reads " + after + ", expected 1");

            SessionStats.Dispose(client);
            SessionStats.Dispose(host);
            if (client.ActiveModifiers.Count + client.InactiveModifiers.Count != 0 ||
                host.ActiveModifiers.Count + host.InactiveModifiers.Count != 0)
                failures.Add("modifiers left after dispose");
            ModStats.TryGet(client, "selftest/Scale", out after);
            if (!Near(after, 1f)) failures.Add("after dispose the client reads " + after + ", expected 1");
        }

        private static void Registered()
        {
            List<string> failures = new List<string>();
            foreach (KeyValuePair<string, int> kv in GenericIds)
            {
                int id = FrameworkStats.Id(kv.Key);
                if (id != kv.Value) failures.Add(kv.Key + " is " + id + ", expected " + kv.Value);
                if (ModStats.Id("ectype/" + kv.Key) != kv.Value) failures.Add("ectype/" + kv.Key + " does not resolve");
            }
            if (failures.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: generic framework stats passed (" + GenericIds.Length +
                                   " at their documented ids).");
            else
                Plugin.Log.LogError("Ectype: dev self-test: generic framework stats FAILED: " +
                                    string.Join("; ", failures.ToArray()));
        }

        private static float Read(Dictionary<string, float> values, int id)
        {
            float v;
            return values.TryGetValue(id.ToString(System.Globalization.CultureInfo.InvariantCulture), out v) ? v : float.NaN;
        }

        private static bool Near(float a, float b) { return Math.Abs(a - b) < 0.0001f; }
    }
}
