using System;

using BepInEx;
using BepInEx.Logging;
using Ectype;
using CG.Ship.Object;
using Gameplay.Loot;
using ResourceAssets;

namespace ExampleLootTables
{
    /// <summary>
    /// Adds one more entry for an item to the stock loot rolls that already drop weapon mods,
    /// through Ectype.LootTables. The item here is a stock weapon mod, so every client can
    /// resolve it; a real consumer passes the GUID of an item its own mod registered.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Loot Tables", PLUGIN_VERSION)]
    [BepInProcess("Void Crew.exe")]
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
                    Owner = PLUGIN_GUID,                                // this plugin's BepInEx GUID
                    Item = new GUIDUnion(ItemGuid),
                    Rarity = LootTables.ToLootRarity(RarityType.Common),
                    // Which stock entries decide where the item may drop: every weapon mod.
                    IsAnchor = LootTables.AnyCarryableTagged("Carryable_WeaponMod"),
                    // Only into tables that already offer Common entries, so the item
                    // cannot become the only Common drop of a table that has none.
                    RequireRarityInTable = true,
                    // No Gate: a stock item resolves on every client. A role-owned item
                    // would pass Gate = () => RoleRegistry.IsAvailableInRoom(roleId).
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
    }
}
