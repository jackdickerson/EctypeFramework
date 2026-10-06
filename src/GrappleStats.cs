using System;
using System.Collections.Generic;
using System.Reflection;

using Gameplay.Utilities;
using HarmonyLib;

namespace Ectype
{
    /// <summary>
    /// Gives the grappling hook a stat surface, so a perk can move its numbers as data instead
    /// of code keyed to a perk's identity.
    ///
    /// The hook arrives with two of its six numbers already modifiable and four not. Its range
    /// is a ModifiableFloat the ability registers as StatType.GrapplingHookRange, and its
    /// cooldown is the ModifiableFloat every role ability registers as
    /// StatType.ClassAbilityCooldown. Flight speed, pull speed, pull acceleration and the
    /// miss cooldown are plain floats read straight off the fields - two of them from inside
    /// a per-tick method with no seam of its own - so each becomes a FieldStat registered into
    /// the ability's own stat collection, with the field as its sink. See FieldStat for the
    /// mechanism and its one gap.
    ///
    /// **The cooldown: a second id onto the same object.** A modifier against the vanilla
    /// ClassAbilityCooldown alias would reach every ability on the character, so the hook's
    /// CooldownSeconds is registered a second time under a grapple-specific id - a key
    /// nothing else has, needing no tag scoping and no code on the cooldown path.
    ///
    /// The stats go into the ability's own collection, which ClassAbility.Awake creates as a
    /// child of the player's, so they are scoped to the hook while a Character-target perk
    /// still reaches them through the recursion.
    ///
    /// This runs for every ChannelGrapplingHook the client instantiates, remote players' and
    /// other mods' subclasses included; registration is per collection and idempotent.
    /// </summary>
    internal static class GrappleStats
    {
        /// <summary>One plain float field of the hook, and the framework stat that drives it.</summary>
        private struct Knob
        {
            public string Field;
            public string Stat;
            public Knob(string field, string stat) { Field = field; Stat = stat; }
        }

        private static readonly Knob[] Knobs =
        {
            new Knob("projectileSpeed", FrameworkStats.GrappleFlightSpeed),
            new Knob("characterGrappleMaxSpeed", FrameworkStats.GrapplePullSpeed),
            new Knob("characterGrappleAcceleration", FrameworkStats.GrapplePullAcceleration),
            new Knob("cooldownWhenHookMiss", FrameworkStats.GrappleMissCooldown),
        };

        private static readonly Dictionary<string, FieldInfo> _fields =
            new Dictionary<string, FieldInfo>();

        private static bool _loggedWiring;
        private static bool _loggedMissingFields;

        /// <summary>
        /// Register the framework's grapple stats onto one hook. Called from a postfix of the
        /// ability's own Awake, so it must never throw.
        /// </summary>
        public static void Wire(ChannelGrapplingHook hook)
        {
            try
            {
                WireInner(hook);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: wiring grapple stats onto " +
                                    (hook == null ? "(null)" : hook.GetType().Name) +
                                    " failed - the hook keeps its authored numbers: " + e);
            }
        }

        private static void WireInner(ChannelGrapplingHook hook)
        {
            if (hook == null || hook.Stats == null) return;

            FrameworkStats.EnsureRegistered();
            if (FrameworkStats.Id(FrameworkStats.GrapplePullSpeed) == 0) return;

            int wired = 0;
            List<string> summary = new List<string>();

            foreach (Knob knob in Knobs)
            {
                FieldInfo f = Field(knob.Field);
                if (f == null) continue;

                FieldStat fs = FieldStat.Wire(hook.Stats, knob.Stat, "grapple " + knob.Stat,
                                              () => (float)f.GetValue(hook),
                                              v => f.SetValue(hook, v));
                if (fs == null) continue;   // unregistered, or this hook already served
                wired++;
                summary.Add(knob.Stat + "=" + fs.Authored.ToString("0.###"));
            }

            // The cooldown alias. No write-back and no handler: GoOnCooldown reads
            // CooldownSeconds.Value, and this is that same object under a second key.
            int cdId = FrameworkStats.Id(FrameworkStats.GrappleCooldown);
            if (cdId != 0 && hook.CooldownSeconds != null &&
                !hook.Stats.Stats.ContainsKey(cdId))
            {
                ModifiableFloat cd = hook.CooldownSeconds;
                hook.Stats.RegisterStat(cdId, ref cd);
                wired++;
                summary.Add(FrameworkStats.GrappleCooldown + "=" +
                            hook.CooldownSeconds.Value.ToString("0.###"));
            }

            if (wired > 0 && !_loggedWiring)
            {
                _loggedWiring = true;
                Plugin.Log.LogInfo("Ectype: grapple stat surface installed on " +
                                   hook.GetType().Name + " (" +
                                   string.Join(", ", summary.ToArray()) + ").");
            }
        }

        private static FieldInfo Field(string name)
        {
            FieldInfo f;
            if (_fields.TryGetValue(name, out f)) return f;
            f = AccessTools.Field(typeof(ChannelGrapplingHook), name);
            _fields[name] = f;
            if (f == null && !_loggedMissingFields)
            {
                _loggedMissingFields = true;
                Plugin.Log.LogError("Ectype: ChannelGrapplingHook." + name + " not found " +
                                    "(game update?) - that grapple stat is registered nowhere " +
                                    "and perks written against it do nothing.");
            }
            return f;
        }
    }

    /// <summary>
    /// The hook's Awake is where the game registers its own two modifiable numbers, and where
    /// the ability's stat collection first exists. Postfix, not prefix: the collection is
    /// created by base.Awake().
    /// </summary>
    [HarmonyPatch(typeof(ChannelGrapplingHook), "Awake")]
    internal static class ChannelGrapplingHook_Awake_Patch
    {
        private static void Postfix(ChannelGrapplingHook __instance)
        {
            if (!Plugin.PatchesApplied || !EctypeConfig.Enabled.Value) return;
            GrappleStats.Wire(__instance);
        }
    }
}
