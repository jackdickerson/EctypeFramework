using System;
using System.Collections.Generic;
using System.Reflection;

using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

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
#if ECTYPE_DEV
            DevTools.Awake(Config);
#endif

            // Not role machinery: dependents call the gate whether or not roles are enabled.
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
                             "session and no roles register: " + e);
                return;
            }
            PatchesApplied = true;

            // Resolved and patched gate by gate, so a renamed game member costs only the
            // system that needs it.
            try { EnemyAbilities.ApplyPatches(harmony); }
            catch (Exception e) { Log.LogError("Ectype: enemy ability gates failed: " + e); }
            try { LootTables.ApplyPatch(harmony); }
            catch (Exception e) { Log.LogError("Ectype: loot table patch failed: " + e); }
            // Not a Harmony patch: Unity Localization's own table-load hook. Retried by the
            // driver when the localization settings are not loaded yet.
            try { RoleWording.Install(); }
            catch (Exception e) { Log.LogError("Ectype: role wording install failed: " + e); }

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
    /// framework and every role mod alike.
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

            if (RoleRegistry.Roles.Count == 0)
            {
                GUILayout.Label("No role manifests discovered" +
                                (RoleRegistry.Discovered ? "." : " yet."));
            }
            foreach (RoleRecord rec in RoleRegistry.Roles)
            {
                GUILayout.BeginHorizontal();
                DrawSuppressToggle(rec);
                GUILayout.Label(rec.Manifest.roleName + "  (tree " + rec.Ordinal + ", " +
                                rec.SourceAssembly.GetName().Name + ")");
                GUILayout.EndHorizontal();
                GUILayout.Label("   " + rec.Status);
            }
            GUILayout.Label("Host suppress checkboxes above: yours only, applied while " +
                            "everyone is in the hub. See the [RoleSuppression] section " +
                            "of this mod's .cfg for the full explanation.");
            GUILayout.Space(8f);

            GUILayout.Label("Config: BepInEx/config/" + EctypePluginInfo.PLUGIN_GUID + ".cfg");

#if ECTYPE_DEV
            MenuKit.DevZone("ectype-framework", DevTools.DrawMenu);
#endif
        }

        private static string SafeStatCount()
        {
            try { return StatRegistry.VanillaCount.ToString(); }
            catch { return "?"; }
        }

        /// <summary>The toggle isn't bound until EnsureRoleToggles has run at least once
        /// after this role was discovered (usually the same frame) - draw nothing rather
        /// than throw on that first frame.</summary>
        private static void DrawSuppressToggle(RoleRecord rec)
        {
            BepInEx.Configuration.ConfigEntry<bool> entry;
            if (!EctypeConfig.RoleSuppressToggles.TryGetValue(rec.Manifest.roleId, out entry) ||
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
#if ECTYPE_DEV
            Step("layout poll", DevLayout.Poll);
#endif
            Step("role toggles", EctypeConfig.EnsureRoleToggles);
            Step("room sync", RoomSync.Poll);
            Step("enemy statuses", EnemyStatuses.Poll);
            // After the status pump: an expiry this frame has released its hosts first.
            Step("enemy abilities", EnemyAbilities.Poll);
            Step("character statuses", CharacterStatuses.Poll);
            Step("timed stats", TimedStatContributions.Poll);
            Step("held ability starts", AbilityActiveMessage.PollHeld);
            Step("ability bar", HudAbilityBar.Tick);
            Step("loot warm-up", LootTables.PollWarmUp);
            Step("role wording", RoleWording.Poll);
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
    }
}
