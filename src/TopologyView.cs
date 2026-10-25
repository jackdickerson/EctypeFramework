using System;
using System.Collections.Generic;

using CG.Space;

namespace Ectype
{
    /// <summary>
    /// The per-player and per-ship facts that perk scopes, holder conditions and RoleContext
    /// are computed from, keyed by actor number. A ship is an opaque handle: the live view hands
    /// out PlayerControlledShip objects, compared by reference; a dev build's self-tests hand out
    /// synthetic ones, so every rule built on this class can be exercised for a room with several
    /// ships and sides without one.
    /// </summary>
    internal abstract class TopologyView
    {
        /// <summary>More than one player ship object exists, dying ones included.</summary>
        internal abstract bool Multi { get; }

        /// <summary>Every living player ship, by ship number.</summary>
        internal abstract IList<object> LivingShips();

        /// <summary>Exists and has not started dying.</summary>
        internal abstract bool Living(object ship);

        /// <summary>The player's crew ship, or null while pending; with one ship object, the
        /// room's ship when it is on the player's side.</summary>
        internal abstract object CrewShip(int actor);

        /// <summary>The ship whose rooms or inertia bubble hold the player's character, of any
        /// side; null in open space or without a character.</summary>
        internal abstract object AboardShip(int actor);

        /// <summary>While the player's character is seated, the ship the seat belongs to; else
        /// null.</summary>
        internal abstract object SeatShip(int actor);

        /// <summary>The ship is on the player's side.</summary>
        internal abstract bool OnSide(object ship, int actor);

        /// <summary>Two ships are on one side.</summary>
        internal abstract bool SameSide(object a, object b);

        /// <summary>The player's side, -1 when unknown.</summary>
        internal abstract int ForceOf(int actor);

        /// <summary>The player has a live character.</summary>
        internal abstract bool HasCharacter(int actor);

        /// <summary>A multi-ship provider is active, ships exist, and it places the player on
        /// none of them (spectating, waiting, or crew of a lost ship).</summary>
        internal abstract bool PlacedOnNoShip(int actor);

        /// <summary>A team match is running.</summary>
        internal abstract bool TeamMatch { get; }
    }

    /// <summary>The view every shipped code path reads: Topology's own answers.</summary>
    internal sealed class LiveTopologyView : TopologyView
    {
        internal static readonly LiveTopologyView Instance = new LiveTopologyView();

        private LiveTopologyView() { }

        internal override bool Multi { get { return Topology.Multi; } }

        internal override IList<object> LivingShips()
        {
            IReadOnlyList<PlayerControlledShip> ships = Topology.Ships;
            List<object> list = new List<object>(ships.Count);
            for (int i = 0; i < ships.Count; i++) list.Add(ships[i]);
            return list;
        }

        internal override bool Living(object ship)
        {
            PlayerControlledShip s = ship as PlayerControlledShip;
            return s != null && !s.StartedDeath;
        }

        /// <summary>With one ship object the player-ship getter is the same on every client, and
        /// it is read as the game reads it; otherwise the crew-ship rules.</summary>
        internal override object CrewShip(int actor)
        {
            string source;
            return CrewShip(actor, out source);
        }

        internal PlayerControlledShip CrewShip(int actor, out string source)
        {
            CG.Game.Player.Player character = Topology.CharacterOf(actor);
            if (!Topology.Multi)
            {
                CG.Game.ClientGame game = CG.Game.ClientGame.Current;
                PlayerControlledShip only = game != null ? game.PlayerShip : null;
                // The last ship of a team match is not the other team's ship.
                if (only != null && !Topology.OnSide(only, actor, character))
                {
                    source = "single, other side's ship";
                    return null;
                }
                source = "single";
                return only != null ? only : null;
            }
            return Topology.ResolveCrewShip(actor, character, out source);
        }

        internal override object AboardShip(int actor)
        {
            return Topology.AboardShipOf(Topology.CharacterOf(actor));
        }

        internal override object SeatShip(int actor)
        {
            return Topology.SeatShipOf(Topology.CharacterOf(actor));
        }

        internal override bool OnSide(object ship, int actor)
        {
            return Topology.OnSide(ship as PlayerControlledShip, actor, Topology.CharacterOf(actor));
        }

        internal override bool SameSide(object a, object b)
        {
            return Topology.SameSide(a as PlayerControlledShip, b as PlayerControlledShip);
        }

        internal override int ForceOf(int actor)
        {
            CG.Game.Player.Player character = Topology.CharacterOf(actor);
            if (character != null) return character.Faction;
            Photon.Realtime.Room room = Photon.Pun.PhotonNetwork.CurrentRoom;
            Photon.Realtime.Player p = room != null ? room.GetPlayer(actor) : null;
            return p != null ? Topology.ForceOf(p) : -1;
        }

        internal override bool HasCharacter(int actor)
        {
            return Topology.CharacterOf(actor) != null;
        }

        internal override bool PlacedOnNoShip(int actor)
        {
            return Topology.PlacedOnNoShip(actor);
        }

        internal override bool TeamMatch { get { return Topology.TeamMatch; } }
    }
}
