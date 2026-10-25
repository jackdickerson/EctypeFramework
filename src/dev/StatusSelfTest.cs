using System;
using System.Collections.Generic;

using CG.Space;
using Photon.Pun;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// Dev build self-tests for enemy-status authority and coalescing.
    ///
    /// Coalescing and the cap margin (offline, at load):
    /// - ten applications to one target and key, 10 ms apart, go into an outbox with a 150 ms
    ///   window. Nothing may be due before the window closes; after it, exactly one batch must
    ///   come out, carrying 10 applications and the local status's start and latest end, in
    ///   fields that match the ApplyN layout;
    /// - a batch closed because its status was created anew goes out before the next life's
    ///   batch, each with its own count and timer;
    /// - a removal drops open and closed batches alike, and only for its own key (a key that
    ///   extends another with '#' is untouched);
    /// - the cap margin: 601 m does not displace 600 m, 400 m displaces 600 m, 80 m does not
    ///   displace 100 m, 70 m does.
    ///
    /// Authority (in game, once a character and its crew ship exist): status templates with no
    /// stat entries and no marker are driven through the receive path with synthetic senders,
    /// against this client's own crew ship:
    /// - a Hostile status sent twice by this client's own actor, which is on the ship's side,
    ///   must be refused both times with exactly one log line, and must not be active; a local
    ///   TryApply of it must be refused for its stance too;
    /// - an Any status received as ApplyN with 10 applications, then 3, must hold 13 stacks, and
    ///   a removal from actor 9999 must take, since Any lets anyone remove;
    /// - a Hostile status placed directly must refuse a removal from a sender that is not the
    ///   master and that its stance would not let apply it: this client's own actor when it is
    ///   not the master (same side), else actor 9999 (unknown side); the master's removal must
    ///   take when this client is the master;
    /// - a received removal must drop this client's own queued batch for that target and key;
    /// - on the master with an NPC in the sector, a Hostile application from the master whose
    ///   attacker is that NPC must be judged by the NPC's side and land on the ship.
    /// </summary>
    internal static class StatusSelfTest
    {
        private const string HostileKey = "ectype.dev.selftest-hostile";
        private const string OpenKey = "ectype.dev.selftest-open";
        private const string QueuedKey = "ectype.dev.selftest-queued";
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
                    box.Add(4242, "ectype.dev.coalesce", "probe", 100000, 105000 + i * 10, 1f, 0,
                            window, 10f + i * 0.01f);
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
                    if (b.StartTs != 100000 || b.DurationMs != 5090)
                        fails.Add("timer start " + b.StartTs + " + " + b.DurationMs +
                                  " ms, expected 100000 + 5090 ms");
                }

                sent.Clear();
                box.Add(4242, "ectype.dev.life", "probe", 0, 1000, 1f, 0, window, 20f);
                box.Add(4242, "ectype.dev.life", "probe", 0, 1000, 1f, 0, window, 20f);
                box.Close(4242, "ectype.dev.life");
                box.Add(4242, "ectype.dev.life", "probe", 2000, 3000, 1f, 0, window, 20.05f);
                box.TakeDue(30f, false, sent);
                if (sent.Count != 2 || sent[0].Applications != 2 || sent[0].EndTs != 1000 ||
                    sent[1].Applications != 1 || sent[1].StartTs != 2000)
                    fails.Add("two lives of one status made " + sent.Count + " batch(es) " + Describe(sent) +
                              ", expected 2 then 1 application(s), the first ending at 1000 and the second " +
                              "starting at 2000");

                sent.Clear();
                box.Add(4242, "ectype.dev.c", "probe", 0, 1000, 1f, 0, window, 40f);
                box.Close(4242, "ectype.dev.c");
                box.Add(4242, "ectype.dev.c", "probe", 0, 1000, 1f, 0, window, 40f);
                box.Add(4242, "ectype.dev.c#1", "probe", 0, 1000, 1f, 0, window, 40f);
                box.Drop(4242, "ectype.dev.c");
                box.TakeDue(0f, true, sent);
                if (sent.Count != 1 || sent[0].Key != "ectype.dev.c#1")
                    fails.Add("a removal left " + Describe(sent) + ", expected only ectype.dev.c#1");

                if (EnemyStatusRank.Displaces(601f, 600f) || !EnemyStatusRank.Displaces(400f, 600f) ||
                    EnemyStatusRank.Displaces(80f, 100f) || !EnemyStatusRank.Displaces(70f, 100f))
                    fails.Add("the cap margin answered 601/600 " + EnemyStatusRank.Displaces(601f, 600f) +
                              ", 400/600 " + EnemyStatusRank.Displaces(400f, 600f) + ", 80/100 " +
                              EnemyStatusRank.Displaces(80f, 100f) + ", 70/100 " +
                              EnemyStatusRank.Displaces(70f, 100f) + "; expected False, True, False, True");
            }
            catch (Exception e)
            {
                fails.Add("threw " + e);
            }
            if (fails.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: status coalescing passed - ten applications " +
                                   "in one 150 ms window made one send carrying 10 applications; two " +
                                   "lives of a status made two batches; a removal dropped only its own key; " +
                                   "the cap margin held.");
            else
                Plugin.Log.LogError("Ectype: dev self-test: status coalescing FAILED - " +
                                    string.Join("; ", fails.ToArray()) + ".");
        }

        private static string Describe(List<EnemyStatusOutbox.Batch> batches)
        {
            List<string> parts = new List<string>();
            foreach (EnemyStatusOutbox.Batch b in batches)
                parts.Add(b.Key + " x" + b.Applications + " [" + b.StartTs + ".." + b.EndTs + "]");
            return "(" + string.Join(", ", parts.ToArray()) + ")";
        }

        internal static void Poll()
        {
            if (_done || !Plugin.PatchesApplied) return;
            if (Time.unscaledTime < _nextTry) return;
            _nextTry = Time.unscaledTime + 5f;

            int me = EnemyStatuses.LocalActor();
            CG.Game.Player.Player character = Topology.CharacterOf(me);
            if (character == null) return;
            PlayerControlledShip ship = Topology.CrewShipOf(character);
            if (ship == null || ship.photonView == null || ship.Faction != character.Faction) return;
            _done = true;
            RunAuthority(me, ship);
        }

        private static void RunAuthority(int me, PlayerControlledShip ship)
        {
            List<string> fails = new List<string>();
            List<string> notes = new List<string>();
            EnemyStatusDef hostileDef = null;
            try
            {
                hostileDef = new EnemyStatusDef(HostileKey) { Stance = StatusStance.Hostile, ShowMarker = false };
                EnemyStatuses.Register(hostileDef);
                EnemyStatuses.Register(new EnemyStatusDef(OpenKey) { MaxStacks = 20, ShowMarker = false });
                EnemyStatuses.Register(new EnemyStatusDef(QueuedKey) { CoalesceMs = 20000, ShowMarker = false });
                int view = ship.photonView.ViewID;
                int now = PhotonNetwork.ServerTimestamp;
                bool master = EnemyStatuses.IsMaster(me);

                // A same-side Hostile application.
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

                // ApplyN deltas, then an open removal.
                StatusRefusal n1 = EnemyStatuses.HandleStacks(me, new object[] { view, OpenKey, now, 5000, 1f, 0, 10 });
                StatusRefusal n2 = EnemyStatuses.HandleStacks(me, new object[] { view, OpenKey, now, 5000, 1f, 0, 3 });
                int stacks = EnemyStatuses.Stacks(ship, OpenKey);
                if (n1 != StatusRefusal.None || n2 != StatusRefusal.None || stacks != 13)
                    fails.Add("ApplyN 10 then 3 answered " + n1 + ", " + n2 + " and left " + stacks +
                              " stack(s), expected 13");
                StatusRefusal ro = EnemyStatuses.HandleMessage(Stranger, new object[] { (byte)1, view, OpenKey });
                if (ro != StatusRefusal.None || EnemyStatuses.IsActive(ship, OpenKey))
                    fails.Add("removal of an Any status from actor " + Stranger + " answered " + ro +
                              (EnemyStatuses.IsActive(ship, OpenKey) ? " and left it active" : "") +
                              ", expected it to take");

                // A Hostile status refuses a remover its stance would not let apply it.
                bool created;
                EnemyStatuses.ApplyLocal(ship, hostileDef, now, 5000, 1f, null, Stranger, -1, false, 1, out created);
                int remover = master ? Stranger : me;
                StatusRefusal rh = EnemyStatuses.HandleMessage(remover, new object[] { (byte)1, view, HostileKey });
                if (rh != StatusRefusal.Authority || !EnemyStatuses.IsActive(ship, HostileKey))
                    fails.Add("removal of a Hostile status from actor " + remover + " (" +
                              (master ? "unknown side" : "same side") + ", not master) answered " + rh +
                              (EnemyStatuses.IsActive(ship, HostileKey) ? "" : " and took") + ", expected Authority");
                notes.Add("Hostile removal refused for actor " + remover + " (" +
                          (master ? "unknown side" : "same side") + ")");
                if (master)
                {
                    StatusRefusal rm = EnemyStatuses.HandleMessage(me, new object[] { (byte)1, view, HostileKey });
                    if (rm != StatusRefusal.None || EnemyStatuses.IsActive(ship, HostileKey))
                        fails.Add("the master's removal of a Hostile status answered " + rm);
                    else notes.Add("the master's removal took");
                }

                // A received removal drops this client's queued batch.
                int queued = EnemyStatuses.Outbox.Count;
                StatusRefusal qa = EnemyStatuses.TryApply(ship, QueuedKey, 30f);
                int afterApply = EnemyStatuses.Outbox.Count;
                StatusRefusal qr = EnemyStatuses.HandleMessage(Stranger, new object[] { (byte)1, view, QueuedKey });
                int afterRemove = EnemyStatuses.Outbox.Count;
                if (qa != StatusRefusal.None || afterApply != queued + 1 || qr != StatusRefusal.None ||
                    afterRemove != queued)
                    fails.Add("queued batch: apply " + qa + " left " + afterApply + " batch(es) from " + queued +
                              ", received removal " + qr + " left " + afterRemove + ", expected " +
                              (queued + 1) + " then " + queued);

                // The master applying on an NPC's behalf is judged by the NPC's side.
                SpaceCraft npc = master ? HostileNpc(ship) : null;
                if (npc != null)
                {
                    StatusRefusal rn = EnemyStatuses.HandleMessage(me, new object[]
                        { (byte)0, view, HostileKey, now, 5000, 1f, npc.photonView.ViewID });
                    if (rn != StatusRefusal.None || !EnemyStatuses.IsActive(ship, HostileKey))
                        fails.Add("a Hostile apply from the master with NPC attacker " + npc.name +
                                  " answered " + rn + ", expected it to land");
                    else notes.Add("an NPC-attacker Hostile apply from the master landed");
                }
                else notes.Add(master ? "NPC-attacker case skipped: no hostile NPC in the sector"
                                      : "NPC-attacker case skipped: this client is not the master");
            }
            catch (Exception e)
            {
                fails.Add("threw " + e);
            }
            finally
            {
                try { EnemyStatuses.RemoveLocal(ship, OpenKey); } catch (Exception) { }
                try { EnemyStatuses.RemoveLocal(ship, HostileKey); } catch (Exception) { }
                try
                {
                    EnemyStatuses.RemoveLocal(ship, QueuedKey);
                    EnemyStatuses.Outbox.Drop(ship.photonView.ViewID, QueuedKey);
                }
                catch (Exception) { }
            }
            if (fails.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: status authority passed - a Hostile status " +
                                   "from actor " + me + " on its own ship was refused twice with one " +
                                   "log line, ApplyN 10 then 3 left 13 stacks, an Any status was removed " +
                                   "by actor " + Stranger + ", a received removal dropped the queued batch; " +
                                   string.Join("; ", notes.ToArray()) + ".");
            else
                Plugin.Log.LogError("Ectype: dev self-test: status authority FAILED - " +
                                    string.Join("; ", fails.ToArray()) + ".");
        }

        /// <summary>An NPC ship hostile to the given ship, or null. One scene scan, once per launch.</summary>
        private static SpaceCraft HostileNpc(PlayerControlledShip ship)
        {
            foreach (SpaceCraft s in UnityEngine.Object.FindObjectsOfType<SpaceCraft>())
            {
                if (s == null || s is PlayerControlledShip || s.photonView == null) continue;
                if (Topology.ShipOfPart(s) != null) continue;
                if (Topology.Hostile(s, ship)) return s;
            }
            return null;
        }
    }
}
