using System;

using BepInEx;
using BepInEx.Logging;
using Ectype;
using CG.Ship.Object;
using Gameplay.Loot;
using ResourceAssets;

using VoidManager;
using VoidManager.MPModChecks;

namespace ExampleLootTables
{
    /// <summary>
    /// Adds one more entry for an item to the stock loot rolls that already drop weapon mods,
    /// through Ectype.LootTables. The item here is a stock weapon mod, so every client can
    /// resolve it; a real consumer passes the GUID of an item its own mod registered.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Loot Tables", PLUGIN_VERSION)]
    [BepInProcess("Void Crew.exe")]
    [BepInDependency(VoidManager.MyPluginInfo.PLUGIN_GUID)]
    [BepInDependency("jack.voidcrew.ectype")]
    public class Plugin : BaseUnityPlugin
    {
#error Replace every REPLACE_ME (the plugin GUID) with your own unique value, then delete this line.
        public const string PLUGIN_GUID = "REPLACE_ME";
        // Keep equal to <Version> in the csproj.
        public const string PLUGIN_VERSION = "0.1.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            try
            {
                // Register once at startup. The call is idempotent, so retrying is safe.
                bool ok = LootTables.Register(new LootDrop
                {
                    Owner = "Example Loot Tables",                      // log text only
                    Item = new GUIDUnion(ItemGuid),
                    Rarity = LootTables.ToLootRarity(RarityType.Common),
                    // Which stock entries decide where the item may drop: every weapon mod.
                    IsAnchor = LootTables.AnyCarryableTagged("Carryable_WeaponMod"),
                    // Only into tables that already offer Common entries, so the item
                    // cannot become the only Common drop of a table that has none.
                    RequireRarityInTable = true,
                    // Asked at each roll. Close it for any room where a member could not
                    // resolve the item.
                    Gate = RoomCanResolveItem,
                });
                if (!ok) Log.LogWarning("Loot registration was refused; see the Ectype log lines.");
                Log.LogInfo("Example Loot Tables v" + PLUGIN_VERSION + " loaded.");
            }
            catch (Exception e)
            {
                Log.LogError("Example Loot Tables failed to start: " + e);
            }
        }

        // Stock weapon mod "Heatsink MkI".
        private const string ItemGuid = "67819aed3fd8de240a7e3f610f1d3267";

        private static bool RoomCanResolveItem()
        {
            try
            {
                // A stock item resolves everywhere. A mod's own item resolves only for
                // members running that mod, which is what the selection gate reports.
                // The verdict is NotHost off the host, and Fail only for a real mismatch.
                GateResult r = SelectionGate.Current(PLUGIN_GUID);
                return r == null || r.Verdict != GateVerdict.Fail;
            }
            catch (Exception e)
            {
                Log.LogError("Loot gate failed: " + e);
                return false;
            }
        }
    }

    /// <summary>Session: the extra drop changes what a run contains, so presence flags the
    /// session. Rolls that spawn items run on the host.</summary>
    public class ExampleVoidPlugin : VoidPlugin
    {
        public override MultiplayerType MPType { get { return MultiplayerType.Session; } }
        public override string Author { get { return "Example Author"; } }
        public override string Description { get { return "Adds a stock weapon mod to the loot rolls that drop weapon mods."; } }
    }
}
