using System;
using System.Collections.Generic;

using Gameplay.Perks;
using Gameplay.Utilities;
using ResourceAssets;

namespace Ectype
{
    /// <summary>Which collections a framework-routed perk's effects land in, per holder.</summary>
    internal enum ShipSet : byte
    {
        /// <summary>The holder's own character.</summary>
        Character,
        /// <summary>The holder's crew ship.</summary>
        Crew,
        /// <summary>The ship the holder's own client says they are aboard, when it is living and
        /// on the holder's side.</summary>
        Aboard,
        /// <summary>Every living ship on the holder's side.</summary>
        Force,
        /// <summary>While seated, every living ship on the seat's ship's side.</summary>
        SeatForce,
    }

    /// <summary>How an authored amount is shared among the ships a perk lands on.</summary>
    internal enum Share : byte
    {
        /// <summary>Every ship gets the authored amount.</summary>
        Each,
        /// <summary>The authored amount is divided evenly among the ships.</summary>
        Split,
    }

    /// <summary>
    /// One perk's routing, parsed from its manifest entry. <see cref="GameTarget"/> is the value
    /// written to the perk asset, the only part the game sees: one of the stock targets the game's
    /// perk loader applies itself, or <see cref="CharacterShipRoute.CharacterShip"/>, which the
    /// loader skips and the framework routes from this record.
    /// </summary>
    internal sealed class PerkScope
    {
        /// <summary>The authored target name, canonical spelling.</summary>
        internal readonly string Target;
        internal readonly PerkBuffTarget GameTarget;
        internal readonly ShipSet Ships;
        internal readonly Share Share;
        /// <summary>Owner-evaluated conditions the holder must meet for the effects to apply.</summary>
        internal readonly ConditionSet When;

        internal PerkScope(string target, PerkBuffTarget gameTarget, ShipSet ships, Share share,
                           ConditionSet when = default(ConditionSet))
        {
            Target = target;
            GameTarget = gameTarget;
            Ships = ships;
            Share = share;
            When = when;
        }

        /// <summary>The framework applies this perk, not the game's perk loader.</summary>
        internal bool Routed { get { return GameTarget == CharacterShipRoute.CharacterShip; } }
    }

    /// <summary>
    /// The scope table: framework-routed perks by asset GUID. The gene-tree tooltip renders a
    /// perk's description and its levels' modifiers and never reads the target, and the game's
    /// perk loader applies targets 1 to 3 and skips any other value. So every framework-routed
    /// target is written to the asset as the one value the loader skips, 4, and what it means
    /// lives here. A perk with no entry and value 4 is characterShip.
    /// </summary>
    internal static class PerkScopes
    {
        internal const string KnownTargets =
            "character, seat, seatShip, characterShip, characterAboard, characterFleet, seatFleet";
        internal const string KnownDistributions = "each, split";

        internal static readonly PerkScope CharacterScope =
            new PerkScope("character", PerkBuffTarget.Character, ShipSet.Character, Share.Each);

        internal static readonly PerkScope CharacterShipScope =
            new PerkScope(CharacterShipRoute.CharacterShipName, CharacterShipRoute.CharacterShip, ShipSet.Crew, Share.Each);

        private static readonly Dictionary<GUIDUnion, PerkScope> ByGuid = new Dictionary<GUIDUnion, PerkScope>();

        /// <summary>Records a routed perk's scope; a perk the game's loader applies has none.</summary>
        internal static void Set(GUIDUnion guid, PerkScope scope)
        {
            if (scope != null && scope.Routed) ByGuid[guid] = scope;
            else ByGuid.Remove(guid);
        }

        internal static void Remove(GUIDUnion guid) { ByGuid.Remove(guid); }

        /// <summary>The scope of a perk whose asset carries value 4: its entry, else characterShip.</summary>
        internal static PerkScope Of(GUIDUnion guid)
        {
            PerkScope scope;
            return ByGuid.TryGetValue(guid, out scope) ? scope : CharacterShipScope;
        }

        /// <summary>The authored target name of a perk asset.</summary>
        internal static string NameOf(GUIDUnion guid, PerkBuffTarget target)
        {
            return target == CharacterShipRoute.CharacterShip ? Of(guid).Target : EctypeAssets.TargetName(target);
        }

