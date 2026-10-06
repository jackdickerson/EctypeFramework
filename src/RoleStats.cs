using System;

using Gameplay.Utilities;

namespace Ectype
{
    /// <summary>
    /// Role code's access to the custom stats its own manifest declares, and the one use
    /// every role ability has for them: a cooldown or duration that only that ability reads.
    ///
    /// A role ability registers its CooldownSeconds as ClassAbilityCooldown and its
    /// FiniteTimeDuration as ClassAbilityDuration into its own StatTagCollection, a child of
    /// the player's. A character-target perk mod against either vanilla id recurses into
    /// every role ability on the character - the unselected vanilla four, the grapple
    /// another mod installs, every other framework role's ability. Registering the same
    /// ModifiableFloat a second time under a role-declared id scopes a modifier to this
    /// one collection: StatTagCollection.ApplyModifier matches a StatMod's Type against
    /// every key of the collection, and the one float under both keys is what the game's
    /// own AbilityStarted (duration) and GoOnCooldown (cooldown) read. No read-path code.
    ///
    /// A source names one of the two ids, never both, or the two modifiers compound.
    /// </summary>
    public static class RoleStats
    {
        /// <summary>
        /// The stat id a custom stat in <paramref name="roleId"/>'s manifest registered
        /// under, or 0 when the role is not installed, has not registered yet, or declares
        /// no such stat. 0 is never a usable stat id, so a caller can skip on it.
        /// </summary>
        public static int Id(string roleId, string statName)
        {
            if (string.IsNullOrEmpty(roleId) || string.IsNullOrEmpty(statName)) return 0;
            RoleRecord rec = RoleRegistry.Find(roleId);
            if (rec == null) return 0;
            int id;
            return rec.CustomStats.TryGetValue(statName, out id) ? id : 0;
        }

        /// <summary>
        /// Register <paramref name="ability"/>'s own FiniteTimeDuration and CooldownSeconds
        /// into its own collection under the role-declared stats named here. Either name may
        /// be null to skip that half. Idempotent per id, so it can run from Awake (which the
        /// ability array setter can re-run) and again on first use. Returns false while any
        /// named stat is unresolved, which is the caller's cue to retry later.
        /// </summary>
        public static bool RegisterAbilityTiming(FiniteDurationClassAbility ability,
                                                 string roleId, string durationStat,
                                                 string cooldownStat)
        {
            if (ability == null) throw new ArgumentNullException("ability");
            StatTagCollection stats = ability.Stats;
            if (stats == null) return false;

            bool ok = true;
            if (!string.IsNullOrEmpty(durationStat))
            {
                int id = Id(roleId, durationStat);
                if (id == 0) ok = false;
                else if (!stats.Stats.ContainsKey(id))
                    stats.RegisterStat(id, ref ability.FiniteTimeDuration);
            }
            if (!string.IsNullOrEmpty(cooldownStat))
            {
                int id = Id(roleId, cooldownStat);
                if (id == 0) ok = false;
                else if (!stats.Stats.ContainsKey(id))
                    stats.RegisterStat(id, ref ability.CooldownSeconds);
            }
            return ok;
        }
    }
}
