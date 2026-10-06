using System;
using System.Collections.Generic;
using System.Reflection;

using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

using VoidManager;
using VoidManager.CustomGUI;
using VoidManager.MPModChecks;

namespace Ectype
{
    [BepInPlugin(EctypePluginInfo.PLUGIN_GUID, EctypePluginInfo.USERS_PLUGIN_NAME,
                 EctypePluginInfo.PLUGIN_VERSION)]
    [BepInProcess("Void Crew.exe")]
    [BepInDependency(VoidManager.MyPluginInfo.PLUGIN_GUID)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        /// <summary>True when [Debug] VerboseLogging is on. Per-event log lines test this before
        /// building their message, so an off switch costs one bool read.</summary>
        internal static bool Verbose
        {
            get { return EctypeConfig.VerboseLogging != null && EctypeConfig.VerboseLogging.Value; }
        }

        /// <summary>True only after CreateAndPatchAll returned cleanly. Harmony does not roll
        /// back a failed PatchAll, so every registration entry point gates on this flag rather
        /// than registering content on a half-applied patch set.</summary>
        internal static bool PatchesApplied;

        private void Awake()
        {
            Log = Logger;
            EctypeConfig.Bind(Config);
            // Unconditional: a layout file overrides the shipped defaults wherever it exists;
            // the workbench switch only decides whether the F5 menu can reload it mid-session.
            DevLayout.Reload();

            // Not class machinery: dependents call the gate whether or not classes are enabled.
            try { SelectionGate.EnsureBound(); }
            catch (Exception e) { Log.LogError("Ectype: binding the selection gate failed: " + e); }

            if (!EctypeConfig.Enabled.Value)
            {
                Log.LogWarning("Ectype: disabled via config ([General] Enabled = false).");
                return;
            }

            Harmony harmony;
            try
            {
                harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(),
                                                    EctypePluginInfo.PLUGIN_GUID);
            }
            catch (Exception e)
            {
                Log.LogError("Ectype: FATAL - patching failed, framework is inert this " +
                             "session and no classes register: " + e);
                return;
            }
            PatchesApplied = true;

            // Resolved and patched gate by gate, so a renamed game member costs only the
            // system that needs it.
            try { EnemyAbilities.ApplyPatches(harmony); }
            catch (Exception e) { Log.LogError("Ectype: enemy ability gates failed: " + e); }
            try { LootTables.ApplyPatch(harmony); }
            catch (Exception e) { Log.LogError("Ectype: loot table patch failed: " + e); }

            GameObject driver = new GameObject("Ectype.Driver");
            UnityEngine.Object.DontDestroyOnLoad(driver);
            driver.hideFlags = HideFlags.HideAndDontSave;
            driver.AddComponent<EctypeDriver>();

            Log.LogInfo(EctypePluginInfo.USERS_PLUGIN_NAME + " v" +
                        EctypePluginInfo.PLUGIN_VERSION + " loaded (contract v" +
                        EctypePluginInfo.CONTRACT_VERSION + ").");
        }
    }

    /// <summary>
    /// MultiplayerType.All, not Session: an unmodded peer throws on a framework perk GUID and
    /// wipes that player's perk set on its own client, so everyone in the room needs this DLL -
    /// framework and every class mod alike.
    /// </summary>
    public class EctypeVoidPlugin : VoidPlugin
    {
        public override MultiplayerType MPType { get { return MultiplayerType.All; } }
        public override string Author { get { return EctypePluginInfo.PLUGIN_AUTHORS; } }
        public override string Description { get { return EctypePluginInfo.PLUGIN_DESCRIPTION; } }
        public override string ThunderstoreID { get { return EctypePluginInfo.PLUGIN_THUNDERSTORE_ID; } }

        public override SessionChangedReturn OnSessionChange(SessionChangedInput input)
        {
            return new SessionChangedReturn { SetMod_Session = ModFlagPolicy.FlagsSession };
        }
    }

    public class EctypeSettingsMenu : ModSettingsMenu
    {
        public override string Name() { return EctypePluginInfo.USERS_PLUGIN_NAME; }

        public override void Draw()
        {
            GUILayout.Label("Pipeline: " + EctypeAssets.GlobalStatus);
            GUILayout.Label("Stats  : " + SafeStatCount() + " vanilla aliases, " +
                            "framework-owned " + FrameworkStats.Status);
            GUILayout.Label("Suppress: " + Suppression.Status);
            GUILayout.Label("Host suppress: " + HostSuppression.Status);
            GUILayout.Space(8f);

            if (ClassRegistry.Classes.Count == 0)
            {
                GUILayout.Label("No class manifests discovered" +
                                (ClassRegistry.Discovered ? "." : " yet."));
            }
            foreach (ClassRecord rec in ClassRegistry.Classes)
            {
                GUILayout.BeginHorizontal();
                DrawSuppressToggle(rec);
                GUILayout.Label(rec.Manifest.className + "  (tree " + rec.Ordinal + ", " +
                                rec.SourceAssembly.GetName().Name + ")");
                GUILayout.EndHorizontal();
                GUILayout.Label("   " + rec.Status);
            }
            GUILayout.Label("Host suppress checkboxes above: yours only, applied while " +
                            "everyone is in the hub. See the [ClassSuppression] section " +
                            "of this mod's .cfg for the full explanation.");
            GUILayout.Space(8f);

            GUILayout.Label("Config: BepInEx/config/" + EctypePluginInfo.PLUGIN_GUID + ".cfg");

            MenuKit.DevZone("ectype-framework", DrawDevTools);
        }

        private static string SafeStatCount()
        {
            try { return StatRegistry.VanillaCount.ToString(); }
            catch { return "?"; }
        }

        /// <summary>Dev tooling. MenuKit.DevZone already gates this on VoidManager's DebugMode
        /// and exception-isolates the body, so neither is repeated here.</summary>
        private static void DrawDevTools()
        {
            if (GUILayout.Button("Dump gene-tree grid to LogOutput.log"))
                TreeDump.Dump();
            GUILayout.Label(TreeDump.LastSummary);
            GUILayout.Space(8f);

            EctypeConfig.DemoHudButton.Value = GUILayout.Toggle(EctypeConfig.DemoHudButton.Value,
                "Demo button on the HUD ability bar (F9 chip, 8 s cooldown loop)");
            GUILayout.Label("HUD bar: " + HudAbilityBar.Buttons.Count + "/" +
                            HudAbilityBar.MaxButtons + " buttons");
            if (GUILayout.Button("Dump HUD ability bar to LogOutput.log"))
                HudAbilityBar.DumpTree();
            GUILayout.Space(8f);

            // Read at registration, so typing here changes nothing until the next launch.
            GUILayout.Label("characterShip test perk - \"classId/perkKey\", empty = off. " +
                            "Restart to apply; every client in the room needs the same value.");
            EctypeConfig.CharacterShipTestPerk.Value =
                GUILayout.TextField(EctypeConfig.CharacterShipTestPerk.Value ?? string.Empty);
            GUILayout.Space(8f);

            if (EctypeConfig.LayoutWorkbench != null && EctypeConfig.LayoutWorkbench.Value)
            {
                GUILayout.Label("Layout workbench - " + DevLayout.Dir);
                GUILayout.Label("Watcher: " + (EctypeConfig.LayoutPollSeconds.Value > 0f
                    ? "every " + EctypeConfig.LayoutPollSeconds.Value.ToString("0.#") + "s"
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

        /// <summary>The toggle isn't bound until EnsureClassToggles has run at least once
        /// after this class was discovered (usually the same frame) - draw nothing rather
        /// than throw on that first frame.</summary>
        private static void DrawSuppressToggle(ClassRecord rec)
        {
            BepInEx.Configuration.ConfigEntry<bool> entry;
            if (!EctypeConfig.ClassSuppressToggles.TryGetValue(rec.Manifest.classId, out entry) ||
                entry == null)
            {
                GUILayout.Label("[ ]", GUILayout.Width(24f));
                return;
            }
            entry.Value = GUILayout.Toggle(entry.Value, "", GUILayout.Width(24f));
        }
    }

    /// <summary>
    /// One hidden DontDestroyOnLoad driver. The asset containers are Resources-backed and do
    /// not exist at plugin Awake, so registration retries quietly until a scene has loaded
    /// them (the PerkLoadout ctor prefix usually wins the race first).
    /// </summary>
    internal class EctypeDriver : MonoBehaviour
    {
        private float _nextRegister;
        private readonly HashSet<string> _failedSteps = new HashSet<string>();

        // Each step is guarded on its own, so one throwing step does not starve the rest.
        private void Update()
        {
            if (Time.unscaledTime >= _nextRegister)
            {
                _nextRegister = Time.unscaledTime + 2f;
                Step("register", EctypeAssets.EnsureRegistered);
            }
            Step("layout key", PollLayoutReloadKey);
            Step("layout poll", DevLayout.Poll);
            Step("class toggles", EctypeConfig.EnsureClassToggles);
            Step("room sync", RoomSync.Poll);
            Step("enemy statuses", EnemyStatuses.Poll);
            // After the status pump: an expiry this frame has released its hosts first.
            Step("enemy abilities", EnemyAbilities.Poll);
            Step("character statuses", CharacterStatuses.Poll);
            Step("timed stats", TimedStatContributions.Poll);
            Step("ability bar", HudAbilityBar.Tick);
            Step("loot warm-up", LootTables.PollWarmUp);
        }

        /// <summary>Runs one step; logs its first failure and suppresses repeats.</summary>
        private void Step(string name, Action step)
        {
            try { step(); }
            catch (Exception e)
            {
                if (_failedSteps.Add(name))
                    Plugin.Log.LogError("Ectype: driver step '" + name + "' failed, " +
                                        "suppressing further reports: " + e);
            }
        }

        /// <summary>
        /// Shift+F10 reloads the layout file and re-styles the terminal you are standing at,
        /// so a tuning pass never leaves the gene-tree screen (the F5 menu carries the same
        /// action, but opening it closes the terminal being looked at). Shift is required
        /// because a bare function key fires while the terminal has focus and while typing
        /// anywhere else.
        /// </summary>
        private void PollLayoutReloadKey()
        {
            if (EctypeConfig.LayoutWorkbench == null || !EctypeConfig.LayoutWorkbench.Value)
                return;
            // kitbash mode closes the gate; see shared/devkeys/DevKeyGate.cs
            if (!DevKeyGate.Open) return;
            Keyboard kb = Keyboard.current;
            if (kb == null || !kb[Key.F10].wasPressedThisFrame) return;
            if (!kb[Key.LeftShift].isPressed && !kb[Key.RightShift].isPressed) return;

            DevInspector.ReloadAndApply();
            Plugin.Log.LogInfo("Ectype: Shift+F10 - " + DevInspector.LastSummary);
        }
    }
}