        /// <summary>The target name a ship set is authored as.</summary>
        internal static string TargetOf(ShipSet set)
        {
            switch (set)
            {
                case ShipSet.Crew: return CharacterShipRoute.CharacterShipName;
                case ShipSet.Aboard: return "characterAboard";
                case ShipSet.Force: return "characterFleet";
                case ShipSet.SeatForce: return "seatFleet";
                default: return "character";
            }
        }

        /// <summary>The set can hold more than one ship.</summary>
        internal static bool Multiple(ShipSet set)
        {
            return set == ShipSet.Force || set == ShipSet.SeatForce;
        }

        // ------------------------------------------------------------------ parsing

        /// <summary>A manifest target name: the game value written to the asset, the ship set
        /// the framework routes it to, and the canonical name. False for an unknown name.</summary>
        internal static bool TryParseTarget(string s, out PerkBuffTarget target, out ShipSet ships, out string name)
        {
            ships = ShipSet.Character;
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "": case "character": target = PerkBuffTarget.Character; name = "character"; return true;
                case "seat": target = PerkBuffTarget.Seat; name = "seat"; return true;
                case "seatship": target = PerkBuffTarget.SeatShip; name = "seatShip"; return true;
                case "charactership":
                    target = CharacterShipRoute.CharacterShip; ships = ShipSet.Crew; name = "characterShip"; return true;
                case "characteraboard":
                    target = CharacterShipRoute.CharacterShip; ships = ShipSet.Aboard; name = "characterAboard"; return true;
                case "characterfleet":
                    target = CharacterShipRoute.CharacterShip; ships = ShipSet.Force; name = "characterFleet"; return true;
                case "seatfleet":
                    target = CharacterShipRoute.CharacterShip; ships = ShipSet.SeatForce; name = "seatFleet"; return true;
                default: target = PerkBuffTarget.Character; name = null; return false;
            }
        }

