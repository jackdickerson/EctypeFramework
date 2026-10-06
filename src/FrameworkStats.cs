using System;
using System.Collections.Generic;
using UnityEngine;

using Client.Player.Interactions;

namespace Ectype
{
    /// <summary>
    /// Stats the FRAMEWORK owns, as opposed to the ones a role declares in its own manifest.
    ///
    /// A number belonging to something the game gives every player is declared here rather
    /// than in one role's manifest: registered once per session whether or not any role is
    /// installed, nameable from every manifest, and read by a framework consumer.
    ///
    /// Ids derive from the reserved owner string below in place of a roleId. The names and
    /// that string are part of the shipped contract - renaming one derives a different id,
    /// which is a silent no-op on one side of a mixed room.
    ///
    /// Adding one is four edits: a name constant and a spec here, a consumer that reads it,
    /// a row in docs/stat-names.csv (with the id printed at registration) so tooling that
    /// reads the alias list can offer it, and an entry in docs/framework-stats.json saying what the
    /// number does and what the game authored. A stat with no consumer is inert, and the
    /// log says so.
    ///
    /// Public, because a role mod's ability code contributes to a framework stat the normal
    /// vanilla way - a StatMod against Id(name) applied to the player's collection on start
    /// and removed by source on stop - and needs the id to do it.
    /// </summary>
    public static class FrameworkStats
    {
        /// <summary>
        /// Stands in for a roleId when deriving framework-owned stat ids. Never change it:
        /// every id below is a hash of it.
        /// </summary>
        public const string Owner = "ectype";

        // The grappling hook's numbers. Range and cooldown are absent on purpose - the game
        // already registers `range` as StatType.GrapplingHookRange and CooldownSeconds as
        // StatType.ClassAbilityCooldown, so those two have a working stat surface already and
        // a manifest names the vanilla alias. GrapplingHookCooldown exists only to SCOPE the
        // vanilla one: see GrappleStats.
        /// <summary>How fast the grappling hook head travels to its target.</summary>
        public const string GrappleFlightSpeed = "GrapplingHookFlightSpeed";
        /// <summary>How fast the grappling hook pulls the player along the line.</summary>
        public const string GrapplePullSpeed = "GrapplingHookPullSpeed";
        /// <summary>How hard the grappling hook accelerates the player toward its anchor.</summary>
        public const string GrapplePullAcceleration = "GrapplingHookPullAcceleration";
        /// <summary>The cooldown left behind when the grappling hook attaches to nothing; lower is better.</summary>
        public const string GrappleMissCooldown = "GrapplingHookMissCooldown";
        /// <summary>Scopes the grappling hook's own recharge time apart from other role abilities; lower is better.</summary>
        public const string GrappleCooldown = "GrapplingHookCooldown";

        // The EVA suit's inertial damping: Opsive's CharacterLocomotion.ExternalForceDamping,
        // the per-tick decay on the external-force velocity the jetpack steers with. A plain
        // property over a plain field, wired onto the player's own collection by
        // CharacterStats. Lower means momentum persists longer; "better" depends on the role,
        // so the tooltip does not invert it.
        /// <summary>Per-tick decay of the EVA suit's external-force velocity; lower keeps momentum longer.</summary>
        public const string ExternalForceDamping = "ExternalForceDamping";
        /// <summary>How long after leaving a moving ship the EVA suit keeps its momentum before damping applies in full.</summary>
        public const string ExternalForceDampingTime = "ExternalForceDampingTime";
        /// <summary>How quickly the player stops on foot when input is released; lower slides further.</summary>
        public const string MotorDamping = "MotorDamping";
        /// <summary>How hard the player's legs push when moving on foot; with <see cref="MotorDamping"/> it sets top walking speed.</summary>
        public const string MotorAcceleration = "MotorAcceleration";
        /// <summary>How strongly a gravity field pulls the player down.</summary>
        public const string GravityAmount = "GravityAmount";

        // A factor on hostile NPC projectile speed, on the player ship's own collection;
        // consumed by ShipStats. Meant for seatShip perks.
        /// <summary>Multiplier on hostile NPC projectile speed, on the player ship's collection; lower is better.</summary>
        public const string IncomingProjectileSpeed = "IncomingProjectileSpeed";

        // A hand-held attractor's range and pull, apart from the ship attractor's vanilla
        // stats so a source can name the hand alone. Registered by the owning ability through
        // HandAttractorStats, which also mirrors AttractorMaxRange/AttractorPullVelocity
        // onto them.
        /// <summary>Reach of the hand-held attractor beam.</summary>
        public const string HandAttractorMaxRange = "HandAttractorMaxRange";
        /// <summary>How fast the hand-held attractor beam pulls an item in.</summary>
        public const string HandAttractorPullVelocity = "HandAttractorPullVelocity";

