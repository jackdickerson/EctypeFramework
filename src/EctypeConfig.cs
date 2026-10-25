using BepInEx.Configuration;

namespace Ectype
{
    /// <summary>
    /// The registration stages exist as a bisection aid, since several of the steps fail
    /// silently. The tree-def publish is interlocked on its element injection at runtime, so
    /// the default runs everything; PublishTreeDefs is the emergency brake.
    /// </summary>
    internal static class EctypeConfig
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<int> RegisterStage;
        public static ConfigEntry<bool> PublishTreeDefs;
        public static ConfigEntry<bool> VerboseLogging;

        public static ConfigEntry<bool> StatusMarkers;
        public static ConfigEntry<int> StatusMarkerCap;
        public static ConfigEntry<bool> StatusVfx;
        public static ConfigEntry<int> StatusVfxCap;

        public static ConfigEntry<bool> EnableScroll;
        public static ConfigEntry<string> ScrollExcludeDocuments;
        public static ConfigEntry<bool> RoleWording;

        public static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind("General", "Enabled", true,
                "Master switch. Off: no patches, no discovery, no registration - inert.");

            RegisterStage = cfg.Bind("General", "RegisterStage", 9,
                "Bisection aid: stop each role's registration after this step " +
                "(1 custom stats + perk assets built, 2 runtime assets registered, " +
                "3 context infos, 4 perk defs published, 5 tree asset registered, " +
                "6 tree def published). 9 = run everything.");

            PublishTreeDefs = cfg.Bind("General", "PublishTreeDefs", true,
                "Emergency brake on stage 6. A published PerkTreeDef whose PerkTreeVE " +
                "element failed to inject is filtered out of the terminal build so the " +
                "terminal survives; set this false to keep all tree defs unpublished " +
                "(roles register but no tree renders).");

            VerboseLogging = cfg.Bind("Debug", "VerboseLogging", false,
                "Log per-character, per-switch and per-open events: ability installs and " +
                "adoptions, ability-display slot changes, HUD bar button register/build/" +
                "remove/layout, stat value changes, gene-tree injection and scroller " +
                "installs, per-perk unlock and USS palette registration lines. Off keeps " +
                "warnings, errors and the one-time startup summaries.");

            StatusMarkers = cfg.Bind("EnemyStatuses", "Markers", true,
                "Countdown markers over objects carrying an enemy status. Each marker " +
                "is a live UI entity the game's viewer repositions every frame, so " +
                "they are capped and churn-guarded; off disables them entirely " +
                "(statuses themselves still run).");

            StatusMarkerCap = cfg.Bind("EnemyStatuses", "MarkerCap", 10,
                "Most status markers alive at once, across all statuses and targets. " +
                "Beyond the cap a status runs without a marker until one frees up.");

            StatusVfx = cfg.Bind("EnemyStatuses", "Vfx", true,
                "Attached visuals on objects carrying an enemy status; off disables " +
                "them entirely (statuses themselves still run).");

            StatusVfxCap = cfg.Bind("EnemyStatuses", "VfxCap", 8,
                "Most attached status visuals alive at once, across all statuses and " +
                "targets.");

            EnableScroll = cfg.Bind("Presentation", "EnableScroll", true,
                "Wrap the gene-tree row in a horizontal scroller when more than four " +
                "trees survive (registered roles minus suppressed vanilla trees). " +
                "With four or fewer, the layout is never touched. Off: extra trees " +
                "overflow off-layout as before.");

            ScrollExcludeDocuments = cfg.Bind("Presentation", "ScrollExcludeDocuments", "",
                "Comma-separated, case-insensitive substrings; a document whose " +
                "UIDocument name, GameObject name or visualTreeAsset name matches any " +
                "of them keeps its authored layout (no scroller). The per-document " +
                "opt-out for a layout that reacts badly to the wrap - the layout-failed " +
                "warning (needs [Debug] VerboseLogging) prints each document's names.");

            RoleWording = cfg.Bind("Presentation", "RoleWording", true,
                "Where the game's English text calls a playable role a \"Class\", show " +
                "\"Role\" instead: the Class Ability keybind in Settings -> Controls, the " +
                "Class Ability Cooldown/Duration lines on item tooltips, the stock hero-perk " +
                "descriptions and the hub's \"Class: <role>\" projections. Display text only; " +
                "other languages are untouched. Off restores the game's wording; a screen " +
                "already open keeps its text until the game rebuilds it.");
        }
    }
}
