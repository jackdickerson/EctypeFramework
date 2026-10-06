using System;
using System.Collections.Generic;

using CG.Game.Player;
using HarmonyLib;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// The character's own raw movement fields, given a stat surface on the player's
    /// top-level collection.
    ///
    /// The locomotion carries no StatTagCollection of its own, so each field here becomes a
    /// FieldStat on the player's Stats instead. docs/framework-stats.json carries what each
    /// one does and its authored value.
    ///
    /// Seeding once at spawn is sound because the game never writes these fields after Awake
    /// (verified by decompile), so the stat's write-back is the field's only writer.
    ///
    /// MotorAcceleration is a Vector3 (per-axis force, y unused); its stat is the authored
    /// horizontal magnitude, and the write-back scales the authored vector by the ratio
    /// so per-axis proportions are preserved.
    /// </summary>
    internal static class CharacterStats
    {
        private static bool _loggedWiring;

        public static void Wire(Player player)
        {
            try
            {
                if (player == null || player.Stats == null) return;
                CustomCharacterLocomotion loco = player.Locomotion;
                if (loco == null) return;

                List<string> summary = new List<string>();
                int wired = 0;

                Vector3 authoredAccel = loco.MotorAcceleration;
                float authoredAccelMag = new Vector3(authoredAccel.x, 0f, authoredAccel.z).magnitude;

                Knob[] knobs =
                {
                    new Knob(FrameworkStats.ExternalForceDamping,
                             () => loco.ExternalForceDamping, v => loco.ExternalForceDamping = v),
                    new Knob(FrameworkStats.ExternalForceDampingTime,
                             () => loco.ExternalForceDampingTime, v => loco.ExternalForceDampingTime = v),
                    new Knob(FrameworkStats.MotorDamping,
                             () => loco.MotorDamping, v => loco.MotorDamping = v),
                    new Knob(FrameworkStats.MotorAcceleration,
                             () => authoredAccelMag,
                             v => loco.MotorAcceleration = authoredAccelMag > 0f
                                 ? authoredAccel * (v / authoredAccelMag)
                                 : new Vector3(v, 0f, v)),
                    new Knob(FrameworkStats.GravityAmount,
                             () => loco.GravityAmount, v => loco.GravityAmount = v),
                };

                foreach (Knob k in knobs)
                {
                    FieldStat fs = FieldStat.Wire(player.Stats, k.Name, "character " + k.Name,
                                                  k.Get, k.Set);
                    if (fs == null) continue;   // unregistered, or this player already served
                    wired++;
                    summary.Add(k.Name + "=" + fs.Authored.ToString("0.###"));
                }

                if (wired > 0 && !_loggedWiring)
                {
                    _loggedWiring = true;
                    Plugin.Log.LogInfo("Ectype: character stat surface installed on " +
                                       player.GetType().Name + " (" +
                                       string.Join(", ", summary.ToArray()) + ").");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: wiring character stats onto " +
                                    (player == null ? "(null)" : player.GetType().Name) +
                                    " failed - the character keeps its authored numbers: " + e);
            }
        }

        /// <summary>One locomotion field: the framework stat it answers to, and how to
        /// read and write it.</summary>
        private struct Knob
        {
            public string Name;
            public Func<float> Get;
            public Action<float> Set;
            public Knob(string name, Func<float> get, Action<float> set)
            {
                Name = name; Get = get; Set = set;
            }
        }
    }

    /// <summary>
    /// Postfix, not prefix: Player.Awake is what resolves Locomotion and fills the
    /// collection this registers into. It also covers every replica, since LocalPlayer's
    /// override calls into it.
    /// </summary>
    [HarmonyPatch(typeof(Player), "Awake")]
    internal static class Player_Awake_Patch
    {
        private static void Postfix(Player __instance)
        {
            if (!Plugin.PatchesApplied || !EctypeConfig.Enabled.Value) return;
            CharacterStats.Wire(__instance);
            BuildStats.Wire(__instance);
        }
    }
}
