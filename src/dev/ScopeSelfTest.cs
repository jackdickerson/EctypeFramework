using System;
using System.Collections.Generic;

using ExitGames.Client.Photon;
using Gameplay.Perks;
using Gameplay.Utilities;
using Newtonsoft.Json.Linq;

namespace Ectype
{
    /// <summary>
    /// Dev build self-test of perk scopes, holder conditions and RoleContext, run once at load.
    /// Offline: every answer comes from a synthetic topology installed through
    /// TopologyView.UseTestView for the duration of the call, and nothing touches Photon's client.
    ///
    /// The synthetic room has two sides. Side 0 owns ships A1 and A2 and a dying A3; side 1 owns
    /// B1. Actors: 1 crews A1, stands on A1 and sits in a seat on A2; 2 crews A2 and has boarded
    /// B1; 3 crews B1 and floats in open space; 4 is on side 0 with no character; 5 crews A1 and
    /// stands on A2; 6 is on side 0 and sits in a seat on B1; 7 crews the dying A3 and stands on
    /// it; 8 crews A1 and stands on the dying A3. Ships are numbered by their order in the room.
    /// As in the live view, the crew ship of a crew whose ship is dying is none. A one-ship room
    /// (ship S, actor 1 aboard and seated, actor 2 in open space) checks the collapse.
    ///
    /// - Scope table: each target resolves to the expected ship list for each actor, in both
    ///   rooms, characterAboard from the answer the holder's own client would send; and from
    ///   no answer, an answer naming another side's ship, and one naming a dying ship.
    /// - Distribution: the split factor, and a scaled flat, percent and scalar copy.
    /// - Validation: targets, distributions and conditions accepted and refused as documented.
    /// - Conditions: each actor's mask; every mask 0-127, with an aboard ship number, survives
    ///   the condition-mask message's pack, Protocol18 round trip, unpack and layout check, is
    ///   recorded as the holder's answer, and gates a condition set as its bits say; the
    ///   debounce adopts on the second read.
    /// - RoleContext: the crew and aboard ship, side and predicates of a synthetic owner.
    /// </summary>
    internal static class ScopeSelfTest
    {
        private sealed class Ship
        {
            internal readonly string Name;
            internal readonly int Side;
            internal readonly bool Living;
            internal Ship(string name, int side, bool living) { Name = name; Side = side; Living = living; }
            public override string ToString() { return Name; }
        }

        private sealed class Actor
        {
            internal int Side;
            internal bool Character = true;
            internal Ship Crew, Aboard, Seat;
        }

        private sealed class View : TopologyView
        {
            internal readonly List<Ship> All = new List<Ship>();
            internal readonly Dictionary<int, Actor> Actors = new Dictionary<int, Actor>();
            internal bool Team;

            internal override bool Multi { get { return All.Count > 1; } }

            internal override IList<object> LivingShips()
            {
                List<object> list = new List<object>();
                foreach (Ship s in All) if (s.Living) list.Add(s);
                return list;
            }

            internal override bool Living(object ship) { Ship s = ship as Ship; return s != null && s.Living; }

            /// <summary>With several ships a dying crew ship is none, as the live crew-ship rules
            /// name only living ships; with one, the room's ship dying or not.</summary>
            internal override object CrewShip(int actor)
            {
                Actor a = Get(actor);
                if (a == null || a.Crew == null) return null;
                return a.Crew.Living || !Multi ? a.Crew : null;
            }

            internal override bool IsCrewShip(object ship, int actor)
            {
                Actor a = Get(actor);
                return ship != null && a != null && ReferenceEquals(a.Crew, ship);
            }

            internal override object ShipByNumber(int number)
            {
                if (number < 1 || number > All.Count) return null;
                return All[number - 1].Living ? All[number - 1] : null;
            }

            internal override int NumberOf(object ship)
            {
                Ship s = ship as Ship;
                return s != null ? All.IndexOf(s) + 1 : 0;
            }

            internal override object AboardShip(int actor)
            {
                Actor a = Get(actor);
                return a != null && a.Character ? a.Aboard : null;
            }

            internal override object SeatShip(int actor)
            {
                Actor a = Get(actor);
                return a != null && a.Character ? a.Seat : null;
            }

