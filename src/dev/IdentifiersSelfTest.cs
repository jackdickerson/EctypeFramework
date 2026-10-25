using System;
using System.Collections.Generic;

namespace Ectype
{
    /// <summary>
    /// Dev build only. Runs from the framework's own Awake, while BepInEx is still loading plugins,
    /// which is when a mod registers: the framework's own GUID is already in the chainloader's
    /// plugin table, so a plugin's own GUID passes the identifier rule during its Awake.
    ///
    /// Cases: a conforming owner and id; an id with no GUID prefix, a prefix and no name, an empty
    /// one and one over the length cap; a GUID no loaded plugin has, as an owner and as a prefix;
    /// the GUID spelled with other capitals; characters a GUID cannot hold; the reserved owners
    /// in any case; a role id accepted only where the role exception is asked for; an allowed set
    /// that names its members.
    /// </summary>
    internal static class IdentifiersSelfTest
    {
        private const string Self = EctypePluginInfo.PLUGIN_GUID;

        internal static void Run()
        {
            List<string> fails = new List<string>();
            try { Cases(fails); }
            catch (Exception e) { fails.Add("threw: " + e.Message); }

            if (fails.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: identifiers passed (own GUID present during Awake; " +
                                   "conforming, missing prefix, unknown GUID, reserved owner, role exception).");
            else
                Plugin.Log.LogError("Ectype: dev self-test: identifiers FAILED: " + string.Join("; ", fails.ToArray()));
        }

        private static void Cases(List<string> fails)
        {
            if (PluginLoad.Finished) fails.Add("plugin loading reads as finished during the framework's Awake");
            if (!Identifiers.IsLoadedPlugin(Self)) fails.Add("the framework's own GUID is not in the plugin table during its Awake");

            Accepted(fails, "conforming owner", Identifiers.OwnerProblem(Self));
            Accepted(fails, "conforming id", Identifiers.IdProblem(Self + "/burn"));
            Accepted(fails, "id whose name holds '/'", Identifiers.IdProblem(Self + "/burn/strong"));

            Refused(fails, "missing prefix", Identifiers.IdProblem("burn"), "no plugin GUID before a '/'");
            Refused(fails, "old-style prefix", Identifiers.IdProblem(Self + ".burn"), "no plugin GUID before a '/'");
            Refused(fails, "empty prefix", Identifiers.IdProblem("/burn"), "no plugin GUID before the '/'");
            Refused(fails, "no name", Identifiers.IdProblem(Self + "/"), "no name after the '/'");
            Refused(fails, "empty id", Identifiers.IdProblem(""), "the id is empty");
            Refused(fails, "long id", Identifiers.IdProblem(Self + "/" + new string('x', Identifiers.MaxLength)),
                    "longer than 128 characters");

            Refused(fails, "unknown GUID owner", Identifiers.OwnerProblem("com.example.not-installed"),
                    "is not the GUID of a loaded BepInEx plugin");
            Refused(fails, "unknown GUID prefix", Identifiers.IdProblem("com.example.not-installed/burn"),
                    "'com.example.not-installed' is not the GUID of a loaded BepInEx plugin");
            Refused(fails, "GUID in other capitals", Identifiers.OwnerProblem(Self.ToUpperInvariant()),
                    "is not the GUID of a loaded BepInEx plugin");
            Refused(fails, "owner with a space", Identifiers.OwnerProblem("my mod"), "is not a BepInEx plugin GUID");
            Refused(fails, "prefix with a space", Identifiers.IdProblem("my mod/burn"), "is not a BepInEx plugin GUID");

            Refused(fails, "reserved owner", Identifiers.OwnerProblem("ectype"), "is reserved for the framework");
            Refused(fails, "reserved owner, capitals", Identifiers.OwnerProblem("Framework"), "is reserved for the framework");
            Refused(fails, "reserved prefix", Identifiers.IdProblem("ectype/burn"), "is reserved for the framework");

            Func<string, bool> role = o => o == "selftest.role";
            Accepted(fails, "role exception", Identifiers.OwnerProblem("selftest.role", role));
            Refused(fails, "role id without the exception", Identifiers.OwnerProblem("selftest.role"),
                    "is not the GUID of a loaded BepInEx plugin");
            Refused(fails, "reserved owner under the exception", Identifiers.OwnerProblem("ectype", role),
                    "is reserved for the framework");

            HashSet<string> allowed = new HashSet<string> { "com.example.b", "com.example.a" };
            Accepted(fails, "owner in the allowed set", Identifiers.OwnerProblem("com.example.a", null, allowed, "in its folder"));
            Refused(fails, "owner outside the allowed set", Identifiers.OwnerProblem(Self, null, allowed, "in its folder"),
                    "is not the GUID of a plugin in its folder; expected one of 'com.example.a', 'com.example.b'");
        }

        private static void Accepted(List<string> fails, string what, string problem)
        {
            if (problem != null) fails.Add(what + ": refused (" + problem + ")");
        }

        private static void Refused(List<string> fails, string what, string problem, string expect)
        {
            if (problem == null || problem.IndexOf(expect, StringComparison.Ordinal) < 0)
                fails.Add(what + ": " + (problem ?? "accepted") + " (expected '" + expect + "')");
        }
    }
}
