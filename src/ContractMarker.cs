using ResourceAssets;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// One runtime asset whose GUID encodes the network contract, so the game's own join check
    /// refuses a peer whose framework speaks another one.
    ///
    /// The GUID is a fixed framework prefix with <see cref="EctypePluginInfo.NET_CONTRACT_VERSION"/>
    /// in its last byte. It is registered with <c>ModFlagPolicy.RequiredEffect</c>, inside the
    /// game's Required mask, so <c>GameSessionManager.ValidateMods</c> compares it between host
    /// and joiner: two frameworks on different contracts hold different GUIDs and each side
    /// lacks the other's. For an asset the joiner lacks, the joiner asks the host for its
    /// <c>RuntimeAssetInfo.Name</c> and prints it on the rejection screen, so the name carries
    /// the framework version and the contract number. A no-flag build registers it with no
    /// effect, which leaves it out of that comparison, as it does every other framework asset.
    ///
    /// A Required asset also makes the host publish the room's modded tag, so a room hosted
    /// with the framework is tagged with or without VoidManager.
    /// </summary>
    internal static class ContractMarker
    {
        // 30 hex digits of fixed prefix; the last two are the contract.
        private const string Prefix = "ec7e0c0de5ec7e0c0de5ec7e0c0d5e";

        /// <summary>Shown on the F5 page.</summary>
        internal static string Status = "marker not registered yet";

        private static ScriptableObject _asset;

        /// <summary>The marker GUID, as 32 hex digits.</summary>
        internal static string GuidHex
        {
            get { return Prefix + (EctypePluginInfo.NET_CONTRACT_VERSION & 0xFF).ToString("x2"); }
        }

        /// <summary>
        /// The name the game prints for the marker. A rejection screen shows the host's
        /// value, so it reads as the host's framework.
        /// </summary>
        internal static string Name
        {
            get
            {
                return EctypePluginInfo.USERS_PLUGIN_NAME + " " + EctypePluginInfo.PLUGIN_VERSION +
                       " (network contract " + EctypePluginInfo.NET_CONTRACT_VERSION + ")";
            }
        }

        /// <summary>Registers the marker once. Called from the registration pass after the
        /// asset registry is available; idempotent.</summary>
        internal static void EnsureRegistered()
        {
            RuntimeAssetsRegister reg = RuntimeAssetsRegister.Instance;
            if (reg == null) return;
            GUIDUnion guid = new GUIDUnion(GuidHex);
            if (reg.HasAsset(guid)) return;

            if (_asset == null)
            {
                _asset = ScriptableObject.CreateInstance<EctypeContractAsset>();
                _asset.name = "Ectype_NetContract_" + EctypePluginInfo.NET_CONTRACT_VERSION;
                // DontDestroyOnLoad alone does not survive Resources.UnloadUnusedAssets.
                _asset.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(_asset);
            }
            reg.RegisterAsset(guid, _asset, ModFlagPolicy.RequiredEffect,
                              new RuntimeAssetInfo { Name = Name });
            Status = "marker " + GuidHex;
            Plugin.Log.LogInfo("Ectype: network contract " + EctypePluginInfo.NET_CONTRACT_VERSION +
                               " marker registered (" + GuidHex + ", effect " +
                               ModFlagPolicy.RequiredEffect + ").");
        }
    }

    /// <summary>The marker's asset object. It carries nothing: its GUID is the payload.</summary>
    internal sealed class EctypeContractAsset : ScriptableObject
    {
    }
}