            internal override bool OnSide(object ship, int actor)
            {
                Ship s = ship as Ship;
                Actor a = Get(actor);
                return s != null && a != null && s.Side == a.Side;
            }

            internal override bool SameSide(object x, object y)
            {
                Ship a = x as Ship, b = y as Ship;
                return a != null && b != null && a.Side == b.Side;
            }

            internal override int ForceOf(int actor) { Actor a = Get(actor); return a != null ? a.Side : -1; }
            internal override bool HasCharacter(int actor) { Actor a = Get(actor); return a != null && a.Character; }
            internal override bool PlacedOnNoShip(int actor) { return false; }
            internal override bool TeamMatch { get { return Team; } }

            private Actor Get(int actor)
            {
                Actor a;
                return Actors.TryGetValue(actor, out a) ? a : null;
            }
        }

        private const int TestActor = 990001;

        internal static void Run()
        {
            List<string> failures = new List<string>();
            bool inUse = HolderConditions.InUse;
            try
            {
                Step("multi-ship scopes", failures, MultiShip);
                Step("one-ship collapse", failures, OneShip);
                Step("distribution", failures, Distribution);
                Step("validation", failures, Validation);
                Step("conditions", failures, Conditions);
                Step("condition mask round trip", failures, RoundTrip);
                Step("role context", failures, Context);
            }
            finally
            {
                HolderConditions.SetInUse(inUse);
                HolderConditions.Forget(TestActor);
            }

            if (failures.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: perk scopes passed (4 ship sets over 8 actors in a " +
                                   "two-side room and the one-ship collapse, split math, validation, 7 " +
                                   "condition masks, 128 mask round trips, RoleContext).");
            else
                Plugin.Log.LogError("Ectype: dev self-test: perk scopes FAILED: " +
                                    string.Join("; ", failures.ToArray()));
        }

        private static void Step(string name, List<string> failures, Action<List<string>> body)
        {
            try { body(failures); }
            catch (Exception e) { failures.Add(name + " threw: " + e.GetType().Name + ": " + e.Message); }
        }

        // ------------------------------------------------------------------ rooms

        private static Ship A1, A2, A3, B1, S;

        private static View TwoSides()
        {
            View v = new View { Team = true };
            A1 = new Ship("A1", 0, true); A2 = new Ship("A2", 0, true);
            A3 = new Ship("A3", 0, false); B1 = new Ship("B1", 1, true);
            v.All.AddRange(new[] { A1, A2, A3, B1 });
            v.Actors[1] = new Actor { Side = 0, Crew = A1, Aboard = A1, Seat = A2 };
            v.Actors[2] = new Actor { Side = 0, Crew = A2, Aboard = B1 };
            v.Actors[3] = new Actor { Side = 1, Crew = B1 };
            v.Actors[4] = new Actor { Side = 0, Character = false };
            v.Actors[5] = new Actor { Side = 0, Crew = A1, Aboard = A2 };
            v.Actors[6] = new Actor { Side = 0, Seat = B1, Aboard = B1 };
            v.Actors[7] = new Actor { Side = 0, Crew = A3, Aboard = A3 };
            v.Actors[8] = new Actor { Side = 0, Crew = A1, Aboard = A3 };
            return v;
        }

        private static View OneShipRoom()
        {
            View v = new View();
            S = new Ship("S", 0, true);
            v.All.Add(S);
            v.Actors[1] = new Actor { Side = 0, Crew = S, Aboard = S, Seat = S };
            v.Actors[2] = new Actor { Side = 0, Crew = S };
            return v;
        }

        /// <summary>Resolves with the answer the holder's own client would send from this view.</summary>
        private static void Expect(List<string> f, View v, ShipSet set, int actor, params Ship[] want)
        {
            int answer;
            using (TopologyView.UseTestView(v)) answer = HolderConditions.Read(TopologyView.Current, actor);
            ExpectFrom(f, v, set, actor, answer, want);
        }

        /// <summary>Resolves with a given packed answer (-1 for none received).</summary>
        private static void ExpectFrom(List<string> f, View v, ShipSet set, int actor, int answer, params Ship[] want)
        {
            List<object> got = new List<object>();
            using (TopologyView.UseTestView(v))
                PerkScopes.Resolve(TopologyView.Current, set, actor, HolderConditions.MaskPart(answer),
                                   HolderConditions.ShipPart(answer), got);
            bool same = got.Count == want.Length;
            for (int i = 0; same && i < want.Length; i++) same = ReferenceEquals(got[i], want[i]);
            if (!same)
                f.Add(PerkScopes.TargetOf(set) + " for actor " + actor + " (answer " +
                      HolderConditions.DescribeAnswer(answer) + ") gave [" + Join(got) + "], expected [" +
                      Join(want) + "]");
        }

        private static string Join(IEnumerable<object> items)
        {
            List<string> names = new List<string>();
            foreach (object o in items) names.Add(o != null ? o.ToString() : "null");
            return string.Join(", ", names.ToArray());
        }

        // ------------------------------------------------------------------ steps

        private static void MultiShip(List<string> f)
        {
            View v = TwoSides();
            Expect(f, v, ShipSet.Crew, 1, A1);
            Expect(f, v, ShipSet.Crew, 2, A2);
            Expect(f, v, ShipSet.Crew, 3, B1);
            Expect(f, v, ShipSet.Crew, 4);
            Expect(f, v, ShipSet.Crew, 7);            // their ship is dying
            Expect(f, v, ShipSet.Aboard, 1, A1);
            Expect(f, v, ShipSet.Aboard, 2);          // boarded the other side's ship
            Expect(f, v, ShipSet.Aboard, 3);          // open space
            Expect(f, v, ShipSet.Aboard, 5, A2);      // an ally ship of their side
            Expect(f, v, ShipSet.Aboard, 7);          // their ship is dying
            Expect(f, v, ShipSet.Aboard, 8);          // an ally ship that is dying
            int own = HolderConditions.Pack((int)HolderCondition.OnOwnShip, 1);
            ExpectFrom(f, v, ShipSet.Aboard, 1, -1);  // no answer received yet
            ExpectFrom(f, v, ShipSet.Aboard, 1, own, A1);
            // The receiver checks side and life itself, whatever the answer's bits say.
            ExpectFrom(f, v, ShipSet.Aboard, 1, HolderConditions.Pack((int)HolderCondition.OnAllyShip, 4));
            ExpectFrom(f, v, ShipSet.Aboard, 1, HolderConditions.Pack((int)HolderCondition.OnAllyShip, 3));
            ExpectFrom(f, v, ShipSet.Aboard, 1, HolderConditions.Pack((int)HolderCondition.OnAllyShip, 9));
            Expect(f, v, ShipSet.Force, 1, A1, A2);   // the dying A3 is left out
            Expect(f, v, ShipSet.Force, 2, A1, A2);
            Expect(f, v, ShipSet.Force, 3, B1);
            Expect(f, v, ShipSet.SeatForce, 1, A1, A2);
            Expect(f, v, ShipSet.SeatForce, 2);       // not seated
            Expect(f, v, ShipSet.SeatForce, 6, B1);   // the seat's side, not the holder's
        }

        private static void OneShip(List<string> f)
        {
            View v = OneShipRoom();
            Expect(f, v, ShipSet.Crew, 1, S);
            Expect(f, v, ShipSet.Force, 1, S);
            Expect(f, v, ShipSet.Force, 2, S);
            Expect(f, v, ShipSet.Aboard, 1, S);
            Expect(f, v, ShipSet.Aboard, 2);
            // One ship: characterShip gated by onOwnShip, whatever ship number the answer carries.
            ExpectFrom(f, v, ShipSet.Aboard, 1, -1);
            ExpectFrom(f, v, ShipSet.Aboard, 2, HolderConditions.Pack((int)HolderCondition.OnOwnShip, 0), S);
            ExpectFrom(f, v, ShipSet.Aboard, 1, HolderConditions.Pack((int)HolderCondition.InOpenSpace, 1));
            Expect(f, v, ShipSet.SeatForce, 1, S);
            Expect(f, v, ShipSet.SeatForce, 2);
        }

        private static void Distribution(List<string> f)
        {
            Near(f, "each over 3", PerkScopes.SplitFactor(Share.Each, 3), 1f);
            Near(f, "split over 1", PerkScopes.SplitFactor(Share.Split, 1), 1f);
            Near(f, "split over 0", PerkScopes.SplitFactor(Share.Split, 0), 1f);
            Near(f, "split over 3", PerkScopes.SplitFactor(Share.Split, 3), 1f / 3f);
            float third = PerkScopes.SplitFactor(Share.Split, 3);
            Near(f, "percent 0.3 split 3", Scaled(0.3f, ModifierType.AdditiveMultiplier, third), 0.1f);
            Near(f, "flat 6 split 3", Scaled(6f, ModifierType.PrimaryAddend, third), 2f);
            Near(f, "flat2 -3 split 3", Scaled(-3f, ModifierType.SecondaryAddend, third), -1f);
            Near(f, "scalar left alone", Scaled(0.5f, ModifierType.ExponentialMultiplier, third), 0.5f);
            // Three ships' shares add back to the authored amount.
            Near(f, "three shares sum", 3f * Scaled(0.25f, ModifierType.AdditiveMultiplier, third), 0.25f);
        }

        private static float Scaled(float amount, ModifierType type, float factor)
        {
            StatMod mod = new StatMod(new FloatModifier(amount, type), 1);
            PerkScopes.Scale(mod, factor);
            return ((FloatModifier)mod.Mod).Amount;
        }

        private static void Near(List<string> f, string what, float got, float want)
        {
            if (Math.Abs(got - want) > 0.0001f) f.Add(what + ": " + got + ", expected " + want);
        }

        private static void Validation(List<string> f)
        {
            Accept(f, Spec("characterFleet", null, null, "percent"), PerkBuffTarget.Character, ShipSet.Force, Share.Each, 0, 0, true);
            Accept(f, Spec("characterfleet", "split", null, "flat"), PerkBuffTarget.Character, ShipSet.Force, Share.Split, 0, 0, true);
            Accept(f, Spec("seatFleet", "split", null, "percent"), PerkBuffTarget.Character, ShipSet.SeatForce, Share.Split, 0, 0, true);
            Accept(f, Spec("characterAboard", "each", null, "scalar"), PerkBuffTarget.Character, ShipSet.Aboard, Share.Each, 0, 0, true);
            Accept(f, Spec("characterShip", null, null, "percent"), PerkBuffTarget.Character, ShipSet.Crew, Share.Each, 0, 0, true);
            Accept(f, Spec("seatShip", null, null, "percent"), PerkBuffTarget.SeatShip, ShipSet.Character, Share.Each, 0, 0, false);
            Accept(f, Spec("character", null, null, "percent"), PerkBuffTarget.Character, ShipSet.Character, Share.Each, 0, 0, false);
            // A character perk with a condition becomes routed onto the character.
            Accept(f, Spec("character", null, "onEnemyShip", "percent"), PerkBuffTarget.Character, ShipSet.Character, Share.Each,
                   (int)HolderCondition.OnEnemyShip, 0, true);
            Accept(f, Spec("characterFleet", null, new JArray("!inOpenSpace", "TEAMMATCH"), "percent"),
                   PerkBuffTarget.Character, ShipSet.Force, Share.Each,
                   (int)HolderCondition.TeamMatch, (int)HolderCondition.InOpenSpace, true);

            Refuse(f, Spec("characterFleets", null, null, "percent"), "unrecognised target");
            Refuse(f, Spec("characterFleet", "halves", null, "percent"), "unrecognised distribution");
            Refuse(f, Spec("characterShip", "split", null, "percent"), "lands on one collection");
            Refuse(f, Spec("characterAboard", "split", null, "percent"), "lands on one collection");
            Refuse(f, Spec("characterFleet", "split", null, "scalar"), "a scalar effect");
            PerkSpec intSplit = Spec("characterFleet", "split", null, "flat");
            intSplit.mods[0][0].kind = "int";
            Refuse(f, intSplit, "an int effect");
            Refuse(f, Spec("character", null, "onMoon", "percent"), "unrecognised condition 'onMoon'");
            Refuse(f, Spec("character", null, "allyShipHullBelow", "percent"), "reads other ships' state");
            Refuse(f, Spec("character", null, JObject.Parse("{\"allyShipHullBelow\": 0.3}"), "percent"),
                   "reads other ships' state");
            Refuse(f, Spec("character", null, JObject.Parse("{\"onOwnShip\": true}"), "percent"), "written as an object");
            Refuse(f, Spec("character", null, new JArray("onOwnShip", "!onOwnShip"), "percent"), "both required and forbidden");
            Refuse(f, Spec("character", null, new JArray(3), "percent"), "not a name");
            Refuse(f, Spec("seat", null, "onOwnShip", "percent"), "conditions on target 'seat'");
            Refuse(f, Spec("seatShip", null, "onOwnShip", "percent"), "conditions on target 'seatShip'");
        }

        private static PerkSpec Spec(string target, string distribution, object when, string type)
        {
            return new PerkSpec
            {
                key = "k", name = "n", row = 1, target = target, distribution = distribution, when = when,
                mods = new List<List<EffectSpec>>
                {
                    new List<EffectSpec> { new EffectSpec { stat = "Accuracy", type = type, value = 0.3f } },
                },
            };
        }

        /// <summary>routed: the asset carries value 4; otherwise the stock target given.</summary>
        private static void Accept(List<string> f, PerkSpec spec, PerkBuffTarget stock, ShipSet ships, Share share,
                                   int require, int forbid, bool routed)
        {
            PerkScope scope;
            string problem = PerkScopes.Parse(spec, out scope);
            string what = "target '" + spec.target + "' distribution '" + spec.distribution + "'";
            if (problem != null) { f.Add(what + " refused: " + problem); return; }
            PerkBuffTarget want = routed ? CharacterShipRoute.CharacterShip : stock;
            if (scope.GameTarget != want || scope.Ships != ships || scope.Share != share ||
                scope.When.Require != require || scope.When.Forbid != forbid)
                f.Add(what + " parsed as value " + (int)scope.GameTarget + ", " + scope.Ships + ", " + scope.Share +
                      ", require " + scope.When.Require + ", forbid " + scope.When.Forbid);
        }

        private static void Refuse(List<string> f, PerkSpec spec, string reason)
        {
            PerkScope scope;
            string problem = PerkScopes.Parse(spec, out scope);
            string what = "target '" + spec.target + "' distribution '" + spec.distribution + "' when " +
                          (spec.when != null ? spec.when.ToString().Replace("\r", "").Replace("\n", "") : "none");
            if (problem == null) f.Add(what + " was accepted, expected a refusal naming '" + reason + "'");
            else if (problem.IndexOf(reason, StringComparison.Ordinal) < 0)
                f.Add(what + " refused with '" + problem + "', expected '" + reason + "'");
        }

        private static void Conditions(List<string> f)
        {
            View v = TwoSides();
            int team = (int)HolderCondition.TeamMatch, alive = (int)HolderCondition.OwnShipAlive;
            Mask(f, v, 1, team | alive | (int)HolderCondition.OnOwnShip);
            Mask(f, v, 2, team | alive | (int)HolderCondition.OnEnemyShip);
            Mask(f, v, 3, team | alive | (int)HolderCondition.InOpenSpace);
            Mask(f, v, 4, team | (int)HolderCondition.Spectating);
            Mask(f, v, 5, team | alive | (int)HolderCondition.OnAllyShip);
            Mask(f, v, 6, team | (int)HolderCondition.OnEnemyShip);   // no crew ship
            Mask(f, v, 7, team | (int)HolderCondition.OnOwnShip);     // crew ship dying, crew ship none
            Mask(f, v, 8, team | alive);                              // on a dying ally ship: no position
            View one = OneShipRoom();
            Mask(f, one, 1, alive | (int)HolderCondition.OnOwnShip);
            Mask(f, one, 2, alive | (int)HolderCondition.InOpenSpace);

            int adopted = -1, candidate = -1;
            int[] reads = { 5, 9, 5, 9, 9, 9, 3, 9 };
            int[] want = { 5, 5, 5, 5, 9, 9, 9, 9 };
            for (int i = 0; i < reads.Length; i++)
            {
                int got = HolderConditions.Debounce(reads[i], ref adopted, ref candidate);
                if (got != want[i]) { f.Add("debounce read " + i + " adopted " + got + ", expected " + want[i]); break; }
            }
        }

        private static void Mask(List<string> f, View v, int actor, int want)
        {
            int got;
            using (TopologyView.UseTestView(v)) got = HolderConditions.Evaluate(TopologyView.Current, actor);
            if (got != want)
                f.Add("actor " + actor + " mask " + HolderConditions.Describe(got) + ", expected " +
                      HolderConditions.Describe(want));
        }

        private static void RoundTrip(List<string> f)
        {
            Protocol18 protocol = new Protocol18();
            ConditionSet set = new ConditionSet
            {
                Require = (int)HolderCondition.OnEnemyShip,
                Forbid = (int)HolderCondition.Spectating,
            };
            for (int mask = 0; mask <= HolderConditions.AllBits; mask++)
            {
                // Ship numbers 0-6 and the highest an answer may carry.
                int ship = mask == HolderConditions.AllBits ? HolderConditions.MaxShipNumber : mask % 7;
                object[] args = HolderConditions.Message(mask, ship);
                if (!EctypeNet.MatchesLayout(EctypeNet.ConditionMask, args))
                { f.Add("mask " + mask + ": the message matches no layout"); return; }
                object back = protocol.Deserialize(protocol.Serialize(EctypeNet.Pack(EctypeNet.ConditionMask, args)));
                byte subtype;
                object[] got;
                if (!EctypeNet.TryUnpack(back, out subtype, out got) || subtype != EctypeNet.ConditionMask ||
                    !EctypeNet.MatchesLayout(subtype, got) || (int)got[0] != mask || (int)got[1] != ship)
                { f.Add("mask " + mask + " ship " + ship + " did not survive the round trip"); return; }
                HolderConditions.Record(TestActor, HolderConditions.Pack((int)got[0], (int)got[1]));
                int answer = HolderConditions.ReceivedAnswer(TestActor);
                int recorded = HolderConditions.MaskPart(answer);
                if (recorded != mask || HolderConditions.ShipPart(answer) != ship)
                { f.Add("mask " + mask + " ship " + ship + " recorded as " + HolderConditions.DescribeAnswer(answer)); return; }
                bool holds = (mask & (int)HolderCondition.OnEnemyShip) != 0 &&
                             (mask & (int)HolderCondition.Spectating) == 0;
                if (set.Holds(recorded) != holds) { f.Add("onEnemyShip, !spectating on mask " + mask + " gave " + !holds); return; }
            }
            if (set.Holds(-1)) f.Add("a condition held on an unknown mask");
            if (!new ConditionSet().Holds(-1)) f.Add("no condition did not hold on an unknown mask");
        }

        private static void Context(List<string> f)
        {
            View v = TwoSides();
            using (TopologyView.UseTestView(v))
            {
                if (!ReferenceEquals(RoleContext.ShipHandleOf(2, false), A2)) f.Add("RoleContext crew ship of actor 2 is not A2");
                if (!ReferenceEquals(RoleContext.ShipHandleOf(2, true), B1)) f.Add("RoleContext aboard ship of actor 2 is not B1");
                if (RoleContext.ShipHandleOf(3, true) != null) f.Add("RoleContext aboard ship of actor 3 is not none");
                if (RoleContext.ShipHandleOf(0, false) != null) f.Add("RoleContext answered for no owner");
                if (TopologyView.Current.ForceOf(3) != 1) f.Add("RoleContext side of actor 3 is not 1");
                if (!RoleContext.IsTeamMatch) f.Add("RoleContext.IsTeamMatch false in a team match");
                int mask = RoleContext.ConditionsOf(2);
                if ((mask & (int)HolderCondition.OnEnemyShip) == 0) f.Add("RoleContext: actor 2 not on an enemy ship");
                if (RoleContext.ConditionsOf(0) != -1) f.Add("RoleContext gave conditions for no owner");
            }
            if (!ReferenceEquals(TopologyView.Current, LiveTopologyView.Instance)) f.Add("the test view was not restored");
        }
    }
}
