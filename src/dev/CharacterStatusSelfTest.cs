using System;
using System.Collections.Generic;

using CG.Game;
using CG.Game.Player;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// Dev build self-tests for character status requests and presentation copies.
    ///
    /// Offline, at load:
    /// - the request, answer, announcement and end messages, as their builders make them,
    ///   match their declared layouts and survive a Protocol18 round trip;
    /// - the judge, with synthetic views of the two characters, refuses each reason on its own
    ///   input: no template, a template without AcceptsRequests, a duration below MinSeconds and
    ///   a NaN one, a zero and a NaN magnitude, a dead target, a requester with no character, a
    ///   dead requester, a seated requester, a requester or target in a clone tube, Hostile from
    ///   the same side and Friendly from another, 5.5 m against MaxRange 5 on the target's
    ///   client, a blocked line; and accepts 5.0 m, 5.5 m from the requester's view (its limit is
    ///   6 m), any distance with no MaxRange, a seat or a tube the template does not refuse, and
    ///   cuts 100 s and magnitude 5 to MaxSeconds and MaxMagnitude;
    /// - requester limits: with MaxRangeFor 3, MinIntervalFor 0.5 and MaxMagnitudeFor 1.5 over
    ///   constants 2, 1 and 1, the limits are the functions' values, 2.5 m passes where the
    ///   constant refuses it, 3.9 m passes from the requester's view (its limit is 4 m), and
    ///   magnitude 5 is cut to 1.5; with no requester character the functions are not called and
    ///   the constants stand; a NaN range, a negative interval, a throwing interval and a magnitude
    ///   of 0 each fall back to their constant;
    /// - the rate bucket: with Burst 2 per 1 s it takes two at once, refuses a third and one at
    ///   0.5 s, takes one at 1.01 s and keeps ids apart; requests sent 2 s apart that arrive
    ///   1.95 s apart pass the target's bucket (Burst + 1) where a bucket of Burst alone refuses
    ///   the second; the per-requester cap takes 20 at once and refuses the 21st;
    /// - a request handle completed here runs a callback added after completion at once, and
    ///   ignores a second completion;
    /// - the audience rule answers each Visibility for a viewer on the applier's side, on the
    ///   target's side, on a third side and of unknown side;
    /// - registration refuses Burst 0, a negative MaxRange, a NaN MinInterval, MaxSeconds 0,
    ///   MinSeconds above MaxSeconds and MaxMagnitude 0.
    ///
    /// In game, once this client has a character: requests are driven through the receive
    /// path with synthetic senders against this client's own character:
    /// - actor 9999 (no character here) is refused SenderMissing, a key without AcceptsRequests
    ///   NotAccepted, a request naming another actor InvalidTarget;
    /// - this client's own actor, at 0 m, is accepted: the status is active, RequesterOf names
    ///   the actor, and the movement stat it carries changes; MaxMagnitudeFor is called with this
    ///   client's character and cuts the magnitude to 0.75, and OnRequestApplied runs once with
    ///   this client's character as target and requester, magnitude 0.75 and one stack; a second request inside
    ///   MinInterval beyond the target's burst is refused RateLimited and leaves one stack;
    /// - a shorter, stronger request on a running status keeps its end and raises its effect;
    /// - an announcement from actor 9999 naming this client's character is refused Authority,
    ///   and an answer for a request nobody sent is ignored;
    /// - a presentation copy on this client's character leaves the movement stat and the
    ///   OnApplied count as they were, is not IsActive, and answers IsPresented until hidden;
    /// - an OnRemoved that applies the same key again starts a fresh status with one stack and
    ///   leaves no modifier of the ended one;
    /// - the death hook is a postfix on the life-state setter, and ends a running status and a
    ///   copy on the character;
    /// - with another player's character in the room, TryRemove on it answers Authority.
    /// The templates accept requests only from 0.01 m, so no other player can use them.
    /// </summary>
    internal static class CharacterStatusSelfTest
    {
        private const string AcceptKey = EctypePluginInfo.PLUGIN_GUID + "/dev.selftest-char-accept";
        private const string PlainKey = EctypePluginInfo.PLUGIN_GUID + "/dev.selftest-char-plain";
        private const string ExtendKey = EctypePluginInfo.PLUGIN_GUID + "/dev.selftest-char-extend";
        private const string PresentedKey = EctypePluginInfo.PLUGIN_GUID + "/dev.selftest-char-presented";
        private const string RelapseKey = EctypePluginInfo.PLUGIN_GUID + "/dev.selftest-char-relapse";
        private const int Stranger = 9999;

        private static bool _done;
        private static float _nextTry;

        // ------------------------------------------------------------------ offline

        internal static void RunOffline()
        {
            List<string> fails = new List<string>();
            try
            {
                Wire(fails);
                JudgeCases(fails);
                LimitCases(fails);
                RateCases(fails);
                HandleCases(fails);
                AudienceCases(fails);
                ValidateCases(fails);
            }
            catch (Exception e)
            {
                fails.Add("threw " + e);
            }
            if (fails.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: character status requests passed - the " +
                                   "request, answer, announcement and end messages round-trip, every " +
                                   "refusal reason fired on its own input, MasterOnly was judged by the " +
                                   "actor before the requester's character and on a request to one's own " +
                                   "character, requester limits replaced the " +
                                   "constants and fell back on bad values, the rate buckets held with " +
                                   "jitter and the per-requester cap, a completed handle ran its callback, " +
                                   "each audience rule chose its viewers, and six bad templates were refused.");
            else
                Plugin.Log.LogError("Ectype: dev self-test: character status requests FAILED - " +
                                    string.Join("; ", fails.ToArray()) + ".");
        }

        private static void Wire(List<string> fails)
        {
            RoundTrip("request", EctypeNet.CharacterStatusRequest,
                      CharacterStatusSync.RequestArgs(42, 7, EctypePluginInfo.PLUGIN_GUID + "/dev.wire", 2.5f, 0.75f, 1234), fails);
            RoundTrip("answer", EctypeNet.CharacterStatusResult,
                      CharacterStatusSync.ResultArgs(42, CharacterStatusRefusal.OutOfRange), fails);
            RoundTrip("announcement", EctypeNet.CharacterStatusApplied,
                      CharacterStatusSync.AppliedArgs(7, EctypePluginInfo.PLUGIN_GUID + "/dev.wire", 123456, 0.75f), fails);
            RoundTrip("end", EctypeNet.CharacterStatusApplied,
                      CharacterStatusSync.EndedArgs(7, EctypePluginInfo.PLUGIN_GUID + "/dev.wire"), fails);
            object[] req = CharacterStatusSync.RequestArgs(1, 7, "k", 2.5f, 1f, 0);
            if ((int)req[3] != 2500) fails.Add("2.5 s travelled as " + req[3] + " ms, expected 2500");
        }

        private static void RoundTrip(string name, byte subtype, object[] args, List<string> fails)
        {
            if (!EctypeNet.MatchesLayout(subtype, args))
            {
                fails.Add("the " + name + " message's fields match no declared layout");
                return;
            }
            Protocol18 protocol = new Protocol18();
            object back = protocol.Deserialize(protocol.Serialize(EctypeNet.Pack(subtype, args)));
            byte got;
            object[] gotArgs;
            if (!EctypeNet.TryUnpack(back, out got, out gotArgs) || got != subtype ||
                !EctypeNet.MatchesLayout(subtype, gotArgs))
            {
                fails.Add("the " + name + " message did not survive the round trip");
                return;
            }
            for (int i = 0; i < args.Length; i++)
                if (!args[i].Equals(gotArgs[i]))
                    fails.Add("the " + name + " message's field " + i + " " + args[i] + " came back as " + gotArgs[i]);
        }

        private static CharacterStatusDef Strict()
        {
            return new CharacterStatusDef(EctypePluginInfo.PLUGIN_GUID + "/dev.judge")
            {
                AcceptsRequests = true, Stance = StatusStance.Hostile, MaxRange = 5f,
                RequiresLineOfSight = true, RefuseSeated = true, RefuseInTube = true,
                MinInterval = 1f, Burst = 2,
            };
        }

        private static CharacterRequestFacts Clear()
        {
            return new CharacterRequestFacts
            {
                VictimAlive = true, SourceKnown = true, SourceAlive = true, Hostile = true,
                Distance = 3f, LineClear = true,
            };
        }

        private static void Expect(string label, CharacterStatusDef def, CharacterRequestFacts f,
                                   float seconds, float magnitude, CharacterStatusRefusal want,
                                   List<string> fails, bool requesterView = false)
        {
            CharacterStatusRefusal got = CharacterStatusSync.Judge(def, ref seconds, ref magnitude, f,
                                                                   CharacterStatusSync.Limits(def, null, false),
                                                                   requesterView);
            if (got != want) fails.Add(label + " answered " + got + ", expected " + want);
        }

        private static void JudgeCases(List<string> fails)
        {
            CharacterStatusDef d = Strict();
            CharacterRequestFacts f;
            Expect("a clear request", d, Clear(), 3f, 1f, CharacterStatusRefusal.None, fails);
            Expect("no template", null, Clear(), 3f, 1f, CharacterStatusRefusal.NotRegistered, fails);
            Expect("a key without AcceptsRequests", new CharacterStatusDef(EctypePluginInfo.PLUGIN_GUID + "/dev.plain"), Clear(), 3f, 1f,
                   CharacterStatusRefusal.NotAccepted, fails);
            Expect("0.1 s against MinSeconds 0.25", d, Clear(), 0.1f, 1f, CharacterStatusRefusal.InvalidDuration, fails);
            Expect("NaN s", d, Clear(), float.NaN, 1f, CharacterStatusRefusal.InvalidDuration, fails);
            Expect("magnitude 0", d, Clear(), 3f, 0f, CharacterStatusRefusal.InvalidMagnitude, fails);
            Expect("NaN magnitude", d, Clear(), 3f, float.NaN, CharacterStatusRefusal.InvalidMagnitude, fails);
            f = Clear(); f.VictimAlive = false;
            Expect("a dead target", d, f, 3f, 1f, CharacterStatusRefusal.TargetDead, fails);
            f = Clear(); f.SourceKnown = false;
            Expect("no requester character", d, f, 3f, 1f, CharacterStatusRefusal.SenderMissing, fails);
            f = Clear(); f.SourceAlive = false;
            Expect("a dead requester", d, f, 3f, 1f, CharacterStatusRefusal.SenderDead, fails);
            f = Clear(); f.SourceSeated = true;
            Expect("a seated requester", d, f, 3f, 1f, CharacterStatusRefusal.SenderSeated, fails);
            f = Clear(); f.SourceInTube = true;
            Expect("a requester in a tube", d, f, 3f, 1f, CharacterStatusRefusal.InCloneTube, fails);
            f = Clear(); f.VictimInTube = true;
            Expect("a target in a tube", d, f, 3f, 1f, CharacterStatusRefusal.InCloneTube, fails);
            CharacterStatusDef lenient = Strict();
            lenient.RefuseSeated = false;
            lenient.RefuseInTube = false;
            f = Clear(); f.SourceSeated = true; f.SourceInTube = true; f.VictimInTube = true;
            Expect("a seat and tubes the template does not refuse", lenient, f, 3f, 1f,
                   CharacterStatusRefusal.None, fails);
            f = Clear(); f.Hostile = false;
            Expect("Hostile from the same side", d, f, 3f, 1f, CharacterStatusRefusal.Stance, fails);
            CharacterStatusDef friendly = Strict();
            friendly.Stance = StatusStance.Friendly;
            Expect("Friendly from another side", friendly, Clear(), 3f, 1f, CharacterStatusRefusal.Stance, fails);
            CharacterStatusDef masterOnly = Strict();
            masterOnly.Stance = StatusStance.MasterOnly;
            Expect("MasterOnly from a requester that is not the master", masterOnly, Clear(), 3f, 1f,
                   CharacterStatusRefusal.Stance, fails);
            f = Clear(); f.SourceIsMaster = true; f.Hostile = false;
            Expect("MasterOnly from the master on the target's side", masterOnly, f, 3f, 1f,
                   CharacterStatusRefusal.None, fails);
            // MasterOnly is judged by the actor before the requester's character.
            f = Clear(); f.SourceKnown = false; f.SourceAlive = false; f.LineClear = false;
            Expect("MasterOnly from a requester that is not the master and has no character", masterOnly, f, 3f, 1f,
                   CharacterStatusRefusal.Stance, fails);
            f.SourceIsMaster = true;
            Expect("MasterOnly from a master with no character", masterOnly, f, 3f, 1f,
                   CharacterStatusRefusal.None, fails);
            f = Clear(); f.SourceIsMaster = true; f.SourceAlive = false; f.SourceSeated = true;
            f.SourceInTube = true; f.Distance = 1000f; f.LineClear = false;
            Expect("MasterOnly from a dead, seated, far master in a tube", masterOnly, f, 3f, 1f,
                   CharacterStatusRefusal.None, fails);
            f = Clear(); f.SourceIsMaster = true; f.VictimInTube = true;
            Expect("MasterOnly from the master to a target in a tube", masterOnly, f, 3f, 1f,
                   CharacterStatusRefusal.InCloneTube, fails);
            f = Clear(); f.SourceIsMaster = true; f.VictimAlive = false;
            Expect("MasterOnly from the master to a dead target", masterOnly, f, 3f, 1f,
                   CharacterStatusRefusal.TargetDead, fails);
            if (CharacterStatusSync.SelfRequestVerdict(masterOnly, false) != CharacterStatusRefusal.Stance ||
                CharacterStatusSync.SelfRequestVerdict(masterOnly, true) != CharacterStatusRefusal.None ||
                CharacterStatusSync.SelfRequestVerdict(d, false) != CharacterStatusRefusal.None)
                fails.Add("a request on this client's own character answered MasterOnly not master " +
                          CharacterStatusSync.SelfRequestVerdict(masterOnly, false) + ", master " +
                          CharacterStatusSync.SelfRequestVerdict(masterOnly, true) + ", Hostile not master " +
                          CharacterStatusSync.SelfRequestVerdict(d, false) + "; expected Stance, None, None");
            f = Clear(); f.Distance = 5.5f;
            Expect("5.5 m on the target's client", d, f, 3f, 1f, CharacterStatusRefusal.OutOfRange, fails);
            Expect("5.5 m from the requester's view", d, f, 3f, 1f, CharacterStatusRefusal.None, fails, true);
            f = Clear(); f.Distance = 6.5f;
            Expect("6.5 m from the requester's view", d, f, 3f, 1f, CharacterStatusRefusal.OutOfRange, fails, true);
            f = Clear(); f.Distance = 5f;
            Expect("exactly MaxRange", d, f, 3f, 1f, CharacterStatusRefusal.None, fails);
            CharacterStatusDef open = Strict();
            open.MaxRange = 0f;
            f = Clear(); f.Distance = 1000f;
            Expect("1000 m with no MaxRange", open, f, 3f, 1f, CharacterStatusRefusal.None, fails);
            f = Clear(); f.LineClear = false;
            Expect("a blocked line", d, f, 3f, 1f, CharacterStatusRefusal.NoLineOfSight, fails);
            CharacterStatusDef blind = Strict();
            blind.RequiresLineOfSight = false;
            Expect("a blocked line with no line-of-sight check", blind, f, 3f, 1f, CharacterStatusRefusal.None, fails);

            float s = 100f, m = 5f;
            CharacterStatusRefusal cut = CharacterStatusSync.Judge(d, ref s, ref m, Clear(),
                                                                   CharacterStatusSync.Limits(d, null, false), false);
            if (cut != CharacterStatusRefusal.None || s != d.MaxSeconds || m != d.MaxMagnitude)
                fails.Add("100 s at magnitude 5 answered " + cut + " with " + s + " s at " + m +
                          ", expected None cut to " + d.MaxSeconds + " s at " + d.MaxMagnitude);
        }

        /// <summary>The functions ignore their argument, so the limits are evaluated here with no
        /// character and <c>sourceKnown</c> forced true.</summary>
        private static void LimitCases(List<string> fails)
        {
            CharacterStatusDef d = Strict();
            d.MaxRange = 2f;
            d.MinInterval = 1f;
            d.MaxMagnitude = 1f;
            d.MaxRangeFor = p => 3f;
            d.MinIntervalFor = p => 0.5f;
            d.MaxMagnitudeFor = p => 1.5f;
            CharacterRequestLimits lim = CharacterStatusSync.Limits(d, null, true);
            if (lim.MaxRange != 3f || lim.MinInterval != 0.5f || lim.MaxMagnitude != 1.5f)
                fails.Add("requester limits came out " + lim.MaxRange + " m, " + lim.MinInterval + " s, x" +
                          lim.MaxMagnitude + ", expected 3 m, 0.5 s, x1.5");

            CharacterRequestFacts f = Clear();
            f.Distance = 2.5f;
            float s = 3f, m = 5f;
            CharacterStatusRefusal far = CharacterStatusSync.Judge(d, ref s, ref m, f, lim, false);
            if (far != CharacterStatusRefusal.None || m != 1.5f)
                fails.Add("2.5 m at magnitude 5 under MaxRangeFor 3 answered " + far + " at x" + m +
                          ", expected None cut to x1.5");
            s = 3f; m = 1f;
            CharacterStatusRefusal fixedRange = CharacterStatusSync.Judge(d, ref s, ref m, f,
                CharacterStatusSync.Limits(d, null, false), false);
            if (fixedRange != CharacterStatusRefusal.OutOfRange)
                fails.Add("2.5 m against the constant MaxRange 2 answered " + fixedRange + ", expected OutOfRange");
            f.Distance = 3.9f;
            s = 3f; m = 1f;
            CharacterStatusRefusal edge = CharacterStatusSync.Judge(d, ref s, ref m, f, lim, true);
            if (edge != CharacterStatusRefusal.None)
                fails.Add("3.9 m from the requester's view under MaxRangeFor 3 answered " + edge + ", expected None");

            int calls = 0;
            CharacterStatusDef counted = Strict();
            counted.MaxRangeFor = p => { calls++; return 9f; };
            CharacterRequestLimits none = CharacterStatusSync.Limits(counted, null, false);
            if (calls != 0 || none.MaxRange != counted.MaxRange)
                fails.Add("with no requester character the range function ran " + calls + " time(s) and the " +
                          "limit is " + none.MaxRange + " m, expected 0 runs and " + counted.MaxRange + " m");

            CharacterStatusDef bad = Strict();
            bad.MaxRange = 2f;
            bad.MinInterval = 1f;
            bad.MaxMagnitude = 1f;
            bad.MaxRangeFor = p => float.NaN;
            bad.MinIntervalFor = p => -1f;
            bad.MaxMagnitudeFor = p => 0f;
            CharacterRequestLimits back = CharacterStatusSync.Limits(bad, null, true);
            if (back.MaxRange != 2f || back.MinInterval != 1f || back.MaxMagnitude != 1f)
                fails.Add("bad requester limits came out " + back.MaxRange + " m, " + back.MinInterval + " s, x" +
                          back.MaxMagnitude + ", expected the constants 2 m, 1 s, x1");
            bad.MinIntervalFor = p => { throw new InvalidOperationException("dev self-test: a limit function that throws"); };
            if (CharacterStatusSync.Limits(bad, null, true).MinInterval != 1f)
                fails.Add("a throwing MinIntervalFor did not fall back to MinInterval 1");
        }

        private static void RateCases(List<string> fails)
        {
            CharacterStatusRate rate = new CharacterStatusRate();
            bool a = rate.TryTake("1/k", 2, 1f, 10f), b = rate.TryTake("1/k", 2, 1f, 10f), c = rate.TryTake("1/k", 2, 1f, 10f);
            bool half = rate.TryTake("1/k", 2, 1f, 10.5f);
            bool later = rate.TryTake("1/k", 2, 1f, 11.01f);
            bool other = rate.TryTake("2/k", 2, 1f, 10.5f);
            if (!a || !b || c || half || !later || !other)
                fails.Add("the rate bucket answered " + a + ", " + b + ", " + c + " at 0 s, " + half +
                          " at 0.5 s, " + later + " at 1.01 s, " + other + " for another id; expected " +
                          "True, True, False, False, True, True");
            for (int i = 0; i < 20; i++)
                if (!rate.TryTake("3/free", 1, 0f, 10f))
                {
                    fails.Add("an interval of 0 was rate limited");
                    break;
                }

            // Sent 2 s apart (MinInterval 2, Burst 1), arriving 150, 100 and 200 ms late.
            float[] arrive = { 0.15f, 2.10f, 4.20f };
            CharacterStatusRate target = new CharacterStatusRate(), strict = new CharacterStatusRate();
            bool allTarget = true, allStrict = true;
            foreach (float t in arrive)
            {
                allTarget &= target.TryTake("j", 1 + 1, 2f, t);
                allStrict &= strict.TryTake("j", 1, 2f, t);
            }
            if (!allTarget) fails.Add("requests 2 s apart arriving with jitter were refused by the target's bucket");
            if (allStrict) fails.Add("a bucket of Burst alone took the jittered requests, so the case tests nothing");

            CharacterStatusRate cap = new CharacterStatusRate();
            int taken = 0;
            for (int i = 0; i < CharacterStatusSync.SenderBurst + 1; i++)
                if (cap.TryTake("5/*", CharacterStatusSync.SenderBurst, 1f / CharacterStatusSync.SenderBurst, 20f)) taken++;
            if (taken != CharacterStatusSync.SenderBurst)
                fails.Add("the per-requester cap took " + taken + " of " + (CharacterStatusSync.SenderBurst + 1) +
                          " at once, expected " + CharacterStatusSync.SenderBurst);
        }

        private static void HandleCases(List<string> fails)
        {
            CharacterStatusRequest h = new CharacterStatusRequest(null, EctypePluginInfo.PLUGIN_GUID + "/dev.handle");
            h.Complete(CharacterStatusRefusal.Stance);
            int calls = 0;
            CharacterStatusRefusal seen = CharacterStatusRefusal.None;
            h.OnCompleted(r => { calls++; seen = r.Refusal; });
            h.Complete(CharacterStatusRefusal.None);
            if (calls != 1 || seen != CharacterStatusRefusal.Stance || h.Applied || h.Sent || !h.Done)
                fails.Add("a handle completed here ran its callback " + calls + " time(s) with " + seen +
                          " (Applied " + h.Applied + ", Sent " + h.Sent + "), expected once with Stance");
        }

        private static void AudienceCases(List<string> fails)
        {
            List<int> appliers = new List<int> { 1 };
            const int target = 2;
            // Viewer on the applier's side (1), the target's side (2), a third side (3), unknown (-1).
            int[] viewers = { 1, 2, 3, -1 };
            Check(StatusVisibility.All, viewers, appliers, target, new[] { true, true, true, true }, fails);
            Check(StatusVisibility.OwnForce, viewers, appliers, target, new[] { true, false, false, false }, fails);
            Check(StatusVisibility.TargetForce, viewers, appliers, target, new[] { false, true, false, false }, fails);
            Check(StatusVisibility.HostileToTarget, viewers, appliers, target, new[] { true, false, true, false }, fails);
        }

        private static void Check(StatusVisibility rule, int[] viewers, List<int> appliers, int target,
                                  bool[] want, List<string> fails)
        {
            for (int i = 0; i < viewers.Length; i++)
            {
                bool got = CharacterStatusSync.InAudience(rule, viewers[i], appliers, target);
                if (got != want[i])
                    fails.Add(rule + " for a viewer of side " + viewers[i] + " answered " + got);
            }
        }

        private static void ValidateCases(List<string> fails)
        {
            CharacterStatusDef[] bad =
            {
                new CharacterStatusDef(EctypePluginInfo.PLUGIN_GUID + "/dev.bad1") { Burst = 0 },
                new CharacterStatusDef(EctypePluginInfo.PLUGIN_GUID + "/dev.bad2") { MaxRange = -1f },
                new CharacterStatusDef(EctypePluginInfo.PLUGIN_GUID + "/dev.bad3") { MinInterval = float.NaN },
                new CharacterStatusDef(EctypePluginInfo.PLUGIN_GUID + "/dev.bad4") { MaxSeconds = 0f },
                new CharacterStatusDef(EctypePluginInfo.PLUGIN_GUID + "/dev.bad5") { MinSeconds = 5f, MaxSeconds = 2f },
                new CharacterStatusDef(EctypePluginInfo.PLUGIN_GUID + "/dev.bad6") { MaxMagnitude = 0f },
            };
            foreach (CharacterStatusDef d in bad)
                if (CharacterStatusSync.Validate(d) == null) fails.Add(d.Key + " passed validation");
            if (CharacterStatusSync.Validate(Strict()) != null)
                fails.Add("a valid template failed validation: " + CharacterStatusSync.Validate(Strict()));
        }

        // ------------------------------------------------------------------ in game

        internal static void Poll()
        {
            if (_done || !Plugin.PatchesApplied) return;
            if (Time.unscaledTime < _nextTry) return;
            _nextTry = Time.unscaledTime + 5f;
            int me = EnemyStatuses.LocalActor();
            Player character = Topology.CharacterOf(me);
            if (character == null || character.photonView == null || !character.photonView.IsMine) return;
            if (!CharacterStatusSync.Alive(character) || character.Locomotion == null) return;
            _done = true;
            RunInGame(me, character);
        }

        private static float Accel(Player p)
        {
            Vector3 a = p.Locomotion.MotorAcceleration;
            return new Vector3(a.x, 0f, a.z).magnitude;
        }

        private static object[] Req(int targetActor, string key, float seconds, float magnitude)
        {
            return CharacterStatusSync.RequestArgs(1, targetActor, key, seconds, magnitude, 0);
        }

        private static void RunInGame(int me, Player character)
        {
            List<string> fails = new List<string>();
            List<string> notes = new List<string>();
            int applied = 0;
            Player limitArg = null;
            int reqCalls = 0, reqStacks = 0;
            Player reqTarget = null, reqFrom = null;
            float reqMagnitude = 0f;
            try
            {
                // Requests only from 0.01 m: no other player can use these templates.
                CharacterStatuses.Register(new CharacterStatusDef(AcceptKey)
                {
                    AcceptsRequests = true, MaxRange = 0.01f, MinInterval = 60f, Burst = 1, MaxStacks = 3,
                    MaxMagnitudeFor = p => { limitArg = p; return 0.75f; },
                    OnRequestApplied = (t, from, mag, n) =>
                    {
                        if (reqCalls++ > 0) return;
                        reqTarget = t; reqFrom = from; reqMagnitude = mag; reqStacks = n;
                    },
                }.AddStat("MotorAcceleration", "percent", -0.5f));
                CharacterStatuses.Register(new CharacterStatusDef(PlainKey));
                CharacterStatuses.Register(new CharacterStatusDef(ExtendKey)
                {
                    AcceptsRequests = true, MaxRange = 0.01f,
                }.AddStat("MotorAcceleration", "percent", -0.5f));
                CharacterStatusDef presented = new CharacterStatusDef(PresentedKey)
                {
                    Presented = true,
                    OnApplied = (p, n) => applied++,
                }.AddStat("MotorAcceleration", "percent", -0.5f);
                CharacterStatuses.Register(presented);
                CharacterStatusSync.Accepted.Forget(me + "/" + AcceptKey);
                CharacterStatusSync.Flood.Forget(me.ToString());

                int lines = StatusRefusalLog.Lines;
                CharacterStatusRefusal stranger = CharacterStatusSync.HandleRequest(Stranger, Req(me, AcceptKey, 3f, 1f));
                if (stranger != CharacterStatusRefusal.SenderMissing)
                    fails.Add("a request from actor " + Stranger + " answered " + stranger + ", expected SenderMissing");
                CharacterStatusRefusal plain = CharacterStatusSync.HandleRequest(me, Req(me, PlainKey, 3f, 1f));
                if (plain != CharacterStatusRefusal.NotAccepted)
                    fails.Add("a request for a key without AcceptsRequests answered " + plain + ", expected NotAccepted");
                CharacterStatusRefusal elsewhere = CharacterStatusSync.HandleRequest(me, Req(me + 1000, AcceptKey, 3f, 1f));
                if (elsewhere != CharacterStatusRefusal.InvalidTarget)
                    fails.Add("a request naming another actor answered " + elsewhere + ", expected InvalidTarget");
                if (StatusRefusalLog.Lines - lines != 3)
                    fails.Add("three refusals logged " + (StatusRefusalLog.Lines - lines) + " line(s), expected 3");

                float before = Accel(character);
                CharacterStatusRefusal ok = CharacterStatusSync.HandleRequest(me, Req(me, AcceptKey, 3f, 1f));
                float during = Accel(character);
                if (ok != CharacterStatusRefusal.None || !CharacterStatuses.IsActive(character, AcceptKey))
                    fails.Add("a request from this client's own actor answered " + ok + ", expected it to land");
                else if (CharacterStatuses.RequesterOf(character, AcceptKey) != me)
                    fails.Add("RequesterOf answered " + CharacterStatuses.RequesterOf(character, AcceptKey) +
                              ", expected " + me);
                if (!ReferenceEquals(limitArg, character))
                    fails.Add("MaxMagnitudeFor was called with " + (limitArg != null ? limitArg.name : "nothing") +
                              ", expected this client's character");
                if (reqCalls != 1 || !ReferenceEquals(reqTarget, character) || !ReferenceEquals(reqFrom, character) ||
                    reqMagnitude != 0.75f || reqStacks != 1)
                    fails.Add("OnRequestApplied ran " + reqCalls + " time(s) with magnitude " + reqMagnitude + " and " +
                              reqStacks + " stack(s), expected once with this client's character as target and " +
                              "requester, magnitude 0.75 and 1 stack");
                bool statMoved = Math.Abs(during - before) > 0.001f;
                if (statMoved) notes.Add("the accepted request moved MotorAcceleration " + before.ToString("0.##") +
                                         " -> " + during.ToString("0.##"));
                else notes.Add("MotorAcceleration did not move on the accepted request (stat not wired?), so " +
                               "the stat checks are inconclusive");

                // Burst 1 plus the target's one extra token: the second lands, the third is refused.
                CharacterStatusSync.HandleRequest(me, Req(me, AcceptKey, 3f, 1f));
                CharacterStatusRefusal third = CharacterStatusSync.HandleRequest(me, Req(me, AcceptKey, 3f, 1f));
                int stacks = CharacterStatuses.Stacks(character, AcceptKey);
                if (third != CharacterStatusRefusal.RateLimited || stacks != 2)
                    fails.Add("a third request inside MinInterval answered " + third + " with " + stacks +
                              " stack(s), expected RateLimited with 2");
                CharacterStatuses.Remove(character, AcceptKey);
                if (Math.Abs(Accel(character) - before) > 0.001f)
                    fails.Add("removal left MotorAcceleration at " + Accel(character).ToString("0.##") +
                              ", expected " + before.ToString("0.##"));

                // A shorter, stronger request keeps the end and raises the effect.
                CharacterStatusSync.HandleRequest(me, Req(me, ExtendKey, 3f, 0.5f));
                float weak = Accel(character);
                CharacterStatusSync.HandleRequest(me, Req(me, ExtendKey, 0.5f, 1f));
                float strong = Accel(character);
                float left = CharacterStatuses.RemainingSeconds(character, ExtendKey);
                if (left < 2f) fails.Add("a 0.5 s request cut a 3 s status to " + left.ToString("0.00") + " s");
                if (statMoved && !(strong < weak - 0.001f))
                    fails.Add("a stronger request left MotorAcceleration at " + strong.ToString("0.##") +
                              " from " + weak.ToString("0.##") + ", expected lower");

                // An OnRemoved that applies the same key again starts a fresh status and leaks
                // no modifier from the ended one.
                int relapses = 0;
                CharacterStatuses.Register(new CharacterStatusDef(RelapseKey)
                {
                    MaxStacks = 2,
                    OnRemoved = p =>
                    {
                        if (relapses++ == 0 && p != null) CharacterStatuses.Apply(p, RelapseKey, 3f);
                    },
                }.AddStat("MotorAcceleration", "percent", -0.5f));
                CharacterStatuses.Apply(character, RelapseKey, 3f);
                CharacterStatuses.Remove(character, RelapseKey);
                int relapseStacks = CharacterStatuses.Stacks(character, RelapseKey);
                CharacterStatuses.Remove(character, RelapseKey);
                if (relapseStacks != 1)
                    fails.Add("a re-apply from OnRemoved left " + relapseStacks + " stack(s), expected a fresh status with 1");
                if (Math.Abs(Accel(character) - before) > 0.001f)
                    fails.Add("a re-apply from OnRemoved leaked a modifier: MotorAcceleration " +
                              Accel(character).ToString("0.##") + ", expected " + before.ToString("0.##"));

                // The death hook is on the life-state setter.
                Patches info = Harmony.GetPatchInfo(AccessTools.PropertySetter(typeof(Player), "State"));
                bool hooked = false;
                if (info != null)
                    foreach (Patch patch in info.Postfixes)
                        if (patch.PatchMethod != null && patch.PatchMethod.DeclaringType == typeof(CharacterStatusLife))
                            hooked = true;
                if (!hooked) fails.Add("the life-state setter carries no death-hook postfix");

                // Death ends the status, and a copy.
                CharacterStatusCopies.Show(character, presented, PhotonNetwork.ServerTimestamp + 3000);
                int ended = CharacterStatusLife.OnDeath(character);
                if (CharacterStatuses.IsActive(character, ExtendKey) || CharacterStatuses.IsPresented(character, PresentedKey))
                    fails.Add("the death hook left " +
                              (CharacterStatuses.IsActive(character, ExtendKey) ? "the status active" : "the copy shown"));
                else if (ended != 2) fails.Add("the death hook ended " + ended + ", expected 2");
                if (Math.Abs(Accel(character) - before) > 0.001f)
                    fails.Add("the death hook left MotorAcceleration at " + Accel(character).ToString("0.##") +
                              ", expected " + before.ToString("0.##"));
                applied = 0;

                CharacterStatusRefusal forged = CharacterStatusSync.HandleApplied(Stranger,
                    CharacterStatusSync.AppliedArgs(me, PresentedKey, PhotonNetwork.ServerTimestamp + 3000, 1f));
                if (forged != CharacterStatusRefusal.Authority)
                    fails.Add("an announcement from actor " + Stranger + " for this client's character answered " +
                              forged + ", expected Authority");
                CharacterStatusRefusal stray = CharacterStatusSync.HandleResult(Stranger,
                    CharacterStatusSync.ResultArgs(-12345, CharacterStatusRefusal.None));
                if (stray != CharacterStatusRefusal.InvalidTarget)
                    fails.Add("an answer for a request nobody sent answered " + stray + ", expected InvalidTarget");

                float copyBefore = Accel(character);
                CharacterStatusCopies.Show(character, presented, PhotonNetwork.ServerTimestamp + 3000);
                float copyDuring = Accel(character);
                if (!CharacterStatuses.IsPresented(character, PresentedKey))
                    fails.Add("the presentation copy is not IsPresented");
                if (CharacterStatuses.IsActive(character, PresentedKey))
                    fails.Add("the presentation copy made the status IsActive");
                if (Math.Abs(copyDuring - copyBefore) > 0.001f)
                    fails.Add("the presentation copy moved MotorAcceleration " + copyBefore.ToString("0.##") +
                              " -> " + copyDuring.ToString("0.##"));
                if (applied != 0) fails.Add("the presentation copy ran OnApplied " + applied + " time(s)");
                CharacterStatusCopies.Hide(character, PresentedKey);
                if (CharacterStatuses.IsPresented(character, PresentedKey))
                    fails.Add("the presentation copy is still IsPresented after Hide");

                Player other = OtherCharacter(me);
                if (other != null)
                {
                    CharacterStatusRefusal remote = CharacterStatuses.TryRemove(other, AcceptKey);
                    if (remote != CharacterStatusRefusal.Authority)
                        fails.Add("TryRemove on " + other.name + " answered " + remote + ", expected Authority");
                    else notes.Add("TryRemove on another player's character answered Authority");
                }
                else notes.Add("remote removal case skipped: no other player's character in the room");
            }
            catch (Exception e)
            {
                fails.Add("threw " + e);
            }
            finally
            {
                try { CharacterStatuses.Remove(character, AcceptKey); } catch (Exception) { }
                try { CharacterStatuses.Remove(character, ExtendKey); } catch (Exception) { }
                try { CharacterStatuses.Remove(character, PresentedKey); } catch (Exception) { }
                try { CharacterStatuses.Remove(character, RelapseKey); } catch (Exception) { }
                try { CharacterStatusCopies.Hide(character, PresentedKey); } catch (Exception) { }
                CharacterStatusSync.Accepted.Forget(me + "/" + AcceptKey);
                CharacterStatusSync.Flood.Forget(me.ToString());
            }
            if (fails.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: character status authority passed - actor " +
                                   Stranger + " was refused SenderMissing, a key without AcceptsRequests " +
                                   "NotAccepted, another actor's request InvalidTarget, a request from actor " + me +
                                   " landed at the magnitude MaxMagnitudeFor allowed and ran OnRequestApplied, " +
                                   "a third was RateLimited, a shorter request kept the end, a " +
                                   "re-apply from OnRemoved started clean, the death hook is on the life-state " +
                                   "setter and ended a status and a copy, a forged announcement was refused " +
                                   "Authority, and a presentation copy changed no stat and ran no callback; " +
                                   string.Join("; ", notes.ToArray()) + ".");
            else
                Plugin.Log.LogError("Ectype: dev self-test: character status authority FAILED - " +
                                    string.Join("; ", fails.ToArray()) + ".");
        }

        private static Player OtherCharacter(int me)
        {
            ClientGame game = ClientGame.Current;
            if (game == null || game.Players == null) return null;
            foreach (Player p in game.Players)
                if (p != null && p.photonView != null && !p.photonView.IsMine && Topology.ActorOf(p) != me)
                    return p;
            return null;
        }
    }
}
