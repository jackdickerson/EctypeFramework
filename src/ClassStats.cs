using System;

using Gameplay.Utilities;

namespace Ectype
{
    /// <summary>
    /// Class code's access to the custom stats its own manifest declares, and the one use
    /// every class ability has for them: a cooldown or duration that only that ability reads.
    ///
    /// A class ability registers its CooldownSeconds as ClassAbilityCooldown and its
    /// FiniteTimeDuration as ClassAbilityDuration into its own StatTagCollection, a child of
    /// the player's. A character-target perk mod against either vanilla id recurses into
    /// every class ability on the character - the unselected vanilla four, the grapple
    /// another mod installs, every other framework class's ability. Registering the same
    /// ModifiableFloat a second time under a class-declared id scopes a modifier to this
    /// one collection: StatTagCollection.ApplyModifier matches a StatMod's Type against
    /// every key of the collection, and the one float under both keys is what the game's
    /// own AbilityStarted (duration) and GoOnCooldown (cooldown) read. No read-path code.
    ///
    /// A source names one of the two ids, never both, or the two modifiers compound.
    /// </summary>
    public static class ClassStats
    {
        /// <summary>
        /// The stat id a custom stat in <paramref name="classId"/>'s manifest registered
        /// under, or 0 when the class is not installed, has not registered yet, or declares
        /// no such stat. 0 is never a usable stat id, so a caller can skip on it.
        /// </summary>
        public static int Id(string classId, string statName)
        {
            if (string.IsNullOrEmpty(classId) || string.IsNullOrEmpty(statName)) return 0;
            ClassRecord rec = ClassRegistry.Find(classId);
            if (rec == null) return 0;
            int id;
            return rec.CustomStats.TryGetValue(statName, out id) ? id : 0;
        }

        /// <summary>
        /// Register <paramref name="ability"/>'s own FiniteTimeDuration and CooldownSeconds
        /// into its own collection under the class-declared stats named here. Either name may
        /// be null to skip that half. Idempotent per id, so it can run from Awake (which the
        /// ability array setter can re-run) and again on first use. Returns false while any
        /// named stat is unresolved, which is the caller's cue to retry later.
        /// </summary>
        public static bool RegisterAbilityTiming(FiniteDurationClassAbility ability,
                                                 string classId, string durationStat,
                                                 string cooldownStat)
        {
            if (ability == null) throw new ArgumentNullException("ability");
            StatTagCollection stats = ability.Stats;
            if (stats == null) return false;

            bool ok = true;
            if (!string.IsNullOrEmpty(durationStat))
            {
                int id = Id(classId, durationStat);
                if (id == 0) ok = false;
                else if (!stats.Stats.ContainsKey(id))
                    stats.RegisterStat(id, ref ability.FiniteTimeDuration);
            }
            if (!string.IsNullOrEmpty(cooldownStat))
            {
                int id = Id(classId, cooldownStat);
                if (id == 0) ok = false;
                else if (!stats.Stats.ContainsKey(id))
                    stats.RegisterStat(id, ref ability.CooldownSeconds);
            }
            return ok;
        }
    }
}
