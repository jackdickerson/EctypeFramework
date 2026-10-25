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
    ///   displace 100 m, 70 m does;
    /// - catch-up on a copy created late, with a 650 ms grace and 1 s ticks: a start 10.8 s old
    ///   pays no tick, 10 s old pays the one due now, and 300 ms old at 100 ms ticks pays all 3;
    /// - a removal at 1000 kept 2150 ms covers an application started at 900, not one at 1200,
    ///   not another key, and nothing once expired;
    /// - the removal rule over every stance, master or not, and every side.
    ///
    /// Authority (in game, once a character and its crew ship exist): status templates with no
    /// stat entries and no marker are driven through the receive path with synthetic senders,
    /// against this client's own crew ship:
    /// - a Hostile status sent twice by this client's own actor, which is on the ship's side,
    ///   must be refused both times with exactly one log line, and must not be active; a local
    ///   TryApply of it must be refused for its stance too;
    /// - an Any status received as ApplyN with 10 applications, then 3, must hold 13 stacks, and
    ///   a removal from actor 9999 must take, since Any lets anyone remove; a batch started
    ///   before that removal must then be ignored, and one started after it must land;
    /// - a Hostile status placed directly must refuse a removal from a sender that is not the
    ///   master and that its stance would not let apply it: this client's own actor when it is
    ///   not the master (same side), else actor 9999 (unknown side); the master's removal must
    ///   take when this client is the master;
    /// - a received removal must drop this client's own queued batch for that target and key;
    /// - on the master with an NPC in the sector, a Hostile application from the master whose
    ///   attacker is that NPC must be judged by the NPC's side and land on the ship;
    /// - on the master, a Hostile application from the master naming an attacker view that no
    ///   longer resolves must land.
    /// </summary>
    internal static class StatusSelfTest
    {
        private const string HostileKey = EctypePluginInfo.PLUGIN_GUID + "/dev.selftest-hostile";
        private const string OpenKey = EctypePluginInfo.PLUGIN_GUID + "/dev.selftest-open";
        private const string QueuedKey = EctypePluginInfo.PLUGIN_GUID + "/dev.selftest-queued";
        private const string MasterKey = EctypePluginInfo.PLUGIN_GUID + "/dev.selftest-master";
        private const string PartKey = EctypePluginInfo.PLUGIN_GUID + "/dev.selftest-part";
        private const int Stranger = 9999;
        /// <summary>A view id no object in a session carries.</summary>
        private const int GoneView = 987654;

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
                    box.Add(4242, EctypePluginInfo.PLUGIN_GUID + "/dev.coalesce", "probe", 100000, 105000 + i * 10, 1f, 0,
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
                box.Add(4242, EctypePluginInfo.PLUGIN_GUID + "/dev.life", "probe", 0, 1000, 1f, 0, window, 20f);
                box.Add(4242, EctypePluginInfo.PLUGIN_GUID + "/dev.life", "probe", 0, 1000, 1f, 0, window, 20f);
                box.Close(4242, EctypePluginInfo.PLUGIN_GUID + "/dev.life");
                box.Add(4242, EctypePluginInfo.PLUGIN_GUID + "/dev.life", "probe", 2000, 3000, 1f, 0, window, 20.05f);
                box.TakeDue(30f, false, sent);
                if (sent.Count != 2 || sent[0].Applications != 2 || sent[0].EndTs != 1000 ||
                    sent[1].Applications != 1 || sent[1].StartTs != 2000)
                    fails.Add("two lives of one status made " + sent.Count + " batch(es) " + Describe(sent) +
                              ", expected 2 then 1 application(s), the first ending at 1000 and the second " +
                              "starting at 2000");

                sent.Clear();
                box.Add(4242, EctypePluginInfo.PLUGIN_GUID + "/dev.c", "probe", 0, 1000, 1f, 0, window, 40f);
                box.Close(4242, EctypePluginInfo.PLUGIN_GUID + "/dev.c");
                box.Add(4242, EctypePluginInfo.PLUGIN_GUID + "/dev.c", "probe", 0, 1000, 1f, 0, window, 40f);
                box.Add(4242, EctypePluginInfo.PLUGIN_GUID + "/dev.c#1", "probe", 0, 1000, 1f, 0, window, 40f);
                box.Drop(4242, EctypePluginInfo.PLUGIN_GUID + "/dev.c");
                box.TakeDue(0f, true, sent);
                if (sent.Count != 1 || sent[0].Key != EctypePluginInfo.PLUGIN_GUID + "/dev.c#1")
                    fails.Add("a removal left " + Describe(sent) + ", expected only ectype.dev.c#1");

                if (EnemyStatusRank.Displaces(601f, 600f) || !EnemyStatusRank.Displaces(400f, 600f) ||
                    EnemyStatusRank.Displaces(80f, 100f) || !EnemyStatusRank.Displaces(70f, 100f))
                    fails.Add("the cap margin answered 601/600 " + EnemyStatusRank.Displaces(601f, 600f) +
                              ", 400/600 " + EnemyStatusRank.Displaces(400f, 600f) + ", 80/100 " +
                              EnemyStatusRank.Displaces(80f, 100f) + ", 70/100 " +
                              EnemyStatusRank.Displaces(70f, 100f) + "; expected False, True, False, True");

                // Catch-up on a copy created late: grace 650 ms (CoalesceMs 150 + 500), 1 s ticks.
                const int now = 100000, tick = 1000, grace = 650;
                long oldPaid = 10 - EnemyStatusEffect.FirstUnpaidTick(now, now - 10800, tick, grace);
                long edgePaid = 10 - EnemyStatusEffect.FirstUnpaidTick(now, now - 10000, tick, grace);
                long freshPaid = 3 - EnemyStatusEffect.FirstUnpaidTick(now, now - 300, 100, grace);
                int anchor = EnemyStatusEffect.CatchUpFrom(now, now - 10800, grace);
                if (oldPaid != 0 || edgePaid != 1 || freshPaid != 3 || anchor != now - grace)
                    fails.Add("catch-up paid " + oldPaid + " tick(s) for a start 10.8 s old, " + edgePaid +
                              " for 10 s, " + freshPaid + " for 300 ms at 100 ms ticks, anchored " +
                              (anchor - now) + " ms; expected 0, 1, 3 and -650 ms");

                // Removals remembered against applications already in flight.
                EnemyStatusTombstones tombs = new EnemyStatusTombstones();
                tombs.Record(4242, EctypePluginInfo.PLUGIN_GUID + "/dev.t", 1000, 2150);
                bool stale = tombs.Covers(4242, EctypePluginInfo.PLUGIN_GUID + "/dev.t", 900, 1100);
                bool fresh = tombs.Covers(4242, EctypePluginInfo.PLUGIN_GUID + "/dev.t", 1200, 1100);
                bool other = tombs.Covers(4242, EctypePluginInfo.PLUGIN_GUID + "/dev.u", 900, 1100);
                bool expired = tombs.Covers(4242, EctypePluginInfo.PLUGIN_GUID + "/dev.t", 900, 4000);
                if (!stale || fresh || other || expired)
                    fails.Add("a removal at 1000 covered a start of 900 " + stale + ", 1200 " + fresh +
                              ", another key " + other + ", after expiry " + expired +
                              "; expected True, False, False, False");

                // The removal rule as a table.
                string table = Verdicts();
                const string want = "Any:None Any:None Any:None Any:None Any:None Any:None " +
                                    "Hostile:None Hostile:None Hostile:None Hostile:Authority Hostile:None Hostile:Authority " +
                                    "Friendly:None Friendly:None Friendly:None Friendly:Authority Friendly:Authority Friendly:None";
                if (table != want) fails.Add("removal table " + table + ", expected " + want);
            }
            catch (Exception e)
            {
                fails.Add("threw " + e);
            }
            if (fails.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: status coalescing passed - ten applications " +
                                   "in one 150 ms window made one send carrying 10 applications; two " +
                                   "lives of a status made two batches; a removal dropped only its own key; " +
                                   "the cap margin held; a copy created 10.8 s late paid no ticks; a removal " +
                                   "covered applications started before it; the removal table held.");
            else
                Plugin.Log.LogError("Ectype: dev self-test: status coalescing FAILED - " +
                                    string.Join("; ", fails.ToArray()) + ".");
        }

        /// <summary>
        /// Master-only stance, tags and part targets, offline: the application and removal rule
        /// for MasterOnly; tagged and untagged messages against their layouts; and the part table's
        /// stacking, refresh, Tag replacement, expiry, removal and filtering on a fixed clock.
        /// </summary>
        internal static void RunPartsOffline()
        {
            List<string> fails = new List<string>();
            try
            {
                // MasterOnly: the master always, any other client never, whatever the side.
                string verdicts = "";
                foreach (bool master in new[] { true, false })
                    foreach (StatusSide side in new[] { StatusSide.Unknown, StatusSide.Same, StatusSide.Other })
                        verdicts += EnemyStatuses.ApplicationVerdict(StatusStance.MasterOnly, master, side) + "/" +
                                    EnemyStatuses.RemovalVerdict(StatusStance.MasterOnly, master, side) + " ";
                const string wantMaster = "None/None None/None None/None " +
                                          "NotMaster/NotMaster NotMaster/NotMaster NotMaster/NotMaster ";
                if (verdicts != wantMaster) fails.Add("MasterOnly table " + verdicts + ", expected " + wantMaster);
                if (EnemyStatuses.ApplicationVerdict(StatusStance.Hostile, true, StatusSide.Same) != StatusRefusal.Stance)
                    fails.Add("a Hostile application from the master on the target's side was not refused");

                // Messages: an untagged application keeps its old layout; tagged ones and part
                // messages match a layout of their own.
                const string k = EctypePluginInfo.PLUGIN_GUID + "/dev.part";
                object[] plain = EnemyStatuses.ApplyArgs(4242, k, 1000, 5000, 1f, 0, 0);
                object[] tagged = EnemyStatuses.ApplyArgs(4242, k, 1000, 5000, 1f, 0, 5);
                EnemyStatusOutbox box = new EnemyStatusOutbox();
                List<EnemyStatusOutbox.Batch> sent = new List<EnemyStatusOutbox.Batch>();
                box.Add(4242, k, "probe", 1000, 6000, 1f, 0, 150, 1f, 3);
                box.TakeDue(0f, true, sent);
                object[] batch = sent.Count == 1 ? sent[0].Args() : null;
                object[] partApply = { (byte)2, 4242, k, 1000, 5000, 1, 7 };
                object[] partRemove = { (byte)3, 4242, k, 1 };
                if (plain.Length != 7 || !EctypeNet.MatchesLayout(EctypeNet.EnemyStatus, plain) ||
                    tagged.Length != 8 || !EctypeNet.MatchesLayout(EctypeNet.EnemyStatus, tagged) ||
                    batch == null || batch.Length != 8 || (int)batch[7] != 3 ||
                    !EctypeNet.MatchesLayout(EctypeNet.EnemyStatusStacks, batch) ||
                    !EctypeNet.MatchesLayout(EctypeNet.EnemyStatus, partApply) ||
                    !EctypeNet.MatchesLayout(EctypeNet.EnemyStatus, partRemove))
                    fails.Add("message layouts: untagged " + plain.Length + " field(s), tagged " + tagged.Length +
                              ", batch " + (batch != null ? batch.Length.ToString() : "none") +
                              "; expected 7, 8 and 8, each matching a layout, and both part messages matching");

                // The part table on a fixed clock.
                EnemyStatusDef def = new EnemyStatusDef(k) { MaxStacks = 2 };
                PartStatusTable table = new PartStatusTable();
                StatusPart panel = new StatusPart(4242, 1), other = new StatusPart(4242, 2);
                List<string> seen = new List<string>();
                Action<PartStatusEntry, bool> note = (e, created) =>
                    seen.Add(e.Part.Index + ":" + e.Stacks + ":" + e.Tag + (created ? ":new" : ""));
                table.Apply(def, panel, null, false, 1000, 5000, 7, 1, 1, note);    // ends 6000
                table.Apply(def, panel, null, false, 2000, 1000, 9, 1, 1, note);    // ends 3000: no refresh
                table.Apply(def, panel, null, false, 2500, 1000, 9, 1, 1, note);    // at MaxStacks
                PartStatusEntry live = table.Find(panel, k);
                int endBefore = live != null ? live.EndTs : -1;
                table.Apply(def, panel, null, false, 3000, 5000, 9, 1, 1, note);    // ends 8000
                table.Apply(def, other, null, false, 1000, 1000, 4, 1, 1, note);    // ends 2000
                string trail = string.Join(" ", seen.ToArray());
                const string wantTrail = "1:1:7:new 1:2:9 1:2:9 1:2:9 2:1:4:new";
                if (trail != wantTrail) fails.Add("part callbacks " + trail + ", expected " + wantTrail);
                if (endBefore != 6000 || live == null || live.EndTs != 8000)
                    fails.Add("part refresh left the end at " + endBefore + " then " +
                              (live != null ? live.EndTs.ToString() : "none") + ", expected 6000 then 8000");

                List<PartStatusEntry> ended = new List<PartStatusEntry>();
                table.Expire(1999, ended);
                int early = ended.Count;
                table.Expire(2000, ended);
                bool otherEnded = ended.Count == 1 && ended[0].Part.Equals(other) && ended[0].Tag == 4;
                List<PartStatusEntry> listed = new List<PartStatusEntry>();
                table.Collect(k, listed);
                table.Collect(k + "#x", listed);
                PartStatusEntry gone = table.Remove(other, k);
                PartStatusEntry taken = table.Remove(panel, k);
                if (early != 0 || !otherEnded || listed.Count != 1 || gone != null || taken == null ||
                    taken.Tag != 9 || table.Count != 0)
                    fails.Add("part expiry and removal: " + early + " ended before 2000, the other part " +
                              (otherEnded ? "ended at 2000 with Tag 4" : "did not end cleanly") + ", " +
                              listed.Count + " listed under the key, " + table.Count + " left; expected 0, " +
                              "ended, 1 and 0, the removed status carrying Tag 9");
            }
            catch (Exception e)
            {
                fails.Add("threw " + e);
            }
            if (fails.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: status parts passed - MasterOnly accepted the " +
                                   "master and refused every other client on every side; untagged, tagged " +
                                   "and part messages matched their layouts; a part status stacked to 2, " +
                                   "moved its end only later, took the latest Tag, expired on the server " +
                                   "clock and was removed with its Tag.");
            else
                Plugin.Log.LogError("Ectype: dev self-test: status parts FAILED - " +
                                    string.Join("; ", fails.ToArray()) + ".");
        }

        /// <summary>Each stance against (master, unknown), (master, same), (master, other),
        /// (not master, unknown), (not master, other), (not master, same).</summary>
        private static string Verdicts()
        {
            List<string> parts = new List<string>();
            foreach (StatusStance stance in new[] { StatusStance.Any, StatusStance.Hostile, StatusStance.Friendly })
            {
                parts.Add(stance + ":" + EnemyStatuses.RemovalVerdict(stance, true, StatusSide.Unknown));
                parts.Add(stance + ":" + EnemyStatuses.RemovalVerdict(stance, true, StatusSide.Same));
                parts.Add(stance + ":" + EnemyStatuses.RemovalVerdict(stance, true, StatusSide.Other));
                parts.Add(stance + ":" + EnemyStatuses.RemovalVerdict(stance, false, StatusSide.Unknown));
                parts.Add(stance + ":" + EnemyStatuses.RemovalVerdict(stance, false, StatusSide.Other));
                parts.Add(stance + ":" + EnemyStatuses.RemovalVerdict(stance, false, StatusSide.Same));
            }
            return string.Join(" ", parts.ToArray());
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

                // A batch in flight across that removal is ignored; one started after it lands.
                StatusRefusal late = EnemyStatuses.HandleStacks(me, new object[] { view, OpenKey, now - 1000, 5000, 1f, 0, 2 });
                if (late != StatusRefusal.InvalidDuration || EnemyStatuses.IsActive(ship, OpenKey))
                    fails.Add("a batch started before the removal answered " + late +
                              (EnemyStatuses.IsActive(ship, OpenKey) ? " and re-created the status" : "") +
                              ", expected it ignored");
                int after = PhotonNetwork.ServerTimestamp + 1;
                StatusRefusal anew = EnemyStatuses.HandleStacks(me, new object[] { view, OpenKey, after, 5000, 1f, 0, 2 });
                if (anew != StatusRefusal.None || EnemyStatuses.Stacks(ship, OpenKey) != 2)
                    fails.Add("a batch started after the removal answered " + anew + " with " +
                              EnemyStatuses.Stacks(ship, OpenKey) + " stack(s), expected 2");
                EnemyStatuses.RemoveLocal(ship, OpenKey);

                // A Hostile status refuses a remover its stance would not let apply it.
                bool created;
                EnemyStatuses.ApplyLocal(ship, hostileDef, now, 5000, 1f, null, Stranger, -1, false, 0, 1, out created);
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
                        { (byte)0, view, HostileKey, PhotonNetwork.ServerTimestamp + 1, 5000, 1f, npc.photonView.ViewID });
                    if (rn != StatusRefusal.None || !EnemyStatuses.IsActive(ship, HostileKey))
                        fails.Add("a Hostile apply from the master with NPC attacker " + npc.name +
                                  " answered " + rn + ", expected it to land");
                    else notes.Add("an NPC-attacker Hostile apply from the master landed");
                }
                else notes.Add(master ? "NPC-attacker case skipped: no hostile NPC in the sector"
                                      : "NPC-attacker case skipped: this client is not the master");

                // MasterOnly: refused from a client that is not the master, with one line however
                // often it sends; from the master, accepted with its Tag.
                EnemyStatuses.Register(new EnemyStatusDef(MasterKey) { Stance = StatusStance.MasterOnly, ShowMarker = false });
                int linesBefore = EnemyStatuses.NotMasterLines;
                object[] fromStranger = { (byte)0, view, MasterKey, PhotonNetwork.ServerTimestamp, 5000, 1f, 0, 5 };
                StatusRefusal m1 = EnemyStatuses.HandleMessage(Stranger, fromStranger);
                StatusRefusal m2 = EnemyStatuses.HandleMessage(Stranger, fromStranger);
                StatusRefusal m3 = EnemyStatuses.HandleMessage(Stranger, new object[] { (byte)1, view, MasterKey });
                int masterLines = EnemyStatuses.NotMasterLines - linesBefore;
                if (m1 != StatusRefusal.NotMaster || m2 != StatusRefusal.NotMaster || m3 != StatusRefusal.NotMaster ||
                    masterLines != 1 || EnemyStatuses.IsActive(ship, MasterKey))
                    fails.Add("MasterOnly from actor " + Stranger + " answered " + m1 + ", " + m2 + ", " + m3 +
                              " with " + masterLines + " line(s), expected NotMaster three times with 1 line");
                if (master)
                {
                    StatusRefusal ma = EnemyStatuses.TryApply(ship, MasterKey, 30f, 1f, null, 5);
                    if (ma != StatusRefusal.None || EnemyStatuses.Tag(ship, MasterKey) != 5)
                        fails.Add("the master's tagged MasterOnly apply answered " + ma + " with Tag " +
                                  EnemyStatuses.Tag(ship, MasterKey) + ", expected None and 5");
                    StatusRefusal mr = EnemyStatuses.HandleMessage(Stranger, new object[] { (byte)1, view, MasterKey });
                    if (mr != StatusRefusal.NotMaster || !EnemyStatuses.IsActive(ship, MasterKey))
                        fails.Add("a MasterOnly removal from actor " + Stranger + " answered " + mr);
                    EnemyStatuses.TryRemove(ship, MasterKey);
                    notes.Add("the master's tagged MasterOnly apply landed with Tag 5");
                }
                else
                {
                    StatusRefusal ma = EnemyStatuses.TryApply(ship, MasterKey, 30f, 1f, null, 5);
                    if (ma != StatusRefusal.NotMaster)
                        fails.Add("a local MasterOnly TryApply on a client that is not the master answered " + ma);
                    notes.Add("a local MasterOnly TryApply was refused NotMaster");
                }

                // A part of the ship: applied and removed through the receive path, its Tag in both
                // callbacks and in the live list.
                List<string> partSeen = new List<string>();
                EnemyStatuses.Register(new EnemyStatusDef(PartKey)
                {
                    ShowMarker = false,
                    OnInstanceApplied = s => partSeen.Add("on:" + s.Tag + (s.Created ? ":new" : "") + (s.IsPart ? ":part" : "")),
                    OnInstanceRemoved = s => partSeen.Add("off:" + s.Tag),
                });
                StatusPart part = new StatusPart(view, 0);
                StatusRefusal pa = EnemyStatuses.HandleMessage(Stranger, new object[]
                    { (byte)2, view, PartKey, PhotonNetwork.ServerTimestamp, 5000, 0, 11 });
                List<StatusInstance> live = new List<StatusInstance>();
                int listed = EnemyStatuses.GetActive(live, PartKey);
                bool partLive = EnemyStatuses.IsActive(part, PartKey) && EnemyStatuses.Tag(part, PartKey) == 11 &&
                                EnemyStatuses.RemainingSeconds(part, PartKey) > 0f && listed == 1 && live[0].IsPart;
                StatusRefusal pr = EnemyStatuses.HandleMessage(Stranger, new object[] { (byte)3, view, PartKey, 0 });
                string partTrail = string.Join(" ", partSeen.ToArray());
                if (pa != StatusRefusal.None || !partLive || pr != StatusRefusal.None ||
                    EnemyStatuses.IsActive(part, PartKey) || partTrail != "on:11:new:part off:11")
                    fails.Add("part status: apply " + pa + ", live " + partLive + " (" + listed + " listed), remove " +
                              pr + ", callbacks '" + partTrail + "'; expected None, True (1), None, " +
                              "'on:11:new:part off:11'");

                // The master naming an attacker that no longer resolves here (an NPC gone while
                // its batch waited) is accepted.
                if (master)
                {
                    EnemyStatuses.RemoveLocal(ship, HostileKey);
                    StatusRefusal rg = EnemyStatuses.HandleMessage(me, new object[]
                        { (byte)0, view, HostileKey, PhotonNetwork.ServerTimestamp + 1, 5000, 1f, GoneView });
                    if (rg != StatusRefusal.None || !EnemyStatuses.IsActive(ship, HostileKey))
                        fails.Add("a Hostile apply from the master naming a vanished attacker answered " + rg +
                                  ", expected it to land");
                    else notes.Add("a master apply naming a vanished attacker landed");
                }
            }
            catch (Exception e)
            {
                fails.Add("threw " + e);
            }
            finally
            {
                try { EnemyStatuses.RemoveLocal(ship, OpenKey); } catch (Exception) { }
                try { EnemyStatuses.RemoveLocal(ship, HostileKey); } catch (Exception) { }
                try { EnemyStatuses.RemoveLocal(ship, MasterKey); } catch (Exception) { }
                try { EnemyStatuses.RemovePartLocal(new StatusPart(ship.photonView.ViewID, 0), PartKey); } catch (Exception) { }
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
                                   "by actor " + Stranger + " and a batch started before that removal was ignored, a " +
                                   "received removal dropped the queued batch, a MasterOnly status from actor " +
                                   Stranger + " was refused with one log line, a part status carried Tag 11 " +
                                   "through both callbacks; " +
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
