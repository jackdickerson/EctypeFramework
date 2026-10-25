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
    /// - Round trip: an envelope of five values of different JSON shapes is attached to a fresh
    ///   GameSession, serialised through the game's own AsJObject (with the framework's postfix),
    ///   turned into the wire string the way the host's event 9 sends it, parsed and rebuilt
    ///   through the game's own FromJObject (with the postfix, diverted to this test). Every value
    ///   and the session id must come back equal.
    /// - Size caps: a key over its own cap and a key that would take the envelope over 16 KB are
    ///   left out, each with a refusal naming it; an oversize or wrong-format envelope is refused
    ///   on receipt.
    /// - Sidecar: write, read back, refuse a non-id file name, and prune by age and count, in a
    ///   temporary folder that is deleted afterwards. A file not named by a session id survives.
    /// - Host-value stamp: a room value stamped by the master reads; one stamped by another actor,
    ///   one over the cap and one that is not a string do not.
    /// </summary>
    internal static class SessionDataSelfTest
    {
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
            try { Caps(failures); }
            catch (Exception e) { failures.Add("size caps threw: " + e.Message); }
            try { Sidecar(failures); }
            catch (Exception e) { failures.Add("sidecar threw: " + e.Message); }
            try { Stamp(failures); }
            catch (Exception e) { failures.Add("host-value stamp threw: " + e.Message); }

            if (failures.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: session data passed (" + roundTrip + "; size caps; " +
                                   "sidecar write/read/prune; host-value stamp).");
            else
                Plugin.Log.LogError("Ectype: dev self-test: session data FAILED: " +
                                    string.Join("; ", failures.ToArray()));
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
            JObject envelope = SessionData.Pack(items, refusals);
            if (refusals.Count > 0) failures.Add("round trip: refused " + string.Join(", ", refusals.ToArray()));

            GameSession sent = new GameSession();
            sent.GenerateSessionID();
            SessionData.AttachForTest(sent, envelope);
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
            JObject keys = SessionData.Unpack(received, out problem);
            if (keys == null)
            {
                failures.Add("round trip: envelope did not unpack (" + (problem ?? "absent") + ")");
                return "round trip incomplete";
            }
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
            }, refusals);
            JObject keys = (JObject)env["keys"];
            if (refusals.Count != 1 || refusals[0].IndexOf("'ectype.selftest/big'", StringComparison.Ordinal) < 0)
                failures.Add("caps: the over-cap key was not refused by name (" + string.Join(", ", refusals.ToArray()) + ")");
            if (keys["ectype.selftest/big"] != null || keys["ectype.selftest/small"] == null)
                failures.Add("caps: the per-key refusal changed the wrong keys");

            // A key that would take the envelope over the cap.
            refusals.Clear();
            string six = new string('y', 6000);
            env = SessionData.Pack(new List<SessionData.PackItem>
            {
                Item("ectype.selftest/a", JToken.FromObject(six), SessionData.EnvelopeCapBytes),
                Item("ectype.selftest/b", JToken.FromObject(six), SessionData.EnvelopeCapBytes),
                Item("ectype.selftest/c", JToken.FromObject(six), SessionData.EnvelopeCapBytes),
            }, refusals);
            keys = (JObject)env["keys"];
            if (refusals.Count != 1 || refusals[0].IndexOf("'ectype.selftest/c'", StringComparison.Ordinal) < 0)
                failures.Add("caps: the overflowing key was not refused by name (" + string.Join(", ", refusals.ToArray()) + ")");
            if (keys["ectype.selftest/a"] == null || keys["ectype.selftest/b"] == null || keys["ectype.selftest/c"] != null)
                failures.Add("caps: the envelope cap kept the wrong keys");
            if (SessionData.Bytes(env) > SessionData.EnvelopeCapBytes)
                failures.Add("caps: the packed envelope is " + SessionData.Bytes(env) + " bytes");

            // Receipt refuses what the sender should not have sent.
            string problem;
            JObject huge = SessionData.Wrap(new JObject { ["k"] = new string('z', SessionData.EnvelopeCapBytes) });
            if (SessionData.Unpack(huge, out problem) != null || problem == null)
                failures.Add("caps: an oversize envelope was accepted on receipt");
            JObject wrongVersion = new JObject { ["v"] = 99, ["keys"] = new JObject() };
            if (SessionData.Unpack(wrongVersion, out problem) != null || problem == null)
                failures.Add("caps: an envelope of another format was accepted");
            if (SessionData.Unpack(null, out problem) != null || problem != null)
                failures.Add("caps: an absent envelope was reported as a problem");
        }

        private static void Sidecar(List<string> failures)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ectype-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                JObject keys = new JObject { ["ectype.selftest/float"] = 0.25f, ["ectype.selftest/text"] = "saved" };
                string id = Guid.NewGuid().ToString();
                string error;
                if (!SessionData.WriteSidecar(dir, id, keys, out error))
                    failures.Add("sidecar: write failed: " + error);
                JObject back = SessionData.ReadSidecar(dir, id, out error);
                if (back == null || !JToken.DeepEquals(back, keys))
                    failures.Add("sidecar: read back " + (back != null ? back.ToString(Formatting.None) : "nothing") +
                                 (error != null ? " (" + error + ")" : ""));
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
                                 File.Exists(notes);
                if (removed != 3 || !keptRight)
                    failures.Add("sidecar: prune removed " + removed + " (expected 3: two past 30 days, one past " +
                                 "the newest 3) or kept the wrong files");
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
            string raw = SessionData.PackRoomValue(7, JToken.FromObject(1.5f));
            JToken v = SessionData.UnpackRoomValue(raw, 7, SessionData.DefaultKeyCapBytes, out problem);
            if (v == null || v.ToObject<float>() != 1.5f)
                failures.Add("stamp: the master's value did not read (" + problem + ")");
            if (SessionData.UnpackRoomValue(raw, 8, SessionData.DefaultKeyCapBytes, out problem) != null)
                failures.Add("stamp: a value stamped by another actor was accepted");
            if (SessionData.UnpackRoomValue(42, 7, SessionData.DefaultKeyCapBytes, out problem) != null)
                failures.Add("stamp: a non-string room value was accepted");
            string big = SessionData.PackRoomValue(7, JToken.FromObject(new string('q', 5000)));
            if (SessionData.UnpackRoomValue(big, 7, SessionData.DefaultKeyCapBytes, out problem) != null)
                failures.Add("stamp: an over-cap room value was accepted");
        }

        private static SessionData.PackItem Item(string key, JToken token, int cap = SessionData.DefaultKeyCapBytes)
        {
            return new SessionData.PackItem { Key = key, Token = token, Cap = cap };
        }
    }
}
