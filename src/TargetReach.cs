using System.Collections.Generic;

using CG.Game;
using CG.Space;
using Gameplay.Perks;
using Gameplay.Utilities;
using ResourceAssets;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// A stat's category never constrains a perk's target, so an effect can name a stat the
    /// target's collection does not hold. It still registers and changes nothing; this
    /// reports it, once per (perk, stat, target), and never skips or reroutes the effect.
    ///
    /// "Matched nothing" is read the way ApplyModifiers matches: a modifier lands on every
    /// stat keyed by its type in the collection and, recursively, in every child collection.
    /// So a miss is no Stats key equal to the type anywhere in that tree. Seat and ship effects
    /// are checked against the holder's own ship's tree, which contains every module a player
    /// can man; a holder with no resolved ship (spectating, not yet placed) is not checked.
    ///
    /// Stats join a tree late - a module installed, an ability awake after the first reapply -
    /// so a miss is only reported when a later reapply, ConfirmSeconds on, still misses.
    /// Framework perks only: vanilla data is not ours to report on.
    /// </summary>
    internal static class TargetReach
    {
        private const float ConfirmSeconds = 10f;

        private static readonly HashSet<string> _settled = new HashSet<string>();
        private static readonly Dictionary<string, float> _firstMiss = new Dictionary<string, float>();

        public static void Check(PlayerPerkLoader loader)
        {
            if (loader == null || loader.PerkBuffLevels == null) return;
            PerkBuffContainer container = PerkBuffContainer.Instance;
            if (container == null) return;
            HashSet<PerkBuff> ours = FrameworkPerks();
            if (ours.Count == 0) return;

            CG.Game.Player.Player player = loader.GetComponent<CG.Game.Player.Player>();
            PlayerControlledShip ship = HolderShip(loader, player);

            foreach (KeyValuePair<GUIDUnion, int> kv in loader.PerkBuffLevels)
            {
                if (kv.Key.IsNull || kv.Value <= 0) continue;
                PerkBuffDef def;
                if (!container.TryGetByGuid(kv.Key, out def) || def == null) continue;
                PerkBuff perk = def.Asset;
                if (perk == null || !ours.Contains(perk) || perk.Levels == null ||
                    kv.Value > perk.Levels.Count || perk.Levels[kv.Value - 1] == null ||
                    perk.Levels[kv.Value - 1].Mods == null) continue;

                bool routed = perk.Target == CharacterShipRoute.CharacterShip;
                bool onCharacter = perk.Target == PerkBuffTarget.Character ||
                                   (routed && PerkScopes.Of(kv.Key).Ships == ShipSet.Character);
                StatCollection root;
                string where;
                if (onCharacter)
                {
                    root = player != null ? player.Stats : null;
                    where = "the player's";
                }
                else
                {
                    root = ship != null ? ship.Stats : null;
                    where = "the player ship's";
                }
                if (root == null) continue;

                foreach (StatMod mod in perk.Levels[kv.Value - 1].Mods)
                {
                    if (mod == null) continue;
                    string key = perk.name + "|" + mod.Type + "|" + (int)perk.Target;
                    if (_settled.Contains(key)) continue;
                    if (StatRegistry.CollectionHasStat(root, mod.Type) ||
                        (routed && !onCharacter && DetachedHasStat(ship, mod.Type)))
                    {
                        _settled.Add(key);
                        _firstMiss.Remove(key);
                        continue;
                    }
                    float now = Time.realtimeSinceStartup;
                    float first;
                    if (!_firstMiss.TryGetValue(key, out first)) { _firstMiss[key] = now; continue; }
                    if (now - first < ConfirmSeconds) continue;

                    _settled.Add(key);
                    _firstMiss.Remove(key);
                    Plugin.Log.LogWarning("Ectype: perk '" + perk.name + "' (target " +
                                          PerkScopes.NameOf(kv.Key, perk.Target) + ") has an " +
                                          "effect on " + StatRegistry.NameOf(mod.Type) +
                                          " that matches no stat in " + where + " collection " +
                                          "tree - it stays registered and changes nothing there.");
                }
            }
        }


        /// <summary>The ship the holder's ship-side effects land on: the characterShip route's
        /// resolution when it holds one, else the holder's crew ship. Never the player-ship
        /// getter, which with several ships names the ship this client views, not the
        /// holder's.</summary>
        private static PlayerControlledShip HolderShip(PlayerPerkLoader loader, CG.Game.Player.Player player)
        {
            PlayerControlledShip ship = CharacterShipRoute.ShipOf(loader);
            if (ship != null) return ship;
            string source;
            return Topology.ResolveCrewShip(Topology.ActorOf(player), player, out source);
        }

        /// <summary>characterShip effects also land on module trees detached from the
        /// ship's; see CharacterShipRoute.</summary>
        private static bool DetachedHasStat(PlayerControlledShip ship, int id)
        {
            foreach (StatTagCollection root in CharacterShipRoute.DetachedRoots(ship))
                if (StatRegistry.CollectionHasStat(root, id)) return true;
            return false;
        }

        private static HashSet<PerkBuff> FrameworkPerks()
        {
            HashSet<PerkBuff> set = new HashSet<PerkBuff>();
            foreach (RoleRecord rec in RoleRegistry.Roles)
                foreach (PerkBuff perk in rec.Perks.Values)
                    if (perk != null) set.Add(perk);
            return set;
        }
    }
}