        // The owning ability's channel length and cooldown, as hand-scoped second ids onto the
        // ability's FiniteTimeDuration / CooldownSeconds (the vanilla ClassAbilityDuration /
        // ClassAbilityCooldown aliases reach every role ability on the character), and the
        // number of items one channel holds at once. Registered through HandAttractorStats.
        /// <summary>How long the hand-held attractor channel stays on, in seconds.</summary>
        public const string HandAttractorDuration = "HandAttractorDuration";
        /// <summary>Recharge time of the hand-held attractor, in seconds; lower is better.</summary>
        public const string HandAttractorCooldown = "HandAttractorCooldown";
        /// <summary>How many items one hand-attractor channel holds at once.</summary>
        public const string HandAttractorBeams = "HandAttractorBeams";

        // A factor on how fast a player installs and uninstalls ship modules, on the player's
        // own collection; consumed on the master by BuildStats, keyed off the player who
        // started the install.
        /// <summary>Multiplier on how fast the player installs, uninstalls and upgrades ship modules.</summary>
        public const string SystemModuleConstructionSpeed = "SystemModuleConstructionSpeed";

        // The player ship platform's quadratic drag, MovingSpacePlatform.PhysicalData.Friction, on
        // the ship's own collection; wired by ShipPlatformStats.
        /// <summary>The player ship platform's quadratic drag; lower raises top speed and lets the ship coast further.</summary>
        public const string ShipDrag = "ShipDrag";

        /// <summary>A framework stat and the vanilla stat whose icon it borrows.</summary>
        private struct Spec
        {
            public CustomStatSpec Stat;
            public StatType IconDonor;
        }

        private static readonly Spec[] Specs =
        {
            new Spec { IconDonor = StatType.GrapplingHookRange, Stat = new CustomStatSpec
            {
                name = GrappleFlightSpeed,
                displayName = "Grapple Flight Speed",
                desc = "How fast the grappling hook's head travels to its target.",
                displayType = "value",
            } },
            new Spec { IconDonor = StatType.GrapplingHookRange, Stat = new CustomStatSpec
            {
                name = GrapplePullSpeed,
                displayName = "Grapple Pull Speed",
                desc = "How fast the grappling hook pulls you along the line.",
                displayType = "value",
            } },
            new Spec { IconDonor = StatType.GrapplingHookRange, Stat = new CustomStatSpec
            {
                name = GrapplePullAcceleration,
                displayName = "Grapple Pull Acceleration",
                desc = "How hard the grappling hook accelerates you towards its anchor.",
                displayType = "value",
            } },
            new Spec { IconDonor = StatType.GrapplingHookRange, Stat = new CustomStatSpec
            {
                name = GrappleMissCooldown,
                displayName = "Grapple Miss Cooldown",
                desc = "The cooldown left behind when the grappling hook attaches to nothing.",
                displayType = "value",
                lowerIsBetter = true,
            } },
            new Spec { IconDonor = StatType.GrapplingHookRange, Stat = new CustomStatSpec
            {
                name = GrappleCooldown,
                displayName = "Grapple Cooldown",
                desc = "How long the grappling hook takes to recharge.",
                displayType = "value",
                lowerIsBetter = true,
            } },
            new Spec { IconDonor = StatType.JetpackThrust, Stat = new CustomStatSpec
            {
                name = ExternalForceDamping,
                displayName = "Inertial Damping",
                desc = "How quickly the EVA suit bleeds off momentum. Lower keeps you " +
                       "coasting longer.",
                displayType = "value",
            } },
            new Spec { IconDonor = StatType.JetpackThrust, Stat = new CustomStatSpec
            {
                name = ExternalForceDampingTime,
                displayName = "Launch Momentum Time",
                desc = "How long after leaving a moving ship the EVA suit keeps its " +
                       "momentum before the dampener bites in full.",
                displayType = "value",
            } },
            new Spec { IconDonor = StatType.PushForceCounter, Stat = new CustomStatSpec
            {
                name = MotorDamping,
                displayName = "Footing",
                desc = "How quickly you stop when you let go of the stick on foot. " +
                       "Lower slides further.",
                displayType = "value",
            } },
            new Spec { IconDonor = StatType.PushForceCounter, Stat = new CustomStatSpec
            {
                name = MotorAcceleration,
                displayName = "Sprint",
                desc = "How hard your legs push when moving on foot; with Footing, " +
                       "sets your top walking speed.",
                displayType = "value",
            } },
            new Spec { IconDonor = StatType.PushForceCounter, Stat = new CustomStatSpec
            {
                name = GravityAmount,
                displayName = "Gravity",
                desc = "How strongly the deck pulls you down inside a gravity field.",
                displayType = "value",
            } },
            new Spec { IconDonor = StatType.ProjectileSpeed, Stat = new CustomStatSpec
            {
                name = IncomingProjectileSpeed,
                displayName = "Incoming Projectile Speed",
                desc = "How fast enemy projectiles travel toward the ship.",
                displayType = "percentage",
                lowerIsBetter = true,
            } },
            new Spec { IconDonor = StatType.AttractorMaxRange, Stat = new CustomStatSpec
            {
                name = HandAttractorMaxRange,
                displayName = "Hand Attractor Range",
                desc = "How far your hand-held attractor beam reaches.",
                displayType = "value",
            } },
            new Spec { IconDonor = StatType.AttractorPullVelocity, Stat = new CustomStatSpec
            {
                name = HandAttractorPullVelocity,
                displayName = "Hand Attractor Pull Velocity",
                desc = "How fast your hand-held attractor beam pulls an item in.",
                displayType = "value",
            } },
            new Spec { IconDonor = StatType.AttractorMaxRange, Stat = new CustomStatSpec
            {
                name = HandAttractorDuration,
                displayName = "Hand Attractor Duration",
                desc = "How long your hand-held attractor beam stays on.",
                displayType = "value",
            } },
            new Spec { IconDonor = StatType.AttractorMaxRange, Stat = new CustomStatSpec
            {
                name = HandAttractorCooldown,
                displayName = "Hand Attractor Cooldown",
                desc = "How long your hand-held attractor takes to recharge.",
                displayType = "value",
                lowerIsBetter = true,
            } },
            new Spec { IconDonor = StatType.AttractorPullVelocity, Stat = new CustomStatSpec
            {
                name = HandAttractorBeams,
                displayName = "Hand Attractor Beams",
                desc = "How many items your hand-held attractor pulls at once.",
                displayType = "value",
            } },
            new Spec { IconDonor = StatType.ProcessingSpeed, Stat = new CustomStatSpec
            {
                name = SystemModuleConstructionSpeed,
                displayName = "Installation Speed",
                desc = "How fast you install, uninstall and upgrade ship systems.",
                displayType = "percentage",
            } },
            new Spec { IconDonor = StatType.ForwardPower, Stat = new CustomStatSpec
            {
                name = ShipDrag,
                displayName = "Ship Drag",
                desc = "How hard space slows the ship. Lower raises top speed on every axis and " +
                       "lets the ship coast further, and makes stopping take longer.",
                displayType = "value",
                lowerIsBetter = true,
            } },
        };

