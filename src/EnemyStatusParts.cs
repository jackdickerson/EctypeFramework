using System;
using System.Collections.Generic;

using CG.Space;
using Photon.Pun;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// A part of a networked object, named the same way on every client: the id of the
    /// PhotonView the part sits under, and the part's index among that view's components of the
    /// part's type (<see cref="Of{T}"/>). The type is not part of the name: every client must
    /// name and resolve the part with the same type parameter. The index agrees across clients
    /// only while every client holds the same components under the view in the same order, which
    /// holds for a component with a PhotonView of its own and fails when children are added at
    /// runtime under a shared view. An enemy status can target a part: a panel, a door, a console
    /// of a ship.
    /// </summary>
    public readonly struct StatusPart : IEquatable<StatusPart>
    {
        /// <summary>The PhotonView id the part sits under. 0 names no part.</summary>
        public readonly int ViewId;

        /// <summary>The part's index under that view, 0 or more.</summary>
        public readonly int Index;

        /// <summary>A part by view id and index, as another client named it.</summary>
        public StatusPart(int viewId, int index)
        {
            ViewId = viewId;
            Index = index;
        }

        /// <summary>True when the part names a view (id not 0) and a non-negative index.</summary>
        public bool IsValid { get { return ViewId != 0 && Index >= 0; } }

        /// <summary>
        /// Names a component: the nearest PhotonView at or above it, and its index in that view's
        /// <c>GetComponentsInChildren&lt;T&gt;(true)</c>, <typeparamref name="T"/> being the static
        /// type at the call site; pass it explicitly. An invalid part (<see cref="IsValid"/>
        /// false) when the component is null or has no PhotonView above it.
        /// </summary>
        public static StatusPart Of<T>(T part) where T : Component
        {
            if (part == null) return default(StatusPart);
            PhotonView view = part.GetComponentInParent<PhotonView>();
            if (view == null) return default(StatusPart);
            int index = Array.IndexOf(view.GetComponentsInChildren<T>(true), part);
            return index >= 0 ? new StatusPart(view.ViewID, index) : default(StatusPart);
        }

        /// <summary>The component this part names on this client, or null when the view is not
        /// here or has fewer components of the type. The inverse of <see cref="Of{T}"/> for the
        /// same <typeparamref name="T"/>.</summary>
        public T Resolve<T>() where T : Component
        {
            if (!IsValid) return null;
            PhotonView view = PhotonView.Find(ViewId);
            if (view == null) return null;
            T[] all = view.GetComponentsInChildren<T>(true);
            return Index < all.Length ? all[Index] : null;
        }

        /// <inheritdoc/>
        public bool Equals(StatusPart other) { return ViewId == other.ViewId && Index == other.Index; }

        /// <inheritdoc/>
        public override bool Equals(object obj) { return obj is StatusPart && Equals((StatusPart)obj); }

        /// <inheritdoc/>
        public override int GetHashCode() { return ViewId * 397 ^ Index; }

        /// <summary>"view &lt;id&gt; part &lt;index&gt;".</summary>
        public override string ToString() { return "view " + ViewId + " part " + Index; }
    }

    /// <summary>
    /// One live enemy status, as the callbacks and <see cref="EnemyStatuses.GetActive"/> report
    /// it. A snapshot: a later application or refresh is not reflected in a copy already taken,
    /// except that <see cref="RemainingSeconds"/> is computed when read.
    /// </summary>
    public readonly struct StatusInstance
    {
        /// <summary>The status key.</summary>
        public readonly string Key;

        /// <summary>The space object it runs on; null for a part target, and null when the object
        /// was destroyed before the status ended.</summary>
        public readonly OrbitObject Target;

        /// <summary>The part it runs on, when <see cref="IsPart"/>.</summary>
        public readonly StatusPart Part;

        /// <summary>True for a part target, false for a space object.</summary>
        public readonly bool IsPart;

        /// <summary>The Tag of the latest accepted application (0 for none).</summary>
        public readonly int Tag;

        /// <summary>Stacks at the time of the snapshot.</summary>
        public readonly int Stacks;

        /// <summary>When the status ends, in PhotonNetwork.ServerTimestamp milliseconds.</summary>
        public readonly int EndServerTime;

        /// <summary>True in <see cref="EnemyStatusDef.OnInstanceApplied"/> for the application
        /// that started this life of the status; false for later applications and elsewhere.</summary>
        public readonly bool Created;

        internal StatusInstance(string key, OrbitObject target, StatusPart part, bool isPart, int tag,
                                int stacks, int endServerTime, bool created)
        {
            Key = key;
            Target = target;
            Part = part;
            IsPart = isPart;
            Tag = tag;
            Stacks = stacks;
            EndServerTime = endServerTime;
            Created = created;
        }

        /// <summary>Seconds until <see cref="EndServerTime"/> on the server clock, 0 once past.</summary>
        public float RemainingSeconds
        {
            get
            {
                int ms = unchecked(EndServerTime - PhotonNetwork.ServerTimestamp);
                return ms > 0 ? ms / 1000f : 0f;
            }
        }
    }

    /// <summary>One live status on a part.</summary>
    internal sealed class PartStatusEntry
    {
        internal EnemyStatusDef Def;
        internal StatusPart Part;
        internal int StartTs;
        internal int EndTs;
        internal int Stacks;
        internal int Tag;
        internal int ApplierActor;

        /// <summary>The view the part sits under, held so a status ends when that view is
        /// destroyed. Null in the offline self-test, which tracks no view.</summary>
        internal PhotonView View;
        internal bool TracksView;

        internal StatusInstance Instance(bool created)
        {
            return new StatusInstance(Def.Key, null, Part, true, Tag, Stacks, EndTs, created);
        }
    }

    /// <summary>
    /// The live statuses on parts, keyed by (view, index, key), with the same timing, stacking
    /// and refresh rules as a status on a space object: re-applying adds a stack up to MaxStacks
    /// and, with RefreshOnReapply, moves the end later and never earlier; each application
    /// replaces the Tag. Pure data, so the dev self-test drives it with the clock as an argument.
    /// </summary>
    internal sealed class PartStatusTable
    {
        /// <summary>The most part statuses one view may hold at once. A status that would be the
        /// next is refused, so no sender can grow the table without bound.</summary>
        internal const int MaxPerView = 64;

        private readonly Dictionary<string, PartStatusEntry> _live = new Dictionary<string, PartStatusEntry>();
        private readonly Dictionary<int, int> _perView = new Dictionary<int, int>();
        private readonly List<string> _ended = new List<string>();

        internal int Count { get { return _live.Count; } }

        /// <summary>Live part statuses under one view.</summary>
        internal int CountOn(int viewId)
        {
            int n;
            return _perView.TryGetValue(viewId, out n) ? n : 0;
        }

        /// <summary>True when the status is not live and its view already holds
        /// <see cref="MaxPerView"/> statuses, so <see cref="Apply"/> would refuse it.</summary>
        internal bool Full(StatusPart part, string key)
        {
            return Find(part, key) == null && CountOn(part.ViewId) >= MaxPerView;
        }

        private void Counted(int viewId, int delta)
        {
            int n = CountOn(viewId) + delta;
            if (n > 0) _perView[viewId] = n;
            else _perView.Remove(viewId);
        }

        private static string Id(StatusPart part, string key)
        {
            return part.ViewId + "/" + part.Index + "/" + key;
        }

        internal PartStatusEntry Find(StatusPart part, string key)
        {
            PartStatusEntry e;
            return _live.TryGetValue(Id(part, key), out e) ? e : null;
        }

        /// <summary>
        /// Applies <paramref name="applications"/> applications at once, the timer moved once,
        /// and calls <paramref name="each"/> after each with whether it created the status.
        /// Returns the live entry, or null when the status would be new and its view is full.
        /// </summary>
        internal PartStatusEntry Apply(EnemyStatusDef def, StatusPart part, PhotonView view, bool tracksView,
                                       int startTs, int durationMs, int tag, int applierActor,
                                       int applications, Action<PartStatusEntry, bool> each)
        {
            if (applications < 1) applications = 1;
            string id = Id(part, def.Key);
            PartStatusEntry e;
            int end = unchecked(startTs + durationMs);
            bool created = !_live.TryGetValue(id, out e);
            if (created)
            {
                if (CountOn(part.ViewId) >= MaxPerView) return null;
                Counted(part.ViewId, 1);
                e = new PartStatusEntry
                {
                    Def = def, Part = part, View = view, TracksView = tracksView, StartTs = startTs,
                    EndTs = end, ApplierActor = applierActor,
                };
                _live[id] = e;
            }
            else if (def.RefreshOnReapply && unchecked(end - e.EndTs) > 0)
            {
                e.EndTs = end;
            }
            e.Tag = tag;
            for (int i = 0; i < applications; i++)
            {
                bool first = created && i == 0;
                // The creating application always holds one stack, as on a space object.
                if (first || e.Stacks < def.MaxStacks) e.Stacks++;
                if (each != null) each(e, first);
            }
            return e;
        }

        /// <summary>Takes one status out, or returns null when it is not live.</summary>
        internal PartStatusEntry Remove(StatusPart part, string key)
        {
            string id = Id(part, key);
            PartStatusEntry e;
            if (!_live.TryGetValue(id, out e)) return null;
            _live.Remove(id);
            Counted(part.ViewId, -1);
            return e;
        }

        /// <summary>Takes out every status whose end is at or before <paramref name="now"/>, or
        /// whose tracked view is destroyed, into <paramref name="ended"/>.</summary>
        internal void Expire(int now, List<PartStatusEntry> ended)
        {
            if (_live.Count == 0) return;
            _ended.Clear();
            foreach (KeyValuePair<string, PartStatusEntry> kv in _live)
            {
                PartStatusEntry e = kv.Value;
                if (unchecked(now - e.EndTs) >= 0 || (e.TracksView && e.View == null)) _ended.Add(kv.Key);
            }
            for (int i = 0; i < _ended.Count; i++)
            {
                PartStatusEntry e = _live[_ended[i]];
                ended.Add(e);
                _live.Remove(_ended[i]);
                Counted(e.Part.ViewId, -1);
            }
        }

        /// <summary>Appends every live status, or those under one key, to <paramref name="into"/>.</summary>
        internal void Collect(string key, List<PartStatusEntry> into)
        {
            foreach (KeyValuePair<string, PartStatusEntry> kv in _live)
                if (key == null || kv.Value.Def.Key == key) into.Add(kv.Value);
        }
    }

    public static partial class EnemyStatuses
    {
        /// <summary>Live statuses on parts.</summary>
        internal static readonly PartStatusTable Parts = new PartStatusTable();

        /// <summary>GetActive's own list. No callback runs while it is filled, and nothing else
        /// uses it, so a callback that calls GetActive cannot disturb a loop elsewhere.</summary>
        private static readonly List<PartStatusEntry> ActiveScratch = new List<PartStatusEntry>();

        /// <summary>PollParts' list of ended statuses, emptied into an array before any
        /// callback runs.</summary>
        private static readonly List<PartStatusEntry> ExpireScratch = new List<PartStatusEntry>();

        /// <summary>
        /// Apply (or re-apply) a status to a part of a networked object for a duration, and
        /// broadcast it to every peer running the framework. Same timer (server clock), stacking,
        /// refresh, Stance and removal rules as on a space object. A part has no stats, no hit
        /// points and no marker: stat entries, damage over time, OnTick, ability suspension,
        /// the marker and the visual do not apply, and the mod draws its own countdown from
        /// <see cref="RemainingSeconds(StatusPart, string)"/> or <see cref="GetActive"/>.
        /// <paramref name="tag"/> is carried to every client, as on the tagged object Apply.
        /// CoalesceMs does not apply: each application to a part is sent at once.
        /// </summary>
        public static void Apply(StatusPart part, string key, float seconds, int tag = 0)
        {
            ApplyPartCore(part, key, seconds, tag, true);
        }

        /// <summary><see cref="Apply(StatusPart, string, float, int)"/>, returning
        /// <see cref="StatusRefusal.None"/> when the status was applied here and sent, else
        /// why not.</summary>
        public static StatusRefusal TryApply(StatusPart part, string key, float seconds, int tag = 0)
        {
            return ApplyPartCore(part, key, seconds, tag, false);
        }

        /// <summary>Remove a status from a part early, everywhere. Accepted from the senders a
        /// removal from a space object is accepted from.</summary>
        public static void Remove(StatusPart part, string key)
        {
            RemovePartCore(part, key, true);
        }

        /// <summary><see cref="Remove(StatusPart, string)"/>, returning why nothing was sent.</summary>
        public static StatusRefusal TryRemove(StatusPart part, string key)
        {
            return RemovePartCore(part, key, false);
        }

        /// <summary>True while the status with this key is active on the part.</summary>
        public static bool IsActive(StatusPart part, string key)
        {
            return Parts.Find(part, key) != null;
        }

        /// <summary>Seconds until the status on the part ends, 0 when not active.</summary>
        public static float RemainingSeconds(StatusPart part, string key)
        {
            PartStatusEntry e = Parts.Find(part, key);
            return e != null ? e.Instance(false).RemainingSeconds : 0f;
        }

        /// <summary>Current stack count of the status on the part, 0 when not active.</summary>
        public static int Stacks(StatusPart part, string key)
        {
            PartStatusEntry e = Parts.Find(part, key);
            return e != null ? e.Stacks : 0;
        }

        /// <summary>The Tag the latest accepted application to the part carried, 0 when not
        /// active or applied without one.</summary>
        public static int Tag(StatusPart part, string key)
        {
            PartStatusEntry e = Parts.Find(part, key);
            return e != null ? e.Tag : 0;
        }

        /// <summary>
        /// Appends every live status on this client, on space objects and on parts, to
        /// <paramref name="into"/>; only those under <paramref name="key"/> when it is not null.
        /// Returns how many it appended. The list a mod's HUD reads each frame to draw its
        /// countdowns: <see cref="StatusInstance.RemainingSeconds"/> and
        /// <see cref="StatusInstance.Tag"/> per entry.
        /// </summary>
        public static int GetActive(List<StatusInstance> into, string key = null)
        {
            if (into == null) return 0;
            int added = 0;
            foreach (KeyValuePair<string, EnemyStatusEffect> kv in Active)
            {
                EnemyStatusEffect fx = kv.Value;
                if (key != null && fx.Def.Key != key) continue;
                into.Add(new StatusInstance(fx.Def.Key, fx.Target, default(StatusPart), false, fx.Tag,
                                            fx.StackCount, fx.EndTs, false));
                added++;
            }
            ActiveScratch.Clear();
            Parts.Collect(key, ActiveScratch);
            for (int i = 0; i < ActiveScratch.Count; i++) into.Add(ActiveScratch[i].Instance(false));
            added += ActiveScratch.Count;
            ActiveScratch.Clear();
            return added;
        }

        // ---------------------------------------------------------------- part internals

        /// <summary>The space object whose side a part is judged against: the player ship it
        /// belongs to, else the nearest space object above its view. Null when neither exists,
        /// which makes the side unknown.</summary>
        private static OrbitObject OwnerOfPart(PhotonView view)
        {
            if (view == null) return null;
            OrbitObject ship = Topology.ShipOfPart(view);
            return ship != null ? ship : view.GetComponentInParent<OrbitObject>();
        }

        private static string PartText(StatusPart part)
        {
            return part.ToString();
        }

        /// <summary>The removal record of a part status, kept apart from its view's own.</summary>
        private static string PartTombKey(string key, int index)
        {
            return key + "\u0001" + index;
        }

        private static StatusRefusal PartPreflight(StatusPart part, string key, out EnemyStatusDef def,
                                                   out PhotonView view)
        {
            view = null;
            def = null;
            if (!part.IsValid)
            {
                Plugin.Log.LogWarning("Ectype: status '" + key + "' on an invalid part (" + PartText(part) +
                                      ") ignored.");
                return StatusRefusal.InvalidTarget;
            }
            if (!Defs.TryGetValue(key ?? "", out def))
            {
                if (WarnedOnce.Add("nodef:" + key))
                    Plugin.Log.LogError("Ectype: status '" + key + "' is not registered " +
                                        "on this client. Register the template at Awake.");
                return StatusRefusal.NotRegistered;
            }
            if (!Plugin.PatchesApplied)
            {
                if (WarnedOnce.Add("inert"))
                    Plugin.Log.LogWarning("Ectype: framework is inert this session; enemy " +
                                          "statuses are disabled.");
                return StatusRefusal.Inert;
            }
            view = PhotonView.Find(part.ViewId);
            if (view == null)
            {
                if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: status '" + key + "' on " + PartText(part) +
                                       ": no such view on this client.");
                return StatusRefusal.InvalidTarget;
            }
            return StatusRefusal.None;
        }

        /// <summary>One warning per key whose template sets what a part cannot carry.</summary>
        private static void WarnPartFields(EnemyStatusDef def)
        {
            bool unused = def.Entries.Count > 0 || def.DamagePerSecond > 0f || def.OnTick != null ||
                          def.SuspendsAbilities;
            if (unused && WarnedOnce.Add("partfields:" + def.Key))
                Plugin.Log.LogWarning("Ectype: status '" + def.Key + "' applied to a part sets stat " +
                                      "entries, damage over time, OnTick or SuspendsAbilities, which " +
                                      "apply to space objects only; on a part it runs its timer and " +
                                      "callbacks.");
        }

        private static StatusRefusal ApplyPartCore(StatusPart part, string key, float seconds, int tag,
                                                   bool fireAndForget)
        {
            try
            {
                EnemyStatusDef def;
                PhotonView view;
                StatusRefusal pre = PartPreflight(part, key, out def, out view);
                if (pre != StatusRefusal.None) return pre;
                if (!(seconds > 0f) || float.IsInfinity(seconds))
                {
                    Plugin.Log.LogWarning("Ectype: Apply('" + key + "') with duration " +
                                          seconds + "s ignored.");
                    return StatusRefusal.InvalidDuration;
                }
                WarnPartFields(def);
                int me = LocalActor();
                int force;
                StatusSide side = def.Stance == StatusStance.Hostile || def.Stance == StatusStance.Friendly
                                      ? SideOf(me, OwnerOfPart(view), out force) : StatusSide.Unknown;
                StatusRefusal verdict = ApplicationVerdict(def.Stance, IsMaster(me), side);
                if (verdict != StatusRefusal.None)
                {
                    ReportOwnRefusal("Apply", key, PartText(part), Reason(verdict, def, side, me), fireAndForget);
                    return verdict;
                }
                StatusRefusal room = PartRoom(me, part, key, view);
                if (room != StatusRefusal.None) return room;
                int startTs = PhotonNetwork.ServerTimestamp;
                int durationMs = (int)(seconds * 1000f);
                ApplyPartLocal(def, part, view, startTs, durationMs, tag, me, 1);
                Broadcast(new object[] { (byte)2, part.ViewId, key, startTs, durationMs, part.Index, tag });
                return StatusRefusal.None;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: Apply('" + key + "') failed: " + e);
                return StatusRefusal.Failed;
            }
        }

        private static StatusRefusal RemovePartCore(StatusPart part, string key, bool fireAndForget)
        {
            try
            {
                EnemyStatusDef def;
                PhotonView view;
                StatusRefusal pre = PartPreflight(part, key, out def, out view);
                if (pre != StatusRefusal.None) return pre;
                int me = LocalActor();
                StatusSide side;
                StatusRefusal verdict = JudgeRemoval(def, me, OwnerOfPart(view), out side);
                if (verdict != StatusRefusal.None)
                {
                    ReportOwnRefusal("Remove", key, PartText(part), Reason(verdict, def, side, me), fireAndForget);
                    return verdict;
                }
                // Sent and remembered before the callbacks run, as on a space object: an
                // OnInstanceRemoved that applies the same key again travels after the removal.
                int removedTs = PhotonNetwork.ServerTimestamp;
                RecordPartRemoval(part, def, removedTs);
                Broadcast(new object[] { (byte)3, part.ViewId, key, part.Index, removedTs });
                RemovePartLocal(part, key);
                return StatusRefusal.None;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: Remove('" + key + "') failed: " + e);
                return StatusRefusal.Failed;
            }
        }

        internal static void ApplyPartLocal(EnemyStatusDef def, StatusPart part, PhotonView view, int startTs,
                                            int durationMs, int tag, int applierActor, int applications)
        {
            Parts.Apply(def, part, view, view != null, startTs, durationMs, tag, applierActor, applications,
                        ReportPartApplied);
        }

        private static void ReportPartApplied(PartStatusEntry e, bool created)
        {
            if (e.Def.OnInstanceApplied == null) return;
            StatusInstance now = e.Instance(created);
            StatusStats.SafeCb(() => e.Def.OnInstanceApplied(now), WarnedOnce.Add, "status", e.Def.Key);
        }

        private static void ReportPartRemoved(PartStatusEntry e)
        {
            if (e.Def.OnInstanceRemoved == null) return;
            StatusInstance ended = e.Instance(false);
            StatusStats.SafeCb(() => e.Def.OnInstanceRemoved(ended), WarnedOnce.Add, "status", e.Def.Key);
        }

        internal static void RemovePartLocal(StatusPart part, string key)
        {
            // Out of the table before the callback, so an OnInstanceRemoved that applies the same
            // key again starts a new life.
            PartStatusEntry e = Parts.Remove(part, key);
            if (e != null) ReportPartRemoved(e);
        }

        private static void RecordPartRemoval(StatusPart part, EnemyStatusDef def, int removedTs)
        {
            Tombstones.Record(part.ViewId, PartTombKey(def.Key, part.Index), removedTs, RemovalMemoryMs(def));
        }

        /// <summary>True when <paramref name="index"/> can name a component under a view holding
        /// <paramref name="componentCount"/> components: an index at or past that count names no
        /// part of any type.</summary>
        internal static bool PartIndexInRange(int index, int componentCount)
        {
            return index >= 0 && index < componentCount;
        }

        /// <summary>
        /// Whether a part status the table does not hold yet may be created: refused
        /// <see cref="StatusRefusal.Failed"/> when its view already holds
        /// <see cref="PartStatusTable.MaxPerView"/> statuses, and
        /// <see cref="StatusRefusal.InvalidTarget"/> when its index is at or past the number of
        /// components under the view. A status already live passes without either check.
        /// </summary>
        private static StatusRefusal PartRoom(int sender, StatusPart part, string key, PhotonView view)
        {
            if (Parts.Find(part, key) != null) return StatusRefusal.None;
            if (Parts.Full(part, key))
            {
                if (WarnedOnce.Add("partcap:" + part.ViewId))
                    Plugin.Log.LogWarning("Ectype: status '" + key + "' on " + PartText(part) + " refused: the " +
                                          "view already holds " + PartStatusTable.MaxPerView + " part statuses, " +
                                          "the most one view may hold (logged once per view).");
                return StatusRefusal.Failed;
            }
            int count = view != null ? view.GetComponentsInChildren<Component>(true).Length : 0;
            if (!PartIndexInRange(part.Index, count))
            {
                string line = "Ectype: status '" + key + "' on " + PartText(part) + " from actor " + sender +
                              " refused: the view has " + count + " components, so the index names no part.";
                if (sender == LocalActor())
                {
                    if (WarnedOnce.Add("partindex:" + key)) Plugin.Log.LogWarning(line);
                }
                else StatusRefusalLog.Report(sender, "apply", key, line);
                return StatusRefusal.InvalidTarget;
            }
            return StatusRefusal.None;
        }

        /// <summary>Ends part statuses whose time is up or whose view is gone. Every client runs
        /// it on the server clock, so every client ends a status at the same moment.</summary>
        private static void PollParts()
        {
            if (Parts.Count == 0) return;
            EndExpired(Parts, PhotonNetwork.ServerTimestamp);
        }

        /// <summary>
        /// Takes the statuses ended at <paramref name="now"/> out of the table and runs their
        /// OnInstanceRemoved. The callbacks run over an array of their own, so one that reads or
        /// changes the statuses (GetActive, Apply, Remove) cannot cut the loop short: every ended
        /// status gets its callback. Returns how many ended.
        /// </summary>
        internal static int EndExpired(PartStatusTable table, int now)
        {
            ExpireScratch.Clear();
            table.Expire(now, ExpireScratch);
            if (ExpireScratch.Count == 0) return 0;
            PartStatusEntry[] ended = ExpireScratch.ToArray();
            ExpireScratch.Clear();
            for (int i = 0; i < ended.Length; i++) ReportPartRemoved(ended[i]);
            return ended.Length;
        }

        /// <summary>
        /// A received part apply (op 2: <c>[byte 2, int viewId, string key, int startTs,
        /// int durationMs, int index, int tag]</c>) or remove (op 3: <c>[byte 3, int viewId,
        /// string key, int index, int removedTs]</c>), judged like its space-object counterpart,
        /// the part's side being that of the ship or space object it belongs to. The caller has
        /// checked the fields against the op's layout.
        /// </summary>
        internal static StatusRefusal HandlePart(int sender, byte op, int viewId, string key, object[] args)
        {
            if (!EctypeNet.FitsEnemyStatusOp(args) || (op != 2 && op != 3)) return StatusRefusal.Failed;
            StatusPart part = new StatusPart(viewId, op == 2 ? (int)args[5] : (int)args[3]);
            if (!part.IsValid) return StatusRefusal.InvalidTarget;
            PhotonView view = PhotonView.Find(viewId);
            if (view == null) return StatusRefusal.InvalidTarget;   // not here yet, or gone
            EnemyStatusDef def = ReceivedDef(key);
            if (def == null) return StatusRefusal.NotRegistered;
            OrbitObject owner = OwnerOfPart(view);

            if (op == 3)
            {
                StatusSide rside;
                StatusRefusal verdict = JudgeRemoval(def, sender, owner, out rside);
                if (verdict != StatusRefusal.None)
                {
                    if (verdict == StatusRefusal.NotMaster || Parts.Find(part, key) != null)
                        ReportReceivedRefusal(sender, "remove", key, PartText(part), verdict, def, rside);
                    return verdict;
                }
                RecordPartRemoval(part, def, RemovalTime((int)args[4]));
                RemovePartLocal(part, key);
                return StatusRefusal.None;
            }

            int startTs = (int)args[3], durationMs = (int)args[4], tag = (int)args[6];
            if (SessionData.PredatesSession(startTs) ||
                Tombstones.Covers(viewId, PartTombKey(key, part.Index), startTs, PhotonNetwork.ServerTimestamp))
            {
                if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: status '" + key + "' on " + PartText(part) + ": an application " +
                                       "from actor " + sender + " started before this client's session or " +
                                       "before its removal here; ignored.");
                return StatusRefusal.InvalidDuration;
            }
            if (unchecked(startTs + durationMs - PhotonNetwork.ServerTimestamp) <= 0 && Parts.Find(part, key) == null)
                return StatusRefusal.InvalidDuration;   // over before it arrived
            int force;
            StatusSide side = def.Stance == StatusStance.Hostile || def.Stance == StatusStance.Friendly
                                  ? SideOf(sender, owner, out force) : StatusSide.Unknown;
            StatusRefusal stance = ApplicationVerdict(def.Stance, IsMaster(sender), side);
            if (stance != StatusRefusal.None)
            {
                ReportReceivedRefusal(sender, "apply", key, PartText(part), stance, def, side);
                return stance;
            }
            StatusRefusal room = PartRoom(sender, part, key, view);
            if (room != StatusRefusal.None) return room;
            ApplyPartLocal(def, part, view, startTs, durationMs, tag, sender, 1);
            return StatusRefusal.None;
        }
    }
}
