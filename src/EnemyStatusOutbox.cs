using System.Collections.Generic;

namespace Ectype
{
    /// <summary>
    /// The outgoing side of coalesced applications (<see cref="EnemyStatusDef.CoalesceMs"/>).
    /// The applying client has already applied each one locally; this holds, per (target view,
    /// status key), what the peers still need to hear: how many applications arrived since the
    /// batch opened, and the timer of the local status they went into. A batch opens with its
    /// first application and is due one window later, so a burst of hits costs one message per
    /// target per window.
    ///
    /// The message carries a count, not a total, because counts from several appliers add up
    /// on every receiver in any order, where two totals for one target would overwrite each
    /// other. The timer it carries is the local status's own: its start and its current end.
    /// A receiver that creates the status from the message ends it where the applier's does
    /// (paying ticks only from a grace before arrival when that start is old), and one that
    /// already has it extends its end to that point at most.
    ///
    /// A batch belongs to one life of the local status. When that status ends or is created
    /// anew, the open batch is closed and goes out on the next flush, and later applications
    /// open a new one, so no message mixes the count or the timer of two lives.
    ///
    /// Pure data: no game object and no Photon call, so the dev self-test drives it directly.
    /// </summary>
    internal sealed class EnemyStatusOutbox
    {
        /// <summary>The most applications one message may carry; a receiver clamps to the same
        /// bound.</summary>
        internal const int MaxApplications = 255;

        internal sealed class Batch
        {
            internal int ViewId;
            internal string Key;
            internal string TargetName;
            internal int StartTs;
            internal int EndTs;
            internal float Magnitude;
            internal int AttackerView;
            internal int Applications;
            internal int WindowMs;
            internal float DueAt;

            internal int DurationMs { get { return unchecked(EndTs - StartTs); } }

            /// <summary>The message fields after the subtype:
            /// [int viewId, string key, int startTs, int durationMs, float magnitude,
            /// int attackerView, int applications].</summary>
            internal object[] Args()
            {
                return new object[] { ViewId, Key, StartTs, DurationMs, Magnitude, AttackerView,
                                      Applications };
            }
        }

        private struct Id
        {
            internal int ViewId;
            internal string Key;
        }

        private sealed class IdComparer : IEqualityComparer<Id>
        {
            public bool Equals(Id a, Id b) { return a.ViewId == b.ViewId && string.Equals(a.Key, b.Key); }
            public int GetHashCode(Id id) { return id.ViewId * 397 ^ (id.Key != null ? id.Key.GetHashCode() : 0); }
        }

        private readonly Dictionary<Id, Batch> _open = new Dictionary<Id, Batch>(new IdComparer());
        /// <summary>Batches closed before their window ended: due at the next flush, oldest first.</summary>
        private readonly List<Batch> _closed = new List<Batch>();
        private readonly List<Id> _due = new List<Id>();

        internal int Count { get { return _open.Count + _closed.Count; } }

        /// <summary>
        /// Records one local application. <paramref name="startTs"/> and <paramref name="endTs"/>
        /// are the local status's timer after the application. <paramref name="now"/> and the
        /// window are on one monotonic clock, in seconds and milliseconds.
        /// </summary>
        internal void Add(int viewId, string key, string targetName, int startTs, int endTs,
                          float magnitude, int attackerView, int windowMs, float now)
        {
            Id id = new Id { ViewId = viewId, Key = key };
            Batch b;
            if (_open.TryGetValue(id, out b) && b.Applications >= MaxApplications)
            {
                Close(viewId, key);
                b = null;
            }
            if (b == null)
            {
                b = new Batch
                {
                    ViewId = viewId,
                    Key = key,
                    TargetName = targetName,
                    Magnitude = magnitude,
                    AttackerView = attackerView,
                    WindowMs = windowMs,
                    DueAt = now + windowMs / 1000f,
                };
                _open[id] = b;
            }
            b.Applications++;
            b.StartTs = startTs;
            b.EndTs = endTs;
        }

