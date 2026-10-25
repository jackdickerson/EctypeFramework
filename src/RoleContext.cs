using CG.Space;

namespace Ectype
{
    /// <summary>
    /// Where a role ability's owner is and which ships are theirs, for role code. Every answer
    /// is resolved from the owner's actor number through <see cref="Topology"/>, never from the
    /// game's player-ship getter, so a replica of the ability on a crewmate's client names the
    /// same ship as the owner's client does. With several ships that getter returns the ship the
    /// viewing client looks at, and an effect placed through it lands on a different ship on
    /// each client.
    ///
    /// The predicates are the owner-evaluated conditions a manifest's "when" and "abilityWhen"
    /// name, computed from this client's copy of the owner's character. The character's platform
    /// replicates with a few frames' delay, so a replica can disagree with the owner for that
    /// long; decide on the owner (in OwnerCanUse or OnApplyEffect on the owning client) and
    /// replicate the decision when every client must agree.
    ///
    /// In a room with one ship object, ShipOf names that ship, ForceOf the owner's faction,
    /// IsOnAllyShip, IsOnEnemyShip and IsTeamMatch are false. A null ability, or one not yet
    /// installed on a character, answers null, -1 and false.
    /// </summary>
    public static class RoleContext
    {
        /// <summary>
        /// The owner's crew ship (<see cref="Topology.CrewShipOf(CG.Game.Player.Player)"/>), the
        /// same on every client; null while none can be named. With <paramref name="aboard"/>,
        /// the ship whose rooms or inertia bubble hold the owner's character, of any side; null in
        /// open space.
        /// </summary>
        public static PlayerControlledShip ShipOf(EctypeRoleAbility ability, bool aboard = false)
        {
            return ShipHandleOf(OwnerActor(ability), aboard) as PlayerControlledShip;
        }

        /// <summary>The owner's side (<see cref="Topology.ForceOf(CG.Game.Player.Player)"/>):
        /// their character's faction; -1 when unknown.</summary>
        public static int ForceOf(EctypeRoleAbility ability)
        {
            int actor = OwnerActor(ability);
            return actor == 0 ? -1 : Topology.View.ForceOf(actor);
        }

        /// <summary>The owner's character is aboard their own crew ship.</summary>
        public static bool IsOnOwnShip(EctypeRoleAbility ability)
        {
            return Has(ability, HolderCondition.OnOwnShip);
        }

        /// <summary>The owner's character is aboard a ship of their side that is not their crew
        /// ship.</summary>
        public static bool IsOnAllyShip(EctypeRoleAbility ability)
        {
            return Has(ability, HolderCondition.OnAllyShip);
        }

        /// <summary>The owner's character is aboard a ship of another side.</summary>
        public static bool IsOnEnemyShip(EctypeRoleAbility ability)
        {
            return Has(ability, HolderCondition.OnEnemyShip);
        }

        /// <summary>The owner's character is aboard no ship: outside every ship's rooms and
        /// inertia bubble.</summary>
        public static bool IsInOpenSpace(EctypeRoleAbility ability)
        {
            return Has(ability, HolderCondition.InOpenSpace);
        }

        /// <summary>The owner's crew ship exists and has not started dying.</summary>
        public static bool OwnShipAlive(EctypeRoleAbility ability)
        {
            return Has(ability, HolderCondition.OwnShipAlive);
        }

        /// <summary>The owner has no character, or a multi-ship mod places them on no ship
        /// (Void Crew Fleet: spectating, waiting to be placed, or crew of a lost ship).</summary>
        public static bool IsSpectating(EctypeRoleAbility ability)
        {
            return Has(ability, HolderCondition.Spectating);
        }

        /// <summary>A team match is running (<see cref="Topology.TeamMatch"/>).</summary>
        public static bool IsTeamMatch
        {
            get { return Topology.View.TeamMatch; }
        }

        // ---- by actor, for the public members and the dev build's self-tests --------------

        /// <summary>The ability's owner: the actor number of the character it is installed on;
        /// 0 when it has none.</summary>
        internal static int OwnerActor(EctypeRoleAbility ability)
        {
            return ability != null ? Topology.ActorOf(ability.LocalPlayer) : 0;
        }

        internal static object ShipHandleOf(int actor, bool aboard)
        {
            if (actor == 0) return null;
            TopologyView view = Topology.View;
            return aboard ? view.AboardShip(actor) : view.CrewShip(actor);
        }

        internal static int ConditionsOf(int actor)
        {
            return actor == 0 ? -1 : HolderConditions.Evaluate(Topology.View, actor);
        }

        private static bool Has(EctypeRoleAbility ability, HolderCondition condition)
        {
            int mask = ConditionsOf(OwnerActor(ability));
            return mask >= 0 && (mask & (int)condition) != 0;
        }
    }
}
