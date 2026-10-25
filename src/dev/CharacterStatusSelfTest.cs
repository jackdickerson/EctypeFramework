using System;
using System.Collections.Generic;

using CG.Game;
using CG.Game.Player;
using ExitGames.Client.Photon;
using Photon.Pun;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// Dev build self-tests for character status requests and presentation copies.
    ///
    /// Offline, at load:
    /// - the arguments the request, announcement and end messages are built from match their
    ///   declared layouts, and survive a Protocol18 round trip with the same types and values;
    /// - the judge, with synthetic views of the two characters, refuses each reason in turn:
    ///   no template, a template that is not Replicated, a zero and a NaN duration, a negative
    ///   and a NaN magnitude, a dead target, a requester with no character, a dead requester,
    ///   Hostile from the same side and Friendly from another, 5.5 m against MaxRange 5,
    ///   a blocked line; and accepts 5.0 m, any distance with no MaxRange, a blocked line when
    ///   the template does not ask, and cuts 100 s and magnitude 5 to MaxSeconds and MaxMagnitude;
    /// - the rate bucket with Burst 2 and MinInterval 1 s accepts two at once, refuses a third
    ///   and one at 0.5 s, accepts one at 1.01 s, keeps ids apart, and never refuses with no
    ///   MinInterval;
    /// - the audience rule answers each Visibility for a viewer on the applier's side, on the
    ///   target's side, on a third side and of unknown side;
    /// - registration refuses Burst 0, a negative MaxRange, a NaN MinInterval and MaxSeconds 0.
    ///
    /// In game, once this client has a character: requests are driven through the receive
    /// path with synthetic senders against this client's own character:
    /// - actor 9999 (no character here) is refused SenderMissing, a key that is not Replicated
    ///   NotReplicated, a request naming another actor InvalidTarget;
    /// - this client's own actor, at 0 m, is accepted: the status is active, RequesterOf names
    ///   the actor, and the movement stat it carries changes; a second request inside
    ///   MinInterval is refused RateLimited and leaves one stack;
    /// - an announcement from actor 9999 naming this client's character is refused Authority;
    /// - a presentation copy on this client's character leaves the movement stat and the
    ///   OnApplied count as they were, is not IsActive, and answers IsPresented until hidden;
    /// - with another player's character in the room, TryRemove on it answers Authority.
    /// </summary>
    internal static class CharacterStatusSelfTest
    {
        private const string ReplicatedKey = "ectype.dev.selftest-char-replicated";
        private const string PlainKey = "ectype.dev.selftest-char-plain";
        private const string PresentedKey = "ectype.dev.selftest-char-presented";
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
                RateCases(fails);
                AudienceCases(fails);
                ValidateCases(fails);
            }
            catch (Exception e)
            {
                fails.Add("threw " + e);
            }
            if (fails.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: character status requests passed - the " +
                                   "request, announcement and end messages round-trip, every refusal " +
                                   "reason fired on its own input, the rate bucket held Burst 2 per 1 s, " +
                                   "each audience rule chose its viewers, and four bad templates were refused.");
            else
                Plugin.Log.LogError("Ectype: dev self-test: character status requests FAILED - " +
                                    string.Join("; ", fails.ToArray()) + ".");
        }

        private static void Wire(List<string> fails)
        {
            RoundTrip("request", EctypeNet.CharacterStatusRequest,
                      CharacterStatusSync.RequestArgs(7, "ectype.dev.wire", 2.5f, 0.75f, 1234), fails);
            RoundTrip("announcement", EctypeNet.CharacterStatusApplied,
                      CharacterStatusSync.AppliedArgs(7, "ectype.dev.wire", 123456, 0.75f), fails);
            RoundTrip("end", EctypeNet.CharacterStatusApplied,
                      CharacterStatusSync.EndedArgs(7, "ectype.dev.wire"), fails);
            object[] req = CharacterStatusSync.RequestArgs(7, "k", 2.5f, 1f, 0);
            if ((int)req[2] != 2500) fails.Add("2.5 s travelled as " + req[2] + " ms, expected 2500");
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
            return new CharacterStatusDef("ectype.dev.judge")
            {
                Replicated = true, Stance = StatusStance.Hostile, MaxRange = 5f,
                RequiresLineOfSight = true, MinInterval = 1f, Burst = 2,
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
                                   List<string> fails)
        {
            CharacterStatusRefusal got = CharacterStatusSync.Judge(def, ref seconds, ref magnitude, f);
            if (got != want) fails.Add(label + " answered " + got + ", expected " + want);
        }

        private static void JudgeCases(List<string> fails)
        {
            CharacterStatusDef d = Strict();
            CharacterRequestFacts f;
            Expect("a clear request", d, Clear(), 3f, 1f, CharacterStatusRefusal.None, fails);
            Expect("no template", null, Clear(), 3f, 1f, CharacterStatusRefusal.NotRegistered, fails);
            Expect("a non-replicated key", new CharacterStatusDef("ectype.dev.plain"), Clear(), 3f, 1f,
                   CharacterStatusRefusal.NotReplicated, fails);
            Expect("0 s", d, Clear(), 0f, 1f, CharacterStatusRefusal.InvalidDuration, fails);
            Expect("NaN s", d, Clear(), float.NaN, 1f, CharacterStatusRefusal.InvalidDuration, fails);
            Expect("magnitude -1", d, Clear(), 3f, -1f, CharacterStatusRefusal.InvalidMagnitude, fails);
            Expect("NaN magnitude", d, Clear(), 3f, float.NaN, CharacterStatusRefusal.InvalidMagnitude, fails);
            f = Clear(); f.VictimAlive = false;
            Expect("a dead target", d, f, 3f, 1f, CharacterStatusRefusal.TargetDead, fails);
            f = Clear(); f.SourceKnown = false;
            Expect("no requester character", d, f, 3f, 1f, CharacterStatusRefusal.SenderMissing, fails);
            f = Clear(); f.SourceAlive = false;
            Expect("a dead requester", d, f, 3f, 1f, CharacterStatusRefusal.SenderDead, fails);
            f = Clear(); f.Hostile = false;
            Expect("Hostile from the same side", d, f, 3f, 1f, CharacterStatusRefusal.Stance, fails);
            CharacterStatusDef friendly = Strict();
            friendly.Stance = StatusStance.Friendly;
            Expect("Friendly from another side", friendly, Clear(), 3f, 1f, CharacterStatusRefusal.Stance, fails);
            f = Clear(); f.Distance = 5.5f;
            Expect("out of range", d, f, 3f, 1f, CharacterStatusRefusal.OutOfRange, fails);
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
            CharacterStatusRefusal cut = CharacterStatusSync.Judge(d, ref s, ref m, Clear());
            if (cut != CharacterStatusRefusal.None || s != d.MaxSeconds || m != d.MaxMagnitude)
                fails.Add("100 s at magnitude 5 answered " + cut + " with " + s + " s at " + m +
                          ", expected None cut to " + d.MaxSeconds + " s at " + d.MaxMagnitude);
        }

        private static void RateCases(List<string> fails)
        {
            CharacterStatusDef d = Strict();
            CharacterStatusRate rate = new CharacterStatusRate();
            bool a = rate.TryTake("1/k", d, 10f), b = rate.TryTake("1/k", d, 10f), c = rate.TryTake("1/k", d, 10f);
            bool half = rate.TryTake("1/k", d, 10.5f);
            bool later = rate.TryTake("1/k", d, 11.01f);
            bool other = rate.TryTake("2/k", d, 10.5f);
            if (!a || !b || c || half || !later || !other)
                fails.Add("the rate bucket answered " + a + ", " + b + ", " + c + " at 0 s, " + half +
                          " at 0.5 s, " + later + " at 1.01 s, " + other + " for another id; expected " +
                          "True, True, False, False, True, True");
            CharacterStatusDef free = new CharacterStatusDef("ectype.dev.free") { Replicated = true };
            for (int i = 0; i < 20; i++)
                if (!rate.TryTake("3/free", free, 10f))
                {
                    fails.Add("a template with no MinInterval was rate limited");
                    break;
                }
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
                new CharacterStatusDef("ectype.dev.bad1") { Burst = 0 },
                new CharacterStatusDef("ectype.dev.bad2") { MaxRange = -1f },
                new CharacterStatusDef("ectype.dev.bad3") { MinInterval = float.NaN },
                new CharacterStatusDef("ectype.dev.bad4") { MaxSeconds = 0f },
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

        private static void RunInGame(int me, Player character)
        {
            List<string> fails = new List<string>();
            List<string> notes = new List<string>();
            int applied = 0;
            try
            {
                CharacterStatuses.Register(new CharacterStatusDef(ReplicatedKey)
                {
                    Replicated = true, MinInterval = 60f, Burst = 1, MaxStacks = 3,
                }.AddStat("MotorAcceleration", "percent", -0.5f));
                CharacterStatuses.Register(new CharacterStatusDef(PlainKey));
                CharacterStatusDef presented = new CharacterStatusDef(PresentedKey)
                {
                    Presented = true,
                    OnApplied = (p, n) => applied++,
                }.AddStat("MotorAcceleration", "percent", -0.5f);
                CharacterStatuses.Register(presented);
                CharacterStatusSync.Accepted.Forget(me + "/" + ReplicatedKey);

                int lines = StatusRefusalLog.Lines;
                CharacterStatusRefusal stranger = CharacterStatusSync.HandleRequest(Stranger,
                    CharacterStatusSync.RequestArgs(me, ReplicatedKey, 3f, 1f, 0));
                if (stranger != CharacterStatusRefusal.SenderMissing)
                    fails.Add("a request from actor " + Stranger + " answered " + stranger + ", expected SenderMissing");
                CharacterStatusRefusal plain = CharacterStatusSync.HandleRequest(me,
                    CharacterStatusSync.RequestArgs(me, PlainKey, 3f, 1f, 0));
                if (plain != CharacterStatusRefusal.NotReplicated)
                    fails.Add("a request for a non-replicated key answered " + plain + ", expected NotReplicated");
                CharacterStatusRefusal elsewhere = CharacterStatusSync.HandleRequest(me,
                    CharacterStatusSync.RequestArgs(me + 1000, ReplicatedKey, 3f, 1f, 0));
                if (elsewhere != CharacterStatusRefusal.InvalidTarget)
                    fails.Add("a request naming another actor answered " + elsewhere + ", expected InvalidTarget");
                if (StatusRefusalLog.Lines - lines != 3)
                    fails.Add("three refusals logged " + (StatusRefusalLog.Lines - lines) + " line(s), expected 3");

                float before = Accel(character);
                CharacterStatusRefusal ok = CharacterStatusSync.HandleRequest(me,
                    CharacterStatusSync.RequestArgs(me, ReplicatedKey, 3f, 1f, 0));
                float during = Accel(character);
                if (ok != CharacterStatusRefusal.None || !CharacterStatuses.IsActive(character, ReplicatedKey))
                    fails.Add("a request from this client's own actor answered " + ok + ", expected it to land");
                else if (CharacterStatuses.RequesterOf(character, ReplicatedKey) != me)
                    fails.Add("RequesterOf answered " + CharacterStatuses.RequesterOf(character, ReplicatedKey) +
                              ", expected " + me);
                bool statMoved = Math.Abs(during - before) > 0.001f;
                if (statMoved) notes.Add("the accepted request moved MotorAcceleration " + before.ToString("0.##") +
                                         " -> " + during.ToString("0.##"));
                else notes.Add("MotorAcceleration did not move on the accepted request (stat not wired?), so " +
                               "the copy's stat check is inconclusive");

                CharacterStatusRefusal again = CharacterStatusSync.HandleRequest(me,
                    CharacterStatusSync.RequestArgs(me, ReplicatedKey, 3f, 1f, 0));
                int stacks = CharacterStatuses.Stacks(character, ReplicatedKey);
                if (again != CharacterStatusRefusal.RateLimited || stacks != 1)
                    fails.Add("a second request inside MinInterval answered " + again + " with " + stacks +
                              " stack(s), expected RateLimited with 1");
                CharacterStatuses.Remove(character, ReplicatedKey);
                if (Math.Abs(Accel(character) - before) > 0.001f)
                    fails.Add("removal left MotorAcceleration at " + Accel(character).ToString("0.##") +
                              ", expected " + before.ToString("0.##"));

                CharacterStatusRefusal forged = CharacterStatusSync.HandleApplied(Stranger,
                    CharacterStatusSync.AppliedArgs(me, PresentedKey, PhotonNetwork.ServerTimestamp + 3000, 1f));
                if (forged != CharacterStatusRefusal.Authority)
                    fails.Add("an announcement from actor " + Stranger + " for this client's character answered " +
                              forged + ", expected Authority");

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
                    CharacterStatusRefusal remote = CharacterStatuses.TryRemove(other, ReplicatedKey);
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
                try { CharacterStatuses.Remove(character, ReplicatedKey); } catch (Exception) { }
                try { CharacterStatuses.Remove(character, PresentedKey); } catch (Exception) { }
                try { CharacterStatusCopies.Hide(character, PresentedKey); } catch (Exception) { }
                CharacterStatusSync.Accepted.Forget(me + "/" + ReplicatedKey);
            }
            if (fails.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: character status authority passed - actor " +
                                   Stranger + " was refused SenderMissing, a non-replicated key NotReplicated, " +
                                   "another actor's request InvalidTarget, a request from actor " + me +
                                   " landed and a second was RateLimited, a forged announcement was refused " +
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
