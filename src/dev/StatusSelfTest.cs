using System;
using System.Collections.Generic;

using Photon.Pun;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// Dev build self-tests for enemy-status authority and coalescing.
    ///
    /// Coalescing (offline, at load): ten applications to one target and key, 10 ms apart, go
    /// into an outbox with a 150 ms window. Nothing may be due before the window closes; after
    /// it, exactly one batch must come out, carrying 10 applications, the first application's
    /// start and the last one's end, in fields that match the ApplyN layout. A second key
    /// travels in its own batch, a removal drops what is queued, and a template that does not
    /// refresh on re-apply sends the first application's duration.
    ///
    /// Authority (in game, once a character and its crew ship exist): two status templates with
    /// no stat entries and no marker are registered and driven through the receive path with
    /// synthetic senders, against this client's own crew ship:
    /// - a Hostile status sent twice by this client's own actor, which is on the ship's side,
    ///   must be refused both times with exactly one log line, and must not be active; a local
    ///   TryApply of it must be refused for its stance too;
    /// - an Any status received as ApplyN with 10 applications, then 3, must hold 13 stacks;
    /// - a removal from actor 9999, which neither applied it, owns the ship nor is the master,
    ///   must be ignored, and the applier's own removal must take.
    /// </summary>
    internal static class StatusSelfTest
    {
        private const string HostileKey = "ectype.dev.selftest-hostile";
        private const string OpenKey = "ectype.dev.selftest-open";
        private const int Stranger = 9999;

        private static bool _done;
        private static float _nextTry;

        internal static void RunOutbox()
        {
            List<string> fails = new List<string>();
            try
            {
                const int window = 150;
                EnemyStatusOutbox box = new EnemyStatusOutbox();
                List<EnemyStatusOutbox.Batch> sent = new List<EnemyStatusOutbox.Batch>();
                for (int i = 0; i < 10; i++)
                    box.Add(4242, "ectype.dev.coalesce", "probe", 100000 + i * 10, 5000, 1f, 0,
                            true, window, 10f + i * 0.01f);
                box.TakeDue(10.1f, false, sent);
                if (sent.Count != 0) fails.Add(sent.Count + " send(s) before the window closed");
                sent.Clear();
                box.TakeDue(10.151f, false, sent);
                if (sent.Count != 1) fails.Add("expected 1 send after the window, got " + sent.Count);
                else
                {
                    EnemyStatusOutbox.Batch b = sent[0];
                    object[] args = b.Args();
                    if (!EctypeNet.MatchesLayout(EctypeNet.EnemyStatusStacks, args))
                        fails.Add("the batch's fields match no ApplyN layout");
                    else if ((int)args[6] != 10) fails.Add("addStacks " + args[6] + ", expected 10");
                    if (b.FirstStartTs != 100000 || b.DurationMs != 5090)
                        fails.Add("timer start " + b.FirstStartTs + " + " + b.DurationMs +
                                  " ms, expected 100000 + 5090 ms");
                }

                sent.Clear();
                box.Add(4242, "ectype.dev.a", "probe", 0, 1000, 1f, 0, true, window, 20f);
                box.Add(4242, "ectype.dev.b", "probe", 0, 1000, 1f, 0, false, window, 20f);
                box.Add(4242, "ectype.dev.b", "probe", 500, 1000, 1f, 0, false, window, 20f);
                box.TakeDue(30f, false, sent);
                if (sent.Count != 2) fails.Add("two keys made " + sent.Count + " send(s), expected 2");
                foreach (EnemyStatusOutbox.Batch b in sent)
                    if (b.Key == "ectype.dev.b" && (b.Applications != 2 || b.DurationMs != 1000))
                        fails.Add("no-refresh batch carried " + b.Applications + " application(s) and " +
                                  b.DurationMs + " ms, expected 2 and 1000 ms");

                sent.Clear();
                box.Add(4242, "ectype.dev.c", "probe", 0, 1000, 1f, 0, true, window, 40f);
                box.Drop(4242, "ectype.dev.c");
                box.TakeDue(0f, true, sent);
                if (sent.Count != 0) fails.Add("a dropped batch was still sent");
            }
            catch (Exception e)
            {
                fails.Add("threw " + e);
            }
            if (fails.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: status coalescing passed - ten applications " +
                                   "in one 150 ms window made one send carrying 10 applications.");
            else
                Plugin.Log.LogError("Ectype: dev self-test: status coalescing FAILED - " +
                                    string.Join("; ", fails.ToArray()) + ".");
        }

        internal static void Poll()
        {
            if (_done || !Plugin.PatchesApplied) return;
            if (Time.unscaledTime < _nextTry) return;
            _nextTry = Time.unscaledTime + 5f;

            int me = EnemyStatuses.LocalActor();
            CG.Game.Player.Player character = Topology.CharacterOf(me);
            if (character == null) return;
            CG.Space.PlayerControlledShip ship = Topology.CrewShipOf(character);
            if (ship == null || ship.photonView == null || ship.Faction != character.Faction) return;
            _done = true;
            RunAuthority(me, ship);
        }

        private static void RunAuthority(int me, CG.Space.PlayerControlledShip ship)
        {
            List<string> fails = new List<string>();
            try
            {
                EnemyStatuses.Register(new EnemyStatusDef(HostileKey)
                {
                    Stance = StatusStance.Hostile,
                    ShowMarker = false,
                });
                EnemyStatuses.Register(new EnemyStatusDef(OpenKey)
                {
                    MaxStacks = 20,
                    ShowMarker = false,
                });
                int view = ship.photonView.ViewID;
                int now = PhotonNetwork.ServerTimestamp;

                int before = StatusRefusalLog.Lines;
                object[] hostile = { (byte)0, view, HostileKey, now, 5000, 1f, 0 };
                StatusRefusal r1 = EnemyStatuses.HandleMessage(me, hostile);
                StatusRefusal r2 = EnemyStatuses.HandleMessage(me, hostile);
                int lines = StatusRefusalLog.Lines - before;
                if (r1 != StatusRefusal.Stance || r2 != StatusRefusal.Stance)
                    fails.Add("same-side Hostile apply answered " + r1 + ", " + r2 + ", expected Stance");
                if (lines != 1) fails.Add("two refusals logged " + lines + " line(s), expected 1");
                if (EnemyStatuses.IsActive(ship, HostileKey)) fails.Add("the refused Hostile status is active");
                StatusRefusal local = EnemyStatuses.TryApply(ship, HostileKey, 5f);
                if (local != StatusRefusal.Stance) fails.Add("local TryApply answered " + local + ", expected Stance");

                StatusRefusal n1 = EnemyStatuses.HandleStacks(me, new object[] { view, OpenKey, now, 5000, 1f, 0, 10 });
                StatusRefusal n2 = EnemyStatuses.HandleStacks(me, new object[] { view, OpenKey, now, 5000, 1f, 0, 3 });
                int stacks = EnemyStatuses.Stacks(ship, OpenKey);
                if (n1 != StatusRefusal.None || n2 != StatusRefusal.None || stacks != 13)
                    fails.Add("ApplyN 10 then 3 answered " + n1 + ", " + n2 + " and left " + stacks +
                              " stack(s), expected 13");

                StatusRefusal rs = EnemyStatuses.HandleMessage(Stranger, new object[] { (byte)1, view, OpenKey });
                if (rs != StatusRefusal.Authority || !EnemyStatuses.IsActive(ship, OpenKey))
                    fails.Add("removal from actor " + Stranger + " answered " + rs +
                              (EnemyStatuses.IsActive(ship, OpenKey) ? "" : " and took") + ", expected Authority");
                StatusRefusal rm = EnemyStatuses.HandleMessage(me, new object[] { (byte)1, view, OpenKey });
                if (rm != StatusRefusal.None || EnemyStatuses.IsActive(ship, OpenKey))
                    fails.Add("the applier's removal answered " + rm + " and left the status " +
                              (EnemyStatuses.IsActive(ship, OpenKey) ? "active" : "removed"));
            }
            catch (Exception e)
            {
                fails.Add("threw " + e);
            }
            finally
            {
                try { EnemyStatuses.RemoveLocal(ship, OpenKey); } catch (Exception) { }
                try { EnemyStatuses.RemoveLocal(ship, HostileKey); } catch (Exception) { }
            }
            if (fails.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: status authority passed - a Hostile status " +
                                   "from actor " + me + " on its own ship was refused twice with one " +
                                   "log line, ApplyN 10 then 3 left 13 stacks, and a removal from actor " +
                                   Stranger + " was ignored.");
            else
                Plugin.Log.LogError("Ectype: dev self-test: status authority FAILED - " +
                                    string.Join("; ", fails.ToArray()) + ".");
        }
    }
}
