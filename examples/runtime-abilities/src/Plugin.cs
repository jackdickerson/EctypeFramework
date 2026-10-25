using System;

using BepInEx;
using BepInEx.Logging;
using Ectype;
using CG.Game;
using CG.Game.Player;
using Opsive.UltimateCharacterController.Character;
using Opsive.UltimateCharacterController.Character.Abilities;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ExampleRuntimeAbility
{
    /// <summary>
    /// Installs a small Opsive ability on every character at runtime and registers it with
    /// Ectype.RuntimeAbilities, so its start and stop replicate to the other clients by key
    /// instead of by array position. F8 starts it on the local character; every client logs the
    /// start and stop for that character.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Runtime Ability", PLUGIN_VERSION)]
    [BepInProcess("Void Crew.exe")]
    [BepInDependency("jack.voidcrew.ectype")]
    public class Plugin : BaseUnityPlugin
    {
#error Replace every REPLACE_ME (the plugin GUID) with your own unique value, then delete this line.
        public const string PLUGIN_GUID = "REPLACE_ME";
        // Keep equal to <Version> in the csproj.
        public const string PLUGIN_VERSION = "0.1.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            try
            {
                // Nothing to do here: characters spawn later, so installation is polled.
                Log.LogInfo("Example Runtime Ability v" + PLUGIN_VERSION + " loaded.");
            }
            catch (Exception e)
            {
                Log.LogError("Example Runtime Ability failed to start: " + e);
            }
        }

        // The key crosses the network. Use a literal, namespaced by this mod, never a value
        // derived from runtime state: it must be identical on every client.
        private const string AbilityKey = PLUGIN_GUID + ".ping";

        private void Update()
        {
            try
            {
                ClientGame game = ClientGame.Current;
                if (game == null) return;

                // Every client installs the ability on every character, remote ones too: a
                // peer that has no ability under the key ignores the message.
                foreach (Player p in game.Players)
                    if (p != null && p.Locomotion != null) Install(p.Locomotion);

                Keyboard kb = Keyboard.current;
                if (kb == null || !kb[Key.F8].wasPressedThisFrame) return;
                Player me = LocalPlayer.I;
                if (me == null || me.Locomotion == null) return;
                PingAbility ability = me.Locomotion.GetAbility<PingAbility>();
                if (ability != null) me.Locomotion.TryStartAbility(ability);
            }
            catch (Exception e)
            {
                Log.LogError("Update failed: " + e);
            }
        }

        private static void Install(UltimateCharacterLocomotion loco)
        {
            if (loco.GetAbility<PingAbility>() != null) return;

            PingAbility ability = new PingAbility();
            Ability[] old = loco.Abilities ?? new Ability[0];
            Ability[] grown = new Ability[old.Length + 1];
            Array.Copy(old, grown, old.Length);
            grown[old.Length] = ability;
            loco.Abilities = grown;      // the setter initialises the new ability

            // Register after the ability is in the array. From here its start and stop
            // travel on a keyed message rather than the game's index-based RPC.
            RuntimeAbilities.Register(ability, AbilityKey);
            ability.Start();             // manual: Unity only starts prefab abilities
        }
    }

    /// <summary>An ability that is active for two seconds when started. It does nothing but
    /// log, so the replication is visible: the log line appears on every client.</summary>
    public class PingAbility : Ability
    {
        private float _startedAt;

        public PingAbility()
        {
            m_StartType = AbilityStartType.Manual;
            m_StopType = AbilityStopType.Manual;
        }

        protected override void AbilityStarted()
        {
            base.AbilityStarted();
            _startedAt = Time.time;
            Plugin.Log.LogInfo("Ping started on " + m_GameObject.name);
        }

        public override void Update()
        {
            if (Time.time - _startedAt > 2f) StopAbility();
        }

        protected override void AbilityStopped(bool force)
        {
            base.AbilityStopped(force);
            Plugin.Log.LogInfo("Ping stopped on " + m_GameObject.name);
        }
    }
}
