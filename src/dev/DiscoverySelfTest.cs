using System.Collections.Generic;

namespace Ectype
{
    /// <summary>
    /// Dev build only. Runs from the framework's own Awake, while BepInEx is still loading plugins:
    /// role and stats-file discovery asked for then must defer, not latch on the plugins loaded so
    /// far. The driver's own discovery later logs the full counts.
    /// </summary>
    internal static class DiscoverySelfTest
    {
        internal static void Run()
        {
            List<string> fails = new List<string>();
            if (PluginLoad.Finished) fails.Add("plugin loading reads as finished during the framework's Awake");
            RoleRegistry.DiscoverOnce();
            ModStats.DiscoverAndRegister();
            if (RoleRegistry.Discovered) fails.Add("role discovery latched during plugin loading");
            if (ModStats.Discovered) fails.Add("stats-file discovery latched during plugin loading");

            if (fails.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: discovery deferral passed (plugin loading " +
                                   "not finished at load; role and stats-file discovery left to the driver).");
            else
                Plugin.Log.LogError("Ectype: dev self-test: discovery deferral FAILED: " +
                                    string.Join("; ", fails.ToArray()));
        }
    }
}
