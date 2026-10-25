namespace Ectype
{
    internal static class EctypePluginInfo
    {
        public const string PLUGIN_GUID = "jack.voidcrew.ectype";
        public const string PLUGIN_NAME = "EctypeFramework";
        public const string USERS_PLUGIN_NAME = "Ectype Framework";
        public const string PLUGIN_VERSION = "0.21.0";
        public const string PLUGIN_DESCRIPTION =
            "Role framework and gameplay utilities for Void Crew mods: playable roles from a " +
            "JSON manifest, plus stats, statuses, loot and HUD helpers.";
        public const string PLUGIN_AUTHORS = "Jack";
        public const string PLUGIN_THUNDERSTORE_ID = "Jack_Modding/Ectype_Framework";

        /// <summary>
        /// The manifest contract version this build understands. A manifest declaring a
        /// different major version is rejected by name and its role is skipped - a skewed
        /// role mod degrades to "role absent", never to a broken terminal.
        /// </summary>
        public const int CONTRACT_VERSION = 2;

        /// <summary>
        /// The network contract: the framework's Photon event code, its message subtypes and
        /// each message's field layout. Two frameworks exchange messages only when this
        /// number matches, and the contract marker asset makes the game's own join check
        /// refuse a peer on another number. Raise it whenever a subtype is added, removed or
        /// renumbered or a message's fields change, and name the new value in the changelog.
        /// Independent of <see cref="CONTRACT_VERSION"/>, which gates role manifests.
        /// </summary>
        public const int NET_CONTRACT_VERSION = 1;

        /// <summary>
        /// A role mod self-identifies by embedding a JSON manifest whose resource name ends
        /// with this suffix. No reference to this assembly is needed for a data-only role.
        /// </summary>
        public const string MANIFEST_SUFFIX = "ectype-role.json";

        /// <summary>
        /// First tree ordinal handed to a registered role, one past EPerkTree's four stock
        /// members. Ordinals above them are reachable by cast and stringify to "4", "5", ... -
        /// the names the injected PerkTreeVE elements carry.
        /// </summary>
        public const int FIRST_TREE_ORDINAL = 4;
    }
}
