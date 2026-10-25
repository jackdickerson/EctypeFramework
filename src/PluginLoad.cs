using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;

namespace Ectype
{
    /// <summary>
    /// Whether BepInEx has finished loading plugins. Until it has, <see cref="Chainloader.PluginInfos"/>
    /// lists only the plugins loaded so far, and a plugin's own entry gets its <c>Instance</c> only
    /// after its <c>Awake</c> returns, so anything that scans every plugin (role manifests, stats
    /// files) must wait.
    /// </summary>
    internal static class PluginLoad
    {
        private static readonly FieldInfo LoadedField =
            typeof(Chainloader).GetField("_loaded", BindingFlags.Static | BindingFlags.NonPublic);

        private static readonly HashSet<string> Deferred = new HashSet<string>();

        /// <summary>Set by the driver's first frame, which runs after every plugin's Awake: the
        /// chainloader adds every plugin in one synchronous pass before Unity's first Update.</summary>
        internal static bool DriverStarted;

        /// <summary>True once the chainloader has set its own loaded flag, or, when that private
        /// field cannot be read, once the driver has run a frame.</summary>
        internal static bool Finished
        {
            get
            {
                if (DriverStarted) return true;
                if (LoadedField == null) return false;
                try { return (bool)LoadedField.GetValue(null); }
                catch { return false; }
            }
        }

        /// <summary>
        /// False, and one log line per <paramref name="what"/>, while plugins are still loading.
        /// The caller returns without latching, and the registration driver runs it again.
        /// </summary>
        internal static bool ReadyFor(string what)
        {
            if (Finished) return true;
            if (Deferred.Add(what))
                Plugin.Log.LogInfo("Ectype: " + what + " deferred: BepInEx is still loading plugins, " +
                                   "and a plugin loaded after this call would be missed. The " +
                                   "registration driver runs it once loading has finished.");
            return false;
        }
    }
}
