namespace Ectype
{
    internal static class EctypePluginInfo
    {
        public const string PLUGIN_GUID = "jack.voidcrew.ectype";
        public const string PLUGIN_NAME = "EctypeFramework";
        public const string USERS_PLUGIN_NAME = "Ectype Framework";
        public const string PLUGIN_VERSION = "0.20.0";
        public const string PLUGIN_DESCRIPTION =
            "Role framework and gameplay utilities for Void Crew mods: playable roles from a " +
            "JSON manifest, plus stats, statuses, loot and HUD helpers. Every player in the " +
            "room needs the same build.";
        public const string PLUGIN_AUTHORS = "Jack";
        public const string PLUGIN_THUNDERSTORE_ID = "Jack_Modding/Ectype_Framework";

        /// <summary>
        /// The manifest contract version this build understands. A manifest declaring a
        /// different major version is rejected by name and its role is skipped - a skewed
        /// role mod degrades to "role absent", never to a broken terminal.
        /// </summary>
        public const int CONTRACT_VERSION = 2;

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
