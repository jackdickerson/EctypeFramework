using System;
using System.Collections.Generic;

using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;

namespace Ectype
{
    /// <summary>
    /// The framework's own message channel: one Photon event code, whose payload is
    /// <c>object[] { byte subtype, arg0, arg1, ... }</c>. Each subtype has one handler,
    /// registered once at plugin load; the handler receives the sender's actor number and the
    /// arguments after the subtype, so a message's field layout is exactly the array its
    /// sender built. Nothing here depends on another mod.
    ///
    /// Event code survey (a room delivers every raised event to every client's handler, so a
    /// code shared with another mod reaches that mod's handler too):
    ///  * 0-10: the game's own events (NetworkEvent, including 7 and 8, the missing-asset
    ///    request and reply).
    ///  * 95-99: VoidManager (95 progression block, 96 session escalation, 97 kick, 98 mod
    ///    messages, 99 mod lists).
    ///  * 155-160: Opsive's PUN add-on.
    ///  * 173: Void Crew Fleet.
    ///  * 200 and above: Photon's reserved codes (RPC 200, serialization 201 and 206,
    ///    instantiation 202, ownership 209 and 210, and the rest).
    /// 181 sits in none of those ranges.
    ///
    /// Sending goes through <c>PhotonNetwork.RaiseEvent</c> with <c>TargetActors</c>, so a
    /// player outside the recipient list never receives the event. Receiving goes through
    /// <c>LoadBalancingClient.EventReceived</c> on <c>PhotonNetwork.NetworkingClient</c>. PUN
    /// creates that client once, in <c>PhotonNetwork</c>'s static constructor, and keeps it
    /// across disconnects and rooms; <see cref="Poll"/> still compares the instance every
    /// frame and moves the subscription if it is ever replaced.
    ///
    /// Bump <see cref="EctypePluginInfo.NET_CONTRACT_VERSION"/> whenever a subtype is added,
    /// removed or renumbered, or a message's field layout changes.
    /// </summary>
    internal static class EctypeNet
    {
        /// <summary>The framework's Photon event code. See the class remarks for the survey.</summary>
        internal const byte EventCode = 181;

        /// <summary>Runtime ability start and stop: [int viewId, string key, bool active,
        /// object[] startData, object[] extendedStartData, bool lateJoin].</summary>
        internal const byte AbilityActive = 1;

        /// <summary>Enemy status apply and remove: [byte op, int viewId, string key, ...]
        /// (op 0 adds int startTs, int durationMs, float magnitude, int attackerView).</summary>
        internal const byte EnemyStatus = 2;

        private sealed class Channel
        {
            internal string Name;
            internal Action<int, object[]> Handler;
        }

        private static readonly Dictionary<byte, Channel> Channels = new Dictionary<byte, Channel>();
        private static readonly HashSet<string> Warned = new HashSet<string>();
        private static LoadBalancingClient _subscribed;

        /// <summary>
        /// Routes one subtype to a handler. Called once per subtype at plugin load; a second
        /// registration of the same subtype replaces the first and logs it.
        /// </summary>
        internal static void Register(byte subtype, string name, Action<int, object[]> handler)
        {
            if (handler == null) throw new ArgumentNullException("handler");
            if (Channels.ContainsKey(subtype))
                Plugin.Log.LogWarning("Ectype: net subtype " + subtype + " registered twice; '" +
                                      name + "' replaces '" + Channels[subtype].Name + "'.");
            Channels[subtype] = new Channel { Name = name ?? subtype.ToString(), Handler = handler };
        }

        /// <summary>
        /// Sends one message to the listed players. A null or empty list sends nothing. The
        /// arguments must be types Photon serializes (int, float, bool, byte, string, and
        /// arrays of them, nested <c>object[]</c> included).
        /// </summary>
        internal static void Send(byte subtype, Player[] to, object[] args, bool reliable)
        {
            if (to == null || to.Length == 0) return;
            List<int> actors = new List<int>(to.Length);
            foreach (Player p in to)
                if (p != null) actors.Add(p.ActorNumber);
            if (actors.Count == 0) return;

            object[] payload = Pack(subtype, args);
            PhotonNetwork.RaiseEvent(EventCode, payload,
                                     new RaiseEventOptions { TargetActors = actors.ToArray() },
                                     reliable ? SendOptions.SendReliable : SendOptions.SendUnreliable);
            if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: net send " + NameOf(subtype) + " (subtype " + subtype +
                                   ") to actor(s) " + string.Join(",", actors) + ".");
        }

        /// <summary>The wire payload for one message: the subtype, then the arguments.</summary>
        internal static object[] Pack(byte subtype, object[] args)
        {
            int n = args != null ? args.Length : 0;
            object[] payload = new object[n + 1];
            payload[0] = subtype;
            if (n > 0) Array.Copy(args, 0, payload, 1, n);
            return payload;
        }

        /// <summary>
        /// Driver step: keeps the receive subscription on the live client. Cheap when nothing
        /// changed: one reference comparison.
        /// </summary>
        internal static void Poll()
        {
            LoadBalancingClient client = PhotonNetwork.NetworkingClient;
            if (client == _subscribed) return;
            if (_subscribed != null) _subscribed.EventReceived -= OnEvent;
            if (client != null) client.EventReceived += OnEvent;
            _subscribed = client;
            if (client != null)
                Plugin.Log.LogInfo("Ectype: listening on Photon event " + EventCode + " (" +
                                   Channels.Count + " message type(s), network contract " +
                                   EctypePluginInfo.NET_CONTRACT_VERSION + ").");
        }

        private static void OnEvent(EventData ev)
        {
            if (ev == null || ev.Code != EventCode) return;
            try { Dispatch(ev.Sender, ev.CustomData); }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: handling net event from actor " + ev.Sender +
                                    " failed: " + e);
            }
        }

        /// <summary>
        /// Splits a received payload into subtype and arguments and calls the subtype's
        /// handler. The receive path and the dev build's serializer self-test both enter here.
        /// </summary>
        internal static void Dispatch(int senderActor, object customData)
        {
            object[] payload = customData as object[];
            if (payload == null || payload.Length == 0 || !(payload[0] is byte))
            {
                if (Warned.Add("malformed"))
                    Plugin.Log.LogWarning("Ectype: ignored a malformed net event from actor " +
                                          senderActor + " (no subtype byte).");
                return;
            }
            byte subtype = (byte)payload[0];
            Channel channel;
            if (!Channels.TryGetValue(subtype, out channel))
            {
                if (Warned.Add("subtype:" + subtype))
                    Plugin.Log.LogWarning("Ectype: ignored net subtype " + subtype + " from actor " +
                                          senderActor + ": this build has no handler for it " +
                                          "(a different network contract?).");
                return;
            }
            object[] args = new object[payload.Length - 1];
            Array.Copy(payload, 1, args, 0, args.Length);
            if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: net recv " + channel.Name + " (subtype " + subtype +
                                   ") from actor " + senderActor + ".");
            channel.Handler(senderActor, args);
        }

        private static string NameOf(byte subtype)
        {
            Channel channel;
            return Channels.TryGetValue(subtype, out channel) ? channel.Name : "unregistered";
        }
    }
}
