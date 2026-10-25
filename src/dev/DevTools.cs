using System;
using System.Collections.Generic;

using BepInEx.Configuration;
using Gameplay.Perks;
using Gameplay.Utilities;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// Entry points of the dev tools build (ECTYPE_DEV): the [Dev] config section, the
    /// startup line that marks the build, and the F5 page's developer section. A release
    /// build compiles none of this, so its .cfg and F5 page carry no [Dev] keys.
    /// </summary>
    internal static class DevTools
    {
        public static ConfigEntry<bool> WriteVanillaDump;
        public static ConfigEntry<bool> LayoutWorkbench;
        public static ConfigEntry<float> LayoutPollSeconds;
        public static ConfigEntry<string> CharacterShipTestPerk;

        /// <summary>Called from Plugin.Awake right after the player config is bound, before
        /// the master switch is read, so the build is identified in every log.</summary>
        public static void Awake(ConfigFile cfg)
        {
            Plugin.Log.LogWarning(EctypePluginInfo.USERS_PLUGIN_NAME + " " +
                                  EctypePluginInfo.PLUGIN_VERSION + " (dev tools build)");
            Bind(cfg);
            // Unconditional: a layout file overrides the shipped defaults wherever it exists;
            // the workbench switch only decides whether the F5 menu can reload it mid-session.
            DevLayout.Reload();
            NetSelfTest.Run();
            StatusSelfTest.RunOutbox();
            StatusSelfTest.RunPartsOffline();
            InteractionGateDev.SelfTest();
            InteractionGateDev.Bind(cfg);
            ContentSelfTest.Run();
            CharacterStatusSelfTest.RunOffline();
            DiscoverySelfTest.Run();
            IdentifiersSelfTest.Run();
            ScopeSelfTest.Run();
        }

        private static void Bind(ConfigFile cfg)
        {
            WriteVanillaDump = cfg.Bind("Dev", "WriteVanillaDump", false,
                "Write the game's stock role trees to BepInEx/EctypeDump/vanilla-roles.json " +
                "once per launch, for tooling that reads the stock trees.");

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

        /// <summary>Called by the registration pass once role discovery has run.</summary>
        public static void AfterDiscovery()
        {
            if (WriteVanillaDump != null && WriteVanillaDump.Value)
                VanillaDump.EnsureDumped();
        }

        /// <summary>The F5 page's developer section. MenuKit.DevZone already gates this on
        /// VoidManager's DebugMode and exception-isolates the body, so neither is repeated
        /// here.</summary>
        public static void DrawMenu()
        {
            if (GUILayout.Button("Dump gene-tree grid to LogOutput.log"))
                TreeDump.Dump();
            GUILayout.Label(TreeDump.LastSummary);
            GUILayout.Space(8f);

            GUILayout.Label("HUD bar: " + HudAbilityBar.Buttons.Count + "/" +
                            HudAbilityBar.MaxButtons + " buttons");
            if (GUILayout.Button("Dump HUD ability bar to LogOutput.log"))
                HudAbilityBar.DumpTree();
            GUILayout.Space(8f);

            // Read at registration, so typing here changes nothing until the next launch.
            GUILayout.Label("characterShip test perk - \"roleId/perkKey\", empty = off. " +
                            "Restart to apply; every client in the room needs the same value.");
            CharacterShipTestPerk.Value =
                GUILayout.TextField(CharacterShipTestPerk.Value ?? string.Empty);
            GUILayout.Space(8f);

            if (LayoutWorkbench != null && LayoutWorkbench.Value)
            {
                GUILayout.Label("Layout workbench - " + DevLayout.Dir);
                GUILayout.Label("Watcher: " + (LayoutPollSeconds.Value > 0f
                    ? "every " + LayoutPollSeconds.Value.ToString("0.#") + "s"
                    : "off ([Dev] LayoutPollSeconds)"));
                if (GUILayout.Button("Reload layout file, re-style and capture the terminal"))
                    DevInspector.ReloadAndApply();
                if (GUILayout.Button("Reload and rebuild the terminal panel (re-injects)"))
                    DevInspector.RebuildTerminal();
                if (GUILayout.Button("Write the current layout out as a starting file"))
                    DevLayout.WriteTemplate();
                if (GUILayout.Button("Dump a gene tree's visual elements"))
                    DevInspector.DumpTree();
                if (GUILayout.Button("Dump the chrome recon (scroller art and USS rules)"))
                    DevInspector.DumpChrome();
                GUILayout.Label(DevInspector.LastSummary);
            }
        }
    }

    /// <summary>
    /// [Dev] CharacterShipTestPerk: one named framework perk registers as characterShip with
    /// a single JumpChargeSpeed +50% effect at every level, in place of its authored effects.
    /// </summary>
    internal static class CharacterShipTestPerk
    {
        private const string Stat = "JumpChargeSpeed";
        private const float Value = 0.5f;

        /// <summary>Rewrites a freshly built perk when the config names it as
        /// "roleId/perkKey"; leaves every other perk alone.</summary>
        public static void Apply(RoleRecord rec, PerkSpec spec, PerkBuff perk)
        {
            if (!Matches(rec.Manifest, spec)) return;
            perk.Target = CharacterShipRoute.CharacterShip;
            PerkScopes.Set(perk.ContainerGuid, PerkScopes.CharacterShipScope);
            perk.Levels = Levels(rec, spec);
        }

        private static bool Matches(RoleManifest m, PerkSpec spec)
        {
            string want = DevTools.CharacterShipTestPerk != null
                ? (DevTools.CharacterShipTestPerk.Value ?? "").Trim() : "";
            return want.Length > 0 && m != null && spec != null &&
                   string.Equals(want, m.roleId + "/" + spec.key, StringComparison.OrdinalIgnoreCase);
        }

        private static List<PerkBuffLevel> Levels(RoleRecord rec, PerkSpec spec)
        {
            int count = Math.Max(1, Math.Max(spec.levels, spec.mods != null ? spec.mods.Count : 0));
            int statId = StatRegistry.Resolve(Stat, rec.CustomStats);
            ModifierType type = StatRegistry.ResolveModifierType("percent");
            List<PerkBuffLevel> levels = new List<PerkBuffLevel>();
            for (int i = 0; i < count; i++)
                levels.Add(new PerkBuffLevel
                {
                    Mods = new List<StatMod> { new StatMod(new FloatModifier(Value, type), statId) }
                });
            Plugin.Log.LogWarning("Ectype: [Dev] CharacterShipTestPerk - '" + rec.Manifest.roleName +
                                  "' perk '" + spec.key + "' registers as characterShip with " +
                                  Stat + " +" + (Value * 100f) + "% at every level, " +
                                  "replacing its authored effects.");
            return levels;
        }
    }
}
