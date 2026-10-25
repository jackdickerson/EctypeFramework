using System;

using BepInEx;
using BepInEx.Logging;
using CG.Objects;
using CG.Ship.Object;
using Ectype;
using ResourceAssets;
using UnityEngine;

namespace ExampleContentRegistry
{
    /// <summary>
    /// Publishes one new carryable through Ectype.RuntimeContent: a copy of a stock weapon mod
    /// under a GUID of its own, with its own name and crafting data, then adds it to the loot
    /// rolls that drop weapon mods. The copy keeps the donor's model and stat mods.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Content Registry", PLUGIN_VERSION)]
    [BepInProcess("Void Crew.exe")]
    [BepInDependency("jack.voidcrew.ectype")]
    public class Plugin : BaseUnityPlugin
    {
#error Replace every REPLACE_ME (the plugin GUID) with your own unique value, and ItemGuid with a GUID you pick, then delete this line.
        public const string PLUGIN_GUID = "REPLACE_ME";
        // Keep equal to <Version> in the csproj.
        public const string PLUGIN_VERSION = "0.1.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            // The donor prefab lives in a game container that does not exist yet at Awake,
            // so a small driver builds the copy once it does.
            GameObject driver = new GameObject("ExampleContentRegistry.Driver");
            DontDestroyOnLoad(driver);
            driver.hideFlags = HideFlags.HideAndDontSave;
            driver.AddComponent<Publisher>();
            Log.LogInfo("Example Content Registry v" + PLUGIN_VERSION + " loaded.");
        }
    }

    internal class Publisher : MonoBehaviour
    {
        // The new item's GUID: picked once by hand, identical on every client, never changed
        // after release. Replace it with your own.
        private const string ItemGuid = "c0a7e57e3a4b4d5c9e1f20a1b2c3d4e5";

        // Stock weapon mod "Heatsink MkI": the model and stat mods the copy starts from.
        private const string DonorGuid = "67819aed3fd8de240a7e3f610f1d3267";

        private float _next;
        private bool _done;

        private void Update()
        {
            if (_done || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            try
            {
                CarryableDef donor;
                if (!TryGetDonor(out donor)) return;   // containers not up yet
                _done = true;

                GameObject prefab = CopyPrefab(donor.Asset.gameObject, "ExampleHeatsink");
                ContentHandle handle = RuntimeContent.Publish(ContentKind.Carryable,
                    new GUIDUnion(ItemGuid),
                    new ContentAssets
                    {
                        Asset = prefab,
                        Description = "A copy of the stock heatsink, published by an example mod.",
                        Icon = donor.ContextInfo != null ? donor.ContextInfo.Icon : null,
                        // Mandatory for a carryable: printed for 30 alloys, recycled for 10.
                        Crafting = new CraftingRules
                        {
                            CraftingMethod = CraftMethod.Resource,
                            Creation = new ResourceCurrency(ResourceType.Alloys, 30),
                            CanBeRecycled = true,
                            Recycle = new ResourceCurrency(ResourceType.Alloys, 10),
                        },
                        ItemCategory = PurchasableItemSubCategory.Mods_Offensive,
                        Rarity = RarityType.Common,
                    },
                    new ModContentEntry
                    {
                        DisplayName = "Example Heatsink",
                        Owner = Plugin.PLUGIN_GUID,
                        Tier = 1,
                    });
                Plugin.Log.LogInfo("Example Heatsink: " + handle.State +
                                   (handle.Reason != null ? " (" + handle.Reason + ")" : "") + ".");

                // Optional: drop it where weapon mods drop. Idempotent, safe to call once the
                // GUID is chosen, whatever the publication's state.
                LootTables.Register(new LootDrop
                {
                    Owner = Plugin.PLUGIN_GUID,
                    Item = new GUIDUnion(ItemGuid),
                    Rarity = LootTables.ToLootRarity(RarityType.Common),
                    IsAnchor = LootTables.AnyCarryableTagged("Carryable_WeaponMod"),
                    RequireRarityInTable = true,
                });
            }
            catch (Exception e)
            {
                _done = true;
                Plugin.Log.LogError("Example Heatsink failed: " + e);
            }
        }

        private static bool TryGetDonor(out CarryableDef donor)
        {
            donor = null;
            try
            {
                CarryableContainer c = CarryableContainer.Instance;
                return c != null && c.TryGetByGuid(new GUIDUnion(DonorGuid), out donor) &&
                       donor != null && donor.Asset != null;
            }
            catch
            {
                return false;   // the container register is not loaded yet
            }
        }

        /// <summary>
        /// A dormant copy of a prefab. Instantiating under an inactive holder keeps the donor's
        /// behaviours from waking; the copy is then a root of its own, still inactive, kept
        /// across scene loads, which is how the game's own converter keeps a runtime prefab.
        /// </summary>
        private static GameObject CopyPrefab(GameObject donor, string name)
        {
            GameObject holder = new GameObject("ExampleContentRegistry.Holder");
            holder.SetActive(false);
            GameObject copy = Instantiate(donor, holder.transform, false);
            copy.name = name;
            copy.SetActive(false);
            copy.transform.SetParent(null, false);
            DontDestroyOnLoad(copy);
            Destroy(holder);
            return copy;
        }
    }
}
