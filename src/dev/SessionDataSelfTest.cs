using System;
using System.Collections.Generic;
using System.IO;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Ectype
{
    /// <summary>
    /// Dev build self-test for session data, run once on the driver's first frame (after the
    /// patches are applied). Offline: no room is joined and no live session state changes.
    ///
    /// - Round trip: five values of different JSON shapes are attached to a fresh GameSession
    ///   with a host stamp, serialised through the game's own AsJObject (with the framework's
    ///   postfix), turned into the wire string the way the host's event 9 sends it, parsed and
    ///   rebuilt through the game's own FromJObject (with the postfix, diverted to this test).
    ///   Every value, the stamp and the session id must come back equal. A second session object
    ///   with nothing attached must serialise without the key.
    /// - Size caps: a key over its own cap is refused by name; when the envelope is over 16 KB the
    ///   largest key is refused by name; an oversize, wrong-format or overflowing envelope is
    ///   refused on receipt without throwing.
    /// - Writer rule: live and cached deliveries, stamped by the master, by a departed host, by a
    ///   present non-master, mismatched or unstamped.
    /// - Sidecar: write, overwrite in place, read back, refuse a non-id file name, and prune
    ///   leftover temporary files, then by age and count, in a temporary folder that is deleted
    ///   afterwards. A file not named by a session id survives.
    /// - Host-value room format: a value stamped by the master reads; one stamped by another
    ///   actor, one with an overflowing stamp, one over the cap and one that is not a string do
    ///   not. Token kinds are checked against the value type.
    /// - Type checks: scalars and plain data pass; Unity types are refused, and anything but a
    ///   scalar is refused for a host value.
    /// The capture point itself (GameSessionManager.PrepareNextSession) needs a live session start
    /// and is outside this test.
    /// </summary>
    internal static class SessionDataSelfTest
    {
        private const int TestActor = 7;
        private static bool _done;

        public static void Poll()
        {
            if (_done || !Plugin.PatchesApplied) return;
            _done = true;
            List<string> failures = new List<string>();
            string roundTrip = "not run";
            try { roundTrip = RoundTrip(failures); }
            catch (Exception e) { failures.Add("round trip threw: " + e.Message); }
            finally { SessionData.SelfTestSink = null; }
            Run("size caps", Caps, failures);
            Run("writer rule", Writer, failures);
            Run("sidecar", Sidecar, failures);
            Run("host-value room format", Stamp, failures);
            Run("type checks", Types, failures);

            if (failures.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: session data passed (" + roundTrip + "; size caps; " +
                                   "writer rule; sidecar write/read/prune; host-value stamp; type checks).");
            else
                Plugin.Log.LogError("Ectype: dev self-test: session data FAILED: " +
                                    string.Join("; ", failures.ToArray()));
        }

        private static void Run(string name, Action<List<string>> test, List<string> failures)
        {
            try { test(failures); }
            catch (Exception e) { failures.Add(name + " threw: " + e.Message); }
        }

        private static string RoundTrip(List<string> failures)
        {
            List<SessionData.PackItem> items = new List<SessionData.PackItem>
            {
                Item("ectype.selftest/float", JToken.FromObject(0.75f)),
                Item("ectype.selftest/int", JToken.FromObject(42)),
                Item("ectype.selftest/text", JToken.FromObject("héllo \"quoted\"")),
                Item("ectype.selftest/list", JToken.FromObject(new List<int> { 1, 2, 3 })),
                Item("ectype.selftest/object", new JObject { ["spacing"] = 450.5, ["mode"] = "pvp" }),
            };
            List<string> refusals = new List<string>();
            JObject envelope = SessionData.Pack(items, refusals, TestActor);
            if (refusals.Count > 0) failures.Add("round trip: refused " + string.Join(", ", refusals.ToArray()));

            GameSession other = new GameSession();
            other.GenerateSessionID();
            if (other.AsJObject()[SessionData.EnvelopeKey] != null)
                failures.Add("round trip: a session object with nothing attached was given an envelope");

            GameSession sent = new GameSession();
            sent.GenerateSessionID();
            SessionData.AttachForTest(sent, (JObject)envelope["keys"], TestActor);
            JObject json = sent.AsJObject();
            if (json[SessionData.EnvelopeKey] == null)
            {
                failures.Add("round trip: AsJObject wrote no '" + SessionData.EnvelopeKey + "' key");
                return "round trip incomplete";
            }
            string wire = JsonConvert.SerializeObject(json);

            JToken received = null;
            bool sinkRan = false;
            SessionData.SelfTestSink = t => { received = t; sinkRan = true; };
            GameSession rebuilt = GameSession.FromJObject(JObject.Parse(wire));
            SessionData.SelfTestSink = null;

            if (!sinkRan) failures.Add("round trip: the FromJObject postfix did not run");
            if (rebuilt == null || rebuilt.GameSessionID != sent.GameSessionID)
                failures.Add("round trip: the session id did not survive");

            string problem;
            int stamp;
            JObject keys = SessionData.Unpack(received, out problem, out stamp);
            if (keys == null)
            {
                failures.Add("round trip: envelope did not unpack (" + (problem ?? "absent") + ")");
                return "round trip incomplete";
            }
            if (stamp != TestActor) failures.Add("round trip: the host stamp came back as " + stamp);
            foreach (SessionData.PackItem item in items)
                if (!JToken.DeepEquals(item.Token, keys[item.Key]))
                    failures.Add("round trip: " + item.Key + " came back as " +
                                 (keys[item.Key] != null ? keys[item.Key].ToString(Formatting.None) : "nothing"));
            if (keys["ectype.selftest/float"] != null && keys["ectype.selftest/float"].ToObject<float>() != 0.75f)
                failures.Add("round trip: the float did not read back as 0.75");
            return "round trip through GameSession.AsJObject/FromJObject, " + items.Count + " keys, " +
                   SessionData.Bytes(envelope) + " bytes";
        }

        private static void Caps(List<string> failures)
        {
            // A key over its own cap.
            List<string> refusals = new List<string>();
            JObject env = SessionData.Pack(new List<SessionData.PackItem>
            {
                Item("ectype.selftest/big", JToken.FromObject(new string('x', 5000)), SessionData.DefaultKeyCapBytes),
                Item("ectype.selftest/small", JToken.FromObject(1)),
            }, refusals, TestActor);
            JObject keys = (JObject)env["keys"];
            if (refusals.Count != 1 || refusals[0].IndexOf("'ectype.selftest/big'", StringComparison.Ordinal) < 0)
                failures.Add("caps: the over-cap key was not refused by name (" + string.Join(", ", refusals.ToArray()) + ")");
            if (keys["ectype.selftest/big"] != null || keys["ectype.selftest/small"] == null)
                failures.Add("caps: the per-key refusal changed the wrong keys");

            // Over the envelope cap: the largest key goes, whatever its order.
            refusals.Clear();
            env = SessionData.Pack(new List<SessionData.PackItem>
            {
                Item("ectype.selftest/a", JToken.FromObject(new string('y', 6000)), SessionData.EnvelopeCapBytes),
                Item("ectype.selftest/b", JToken.FromObject(new string('y', 7000)), SessionData.EnvelopeCapBytes),
                Item("ectype.selftest/c", JToken.FromObject(new string('y', 5000)), SessionData.EnvelopeCapBytes),
            }, refusals, TestActor);
            keys = (JObject)env["keys"];
            if (refusals.Count != 1 || refusals[0].IndexOf("'ectype.selftest/b'", StringComparison.Ordinal) < 0)
                failures.Add("caps: the largest key was not refused by name (" + string.Join(", ", refusals.ToArray()) + ")");
            if (keys["ectype.selftest/a"] == null || keys["ectype.selftest/b"] != null || keys["ectype.selftest/c"] == null)
                failures.Add("caps: the envelope cap kept the wrong keys");
            if (SessionData.Bytes(env) > SessionData.EnvelopeCapBytes)
                failures.Add("caps: the packed envelope is " + SessionData.Bytes(env) + " bytes");

            // Receipt refuses what the sender should not have sent, without throwing.
            string problem;
            int stamp;
            JObject huge = SessionData.Wrap(new JObject { ["k"] = new string('z', SessionData.EnvelopeCapBytes) }, TestActor);
            if (SessionData.Unpack(huge, out problem, out stamp) != null || problem == null)
                failures.Add("caps: an oversize envelope was accepted on receipt");
            JObject wrongVersion = new JObject { ["v"] = 99, ["keys"] = new JObject() };
            if (SessionData.Unpack(wrongVersion, out problem, out stamp) != null || problem == null)
                failures.Add("caps: an envelope of another format was accepted");
            JObject overflow = JObject.Parse("{\"v\":100000000000000000000000000000,\"keys\":{}}");
            if (SessionData.Unpack(overflow, out problem, out stamp) != null || problem == null)
                failures.Add("caps: an envelope with an overflowing format number was accepted");
            if (SessionData.Unpack(null, out problem, out stamp) != null || problem != null)
                failures.Add("caps: an absent envelope was reported as a problem");
        }

        private static void Writer(List<string> failures)
        {
            HashSet<int> present = new HashSet<int> { 1, 2, 3 };
            Func<int, bool> inRoom = a => present.Contains(a);
            Expect(failures, "live from the master", SessionData.WriterProblem(1, 1, 1, inRoom), true);
            Expect(failures, "live, sender differs from stamp", SessionData.WriterProblem(2, 1, 1, inRoom), false);
            Expect(failures, "live from a present non-master", SessionData.WriterProblem(2, 2, 1, inRoom), false);
            Expect(failures, "live from a departed actor", SessionData.WriterProblem(9, 9, 1, inRoom), true);
            Expect(failures, "cached, stamped by the master", SessionData.WriterProblem(0, 1, 1, inRoom), true);
            Expect(failures, "cached, stamped by a departed host", SessionData.WriterProblem(0, 9, 1, inRoom), true);
            Expect(failures, "cached, stamped by a present non-master", SessionData.WriterProblem(0, 3, 1, inRoom), false);
            Expect(failures, "cached, no stamp", SessionData.WriterProblem(-1, -1, 1, inRoom), false);
        }

        private static void Expect(List<string> failures, string name, string problem, bool accepted)
        {
            if ((problem == null) != accepted)
                failures.Add("writer rule: " + name + " was " + (problem == null ? "accepted" : "refused (" + problem + ")"));
        }

        private static void Sidecar(List<string> failures)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ectype-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                JObject first = new JObject { ["ectype.selftest/float"] = 0.5f };
                JObject keys = new JObject { ["ectype.selftest/float"] = 0.25f, ["ectype.selftest/text"] = "saved" };
                string id = Guid.NewGuid().ToString();
                string error;
                if (!SessionData.WriteSidecar(dir, id, first, out error))
                    failures.Add("sidecar: write failed: " + error);
                if (!SessionData.WriteSidecar(dir, id, keys, out error))
                    failures.Add("sidecar: overwrite failed: " + error);
                JObject back = SessionData.ReadSidecar(dir, id, out error);
                if (back == null || !JToken.DeepEquals(back, keys))
                    failures.Add("sidecar: read back " + (back != null ? back.ToString(Formatting.None) : "nothing") +
                                 (error != null ? " (" + error + ")" : ""));
                if (File.Exists(Path.Combine(dir, id + ".json.tmp")))
                    failures.Add("sidecar: a temporary file was left behind");
                if (SessionData.ReadSidecar(dir, Guid.NewGuid().ToString(), out error) != null || error != null)
                    failures.Add("sidecar: a session with no file read as something");
                if (SessionData.WriteSidecar(dir, "..\\escape", keys, out error) || SessionData.ValidSessionId("../x"))
                    failures.Add("sidecar: a non-id file name was accepted");

                DateTime now = DateTime.UtcNow;
                File.SetLastWriteTimeUtc(Path.Combine(dir, id + ".json"), now);
                int[] ageDays = { 1, 2, 3, 40, 50 };
                List<string> ids = new List<string>();
                foreach (int days in ageDays)
                {
                    string other = Guid.NewGuid().ToString();
                    ids.Add(other);
                    SessionData.WriteSidecar(dir, other, keys, out error);
                    File.SetLastWriteTimeUtc(Path.Combine(dir, other + ".json"), now.AddDays(-days));
                }
                string leftover = Path.Combine(dir, Guid.NewGuid() + ".json.tmp");
                File.WriteAllText(leftover, "{}");
                string notes = Path.Combine(dir, "notes.json");
                File.WriteAllText(notes, "{}");
                File.SetLastWriteTimeUtc(notes, now.AddDays(-90));

                int removed = SessionData.PruneSidecars(dir, TimeSpan.FromDays(30), 3, now);
                bool keptRight = File.Exists(Path.Combine(dir, id + ".json")) &&
                                 File.Exists(Path.Combine(dir, ids[0] + ".json")) &&
                                 File.Exists(Path.Combine(dir, ids[1] + ".json")) &&
                                 !File.Exists(Path.Combine(dir, ids[2] + ".json")) &&
                                 !File.Exists(Path.Combine(dir, ids[3] + ".json")) &&
                                 !File.Exists(Path.Combine(dir, ids[4] + ".json")) &&
                                 !File.Exists(leftover) &&
                                 File.Exists(notes);
                if (removed != 4 || !keptRight)
                    failures.Add("sidecar: prune removed " + removed + " (expected 4: a leftover temporary file, two " +
                                 "past 30 days, one past the newest 3) or kept the wrong files");
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch { }
            }
        }

        private static void Stamp(List<string> failures)
        {
            string problem;
            JObject values = new JObject { ["k"] = 1.5f };
            string raw = SessionData.PackRoomValue(TestActor, values);
            JToken v = SessionData.UnpackRoomValue(raw, TestActor, HostValueRoom.CapBytes, out problem);
            if (!(v is JObject) || v["k"] == null || v["k"].ToObject<float>() != 1.5f)
                failures.Add("stamp: the master's value did not read (" + problem + ")");
            if (SessionData.UnpackRoomValue(raw, TestActor + 1, HostValueRoom.CapBytes, out problem) != null)
                failures.Add("stamp: a value stamped by another actor was accepted");
            if (SessionData.UnpackRoomValue("{\"a\":100000000000000000000000000000,\"v\":{}}", TestActor,
                                            HostValueRoom.CapBytes, out problem) != null)
                failures.Add("stamp: an overflowing stamp was accepted");
            if (SessionData.UnpackRoomValue(42, TestActor, HostValueRoom.CapBytes, out problem) != null)
                failures.Add("stamp: a non-string room value was accepted");
            string big = SessionData.PackRoomValue(TestActor, new JObject { ["k"] = new string('q', 20000) });
            if (SessionData.UnpackRoomValue(big, TestActor, HostValueRoom.CapBytes, out problem) != null)
                failures.Add("stamp: an over-cap room value was accepted");

            if (!SessionData.TokenFits(JToken.FromObject(1.5f), typeof(float)) ||
                !SessionData.TokenFits(JToken.FromObject(3), typeof(float)) ||
                SessionData.TokenFits(JToken.FromObject("3"), typeof(float)) ||
                SessionData.TokenFits(JToken.FromObject(1), typeof(bool)) ||
                SessionData.TokenFits(JToken.FromObject(2.5), typeof(int)) ||
                !SessionData.TokenFits(JToken.FromObject("x"), typeof(string)))
                failures.Add("stamp: a token kind check gave the wrong answer");
        }

        private static void Types(List<string> failures)
        {
            if (SessionData.TypeProblem(typeof(float), true) != null ||
                SessionData.TypeProblem(typeof(string), true) != null ||
                SessionData.TypeProblem(typeof(DayOfWeek), true) != null ||
                SessionData.TypeProblem(typeof(int?), true) != null)
                failures.Add("types: a scalar was refused");
            if (SessionData.TypeProblem(typeof(List<int>), false) != null ||
                SessionData.TypeProblem(typeof(Dictionary<string, float>), false) != null ||
                SessionData.TypeProblem(typeof(string[]), false) != null)
                failures.Add("types: plain data was refused");
            if (SessionData.TypeProblem(typeof(UnityEngine.Vector3), false) == null ||
                SessionData.TypeProblem(typeof(UnityEngine.Color), true) == null ||
                SessionData.TypeProblem(typeof(UnityEngine.ScriptableObject), false) == null ||
                SessionData.TypeProblem(typeof(List<int>), true) == null)
                failures.Add("types: a type that does not survive JSON, or a non-scalar host value, was accepted");
        }

        private static SessionData.PackItem Item(string key, JToken token, int cap = SessionData.DefaultKeyCapBytes)
        {
            return new SessionData.PackItem { Key = key, Token = token, Cap = cap };
        }
    }
}
