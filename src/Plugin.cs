using System;
using System.Collections.Generic;
using System.Reflection;

using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Ectype
{
    [BepInPlugin(EctypePluginInfo.PLUGIN_GUID, EctypePluginInfo.USERS_PLUGIN_NAME,
                 EctypePluginInfo.PLUGIN_VERSION)]
    [BepInProcess("Void Crew.exe")]
    // Soft: loaded first when present, and the framework runs without it. Everything the
    // framework does with VoidManager is in VoidManagerBridge.
    [BepInDependency(VoidManagerBridge.Guid, BepInDependency.DependencyFlags.SoftDependency)]
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

            try { VoidManagerBridge.Install(new Harmony(EctypePluginInfo.PLUGIN_GUID)); }
            catch (Exception e) { Log.LogError("Ectype: VoidManager bridge failed: " + e); }

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

            // The framework's message types. The driver subscribes the receive path on its
            // first frame, before any room can be joined.
            EctypeNet.Register(EctypeNet.AbilityActive, "ability-active",
                               AbilityActiveMessage.Receive);
            EctypeNet.Register(EctypeNet.EnemyStatus, "enemy-status",
                               EnemyStatusMessage.Receive);
            EctypeNet.Register(EctypeNet.EnemyStatusStacks, "enemy-status-applyN",
                               EnemyStatusMessage.ReceiveStacks);
            EctypeNet.Register(EctypeNet.CharacterStatusRequest, "character-status-request",
                               CharacterStatusMessage.ReceiveRequest);
            EctypeNet.Register(EctypeNet.CharacterStatusApplied, "character-status-applied",
                               CharacterStatusMessage.ReceiveApplied);

            // Resolved and patched gate by gate, so a renamed game member costs only the
            // system that needs it.
            try { EnemyAbilities.ApplyPatches(harmony); }
            catch (Exception e) { Log.LogError("Ectype: enemy ability gates failed: " + e); }
            try { LootTables.ApplyPatch(harmony); }
            catch (Exception e) { Log.LogError("Ectype: loot table patch failed: " + e); }
            try { HowToHints.ApplyPatches(harmony); }
            catch (Exception e) { Log.LogError("Ectype: how-to hint patches failed: " + e); }
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
    /// The F5 page's content. VoidManagerBridge hands these two methods to the settings menu
    /// it builds when VoidManager is loaded; nothing else calls them.
    /// </summary>
    internal static class SettingsPage
    {
        internal static string Name() { return EctypePluginInfo.USERS_PLUGIN_NAME; }

        internal static void Draw()
        {
            GUILayout.Label("Pipeline: " + EctypeAssets.GlobalStatus);
            GUILayout.Label("Stats  : " + SafeStatCount() + " vanilla aliases, " +
                            "framework-owned " + FrameworkStats.Status);
            GUILayout.Label("Suppress: " + Suppression.Status);
            GUILayout.Label("Network : event " + EctypeNet.EventCode + ", contract " +
                            EctypePluginInfo.NET_CONTRACT_VERSION + ", " +
                            ContractMarker.Status);
            GUILayout.Space(8f);

            if (RoleRegistry.Roles.Count == 0)
            {
                GUILayout.Label("No role manifests discovered" +
                                (RoleRegistry.Discovered ? "." : " yet."));
            }
            foreach (RoleRecord rec in RoleRegistry.Roles)
            {
                GUILayout.Label(rec.Manifest.roleName + "  (tree " + rec.Ordinal + ", " +
                                rec.SourceAssembly.GetName().Name + ")");
                GUILayout.Label("   " + rec.Status);
            }
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
            Step("dev self-test", DevSelfTest.Poll);
            Step("status self-test", StatusSelfTest.Poll);
            Step("character status self-test", CharacterStatusSelfTest.Poll);
#endif
            Step("net", EctypeNet.Poll);
            Step("presence", PeerPresence.Publish);
            Step("topology", Topology.Poll);
            Step("characterShip", CharacterShipRoute.Poll);
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
