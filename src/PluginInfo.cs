namespace Ectype
{
    internal static class EctypePluginInfo
    {
        public const string PLUGIN_GUID = "jack.voidcrew.ectype";
        public const string PLUGIN_NAME = "EctypeFramework";
        public const string USERS_PLUGIN_NAME = "Ectype Framework";
        public const string PLUGIN_VERSION = "0.19.10";
        public const string PLUGIN_DESCRIPTION =
            "General framework for Ectype-family mods. Owns the singleton machinery a playable " +
            "class needs (tree-ordinal allocation, the closed-set compatibility patches, the " +
            "registration pipeline, tree UI injection, the stat registry), plus repo-wide " +
            "utilities not specific to classes, such as guarding clients against a session " +
            "mutator they do not have installed. Class mods carry only a JSON manifest, icons " +
            "and optional code callbacks. MPType.All: every client in the room needs the same " +
            "build.";
        public const string PLUGIN_AUTHORS = "Jack";
        public const string PLUGIN_THUNDERSTORE_ID = "Jack_Modding/Ectype_Framework";

        /// <summary>
        /// The manifest contract version this build understands. A manifest declaring a
        /// different major version is rejected by name and its class is skipped - a skewed
        /// class mod degrades to "class absent", never to a broken terminal.
        /// </summary>
        public const int CONTRACT_VERSION = 1;

        /// <summary>
        /// A class mod self-identifies by embedding a JSON manifest whose resource name ends
        /// with this suffix. No reference to this assembly is needed for a data-only class.
        /// </summary>
        public const string MANIFEST_SUFFIX = "ectype-class.json";

        /// <summary>
        /// First tree ordinal handed to a registered class, one past EPerkTree's four stock
        /// members. Ordinals above them are reachable by cast and stringify to "4", "5", ... -
        /// the names the injected PerkTreeVE elements carry.
        /// </summary>
        public const int FIRST_TREE_ORDINAL = 4;
    }
}