        /// <summary>Closes the open batch for one target and key, if any: it goes out at the next
        /// flush, and the next application opens a new batch. Called when the local status ends
        /// or is created anew.</summary>
        internal void Close(int viewId, string key)
        {
            Id id = new Id { ViewId = viewId, Key = key };
            Batch b;
            if (!_open.TryGetValue(id, out b)) return;
            _open.Remove(id);
            _closed.Add(b);
        }

        /// <summary>Forgets everything queued for one target and key, closed batches included
        /// (the status was removed, so the peers must not re-add it afterwards).</summary>
        internal void Drop(int viewId, string key)
        {
            _open.Remove(new Id { ViewId = viewId, Key = key });
            _closed.RemoveAll(b => b.ViewId == viewId && b.Key == key);
        }

        /// <summary>Moves every closed batch, then every open batch due at <paramref name="now"/>
        /// (every batch when <paramref name="all"/>), oldest first, into <paramref name="into"/>.</summary>
        internal void TakeDue(float now, bool all, List<Batch> into)
        {
            into.AddRange(_closed);
            _closed.Clear();
            if (_open.Count == 0) return;
            _due.Clear();
            foreach (KeyValuePair<Id, Batch> kv in _open)
                if (all || now >= kv.Value.DueAt) _due.Add(kv.Key);
            _due.Sort((a, b) => _open[a].DueAt.CompareTo(_open[b].DueAt));
            for (int i = 0; i < _due.Count; i++)
            {
                into.Add(_open[_due[i]]);
                _open.Remove(_due[i]);
            }
        }
    }

    /// <summary>
    /// Recent removals, per (target view, status key), on the server clock. A batch another
    /// client flushed before it learned of a removal can still arrive after the removal here,
    /// because Photon orders messages per sender, not across senders. Such a batch carries the
    /// start of a life that began before the removal, and a single application carries its send
    /// time, so an application whose start precedes the removal is ignored while the record
    /// lasts: long enough for anything already in flight. An application started after the
    /// removal is new and lands.
    ///
    /// Pure data, so the dev self-test drives it directly.
    /// </summary>
    internal sealed class EnemyStatusTombstones
    {
        private struct Tomb
        {
            internal int RemovedTs;
            internal int UntilTs;
        }

        private readonly Dictionary<string, Tomb> _tombs = new Dictionary<string, Tomb>();
        private readonly List<string> _stale = new List<string>();

        private static string Id(int viewId, string key) { return viewId + "\0" + key; }

        /// <summary>Records a removal at <paramref name="removedTs"/>, kept for
        /// <paramref name="keepMs"/>.</summary>
        internal void Record(int viewId, string key, int removedTs, int keepMs)
        {
            Prune(removedTs);
            _tombs[Id(viewId, key)] = new Tomb { RemovedTs = removedTs, UntilTs = unchecked(removedTs + keepMs) };
        }

        /// <summary>True when an application started at <paramref name="startTs"/> predates a
        /// removal recorded for this target and key that is still kept at <paramref name="now"/>.</summary>
        internal bool Covers(int viewId, string key, int startTs, int now)
        {
            Tomb t;
            if (!_tombs.TryGetValue(Id(viewId, key), out t)) return false;
            if (unchecked(now - t.UntilTs) > 0)
            {
                _tombs.Remove(Id(viewId, key));
                return false;
            }
            return unchecked(t.RemovedTs - startTs) > 0;
        }

        private void Prune(int now)
        {
            if (_tombs.Count < 32) return;
            _stale.Clear();
            foreach (KeyValuePair<string, Tomb> kv in _tombs)
                if (unchecked(now - kv.Value.UntilTs) > 0) _stale.Add(kv.Key);
            for (int i = 0; i < _stale.Count; i++) _tombs.Remove(_stale[i]);
        }
    }
}
