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
    /// - Host room values: registration refuses a Unity type, a delegate, a key outside the
    ///   identifier rule and a default over the cap; a value is copied on encoding and keeps a
    ///   date-shaped string and a long decimal exact; the host's copy batches the writes of a frame
    ///   (a later write of a key replaces an earlier one, an unchanged one writes nothing); a new
    ///   host keeps what it held and puts back the keys the room holds differently; another
    ///   player's write is put back only when it differs; unsent writes are dropped when the host
    ///   role is lost; the writer rule takes the master and an unreported writer only.
    /// The capture point itself (GameSessionManager.PrepareNextSession) needs a live session start
    /// and is outside this test.
    /// </summary>
    internal static class SessionDataSelfTest
    {
        private const int TestActor = 7;
        private const string DateShaped = "2026-10-25T12:00:00";
        private const decimal LongDecimal = 1.2345678901234567890123456789m;

        private sealed class WithVector
        {
            public UnityEngine.Vector3 Where = default(UnityEngine.Vector3);
        }

        private sealed class PlainData
        {
            public int Count = 0;
            public List<string> Names = null;
            [JsonIgnore] public UnityEngine.Vector3 Ignored = default(UnityEngine.Vector3);
        }
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
            Run("exact text", Exact, failures);
            Run("host room values", HostRoom, failures);

            if (failures.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: session data passed (" + roundTrip + "; size caps; " +
                                   "writer rule; sidecar write/read/prune; host-value stamp; type checks; exact text; " +
                                   "host room values).");
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
                Item("ectype.selftest/date", JToken.FromObject(DateShaped, SessionData.Json)),
                Item("ectype.selftest/decimal", JToken.FromObject(LongDecimal, SessionData.Json)),
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

            if (!sinkRan || received == null) failures.Add("round trip: the FromJObject postfix did not run or saw no envelope");
            // The client re-reads the envelope from the string the host sent, as the event-9 path does.
            received = SessionData.ExtractEnvelope(wire);
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
            if (keys["ectype.selftest/float"] != null && keys["ectype.selftest/float"].ToObject<float>(SessionData.Json) != 0.75f)
                failures.Add("round trip: the float did not read back as 0.75");
            if (keys["ectype.selftest/date"] == null || keys["ectype.selftest/date"].ToObject<string>(SessionData.Json) != DateShaped)
                failures.Add("round trip: the date-shaped string changed");
            if (keys["ectype.selftest/decimal"] == null || keys["ectype.selftest/decimal"].ToObject<decimal>(SessionData.Json) != LongDecimal)
                failures.Add("round trip: the long decimal changed");
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
            // The documented limit: any number not in the room passes from the cache.
            Expect(failures, "cached, stamped by an actor that never joined", SessionData.WriterProblem(0, 9999, 1, inRoom), true);
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
            if (SessionData.TypeProblem(typeof(List<UnityEngine.Vector3>), false) == null ||
                SessionData.TypeProblem(typeof(Dictionary<string, UnityEngine.Color>), false) == null ||
                SessionData.TypeProblem(typeof(List<UnityEngine.Vector3>[]), false) == null ||
                SessionData.TypeProblem(typeof(WithVector), false) == null)
                failures.Add("types: a Unity type inside a generic, an array or a field was accepted");
            if (SessionData.TypeProblem(typeof(PlainData), false) != null ||
                SessionData.TypeProblem(typeof(decimal?), true) != null)
                failures.Add("types: plain data with an ignored member, or a nullable decimal, was refused");
        }

        /// <summary>A date-shaped string and a decimal with more digits than a double carries come
        /// back unchanged through every reader: the envelope from the wire string, the room
        /// property and the sidecar.</summary>
        private static void Exact(List<string> failures)
        {
            JObject keys = new JObject
            {
                ["date"] = JToken.FromObject(DateShaped, SessionData.Json),
                ["decimal"] = JToken.FromObject(LongDecimal, SessionData.Json),
            };
            string wire = new JObject { ["other"] = 1, [SessionData.EnvelopeKey] = SessionData.Wrap(keys, TestActor) }
                .ToString(Formatting.None);
            string problem;
            int stamp;
            JObject back = SessionData.Unpack(SessionData.ExtractEnvelope(wire), out problem, out stamp);
            CheckExact(failures, "envelope", back);

            string raw = SessionData.PackRoomValue(TestActor, keys);
            CheckExact(failures, "room property",
                       SessionData.UnpackRoomValue(raw, TestActor, HostValueRoom.CapBytes, out problem) as JObject);
            JToken roomDate = SessionData.UnpackRoomValue(raw, TestActor, HostValueRoom.CapBytes, out problem)["date"];
            if (!SessionData.TokenFits(roomDate, typeof(string)))
                failures.Add("exact text: a date-shaped string in the room property does not fit a string setting");

            string dir = Path.Combine(Path.GetTempPath(), "ectype-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                string id = Guid.NewGuid().ToString();
                string error;
                SessionData.WriteSidecar(dir, id, keys, out error);
                CheckExact(failures, "sidecar", SessionData.ReadSidecar(dir, id, out error));
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch { }
            }
        }

        private sealed class Pick
        {
            public List<int> Types = new List<int>();
            public string When = null;
            public decimal Exact = 0m;
        }

        private static void HostRoom(List<string> failures)
        {
            string key = EctypePluginInfo.PLUGIN_GUID + "/selftest-room";
            if (SessionData.HostRoomValueProblem(key, new List<int>(), SessionData.DefaultKeyCapBytes) != null ||
                SessionData.HostRoomValueProblem(key, new Pick(), SessionData.DefaultKeyCapBytes) != null ||
                SessionData.HostRoomValueProblem(key, 3, SessionData.DefaultKeyCapBytes) != null)
                failures.Add("host room: plain data was refused");
            if (SessionData.HostRoomValueProblem(key, default(UnityEngine.Vector3), SessionData.DefaultKeyCapBytes) == null ||
                SessionData.HostRoomValueProblem(key, new List<UnityEngine.Color>(), SessionData.DefaultKeyCapBytes) == null ||
                SessionData.HostRoomValueProblem<Action>(key, null, SessionData.DefaultKeyCapBytes) == null)
                failures.Add("host room: a Unity type or a delegate was accepted");
            if (SessionData.HostRoomValueProblem("no-guid-here", 1, SessionData.DefaultKeyCapBytes) == null ||
                SessionData.HostRoomValueProblem("com.example.notloaded/x", 1, SessionData.DefaultKeyCapBytes) == null)
                failures.Add("host room: a key outside the identifier rule was accepted");
            if (SessionData.HostRoomValueProblem(key, new string('x', 100), 50) == null)
                failures.Add("host room: a default over the cap was accepted");

            Pick pick = new Pick { Types = new List<int> { 1, 2 }, When = DateShaped, Exact = LongDecimal };
            string json;
            Pick copy;
            string problem = HostRoomValue<Pick>.Encode(pick, SessionData.DefaultKeyCapBytes, out json, out copy);
            if (problem != null) failures.Add("host room: encoding a plain class failed: " + problem);
            else
            {
                pick.Types.Add(3);
                if (copy.Types.Count != 2 || ReferenceEquals(copy.Types, pick.Types))
                    failures.Add("host room: the held value shares the object it was set from");
                Pick back;
                string error;
                if (!HostRoomValue<Pick>.Decode(json, out back, out error) || back.When != DateShaped ||
                    back.Exact != LongDecimal)
                    failures.Add("host room: a date-shaped string or a long decimal changed through the room text");
            }
            if (HostRoomValue<string>.Encode(new string('x', 100), 50, out json, out string unused) == null)
                failures.Add("host room: a value over its cap was encoded");

            // Batching: the writes of a frame leave together, the latest value of each key.
            HostRoomCopy host = new HostRoomCopy();
            host.Write("a", "1");
            host.Write("b", "[2]");
            host.Write("a", "3");
            List<KeyValuePair<string, string>> batch = host.TakeBatch(0f);
            if (batch.Count != 2 || batch[0].Key != "a" || batch[0].Value != "3" || batch[1].Key != "b")
                failures.Add("host room: a frame's writes did not batch into one update of the latest values");
            if (host.TakeBatch(0f).Count != 0 || host.Write("a", "3") || host.PendingCount != 0)
                failures.Add("host room: an unchanged value was written again");
            if (host.TotalBytesWith("a", "12345") != 5 + 3)
                failures.Add("host room: the total size counted a replaced value");

            // A batch Photon did not send is pending again, ahead of later writes, with the
            // values held when it is next taken.
            host.Write("c", "4");
            batch = host.TakeBatch(0f);
            host.Write("a", "5");
            host.Write("c", "6");
            host.Requeue(batch);
            batch = host.TakeBatch(0f);
            if (batch.Count != 2 || batch[0].Key != "c" || batch[0].Value != "6" || batch[1].Key != "a")
                failures.Add("host room: an unsent batch was not sent again first, with the latest values");

            // Another player's write: put back only when it differs, at most once per key in
            // PutBackSeconds; an own write in the meantime leaves at once.
            float t = 10f;
            if (!host.Foreign("a", "9", t) || host.Foreign("b", "[2]", t) || !host.Foreign("b", 5, t))
                failures.Add("host room: a foreign write was judged wrongly");
            batch = host.TakeBatch(t);
            if (batch.Count != 2 || batch[0].Value != "5" || batch[1].Value != "[2]")
                failures.Add("host room: the host did not put its own values back");
            host.Foreign("a", "9", t + 0.1f);
            host.Foreign("b", "9", t + 0.1f);
            if (host.TakeBatch(t + 0.1f).Count != 0 || host.PendingCount != 2)
                failures.Add("host room: a key was put back twice within the throttle");
            host.Write("b", "[7]");
            batch = host.TakeBatch(t + 0.2f);
            if (batch.Count != 1 || batch[0].Key != "b" || batch[0].Value != "[7]")
                failures.Add("host room: an own write waited behind a throttled put-back");
            batch = host.TakeBatch(t + HostRoomCopy.PutBackSeconds);
            if (batch.Count != 1 || batch[0].Key != "a" || host.PendingCount != 0)
                failures.Add("host room: a throttled put-back did not leave once due");

            // Provenance: a value is Master only from a write that named the master; the join
            // response is unverified; a value that did not read leaves the held one unverified.
            HostRoomCopy client = new HostRoomCopy();
            client.Took("j", "1", false);
            client.Took("a", "1", true);
            client.Took("b", "[2]", true);
            client.Refused("b", HostRoomRefusal.Writer);
            client.Took("r", "1", true);
            client.Refused("r", HostRoomRefusal.Read);
            if (client.Source("j") != HostRoomSource.Unverified || client.Source("a") != HostRoomSource.Master ||
                client.Source("b") != HostRoomSource.Master || client.Refusal("b") != HostRoomRefusal.Writer ||
                client.Source("r") != HostRoomSource.Unverified || client.Refusal("r") != HostRoomRefusal.Read ||
                client.Held("r") != "1")
                failures.Add("host room: provenance was recorded wrongly");

            // A client that becomes host keeps what it holds, the join response's value included,
            // and puts back a refused forgery; takes the room's value without a write for an
            // unverified key the room holds differently; and leaves alone a room value it could
            // not read.
            Dictionary<string, object> room = new Dictionary<string, object>
            {
                { "j", "1" }, { "a", "1" }, { "b", "[9]" }, { "r", "{\"newer\":1}" }, { "c", "5" },
            };
            HostRoomAdoption adopted = client.Adopt(new[] { "j", "a", "b", "r", "c", "d" },
                                                    k => room.TryGetValue(k, out object raw) ? raw : null);
            batch = client.TakeBatch(0f);
            if (adopted.Back.Count != 1 || adopted.Back[0] != "b" || adopted.FromRoom.Count != 1 ||
                adopted.FromRoom[0] != "c" || adopted.Standing.Count != 1 ||
                adopted.Standing[0] != "r" || adopted.Held != 4 || batch.Count != 1 ||
                batch[0].Key != "b" || batch[0].Value != "[2]")
                failures.Add("host room: adoption did not follow provenance");
            if (client.Held("j") != "1" || client.Held("c") != "5" || client.Source("j") != HostRoomSource.Own ||
                client.Source("a") != HostRoomSource.Own ||
                client.Source("r") != HostRoomSource.Unverified || client.Foreign("r", "x", 0f))
                failures.Add("host room: adoption left the wrong values held or guarded");

            // Losing the host role: a value the room took from it stays; one it never saw the room
            // take goes back to the last the master wrote; one with no master write known is read
            // from the room again.
            client.Echo("j", null);
            client.Write("a", "7");
            client.Write("j", "8");
            client.TakeBatch(0f);
            client.Echo("j", "8");
            client.Write("c", "6");
            HostRoomRelease released = client.Release(new[] { "j", "a", "b", "r", "c", "d" });
            if (client.PendingCount != 0 || released.Reverted.Count != 1 || released.Reverted[0] != "a" ||
                client.Held("a") != "1" || client.Held("j") != "8" || client.Source("j") != HostRoomSource.Master ||
                released.FromRoom.Count != 2 || released.FromRoom[0] != "c" || released.FromRoom[1] != "d")
                failures.Add("host room: losing the host role kept a value the room never took");

            if (!HostRoomCopy.WriterAccepted(2, 2) || HostRoomCopy.WriterAccepted(3, 2) ||
                !HostRoomCopy.WriterAccepted(0, 2) || HostRoomCopy.WriterAccepted(-1, 2))
                failures.Add("host room: the writer rule gave the wrong answer");
        }

        private static void CheckExact(List<string> failures, string where, JObject keys)
        {
            if (keys == null)
            {
                failures.Add("exact text: " + where + " did not read");
                return;
            }
            string date = keys["date"] != null ? keys["date"].ToObject<string>(SessionData.Json) : null;
            if (date != DateShaped) failures.Add("exact text: " + where + " changed the date-shaped string to " + date);
            decimal d = keys["decimal"] != null ? keys["decimal"].ToObject<decimal>(SessionData.Json) : 0m;
            if (d != LongDecimal) failures.Add("exact text: " + where + " changed the decimal to " + d);
        }

        private static SessionData.PackItem Item(string key, JToken token, int cap = SessionData.DefaultKeyCapBytes)
        {
            return new SessionData.PackItem { Key = key, Token = token, Cap = cap };
        }
    }
}
