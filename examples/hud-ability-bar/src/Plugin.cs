using System;

using BepInEx;
using BepInEx.Logging;
using Ectype;
using UnityEngine;
using UnityEngine.InputSystem;

using VoidManager;
using VoidManager.MPModChecks;

namespace ExampleHudBar
{
    /// <summary>
    /// Adds one button to the row beside the class-ability widget, through Ectype.HudAbilityBar.
    /// The bar draws the button and its cooldown fill; this mod owns the key and the cooldown.
    /// F9 logs a line and starts a five second cooldown.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Hud Bar", PLUGIN_VERSION)]
    [BepInProcess("Void Crew.exe")]
    [BepInDependency(VoidManager.MyPluginInfo.PLUGIN_GUID)]
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
                // Safe before the HUD exists. Returns null when the bar is full (4 buttons),
                // the id is taken or the id is empty.
                _button = HudAbilityBar.Register(new HudButtonSpec
                {
                    Id = PLUGIN_GUID + ".ping",        // unique: namespace it with the GUID
                    Glyph = MakeGlyph(),               // square, white on transparent
                    ReadyColor = new Color(0.3f, 0.8f, 1f),
                    Order = 0,
                    Key = () => Key.F9,                // the key chip text; read every frame
                    Fill = () => Mathf.Clamp01(1f - (Time.time - _usedAt) / CooldownSeconds),
                    Visible = () => true,
                });
                if (_button == null) Log.LogWarning("The HUD bar refused the button.");
                Log.LogInfo("Example Hud Bar v" + PLUGIN_VERSION + " loaded.");
            }
            catch (Exception e)
            {
                Log.LogError("Example Hud Bar failed to start: " + e);
            }
        }

        private const float CooldownSeconds = 5f;

        private HudAbilityBar.Handle _button;
        private float _usedAt = -1000f;

        private void Update()
        {
            try
            {
                // The bar draws a key chip but reads no input. The mod does.
                Keyboard kb = Keyboard.current;
                if (kb == null || !kb[Key.F9].wasPressedThisFrame) return;
                if (Time.time - _usedAt < CooldownSeconds) return;

                _usedAt = Time.time;
                Log.LogInfo("Ping pressed.");
            }
            catch (Exception e)
            {
                Log.LogError("Update failed: " + e);
            }
        }

        private void OnDestroy()
        {
            if (_button != null) _button.Dispose();   // removes the button
        }

        /// <summary>A white disc on transparent. Any readable texture works; the bar clears
        /// the corners that would reach past the button's chamfer.</summary>
        private static Texture2D MakeGlyph()
        {
            const int n = 128;
            Texture2D t = new Texture2D(n, n, TextureFormat.RGBA32, false);
            Color[] px = new Color[n * n];
            float r = n * 0.35f, c = (n - 1) / 2f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(r - d + 0.5f));
                }
            t.SetPixels(px);
            t.Apply();
            return t;
        }
    }

    /// <summary>Client: the bar is presentation only. Nothing is networked or registered, so
    /// it cannot flag a session.</summary>
    public class ExampleVoidPlugin : VoidPlugin
    {
        public override MultiplayerType MPType { get { return MultiplayerType.Client; } }
        public override string Author { get { return "Example Author"; } }
        public override string Description { get { return "Adds a HUD button that logs when its key is pressed."; } }
    }
}