        private static readonly Dictionary<string, int> _ids =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private static bool _registered;

        /// <summary>Registration outcome for the F5 status page.</summary>
        public static string Status = "not registered";

        /// <summary>Looks up a framework stat id by name; false when it is not registered.</summary>
        public static bool TryGet(string name, out int id)
        {
            return _ids.TryGetValue(name ?? "", out id);
        }

        /// <summary>
        /// Id of a framework stat, or 0 when registration has not run - a safe sentinel,
        /// because the game's own lowest stat id is 1.
        /// </summary>
        public static int Id(string name)
        {
            int id;
            return _ids.TryGetValue(name ?? "", out id) ? id : 0;
        }

        /// <summary>
        /// Register every framework stat into the live StatTable. Re-runnable and cheap after
        /// the first success; the caller runs it on the same retry cadence as role
        /// registration, because the StatTable is Resources-backed and absent at plugin Awake.
        /// A failure leaves everything that reads these stats inert but never half a set
        /// behind, because RegisterCustomStat is re-entrant per id.
        /// </summary>
        public static void EnsureRegistered()
        {
            if (_registered) return;

            List<string> lines = new List<string>();
            foreach (Spec spec in Specs)
            {
                // Borrowed by reference from a vanilla stat of the same subject, so no PNG
                // ships and the icon tracks the game's own across updates.
                Sprite icon = null;
                try
                {
                    ContextInfo donor =
                        DataTable<StatTable>.Instance.GetContext(spec.IconDonor.Id);
                    if (donor != null) icon = donor.Icon;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning("Ectype: could not borrow the vanilla " +
                                          spec.IconDonor.Name + " icon for " + spec.Stat.name +
                                          ": " + e.Message);
                }

                int id = StatRegistry.RegisterCustomStat(Owner, spec.Stat, icon);
                _ids[spec.Stat.name] = id;
                lines.Add(spec.Stat.name + "=" + id);
            }

            _registered = true;
            Status = _ids.Count + " registered";
            Plugin.Log.LogInfo("Ectype: framework-owned stats registered (" +
                               string.Join(", ", lines.ToArray()) + ").");
        }
    }
}
