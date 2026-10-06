using System;

using Gameplay.Utilities;

namespace Ectype
{
    /// <summary>
    /// A plain float field the game never made modifiable, turned into a registered stat
    /// with the field as its sink: seeded from the value the field holds at wiring time,
    /// registered into a stat collection, and with a change handler that writes the composed
    /// value back into the field the game keeps reading.
    ///
    /// A modifier carrying a ModDynamicValue changes its amount through MarkDirty, which raises no
    /// value change event. The collection's CheckModValueUpdate raises OnModifiersChange for it
    /// instead, so the write-back listens there too. Those writes are not logged: a dynamic value
    /// can move every tick.
    /// </summary>
    internal sealed class FieldStat
    {
        /// <summary>The framework stat name this field answers to.</summary>
        public readonly string Name;
        /// <summary>The registered stat object; the value the field is kept equal to.</summary>
        public readonly ModifiableFloat Stat;
        /// <summary>The value the field held when it was wired.</summary>
        public readonly float Authored;

        private readonly Action<float> _set;
        private readonly string _label;
        private float _lastApplied;

        private FieldStat(string name, string label, float authored, Action<float> set)
        {
            Name = name;
            Authored = authored;
            _lastApplied = authored;
            _set = set;
            _label = label;
            Stat = (ModifiableFloat)authored;
        }

        /// <summary>
        /// Wire one field. Returns null, having changed nothing, when the stat is not
        /// registered (the framework's stat registration has not run, or failed), when the
        /// collection is null, or when the collection already serves that id - which makes
        /// this safe to call on every spawn-time pass. Never throws: callers sit in Awake
        /// postfixes.
        /// </summary>
        /// <param name="stats">The collection the stat registers into.</param>
        /// <param name="name">A FrameworkStats name; its id is what the stat registers under.</param>
        /// <param name="label">What the log calls this field, e.g. "grapple GrapplingHookPullSpeed".</param>
        /// <param name="get">Reads the field; called once, to seed the stat.</param>
        /// <param name="set">Writes the field; called on every composed-value change.</param>
        public static FieldStat Wire(StatTagCollection stats, string name, string label,
                                     Func<float> get, Action<float> set)
        {
            try
            {
                if (stats == null || stats.Stats == null || get == null || set == null)
                    return null;

                // Register on demand: nothing revisits an object once its Awake has passed,
                // so a character spawning before the registration driver has run must not
                // miss out. One dictionary lookup after the first success.
                FrameworkStats.EnsureRegistered();
                int id = FrameworkStats.Id(name);
                if (id == 0) return null;
                if (stats.Stats.ContainsKey(id)) return null;   // already served

                FieldStat f = new FieldStat(name, label, get(), set);
                // Subscribe BEFORE registering: RegisterStat replays modifiers already on
                // this collection, and each replay raises the event.
                f.Stat.OnValueChange += f.Apply;
                stats.OnModifiersChange += f.ApplyQuiet;
                ModifiableFloat stat = f.Stat;
                stats.RegisterStat(id, ref stat);
                f.Apply();                      // and once unconditionally, for the no-mod case
                return f;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: wiring " + label + " as a stat failed - the " +
                                    "field keeps its authored number: " + e);
                return null;
            }
        }

        private void Apply() { Write(true); }

        private void ApplyQuiet() { Write(false); }

        private void Write(bool log)
        {
            try
            {
                float v = Stat.Value;
                _set(v);
                if (Math.Abs(v - _lastApplied) <= 0.0001f) return;
                _lastApplied = v;
                // One line per real change, not per event: perk republishes fire the
                // handler with the same total whenever an unrelated perk moves.
                if (log && Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: " + _label + " now " + v.ToString("0.###") +
                                       " (authored " + Authored.ToString("0.###") + ").");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: writing " + _label + " back onto its field " +
                                    "failed: " + e);
            }
        }
    }
}
