using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

using BepInEx;
using BepInEx.Logging;
using CG.Game;
using CG.Game.Player;
using Gameplay.Utilities;
using UnityEngine;

using VoidManager;
using VoidManager.MPModChecks;

namespace ExampleModStats
{
    /// <summary>
    /// Declares three stats in an embedded stats file and reads them, plus two framework factors,
    /// by id with the game's own API. Nothing here references the framework: the ids come from the
    /// documented formula, and a stat the framework has not registered reads as this mod's own
    /// constant.
    /// </summary>
    [BepInPlugin(PLUGIN_GUID, "Example Mod Stats", PLUGIN_VERSION)]
    [BepInProcess("Void Crew.exe")]
    [BepInDependency(VoidManager.MyPluginInfo.PLUGIN_GUID)]
    public class Plugin : BaseUnityPlugin
    {
#error Replace every REPLACE_ME (the plugin GUID) with your own unique value, then delete this line.
        public const string PLUGIN_GUID = "REPLACE_ME";
        // Keep equal to <Version> in the csproj.
        public const string PLUGIN_VERSION = "0.1.0";

        // The owner in stats\example.ectype-stats.json, and the framework's own owner.
        private const string Owner = "example.modstats";
        private const string FrameworkOwner = "ectype";

        // This mod's constants: the base when the stat exists, the value when it does not.
        private const float HackSeconds = 60f;
        private const float SupplyDropSeconds = 300f;
        private const float PunchDamage = 0.2f;

        private static readonly int HackSecondsId = StatId(Owner, "HackSeconds");
        private static readonly int HackResistanceId = StatId(Owner, "HackResistance");
        private static readonly int SupplyDropIntervalId = StatId(Owner, "SupplyDropInterval");
        private static readonly int MeleeDamageId = StatId(FrameworkOwner, "MeleeDamage");
        private static readonly int HostileSpawnScaleId = StatId(FrameworkOwner, "HostileSpawnScale");

        internal static ManualLogSource Log;
        private float _nextRead;
        private string _last;

        private void Awake()
        {
            Log = Logger;
            // Compare these with the framework's "mod stats: 3 declared" line: the same numbers.
            Log.LogInfo("Example Mod Stats v" + PLUGIN_VERSION + " loaded; ids " +
                        Owner + "/HackSeconds=" + HackSecondsId + ", " +
                        Owner + "/HackResistance=" + HackResistanceId + ", " +
                        Owner + "/SupplyDropInterval=" + SupplyDropIntervalId + ", " +
                        FrameworkOwner + "/MeleeDamage=" + MeleeDamageId + ", " +
                        FrameworkOwner + "/HostileSpawnScale=" + HostileSpawnScaleId + ".");
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextRead) return;
            _nextRead = Time.unscaledTime + 5f;
            try
            {
                StatCollection character = LocalPlayer.Instance != null ? LocalPlayer.Instance.Stats : null;
                StatCollection ship = OwnShipStats();
                StatCollection session = SessionCollection();

                float hack = Read(character, HackSecondsId, HackSeconds) * Read(ship, HackResistanceId, 1f);
                float drop = SupplyDropSeconds * Read(session, SupplyDropIntervalId, 1f);
                float punch = PunchDamage * Read(character, MeleeDamageId, 1f);
                float spawn = Read(session, HostileSpawnScaleId, 1f);

                string line = "hack " + hack.ToString("0.##") + " s, supply drop every " + drop.ToString("0.##") +
                              " s, punch " + punch.ToString("0.###") + ", hostile spawn scale " +
                              spawn.ToString("0.##") + " (character " + Has(character, HackSecondsId) +
                              ", ship " + Has(ship, HackResistanceId) + ", session " +
                              Has(session, SupplyDropIntervalId) + ")";
                if (line == _last) return;
                _last = line;
                Log.LogInfo(line + ".");
            }
            catch (Exception e)
            {
                Log.LogError("Reading stats failed: " + e);
            }
        }

        private static MethodInfo _crewShipOf;
        private static bool _probed;

        /// <summary>
        /// The local player's own crew ship. In a room with several player ships the game's
        /// PlayerShip is whichever ship this client views, so the framework's Topology.CrewShipOf
        /// is asked first, bound by reflection so the mod still loads without the framework; the
        /// game's getter is the fallback, which is right in a one-ship room.
        /// </summary>
        private static StatCollection OwnShipStats()
        {
            LocalPlayer me = LocalPlayer.Instance;
            if (!_probed)
            {
                _probed = true;
                Type topology = Type.GetType("Ectype.Topology, EctypeFramework");
                if (topology != null)
                    _crewShipOf = topology.GetMethod("CrewShipOf", BindingFlags.Public | BindingFlags.Static,
                                                     null, new[] { typeof(Player) }, null);
            }
            if (me != null && _crewShipOf != null)
            {
                IStatCollectionHolder own = _crewShipOf.Invoke(null, new object[] { me }) as IStatCollectionHolder;
                if (own != null) return own.Stats;
            }
            return ClientGame.Current != null && ClientGame.Current.PlayerShip != null
                ? ClientGame.Current.PlayerShip.Stats : null;
        }

        /// <summary>The framework's session collection, found by name and the game's holder
        /// interface; null outside a session or without the framework.</summary>
        private static StatCollection SessionCollection()
        {
            GameObject go = GameObject.Find("Ectype.SessionStats");
            IStatCollectionHolder holder = go != null ? go.GetComponent<IStatCollectionHolder>() : null;
            return holder != null ? holder.Stats : null;
        }

        /// <summary>
        /// 0x20000000 | (the first four bytes of MD5(UTF-8 "ectype:stat:" + owner + "/" + name),
        /// little-endian, &amp; 0x0FFFFFFF). Case-sensitive.
        /// </summary>
        private static int StatId(string owner, string name)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] h = md5.ComputeHash(Encoding.UTF8.GetBytes("ectype:stat:" + owner + "/" + name));
                return 0x20000000 | (BitConverter.ToInt32(h, 0) & 0x0FFFFFFF);
            }
        }

        private static float Read(StatCollection c, int id, float fallback)
        {
            StatBase s;
            if (c == null || c.Stats == null || !c.Stats.TryGetValue(id, out s)) return fallback;
            ModifiableFloat f = s.Data as ModifiableFloat;
            return f != null ? f.Value : fallback;
        }

        private static string Has(StatCollection c, int id)
        {
            if (c == null || c.Stats == null) return "absent";
            return c.Stats.ContainsKey(id) ? "registered" : "not registered";
        }
    }

    /// <summary>All: a perk on one client that names a declared stat needs the stat declared on
    /// every client, so every player in the room runs the mod.</summary>
    public class ExampleVoidPlugin : VoidPlugin
    {
        public override MultiplayerType MPType { get { return MultiplayerType.All; } }
        public override string Author { get { return "Example Author"; } }
        public override string Description { get { return "Declares stats in a data file and reads them by id."; } }
    }
}
