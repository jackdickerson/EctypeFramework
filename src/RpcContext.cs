using System;
using System.Reflection;

using CG.GameLoopStateMachine;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;

namespace Ectype
{
    /// <summary>What kind of Photon message <see cref="RpcContext"/> is reporting.</summary>
    public enum RpcSource
    {
        /// <summary>No RPC or event is being dispatched on this call stack (or the hooks are
        /// not in yet).</summary>
        None = 0,
        /// <summary>A PUN RPC: from the network, or a player's RPC to themselves.</summary>
        Rpc,
        /// <summary>A Photon event: one a mod or the game raised, or PUN's own (ownership
        /// events among them).</summary>
        Event,
    }

    /// <summary>
    /// Who sent the Photon RPC or event being dispatched right now. Most of the game's request
    /// RPCs take no message info, so the method cannot tell who asked; a check inside one reads
    /// the sender here.
    ///
    /// PUN runs every RPC, from the network or a player's RPC to themselves, inside one internal
    /// method that is handed the sender Photon's server stamped on the event, and calls the RPC
    /// method before it returns. Every event a client receives, RPCs and PUN's ownership events
    /// included, is dispatched inside one method of the client's Photon connection, which hands
    /// it to PUN and to every event listener before it returns. A prefix and finalizer on each
    /// record the message for the length of the call, restoring the outer one after a message
    /// dispatched inside another, so the innermost message is the one reported.
    ///
    /// The hooks go on once the game has loaded, after <see cref="Enable"/> was called or an
    /// interaction gate was registered; never at plugin load. Until then every member reports no
    /// message.
    /// </summary>
    public static class RpcContext
    {
        /// <summary>PUN's key for the target view id in an RPC's data.</summary>
        private static readonly object ViewKey = (byte)0;
        /// <summary>PUN's RPC event code.</summary>
        private const byte RpcEvent = 200;

        internal struct Frame
        {
            internal RpcSource Source;
            internal Photon.Realtime.Player Sender;
            internal int Actor;
            internal int View;
            internal byte Code;
            internal EventData Event;
        }

        private static Frame _current;
        private static Harmony _harmony;
        private static bool _requested;
        private static bool _patched;
        private static bool _readEarly;

        /// <summary>
        /// Asks for the hooks. Call it from the plugin's <c>Awake</c>, on every client that reads
        /// the sender; the hooks go on once the game has loaded. Idempotent. Registering an
        /// interaction gate asks for them too. Reading a member without having called it asks
        /// for them from that read on, and logs one warning, since a message dispatched before
        /// the hooks went on has no sender here.
        /// </summary>
        public static void Enable()
        {
            _requested = true;
        }

        /// <summary>Whether the hooks are in. While false, every member reports no
        /// message.</summary>
        public static bool Installed
        {
            get { return _patched; }
        }

        /// <summary>The kind of the innermost message being dispatched now, or
        /// <see cref="RpcSource.None"/>.</summary>
        public static RpcSource Source
        {
            get { Touch(); return _current.Source; }
        }

        /// <summary>The player who sent the innermost RPC or event being dispatched now. Null
        /// outside one, for a message whose sender is no longer in the room (a buffered RPC or
        /// cached event replayed after its sender left), and for one the server sent.</summary>
        public static Photon.Realtime.Player Sender
        {
            get { Touch(); return _current.Sender; }
        }

        /// <summary>The actor number Photon stamped on the innermost message being dispatched now,
        /// also for a sender who has since left the room. 0 outside one, and for a message
        /// that carries no sender.</summary>
        public static int SenderActor
        {
            get { Touch(); return _current.Actor; }
        }

        /// <summary>The view id an RPC being dispatched now targets; 0 for an event and outside
        /// a message.</summary>
        public static int ViewId
        {
            get { Touch(); return _current.Source == RpcSource.Rpc ? _current.View : 0; }
        }

        /// <summary>The code of the event being dispatched now; 0 for an RPC and outside a
        /// message. Test <see cref="Source"/> before trusting a 0.</summary>
        public static byte EventCode
        {
            get { Touch(); return _current.Source == RpcSource.Event ? _current.Code : (byte)0; }
        }

        /// <summary>
        /// The sender of the RPC running now on this view. False outside an RPC, for an RPC on
        /// another view, for an RPC whose sender is no longer in the room, and before the hooks are
        /// in. The view test keeps a method a handler calls directly on another object from being
        /// taken for an RPC.
        /// </summary>
        public static bool TryGetSender(PhotonView view, out Photon.Realtime.Player sender)
        {
            sender = null;
            Touch();
            Frame f = _current;
            if (f.Source != RpcSource.Rpc || f.Sender == null || view == null || f.View == 0 ||
                f.View != view.ViewID) return false;
            sender = f.Sender;
            return true;
        }

        /// <summary>
        /// Whether the RPC running now on this view came from a player who is no longer in the
        /// room: a buffered RPC PUN replays to a later joiner after its sender left. False outside
        /// such an RPC, and before the hooks are in.
        /// </summary>
        public static bool FromAbsentPlayer(PhotonView view)
        {
            Touch();
            Frame f = _current;
            return f.Source == RpcSource.Rpc && f.Sender == null && view != null && f.View != 0 &&
                   f.View == view.ViewID;
        }

