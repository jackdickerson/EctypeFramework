using System.Collections.Generic;

namespace Ectype
{
    /// <summary>
    /// The outgoing side of coalesced applications (<see cref="EnemyStatusDef.CoalesceMs"/>).
    /// The applying client has already applied each one locally; this holds, per (target view,
    /// status key), what the peers still need to hear: how many applications arrived since the
    /// batch opened, and the timer they leave. A batch opens with its first application and is
    /// due one window later, so a burst of hits costs one message per target per window.
    ///
    /// The message carries a count, not a total, because counts from several appliers add up
    /// on every receiver in any order, where two totals for one target would overwrite each
    /// other. The timer it carries is the one the local applications left: the first
    /// application's start, and the last one's end when the template refreshes on re-apply,
    /// else the first one's. A receiver that creates the status from the message therefore
    /// starts and ends it where the applier did, and one that already has it moves the end the
    /// same way.
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
            internal int FirstStartTs;
            internal int FirstDurationMs;
            internal int LastEndTs;
            internal bool Refresh;
            internal float Magnitude;
            internal int AttackerView;
            internal int Applications;
            internal int WindowMs;
            internal float DueAt;

            /// <summary>The duration to send with <see cref="FirstStartTs"/>: it ends where the
            /// local applications left the timer.</summary>
            internal int DurationMs
            {
                get { return Refresh ? unchecked(LastEndTs - FirstStartTs) : FirstDurationMs; }
            }

            /// <summary>The message fields after the subtype:
            /// [int viewId, string key, int startTs, int durationMs, float magnitude,
            /// int attackerView, int applications].</summary>
            internal object[] Args()
            {
                return new object[] { ViewId, Key, FirstStartTs, DurationMs, Magnitude,
                                      AttackerView, Applications };
            }
        }

        private readonly Dictionary<string, Batch> _open = new Dictionary<string, Batch>();
        private readonly List<string> _due = new List<string>();
        private int _overflows;

        internal int Count { get { return _open.Count; } }

        /// <summary>Records one local application. <paramref name="now"/> and the window are in
        /// seconds of one monotonic clock.</summary>
        internal void Add(int viewId, string key, string targetName, int startTs, int durationMs,
                          float magnitude, int attackerView, bool refresh, int windowMs, float now)
        {
            string id = viewId + "/" + key;
            Batch b;
            if (!_open.TryGetValue(id, out b) || b.Applications >= MaxApplications)
            {
                // A full batch is left to its own flush; the overflow opens a fresh one under
                // a distinct id so neither loses applications.
                if (b != null) _open[id + "#" + (++_overflows)] = b;
                b = new Batch
                {
                    ViewId = viewId,
                    Key = key,
                    TargetName = targetName,
                    FirstStartTs = startTs,
                    FirstDurationMs = durationMs,
                    Refresh = refresh,
                    Magnitude = magnitude,
                    AttackerView = attackerView,
                    WindowMs = windowMs,
                    DueAt = now + windowMs / 1000f,
                };
                _open[id] = b;
            }
            b.Applications++;
            b.LastEndTs = unchecked(startTs + durationMs);
        }

        /// <summary>Forgets what is queued for one target and key (the status was removed, so
        /// the peers must not re-add it afterwards).</summary>
        internal void Drop(int viewId, string key)
        {
            string id = viewId + "/" + key;
            _due.Clear();
            foreach (KeyValuePair<string, Batch> kv in _open)
                if (kv.Key == id || kv.Key.StartsWith(id + "#", System.StringComparison.Ordinal))
                    _due.Add(kv.Key);
            for (int i = 0; i < _due.Count; i++) _open.Remove(_due[i]);
        }

        /// <summary>Moves every batch due at <paramref name="now"/> (every batch when
        /// <paramref name="all"/>) into <paramref name="into"/>.</summary>
        internal void TakeDue(float now, bool all, List<Batch> into)
        {
            if (_open.Count == 0) return;
            _due.Clear();
            foreach (KeyValuePair<string, Batch> kv in _open)
                if (all || now >= kv.Value.DueAt) _due.Add(kv.Key);
            // Oldest first, so a receiver's timer ends where the latest batch leaves it.
            _due.Sort((a, b) => _open[a].DueAt.CompareTo(_open[b].DueAt));
            for (int i = 0; i < _due.Count; i++)
            {
                into.Add(_open[_due[i]]);
                _open.Remove(_due[i]);
            }
        }
    }
}
