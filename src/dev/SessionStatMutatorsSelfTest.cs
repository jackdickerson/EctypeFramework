using System;
using System.Collections.Generic;
using System.Globalization;

using Gameplay.Mutators;
using Gameplay.Utilities;
using Newtonsoft.Json;
using ResourceAssets;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// Dev build self-test for session-stat mutators.
    ///
    /// Offline, on the driver's first frame:
    /// - GUID derivation: DeriveGuid("com.captainchunck.voidcrew.fleet", "pilgrimage") is
    ///   b0a920957ba0011396ae1e483f09c19d, the vector the docs print, and an explicit GUID with
    ///   dashes and capitals normalises.
    /// - Spec validation: a good spec owned by the framework's own plugin GUID passes; no owner,
    ///   an owner with '/', the reserved owner, an owner that is no loaded plugin, a bad key, GUID,
    ///   missing name, no effects, too many effects, an unknown kind, a non-finite amount, a factor
    ///   of 0 and an empty stat are each refused with their reason.
    /// - Kinds: flat, percent and factor become PrimaryAddend, AdditiveMultiplier and
    ///   ExponentialMultiplier with an amount of factor - 1.
    /// - Manifest form: a JSON entry parses into a spec; a capitalised kind and a missing amount are
    ///   refused.
    /// - Prediction: the hub reads only its starting list; a mission reads the forced list first,
    ///   then the starting list without duplicates or unresolvable GUIDs; a quest whitelist filters
    ///   the starting list and not the forced one.
    /// - Opening: a synthetic active mutator run through the real opening handler contributes to
    ///   the collection being opened, so its effects are in the values the host publishes; a
    ///   mutator without the effect contributes nothing; the host handlers run the mutator
    ///   contribution before any Opening subscriber.
    /// Once the stats files are read: a framework session stat resolves by both names, a character
    /// stat and an undeclared stat are refused.
    /// </summary>
    internal static class SessionStatMutatorsSelfTest
    {
        private const string Vector = "b0a920957ba0011396ae1e483f09c19d";
        private const string Fleet = "com.captainchunck.voidcrew.fleet";
        private static bool _offlineDone;
        private static bool _registeredDone;
        private static float _nextTry;

        public static void Poll()
        {
            if (!Plugin.PatchesApplied) return;
            if (!_offlineDone)
            {
                _offlineDone = true;
                Offline();
            }
            if (_registeredDone || Time.unscaledTime < _nextTry) return;
            _nextTry = Time.unscaledTime + 5f;
            if (!ModStats.Discovered || FrameworkStats.Id(FrameworkStats.HostileSpawnScale) == 0) return;
            _registeredDone = true;
            Registered();
        }

        private static void Offline()
        {
            List<string> failures = new List<string>();
            try { GuidVector(failures); }
            catch (Exception e) { failures.Add("GUID derivation threw: " + e.Message); }
            try { Validation(failures); }
            catch (Exception e) { failures.Add("validation threw: " + e.Message); }
            try { Kinds(failures); }
            catch (Exception e) { failures.Add("kinds threw: " + e.Message); }
            try { Manifest(failures); }
            catch (Exception e) { failures.Add("manifest form threw: " + e.Message); }
            try { Prediction(failures); }
            catch (Exception e) { failures.Add("prediction threw: " + e.Message); }
            try { Opening(failures); }
            catch (Exception e) { failures.Add("opening threw: " + e.Message); }

            if (failures.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: session-stat mutators passed (GUID vector; spec " +
                                   "validation; kinds; manifest form; activation prediction; contribution " +
                                   "inside the opening, before capture).");
            else
                Plugin.Log.LogError("Ectype: dev self-test: session-stat mutators FAILED: " +
                                    string.Join("; ", failures.ToArray()));
        }

        private static void GuidVector(List<string> failures)
        {
            string g = SessionStatMutators.DeriveGuid(Fleet, "pilgrimage");
            if (g != Vector) failures.Add("DeriveGuid(" + Fleet + ", pilgrimage) is " + g + ", expected " + Vector);
            if (Guids.Derive("ectype:mutator:" + Fleet + "/pilgrimage") != g) failures.Add("DeriveGuid differs from Guids.Derive");
            MutatorSpec pinned = Good();
            pinned.Guid = "B0A92095-7BA0-0113-96AE-1E483F09C19D";
            if (SessionStatMutators.GuidOf(pinned) != Vector)
                failures.Add("an explicit GUID normalised to " + SessionStatMutators.GuidOf(pinned));
            MutatorSpec derived = Good();
            derived.Owner = Fleet;
            if (SessionStatMutators.GuidOf(derived) != Vector) failures.Add("a spec without a GUID did not derive it");
        }

        private static MutatorSpec Good()
        {
            MutatorSpec s = new MutatorSpec { Owner = EctypePluginInfo.PLUGIN_GUID, Key = "pilgrimage", DisplayName = "Pilgrimage" };
            s.Effects.Add(new SessionStatEffect { Stat = "ectype/HostileSpawnScale", Kind = SessionStatModifierKind.Factor, Amount = 1.5f });
            return s;
        }

        private static void Validation(List<string> failures)
        {
            string ok = SessionStatMutators.Validate(Good());
            if (ok != null) failures.Add("a good spec was refused: " + ok);

            Refused(failures, s => s.Owner = null, "no owner", "no owner");
            Refused(failures, s => s.Owner = "fleet/x", "owner with '/'", "is not a BepInEx plugin GUID");
            Refused(failures, s => s.Owner = "ectype", "reserved owner", "is reserved for the framework");
            Refused(failures, s => s.Owner = "com.example.not-installed", "owner no plugin has",
                    "is not the GUID of a loaded BepInEx plugin");
            Refused(failures, s => s.Key = "bad key", "key with a space", "the key 'bad key'");
            Refused(failures, s => s.Guid = "xyz", "bad GUID", "the guid 'xyz'");
            Refused(failures, s => s.DisplayName = " ", "blank name", "no display name");
            Refused(failures, s => s.Effects.Clear(), "no effects", "no effects");
            Refused(failures, s =>
            {
                for (int i = 0; i < SessionStatMutators.MaxEffects; i++) s.Effects.Add(s.Effects[0]);
            }, "too many effects", "more than the 16");
            Refused(failures, s => s.Effects[0].Kind = (SessionStatModifierKind)7, "unknown kind", "effect #0: unknown kind 7");
            Refused(failures, s => s.Effects[0].Amount = float.NaN, "NaN amount", "not a finite number");
            Refused(failures, s => s.Effects[0].Amount = 0f, "factor of 0", "a factor is above 0");
            Refused(failures, s => s.Effects[0].Stat = "", "empty stat", "effect #0: no stat");
        }

        private static void Refused(List<string> failures, Action<MutatorSpec> spoil, string what, string expect)
        {
            MutatorSpec s = Good();
            spoil(s);
            string why = SessionStatMutators.Validate(s);
            if (why == null || !why.Contains(expect))
                failures.Add(what + ": " + (why ?? "accepted") + " (expected '" + expect + "')");
        }

        private static void Kinds(List<string> failures)
        {
            Check(failures, SessionStatModifierKind.Flat, 2f, ModifierType.PrimaryAddend, 2f);
            Check(failures, SessionStatModifierKind.Percent, 0.25f, ModifierType.AdditiveMultiplier, 0.25f);
            Check(failures, SessionStatModifierKind.Factor, 1.5f, ModifierType.ExponentialMultiplier, 0.5f);
        }

        private static void Check(List<string> failures, SessionStatModifierKind kind, float amount, ModifierType type, float expect)
        {
            SessionStatModifier m = SessionStatMutators.ToModifier(
                new SessionStatEffect { Stat = "x/Y", Kind = kind, Amount = amount }, 1);
            if (m.Type != type || !Near(m.Amount, expect))
                failures.Add(kind + " " + amount + " became " + m.Type + " " + m.Amount + ", expected " + type + " " + expect);
        }

        private static void Manifest(List<string> failures)
        {
            MutatorManifestSpec entry = JsonConvert.DeserializeObject<MutatorManifestSpec>(
                "{ \"key\": \"surge\", \"name\": \"Hostile Surge\", \"description\": \"More hostiles.\"," +
                " \"effects\": [ { \"stat\": \"ectype/HostileSpawnScale\", \"kind\": \"factor\", \"amount\": 1.5 } ] }");
            string why;
            MutatorSpec spec = SessionStatMutators.FromManifest("selftest.role", entry, null, out why);
            if (spec == null) failures.Add("a good manifest entry was refused: " + why);
            else if (spec.Owner != "selftest.role" || spec.Key != "surge" || spec.DisplayName != "Hostile Surge" ||
                     spec.Effects.Count != 1 || spec.Effects[0].Kind != SessionStatModifierKind.Factor ||
                     !Near(spec.Effects[0].Amount, 1.5f) ||
                     SessionStatMutators.Validate(spec, o => o == "selftest.role") != null)
                failures.Add("a good manifest entry read back wrong");

            entry.effects[0].kind = "Factor";
            if (SessionStatMutators.FromManifest("selftest.role", entry, null, out why) != null ||
                why == null || !why.Contains("unknown kind 'Factor'"))
                failures.Add("a capitalised kind: " + (why ?? "accepted"));
            entry.effects[0].kind = "factor";
            entry.effects[0].amount = null;
            if (SessionStatMutators.FromManifest("selftest.role", entry, null, out why) != null ||
                why == null || !why.Contains("no amount"))
                failures.Add("a missing amount: " + (why ?? "accepted"));
        }

        private static Mutator MakeMutator(string name, GUIDUnion guid, SessionStatMutatorEffect effect)
        {
            Mutator m = ScriptableObject.CreateInstance<Mutator>();
            m.name = name;
            m.hideFlags = HideFlags.HideAndDontSave;
            m.ContainerGuid = guid;
            if (m.Effects == null) m.Effects = new List<MutatorEffect>();
            if (effect != null) m.Effects.Add(effect);
            return m;
        }

        private static GUIDUnion G(string seed) { return new GUIDUnion(Guids.Derive("selftest:mutator:" + seed)); }

        private static void Prediction(List<string> failures)
        {
            GUIDUnion a = G("a"), b = G("b"), c = G("c"), unknown = G("unknown");
            Mutator ma = MakeMutator("A", a, null), mb = MakeMutator("B", b, null), mc = MakeMutator("C", c, null);
            try
            {
                Dictionary<GUIDUnion, Mutator> known = new Dictionary<GUIDUnion, Mutator> { { a, ma }, { b, mb }, { c, mc } };
                Func<GUIDUnion, Mutator> resolve = g =>
                {
                    Mutator m;
                    return known.TryGetValue(g, out m) ? m : null;
                };

                SessionStatMutators.ActivationInputs hub = new SessionStatMutators.ActivationInputs { IsHub = true };
                hub.Starting.Add(a);
                hub.Forced.Add(b);
                Expect(failures, "hub", SessionStatMutators.PredictActive(hub, resolve), ma);

                SessionStatMutators.ActivationInputs mission = new SessionStatMutators.ActivationInputs();
                mission.Forced.Add(b);
                mission.Starting.AddRange(new[] { a, b, unknown, c });
                Expect(failures, "mission", SessionStatMutators.PredictActive(mission, resolve), mb, ma, mc);

                SessionStatMutators.ActivationInputs listed = new SessionStatMutators.ActivationInputs { UseWhitelist = true };
                listed.Whitelist.Add(a);
                listed.Forced.Add(b);
                listed.Starting.AddRange(new[] { a, c });
                Expect(failures, "whitelist", SessionStatMutators.PredictActive(listed, resolve), mb, ma);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(ma);
                UnityEngine.Object.DestroyImmediate(mb);
                UnityEngine.Object.DestroyImmediate(mc);
            }
        }

        private static void Expect(List<string> failures, string what, List<Mutator> got, params Mutator[] expect)
        {
            bool same = got.Count == expect.Length;
            for (int i = 0; same && i < expect.Length; i++) same = ReferenceEquals(got[i], expect[i]);
            if (same) return;
            List<string> names = new List<string>();
            foreach (Mutator m in got) names.Add(m.name);
            failures.Add(what + ": predicted [" + string.Join(", ", names.ToArray()) + "]");
        }

        private static void Opening(List<string> failures)
        {
            Delegate[] order = SessionStats.HostHandlers().GetInvocationList();
            if (order.Length == 0 || order[0].Method.Name != "ContributeAtOpening" ||
                order[0].Method.DeclaringType != typeof(SessionStatMutators))
                failures.Add("the host handlers do not start with the mutator contribution");

            if (SessionStats.Collection != null || Photon.Pun.PhotonNetwork.InRoom)
            {
                failures.Add("opening not run: a session is open");
                return;
            }
            int surge = ModStats.Id("selftest", "Surge"), interval = ModStats.Id("selftest", "Interval");
            SessionStatMutatorEffect effect = new SessionStatMutatorEffect();
            effect.Modifiers.Add(SessionStatMutators.ToModifier(
                new SessionStatEffect { Stat = "selftest/Surge", Kind = SessionStatModifierKind.Factor, Amount = 1.5f }, surge));
            effect.Modifiers.Add(SessionStatMutators.ToModifier(
                new SessionStatEffect { Stat = "selftest/Interval", Kind = SessionStatModifierKind.Flat, Amount = 2f }, interval));
            effect.Modifiers.Add(SessionStatMutators.ToModifier(
                new SessionStatEffect { Stat = "selftest/Interval", Kind = SessionStatModifierKind.Percent, Amount = 0.25f }, interval));
            GUIDUnion ga = G("surge"), gb = G("plain");
            Mutator active = MakeMutator("Surge", ga, effect), plain = MakeMutator("Plain", gb, null);

            SessionStatMutators.ActivationInputs inputs = new SessionStatMutators.ActivationInputs();
            inputs.Starting.AddRange(new[] { ga, gb });
            Dictionary<string, float> published;
            try
            {
                SessionStatMutators.InputsForTest = inputs;
                SessionStatMutators.ResolveForTest = g => g.Equals(ga) ? active : g.Equals(gb) ? plain : null;
                Action register = () =>
                {
                    StatTagCollection c = SessionStats.Collection;
                    if (c == null) return;
                    ModifiableFloat f = new ModifiableFloat(1f);
                    c.RegisterStat(surge, ref f);
                    ModifiableFloat i = new ModifiableFloat(10f);
                    c.RegisterStat(interval, ref i);
                };
                published = SessionStats.Produce(register + SessionStatMutators.ContributeAtOpening);
            }
            finally
            {
                SessionStatMutators.InputsForTest = null;
                SessionStatMutators.ResolveForTest = null;
                SessionStats.DiscardPendingForTest();
                UnityEngine.Object.DestroyImmediate(active);
                UnityEngine.Object.DestroyImmediate(plain);
            }
            float s = Read(published, surge), iv = Read(published, interval);
            if (!Near(s, 1.5f) || !Near(iv, 15f))
                failures.Add("published " + s + " / " + iv + ", expected 1.5 (x1.5) / 15 ((10 + 2) x 1.25)");
            if (SessionStats.Collection != null) failures.Add("the test's collection is still current after the test");
        }

        private static void Registered()
        {
            List<string> failures = new List<string>();
            int id;
            string why = SessionStatMutators.ResolveSessionStat("ectype/HostileSpawnScale", out id);
            if (why != null || id != FrameworkStats.Id(FrameworkStats.HostileSpawnScale))
                failures.Add("ectype/HostileSpawnScale: " + (why ?? "id " + id));
            why = SessionStatMutators.ResolveSessionStat(FrameworkStats.HostileSpawnScale, out id);
            if (why != null) failures.Add("bare HostileSpawnScale: " + why);
            why = SessionStatMutators.ResolveSessionStat("ectype/MeleeDamage", out id);
            if (why == null || !why.Contains("is a character stat")) failures.Add("ectype/MeleeDamage: " + (why ?? "accepted"));
            why = SessionStatMutators.ResolveSessionStat("nobody/Unknown", out id);
            if (why == null || !why.Contains("is not declared")) failures.Add("nobody/Unknown: " + (why ?? "accepted"));

            if (failures.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: session-stat mutator stats passed (a session stat " +
                                   "resolves; a character stat and an undeclared stat are refused).");
            else
                Plugin.Log.LogError("Ectype: dev self-test: session-stat mutator stats FAILED: " +
                                    string.Join("; ", failures.ToArray()));
        }

        private static float Read(Dictionary<string, float> values, int id)
        {
            float v;
            return values.TryGetValue(id.ToString(CultureInfo.InvariantCulture), out v) ? v : float.NaN;
        }

        private static bool Near(float a, float b) { return Math.Abs(a - b) < 0.0001f; }
    }
}