        internal static bool TryParseDistribution(string s, out Share share)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "": case "each": share = Share.Each; return true;
                case "split": share = Share.Split; return true;
                default: share = Share.Each; return false;
            }
        }

        /// <summary>
        /// A perk entry's scope, or the problem that refuses it, phrased to follow "has" (the
        /// hero's rejection) or "skipped:" (any other perk). Checks the target, the distribution,
        /// that a split lands on a set of several ships and divides only flat and percent float
        /// effects, and the conditions. A character perk with conditions becomes routed: the
        /// framework applies it to the character while they hold.
        /// </summary>
        internal static string Parse(PerkSpec p, out PerkScope scope)
        {
            scope = null;
            PerkBuffTarget target;
            ShipSet ships;
            string name;
            if (!TryParseTarget(p.target, out target, out ships, out name))
                return "unrecognised target '" + p.target + "' (this framework knows " + KnownTargets + ")";
            Share share;
            if (!TryParseDistribution(p.distribution, out share))
                return "unrecognised distribution '" + p.distribution + "' (this framework knows " +
                       KnownDistributions + ")";
            if (share == Share.Split)
            {
                if (!Multiple(ships))
                    return "distribution split on target '" + name + "', which lands on one collection " +
                           "(split needs characterFleet or seatFleet)";
                string effect = UnsplittableEffect(p);
                if (effect != null) return "distribution split with " + effect;
            }
            ConditionSet when;
            string conditionProblem = HolderConditions.Parse(p.when, out when);
            if (conditionProblem != null) return conditionProblem;
            if (!when.Empty)
            {
                // The game's loader applies seat and seatShip itself, sourced from the player, on
                // sit and stand; only a perk this route applies can be switched by a condition.
                if (target == PerkBuffTarget.Seat || target == PerkBuffTarget.SeatShip)
                    return "conditions on target '" + name + "' (conditions need a target the framework " +
                           "applies: character, characterShip, characterAboard, characterFleet, seatFleet)";
                if (target == PerkBuffTarget.Character) target = CharacterShipRoute.CharacterShip;
            }
            scope = new PerkScope(name, target, ships, share, when);
            return null;
        }

        /// <summary>The first effect a split cannot divide, described, or null: a scalar
        /// (a product over ships is not a sum), an int (an integer amount does not divide), or a
        /// type that names no modifier.</summary>
        private static string UnsplittableEffect(PerkSpec p)
        {
            if (p.mods == null) return null;
            for (int level = 0; level < p.mods.Count; level++)
            {
                if (p.mods[level] == null) continue;
                foreach (EffectSpec fx in p.mods[level])
                {
                    if (fx == null) continue;
                    string where = " on '" + fx.stat + "' at level " + (level + 1);
                    ModifierType type;
                    try { type = StatRegistry.ResolveModifierType(fx.type); }
                    catch (Exception) { return "an effect of unknown type '" + fx.type + "'" + where; }
                    if (type == ModifierType.ExponentialMultiplier)
                        return "a scalar effect" + where + " (split divides flat and percent effects only)";
                    if (string.Equals((fx.kind ?? "").Trim(), "int", StringComparison.OrdinalIgnoreCase))
                        return "an int effect" + where + " (an integer amount cannot be divided)";
                }
            }
            return null;
        }

        // ------------------------------------------------------------------ resolution

        /// <summary>
        /// The ships a ship set names for one holder, in ship-number order, into
        /// <paramref name="into"/>. Pure: everything comes from <paramref name="view"/> and the
        /// holder's own answer, <paramref name="mask"/> (-1 before it arrives) and
        /// <paramref name="aboardShip"/> (a ship number, 0 for none).
        /// Aboard is read from that answer, never from this client's copy of the character,
        /// whose inertia-bubble state each client samples at its own moment: the ship the
        /// holder's client names, when it is living and on the holder's side here. With one ship
        /// object in the room every set collapses to the stock targets: Crew and Force name the
        /// room's ship when it is on the holder's side, Aboard names it too while the answer says
        /// onOwnShip, SeatForce names it while the holder is seated in it. Not used for
        /// <see cref="ShipSet.Character"/>.
        /// </summary>
        internal static void Resolve(TopologyView view, ShipSet set, int actor, int mask, int aboardShip,
                                     List<object> into)
        {
            into.Clear();
            switch (set)
            {
                case ShipSet.Crew:
                {
                    object crew = view.CrewShip(actor);
                    if (crew != null) into.Add(crew);
                    return;
                }
                case ShipSet.Aboard:
                {
                    if (mask < 0) return;
                    if (!view.Multi)
                    {
                        if ((mask & (int)HolderCondition.OnOwnShip) == 0) return;
                        object crew = view.CrewShip(actor);
                        if (crew != null) into.Add(crew);
                        return;
                    }
                    object aboard = view.ShipByNumber(aboardShip);
                    if (aboard != null && view.Living(aboard) && view.OnSide(aboard, actor)) into.Add(aboard);
                    return;
                }
                case ShipSet.Force:
                {
                    if (!view.Multi)
                    {
                        object crew = view.CrewShip(actor);
                        if (crew != null) into.Add(crew);
                        return;
                    }
                    foreach (object ship in view.LivingShips())
                        if (view.OnSide(ship, actor)) into.Add(ship);
                    return;
                }
                case ShipSet.SeatForce:
                {
                    object seat = view.SeatShip(actor);
                    if (seat == null) return;
                    if (!view.Multi) { into.Add(seat); return; }
                    foreach (object ship in view.LivingShips())
                        if (view.SameSide(ship, seat)) into.Add(ship);
                    return;
                }
            }
        }

        /// <summary>The factor each ship's copy of an amount is scaled by: 1 for each, 1/n for
        /// split over n ships.</summary>
        internal static float SplitFactor(Share share, int ships)
        {
            return share == Share.Split && ships > 1 ? 1f / ships : 1f;
        }

        /// <summary>Scales one copied modifier's amount. Validation lets only float flat and
        /// percent effects split, so any other modifier is left alone.</summary>
        internal static void Scale(StatMod mod, float factor)
        {
            if (factor == 1f || mod == null) return;
            FloatModifier f = mod.Mod as FloatModifier;
            if (f != null && f.Type != ModifierType.ExponentialMultiplier) f.Amount *= factor;
        }
    }
}