        /// <summary>The event being dispatched now, or null (the gate's socket requests).</summary>
        internal static EventData CurrentEvent
        {
            get { return _current.Source == RpcSource.Event ? _current.Event : null; }
        }

        private static void Touch()
        {
            // The interaction gate reads senders only while a gate is registered, which asks for
            // the hooks by itself.
            if (_requested || _patched || InteractionGate.AnyRegistered) return;
            _requested = true;
            if (_readEarly) return;
            _readEarly = true;
            Plugin.Log.LogWarning("Ectype: RPC context: read before RpcContext.Enable() was called; the " +
                                  "hooks go on once the game has loaded, and a message dispatched before then " +
                                  "has no sender.");
        }

        // ---- hooks --------------------------------------------------------------------------

        /// <summary>At plugin load: keeps the Harmony instance. Patches nothing.</summary>
        internal static void Init(Harmony harmony)
        {
            _harmony = harmony;
        }

        /// <summary>Driver step: patches the hooks once, when they were asked for and the game's
        /// state machine has left its first state and the loading screen.</summary>
        internal static void Poll()
        {
            if (_patched || _harmony == null) return;
            // A registered gate asks too: its owner-side checks read senders here.
            if (!_requested && !InteractionGate.AnyRegistered) return;
            if (!GameLoaded()) return;
            _patched = true;
            int ok = 0;
            if (Patch(typeof(PhotonNetwork), "ExecuteRpc", "RpcPrefix", "RpcFinalizer",
                      "an RPC's sender is unknown; owner-side power, panel and synced-switch checks let everything through"))
                ok++;
            if (Patch(typeof(LoadBalancingClient), "OnEvent", "EventPrefix", "EventFinalizer",
                      "an event's sender is unknown; owner-side socket checks let everything through"))
                ok++;
            Plugin.Log.LogInfo("Ectype: RPC context: " + ok + " of 2 Photon hooks patched after the game loaded.");
        }

        /// <summary>Whether the game's state machine has left its first state and the loading
        /// screen: the earliest point at which patching Photon leaves the game's own connection
        /// set-up alone.</summary>
        internal static bool GameLoaded()
        {
            try
            {
                GameStateMachine machine = GameStateMachine.Instance;
                if (machine == null || machine.CurrentState == null) return false;
                // Compared by name, so a renamed state cannot break the check.
                string state = machine.CurrentState.GetType().Name;
                return state != "GSNull" && state != "GSLoading";
            }
            catch (Exception) { return false; }
        }

        private static bool Patch(Type type, string method, string prefix, string finalizer, string consequence)
        {
            try
            {
                MethodInfo target = AccessTools.DeclaredMethod(type, method);
                if (target == null)
                {
                    Plugin.Log.LogWarning("Ectype: RPC context: " + type.Name + "." + method +
                                          " not found (Photon update?); " + consequence + ".");
                    return false;
                }
                _harmony.Patch(target, prefix: new HarmonyMethod(typeof(RpcContext), prefix),
                               finalizer: new HarmonyMethod(typeof(RpcContext), finalizer));
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: RPC context: " + type.Name + "." + method +
                                      " could not be patched; " + consequence + ": " + e.Message);
                return false;
            }
        }

        private static void RpcPrefix(Hashtable rpcData, Photon.Realtime.Player sender, out Frame __state)
        {
            __state = _current;
            try
            {
                object view = null;
                if (rpcData != null) rpcData.TryGetValue(ViewKey, out view);
                // A network RPC arrives inside PUN's RPC event, which carries the actor number
                // even when the sender has left the room.
                int actor = sender != null ? sender.ActorNumber
                    : __state.Source == RpcSource.Event && __state.Code == RpcEvent ? __state.Actor : 0;
                _current = new Frame
                {
                    Source = RpcSource.Rpc, Sender = sender, Actor = actor, View = view is int ? (int)view : 0,
                };
            }
            catch (Exception e)
            {
                _current = default(Frame);
                LogHookFailure("PhotonNetwork.ExecuteRpc", e);
            }
        }

        private static void RpcFinalizer(Frame __state)
        {
            _current = __state;
        }

        private static void EventPrefix(EventData photonEvent, out Frame __state)
        {
            __state = _current;
            try
            {
                if (photonEvent == null)
                {
                    _current = default(Frame);
                    return;
                }
                int actor = photonEvent.Sender > 0 ? photonEvent.Sender : 0;
                Photon.Realtime.Player player = null;
                Room room = actor > 0 && PhotonNetwork.NetworkingClient != null
                    ? PhotonNetwork.NetworkingClient.CurrentRoom : null;
                if (room != null) player = room.GetPlayer(actor);
                _current = new Frame
                {
                    Source = RpcSource.Event, Sender = player, Actor = actor, Code = photonEvent.Code,
                    Event = photonEvent,
                };
            }
            catch (Exception e)
            {
                _current = default(Frame);
                LogHookFailure("LoadBalancingClient.OnEvent", e);
            }
        }

        private static void EventFinalizer(Frame __state)
        {
            _current = __state;
        }

        private static bool _hookFailed;

        private static void LogHookFailure(string hook, Exception e)
        {
            if (_hookFailed) return;
            _hookFailed = true;
            Plugin.Log.LogError("Ectype: RPC context: " + hook + " hook failed (logged once); that message has " +
                                "no sender: " + e);
        }
    }
}
