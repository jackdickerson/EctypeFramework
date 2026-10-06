using System.Collections.Generic;

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

        /// <summary>One HOST ONLY checkbox per discovered role in the [RoleSuppression]
        /// section, keyed by roleId so a checkbox binds to the same player-facing role
        /// across a plugin-load-order reshuffle. Read only by RoomSync; HostSuppression takes
        /// a plain roleId list and knows nothing of a config UI.</summary>
        public static readonly Dictionary<string, ConfigEntry<bool>> RoleSuppressToggles =
            new Dictionary<string, ConfigEntry<bool>>(
                System.StringComparer.OrdinalIgnoreCase);

        private static ConfigFile _cfg;

        public static ConfigEntry<bool> EnableScroll;
        public static ConfigEntry<string> ScrollExcludeDocuments;
        public static ConfigEntry<bool> RoleWording;
        public static ConfigEntry<bool> WriteVanillaDump;
        public static ConfigEntry<bool> LayoutWorkbench;
        public static ConfigEntry<float> LayoutPollSeconds;
        public static ConfigEntry<string> CharacterShipTestPerk;

        public static void Bind(ConfigFile cfg)
        {
            _cfg = cfg;

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

            WriteVanillaDump = cfg.Bind("Dev", "WriteVanillaDump", false,
                "Write the game's stock role trees to BepInEx/EctypeDump/vanilla-roles.json " +
                "once per launch, for the perk builder's import.");

            LayoutWorkbench = cfg.Bind("Dev", "LayoutWorkbench", false,
                "Show the layout workbench in the F5 menu: reload the gene-tree layout " +
                "from BepInEx/EctypeLayout/layout.json and re-style or rebuild the open " +
                "terminal without restarting, and dump a tree's visual elements. For tuning " +
                "the presentation; the layout file overrides the shipped defaults whenever it " +
                "exists, whether or not this switch is on.");

            LayoutPollSeconds = cfg.Bind("Dev", "LayoutPollSeconds", 0f,
                "Seconds between checks of BepInEx/EctypeLayout/layout.json; 0 disables " +
                "the watcher. When the file changes, the layout reloads, the open terminal " +
                "is re-styled, its panel is written to capture.png and the cycle is " +
                "recorded in status.json - so a tuning loop needs no keypress and no " +
                "window focus. The check is one timestamp read, so a short interval costs " +
                "nothing; a long one only adds latency.");

            CharacterShipTestPerk = cfg.Bind("Dev", "CharacterShipTestPerk", "",
                "Test the characterShip perk target: name one framework perk as " +
                "\"roleId/perkKey\" and it registers as characterShip with a single " +
                "JumpChargeSpeed +50% effect at every level, replacing its authored effects. " +
                "Read at registration, so a change needs a restart; every player in the room " +
                "needs the same value. Empty = off.");
        }

        /// <summary>
        /// Bind one [RoleSuppression] checkbox per registered role, once - roleIds are only
        /// known after discovery runs, well after Bind() above. Cheap to call every frame: a
        /// count comparison short-circuits once every discovered role has an entry.
        /// </summary>
        public static void EnsureRoleToggles()
        {
            if (_cfg == null) return;
            if (RoleSuppressToggles.Count == RoleRegistry.Roles.Count) return;

            foreach (RoleRecord rec in RoleRegistry.Roles)
            {
                string roleId = rec.Manifest.roleId;
                if (RoleSuppressToggles.ContainsKey(roleId)) continue;
                RoleSuppressToggles[roleId] = _cfg.Bind("RoleSuppression",
                    "Suppress_" + roleId, false,
                    "Host only, applied while in the hub: make '" + rec.Manifest.roleName +
                    "' unavailable to every player in the room. Spent points refund; each " +
                    "player sees it on their next terminal open.");
            }
        }
    }
}
