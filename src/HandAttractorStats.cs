using System;

using Gameplay.Utilities;

namespace Ectype
{
    /// <summary>
    /// The stat surface of a hand-held attractor, and its mirror of the ship attractor's stat ids.
    ///
    /// A hand attractor's range and pull speed are two ModifiableFloats in the owning
    /// ability's own StatTagCollection (a child of the player's). Each float is registered
    /// under two ids: its framework stat (HandAttractorMaxRange / HandAttractorPullVelocity)
    /// and the vanilla ship-attractor stat it mirrors (AttractorMaxRange /
    /// AttractorPullVelocity).
    ///
    /// The double registration is the mirror. StatTagCollection.ApplyModifier matches an
    /// incoming StatMod by comparing its Type against every key of the collection's Stats
    /// dictionary, and it recurses from the player's collection into every child, so a
    /// character-side modifier on either id lands on the one float: perk levels, relics,
    /// blessings, dynamic conditions and tag filters all go through the game's own
    /// ApplyModifier, and RemoveModifier(source) takes it off again. Nothing is copied, so
    /// there is no second modifier to keep in step or to leak.
    ///
    /// Consequence: a source that names both the vanilla and the hand id adds two modifiers
    /// to the same float and compounds, so a source names one of them. Neither reaches the
    /// ship's attractor from a character-side source: that attractor is a module in the
    /// ship's tree, which the character's collection never feeds.
    ///
    /// Timing and count: HandAttractorDuration and HandAttractorCooldown are second ids onto
    /// the ability's own FiniteTimeDuration and CooldownSeconds, scoped to the hand where the
    /// vanilla ClassAbilityDuration / ClassAbilityCooldown aliases reach every role ability.
    /// HandAttractorBeams is a plain float the ability rounds to a whole number of beams.
    ///
    /// Scope: the mirror covers modifiers applied into the character's collection tree. A
    /// modifier applied to the ship's collection (seatShip and characterShip perks,
    /// ship-installed items) is in a different tree and does not reach a hand attractor.
    /// </summary>
    public static class HandAttractorStats
    {
        /// <summary>
        /// Register a hand attractor's two floats into <paramref name="stats"/> under their
        /// framework ids and the mirrored vanilla ids. Idempotent per id. Returns false when
        /// the framework ids are unavailable (StatTable not loaded); the vanilla mirror is
        /// installed either way.
        /// </summary>
        public static bool Register(StatTagCollection stats, ref ModifiableFloat range,
                                    ref ModifiableFloat pullVelocity)
        {
            if (stats == null) throw new ArgumentNullException("stats");

            Add(stats, StatType.AttractorMaxRange.Id, ref range);
            Add(stats, StatType.AttractorPullVelocity.Id, ref pullVelocity);

            try
            {
                FrameworkStats.EnsureRegistered();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: hand attractor stats not registered yet (" +
                                      e.Message + "); only the vanilla mirror is installed.");
            }
            int handRange = FrameworkStats.Id(FrameworkStats.HandAttractorMaxRange);
            int handPull = FrameworkStats.Id(FrameworkStats.HandAttractorPullVelocity);
            if (handRange == 0 || handPull == 0) return false;

            Add(stats, handRange, ref range);
            Add(stats, handPull, ref pullVelocity);
            return true;
        }

        /// <summary>
        /// Register the owning ability's timing and beam count into its own collection:
        /// <paramref name="ability"/>'s FiniteTimeDuration and CooldownSeconds under
        /// HandAttractorDuration / HandAttractorCooldown, and <paramref name="beams"/> under
        /// HandAttractorBeams. The first two are the same objects the game already registers
        /// as ClassAbilityDuration / ClassAbilityCooldown and reads when the ability starts
        /// (duration) and goes on cooldown (GoOnCooldown), so a modifier on the hand id is
        /// read there with no code on either path. Idempotent per id. Returns false when the
        /// framework ids are unavailable.
        /// </summary>
        public static bool RegisterTiming(FiniteDurationClassAbility ability,
                                          ref ModifiableFloat beams)
        {
            if (ability == null) throw new ArgumentNullException("ability");
            StatTagCollection stats = ability.Stats;
            if (stats == null) return false;
            try
            {
                FrameworkStats.EnsureRegistered();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: hand attractor timing stats not registered yet (" +
                                      e.Message + ").");
            }
            int duration = FrameworkStats.Id(FrameworkStats.HandAttractorDuration);
            int cooldown = FrameworkStats.Id(FrameworkStats.HandAttractorCooldown);
            int count = FrameworkStats.Id(FrameworkStats.HandAttractorBeams);
            if (duration == 0 || cooldown == 0 || count == 0) return false;

            Add(stats, duration, ref ability.FiniteTimeDuration);
            Add(stats, cooldown, ref ability.CooldownSeconds);
            Add(stats, count, ref beams);
            return true;
        }

        private static void Add(StatTagCollection stats, int id, ref ModifiableFloat value)
        {
            if (!stats.Stats.ContainsKey(id)) stats.RegisterStat(id, ref value);
        }
    }
}
