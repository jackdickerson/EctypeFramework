using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using BepInEx.Bootstrap;

namespace Ectype
{
    /// <summary>
    /// The identifier rule, applied by every framework API that takes an identifier from a mod:
    /// an owner is the registering mod's BepInEx plugin GUID, and a single-string id is
    /// <c>"&lt;plugin GUID&gt;/&lt;name&gt;"</c>. The owner, or the part of an id before its first
    /// <c>/</c>, must be a key of <see cref="Chainloader.PluginInfos"/>. The chainloader adds a
    /// plugin's entry before it creates the plugin's component, so a plugin's own GUID is there
    /// during its <c>Awake</c>; a plugin loaded later is not.
    ///
    /// Exceptions: the framework's own owners (<see cref="ReservedOwners"/>), which a mod may not
    /// use, and a role's id where the role's own content is the declarer (its manifest's stats and
    /// mutators). The rule's text and its log lines live here only.
    /// </summary>
    internal static class Identifiers
    {
        /// <summary>The longest id or owner accepted. A received character-status key longer than
        /// this is refused by its receiver, and a session-data key longer than this is refused.</summary>
        internal const int MaxLength = 128;

        /// <summary>Owners only the framework uses: its own owner string, and the word a reader
        /// would take for it. Matched ignoring case.</summary>
        internal static readonly string[] ReservedOwners = { FrameworkStats.Owner, "framework" };

        /// <summary>The characters the BepInEx chainloader accepts in a plugin GUID; it skips a
        /// plugin whose GUID has any other.</summary>
        internal static readonly Regex GuidPattern = new Regex("^[A-Za-z0-9._-]+\\z");

        internal const string IdForm = "\"<plugin GUID>/<name>\"";
        internal const string OwnerForm = "the registering plugin's BepInEx GUID";

        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>True when <paramref name="guid"/> is a key of the chainloader's plugin table,
        /// compared exactly.</summary>
        internal static bool IsLoadedPlugin(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return false;
            try { return Chainloader.PluginInfos != null && Chainloader.PluginInfos.ContainsKey(guid); }
            catch { return false; }
        }

        /// <summary>True when a discovered role has <paramref name="id"/> as its role id: the
        /// predicate a role-declared owner passes to <see cref="OwnerProblem"/>.</summary>
        internal static bool IsRoleId(string id)
        {
            try { return !string.IsNullOrEmpty(id) && RoleRegistry.Find(id) != null; }
            catch { return false; }
        }

        /// <summary>
        /// Null when <paramref name="owner"/> has the form of a plugin GUID and is not reserved,
        /// else why. Does not ask whether the plugin is loaded.
        /// </summary>
        internal static string OwnerFormProblem(string owner)
        {
            if (string.IsNullOrEmpty(owner)) return "no owner; expected " + OwnerForm;
            foreach (string r in ReservedOwners)
                if (string.Equals(owner, r, StringComparison.OrdinalIgnoreCase))
                    return "owner '" + owner + "' is reserved for the framework; expected " + OwnerForm;
            if (owner.Length > MaxLength)
                return "owner '" + owner + "' is longer than " + MaxLength + " characters";
            if (!GuidPattern.IsMatch(owner))
                return "owner '" + owner + "' is not a BepInEx plugin GUID (letters, digits, '.', '_' and '-' " +
                       "only); expected " + OwnerForm;
            return null;
        }

        /// <summary>
        /// Null when <paramref name="owner"/> is acceptable, else why. Acceptable is: a role id
        /// <paramref name="isRole"/> accepts (the role exception; pass it only where the role's own
        /// content declares), or a GUID of the right form that is in
        /// <paramref name="allowed"/> when that is given, else the GUID of a loaded plugin.
        /// <paramref name="allowedFrom"/> describes where the allowed GUIDs come from, completing
        /// "a plugin ...".
        /// </summary>
        internal static string OwnerProblem(string owner, Func<string, bool> isRole = null,
                                            ICollection<string> allowed = null, string allowedFrom = null)
        {
            if (isRole != null && !string.IsNullOrEmpty(owner) && isRole(owner)) return null;
            string form = OwnerFormProblem(owner);
            if (form != null) return form;
            if (allowed != null)
            {
                if (allowed.Contains(owner)) return null;
                return "owner '" + owner + "' is not the GUID of a plugin " + allowedFrom + "; expected " +
                       (allowed.Count == 0 ? "a plugin GUID, and there is none"
                                           : allowed.Count == 1 ? "'" + Join(allowed) + "'"
                                                                : "one of '" + Join(allowed) + "'");
            }
            if (!IsLoadedPlugin(owner))
                return "owner '" + owner + "' is not the GUID of a loaded BepInEx plugin; expected " + OwnerForm;
            return null;
        }

        /// <summary>Null when <paramref name="id"/> is <c>"&lt;plugin GUID&gt;/&lt;name&gt;"</c> with the
        /// GUID of a loaded plugin and a non-empty name, else why.</summary>
        internal static string IdProblem(string id)
        {
            if (string.IsNullOrEmpty(id)) return "the id is empty; expected " + IdForm;
            if (id.Length > MaxLength) return "longer than " + MaxLength + " characters";
            int slash = id.IndexOf('/');
            if (slash < 0) return "no plugin GUID before a '/'; expected " + IdForm;
            if (slash == id.Length - 1) return "no name after the '/'; expected " + IdForm;
            string guid = id.Substring(0, slash);
            if (guid.Length == 0) return "no plugin GUID before the '/'; expected " + IdForm;
            foreach (string r in ReservedOwners)
                if (string.Equals(guid, r, StringComparison.OrdinalIgnoreCase))
                    return "'" + guid + "' is reserved for the framework; expected " + IdForm;
            if (!GuidPattern.IsMatch(guid))
                return "'" + guid + "' is not a BepInEx plugin GUID; expected " + IdForm;
            if (!IsLoadedPlugin(guid))
                return "'" + guid + "' is not the GUID of a loaded BepInEx plugin; expected " + IdForm;
            return null;
        }

        /// <summary>
        /// For an API released before the rule: logs one warning per (<paramref name="what"/>,
        /// id) when <paramref name="id"/> does not follow it. The id still works.
        /// </summary>
        internal static void WarnId(string what, string id)
        {
            string problem = IdProblem(id);
            if (problem != null) Warn(what, id, problem);
        }

        /// <summary>The owner form of <see cref="WarnId"/>.</summary>
        internal static void WarnOwner(string what, string owner)
        {
            string problem = OwnerProblem(owner);
            if (problem != null) Warn(what, owner, problem);
        }

        /// <summary>
        /// For an API with no refusal reason of its own: false, and one error, when
        /// <paramref name="id"/> does not follow the rule.
        /// </summary>
        internal static bool RefuseId(string what, string id)
        {
            string problem = IdProblem(id);
            if (problem == null) return true;
            Plugin.Log.LogError("Ectype: " + what + " '" + id + "' refused: " + problem + ".");
            return false;
        }

        private static void Warn(string what, string id, string problem)
        {
            if (!Warned.Add(what + "\n" + (id ?? ""))) return;
            Plugin.Log.LogWarning("Ectype: " + what + " '" + id + "' does not follow the identifier rule: " +
                                  problem + ". It still works.");
        }

        private static string Join(ICollection<string> values)
        {
            List<string> list = new List<string>(values);
            list.Sort(StringComparer.Ordinal);
            return string.Join("', '", list.ToArray());
        }
    }
}
