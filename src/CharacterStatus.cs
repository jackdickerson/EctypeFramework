using System;
using System.Collections.Generic;

using CG.Game.Player;
using CG.Space;
using Gameplay.Tags;
using Gameplay.Utilities;

using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// Timed statuses (buffs/debuffs) on a player character - the counterpart to
    /// EnemyStatuses, which only targets CG.Space.OrbitObject. Not role-specific: a mod
    /// registers a template once at Awake, then applies it to any Player for a duration;
    /// the framework drives the timer and removes everything the status added on expiry.
    ///
    /// Built from the game's own machinery:
    ///  * Stat changes are StatMods applied to the character's StatTagCollection via
    ///    ApplyModifiers(mods, source, true) and removed with RemoveModifier(source), the
    ///    same pair a vanilla channel role ability uses on the same collection. Removal
    ///    is by source, so expiry cannot leak a modifier.
    ///  * Fresh StatMod instances per application: applying overwrites a modifier's
    ///    Source and removal matches on it, so one instance can never serve two targets
    ///    or two stacks.
    ///  * The character's stat surface is whatever Player.Awake registered, plus the
    ///    framework's own locomotion knobs (CharacterStats.Wire). A StatMod naming a stat
    ///    the character never registered is a silent no-op in game code, so this layer
    ///    logs one warning per (status, target type) when nothing matched.
    ///
    /// THE STATUS LIVES ON THE CHARACTER'S OWN CLIENT. Every way a character takes damage
    /// resolves on that character's own client, so a vulnerability or resistance mod that
    /// exists only there is read by the code that matters:
    ///  * CustomCharacterHealth.CalculateRawDamage calls ApplyVulnerability in-process,
    ///    against the collection on the replica it runs on - there is no RPC inside
    ///    ApplyHitDamage, so authority is decided entirely by the call site.
    ///  * NPC damage abilities (cone, cylinder, AOE) gate their ApplyHitDamage on
    ///    Target.AmOwner - the victim's client.
    ///  * Environmental damage (void rifts, damage volumes, thruster wash, black holes,
    ///    oxygen and temperature attributes) reaches characters only through LocalPlayer.
    ///  * The one path that is not victim-side is a ship weapon's projectile impacting a
    ///    character, computed on the shooter's client; CalculateRawDamage zeroes
    ///    same-faction damage before vulnerability is read, so crew are unreachable
    ///    through it anyway.
    /// Movement is the same: the owner's client simulates its own character's locomotion.
    /// Apply runs where it is called. To put a status on another player's character,
    /// <see cref="CharacterStatuses.Request"/> asks that player's client, which judges the
    /// request against its own view of the requester and applies it locally
    /// (CharacterStatusSync). Other clients learn of it only through presentation copies,
    /// which carry no stats and run no callbacks.
    ///
    /// Timing is Time.time on the owning client, the clock the ability durations and
    /// cooldowns beside it run on. A presentation copy runs on the Photon server clock,
    /// converted once when the owner announces the status.
    /// </summary>
    public sealed class CharacterStatusDef
    {
        /// <summary>Identity. Namespace it with the owning mod ("myrole.dash-guard").
        /// Requests and presentation copies carry it, so every client must register the
        /// same literal.</summary>
        public readonly string Key;

        /// <summary>Shown by GetHeader to anything that renders modifier sources.</summary>
        public string DisplayName;

        /// <summary>OnTick slice length in seconds. Ticks are indexed against the status's
        /// own start time, so a frame hitch pays every missed slice rather than losing
        /// them, and driving the poll twice in a frame cannot double-tick.</summary>
        public float TickSeconds = 1f;

        /// <summary>How many times Apply can compound on one character. Each stack applies
        /// the stat entries once more.</summary>
        public int MaxStacks = 1;

        /// <summary>Re-applying an active status re-arms its timer (true) or leaves the
        /// original expiry (false). Stacking applies either way, up to MaxStacks.</summary>
        public bool RefreshOnReapply = true;

        /// <summary>Runs where the status lives (target, stack count) when it lands or gains
        /// a stack. Never on a presentation copy.</summary>
        public Action<Player, int> OnApplied;

        /// <summary>Runs once per elapsed tick (target, slice seconds), where the status
        /// lives. The place for bespoke per-tick work that is not a registered stat.</summary>
        public Action<Player, float> OnTick;

        /// <summary>Runs when the status ends, after its StatMods are removed and it is no longer
        /// active, so applying the same key again from here starts a fresh status. Target can
        /// be null if the character was destroyed first. On death it runs inside the game's own
        /// death handling, before the game marks the character's health dead, and it can run
        /// inside this status's own OnTick when that tick killed the character: it must not
        /// kill the character.</summary>
        public Action<Player> OnRemoved;

        /// <summary>Declares that this status should be visible on every client. It changes
        /// nothing: a status runs on the client that owns the character, and applying a
        /// template that sets it logs one warning naming the key.
        /// <see cref="AcceptsRequests"/> lets other clients request the status, and
        /// <see cref="Presented"/> shows it to them.</summary>
        public bool Replicated;

        // ---- requests from other clients (CharacterStatuses.Request) ----

        /// <summary>Accepts requests from other clients. Off (the default), the character's
        /// own client refuses every request for this key, and only that client can apply
        /// the status. The checks below judge requests only; they never apply to the owning
        /// client's own Apply.</summary>
        public bool AcceptsRequests;

        /// <summary>Which relation between the requester's character and the target the
        /// status accepts: Any (the default), Hostile (another side) or Friendly (the same
        /// side), by the game's own different-faction check.</summary>
        public StatusStance Stance = StatusStance.Any;

        /// <summary>Metres between the requester's character and the target, measured on the
        /// target's client. 0 (the default) checks no range. The requesting client checks a
        /// looser limit, so a request at the edge reaches the target's client to be judged.</summary>
        public float MaxRange;

        /// <summary>Refuse a request when level geometry or a space object blocks the line
        /// between the two characters' chests, cast on the target's client.</summary>
        public bool RequiresLineOfSight;

        /// <summary>Refuse a request from a player who is seated: a chair, the helm or a turret.</summary>
        public bool RefuseSeated;

        /// <summary>Refuse a request when the requester or the target is in a clone tube.</summary>
        public bool RefuseInTube;

        /// <summary>Seconds per request from one requester for this key, refilled one at a
        /// time. 0 (the default) checks no rate.</summary>
        public float MinInterval;

        /// <summary>Requests from one requester the requesting client sends back to back before
        /// MinInterval applies. The target's client accepts one more, for requests the network
        /// delivered closer together than they were sent. 1 or more.</summary>
        public int Burst = 1;

        /// <summary>Shortest duration a request may ask for; a shorter one is refused.</summary>
        public float MinSeconds = 0.25f;

        /// <summary>Longest duration a request may ask for; a longer one is cut to this.</summary>
        public float MaxSeconds = 30f;

        /// <summary>Largest magnitude a request may ask for; a larger one is cut to this. A
        /// magnitude of 0 or below is refused: it would turn a debuff into a buff, or pin the
        /// status at no effect.</summary>
        public float MaxMagnitude = 1f;

        // ---- presentation copies ----

        /// <summary>The owning client announces the status to the peers Visibility allows,
        /// and each runs a presentation copy for as long as it lasts: CreateVfx and the
        /// IsPresented query, with no stat changes and no callbacks.</summary>
        public bool Presented;

        /// <summary>Which peers receive presentation copies: All (the default), OwnForce (on
        /// the side of a player whose application was accepted), TargetForce (on the target's
        /// side) or HostileToTarget (on another side from the target). The owning client
        /// chooses the recipients, so a peer outside the audience never receives the
        /// message.</summary>
        public StatusVisibility Visibility = StatusVisibility.All;

        /// <summary>The visual of a presentation copy: return a fresh GameObject and the
        /// framework parents it to the character, at the character's origin (the feet), and
        /// destroys it when the copy ends. Not run on the owning client.</summary>
        public Func<Player, GameObject> CreateVfx;

        internal readonly List<StatusStatEntry> Entries = new List<StatusStatEntry>();


        /// <summary>Starts a definition. The key identifies the status and must not be empty.</summary>
        public CharacterStatusDef(string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("key is empty", "key");
            Key = key;
        }

        /// <summary>Add one stat modifier. Stat by alias - a vanilla StatType name
        /// ("KineticVulnerability"), a framework-owned stat ("ExternalForceDamping"), or a
        /// raw int id; modifier types as the manifest spells them: "percent" (+X of 1.0,
        /// multiplicative), "scalar" (xN), "flat", "flat2", or an exact ModifierType name.
        /// Amounts scale with Apply's magnitude.</summary>
        public CharacterStatusDef AddStat(string statAlias, string modifierType, float amount)
        {
            StatusStats.Add(Entries, statAlias, modifierType, amount, false);
            return this;
        }

        /// <summary>AddStat for an int-backed stat (vanilla ships no bool modifier; a
        /// conceptually-boolean stat is an int amount of 1).</summary>
        public CharacterStatusDef AddIntStat(string statAlias, string modifierType, int amount)
        {
            StatusStats.Add(Entries, statAlias, modifierType, amount, true);
            return this;
        }

        /// <summary>Every damage type at once, aggregate included. The aggregate alone is
        /// not enough: vanilla character perks modify the per-type stats, and the
        /// aggregate's read path on characters is unverified.</summary>
        public CharacterStatusDef AddAllVulnerabilities(string modifierType, float amount)
        {
            foreach (string alias in AllVulnerabilityAliases) AddStat(alias, modifierType, amount);
            return this;
        }

        internal static readonly string[] AllVulnerabilityAliases =
        {
            "Vulnerability", "KineticVulnerability", "EnergyVulnerability",
            "ElectricVulnerability", "FireVulnerability", "FreezingVulnerability",
            "PhysicalVulnerability", "RadiationVulnerability", "VoidVulnerability",
            "AsphyxiationVulnerability"
        };
    }

    /// <summary>
    /// The registry and the API. Register templates at plugin Awake; Apply/Request/Remove
    /// from anywhere on the main thread. See CharacterStatusDef for the mechanism and the
    /// rules.
    /// </summary>
    public static class CharacterStatuses
    {
        private static readonly Dictionary<string, CharacterStatusDef> Defs =
            new Dictionary<string, CharacterStatusDef>();

        // One live status per (character instance id, key).
        private static readonly Dictionary<string, CharacterStatus> Active =
            new Dictionary<string, CharacterStatus>();
        private static readonly List<CharacterStatus> DriveScratch = new List<CharacterStatus>();

        private static readonly HashSet<string> WarnedOnce = new HashSet<string>();

        /// <summary>Guarded and re-runnable: same key re-registers (last wins, named log)
        /// so a hot-reload cannot throw. A template whose request fields are out of range is
        /// refused with one error naming the field.</summary>
        public static void Register(CharacterStatusDef def)
        {
            if (def == null) { Plugin.Log.LogError("Ectype: Register(null character status)"); return; }
            string invalid = CharacterStatusSync.Validate(def);
            if (invalid != null)
            {
                Plugin.Log.LogError("Ectype: character status '" + def.Key + "' not registered: " +
                                    invalid + ".");
                return;
            }
            if (Defs.ContainsKey(def.Key))
                Plugin.Log.LogWarning("Ectype: character status '" + def.Key + "' " +
                                      "re-registered; replacing the previous template.");
            Defs[def.Key] = def;
            Plugin.Log.LogInfo("Ectype: character status '" + def.Key + "' registered (" +
                               def.Entries.Count + " stat mods, max stacks " + def.MaxStacks +
                               (def.AcceptsRequests ? ", accepts requests" : "") +
                               (def.Presented ? ", presented" : "") + ").");
            CharacterStatusSync.WarnUnusedFields(def);
        }

        /// <summary>
        /// Apply (or re-apply: refresh/stack per the template) a status to a character for
        /// a duration, on this client. Magnitude scales every stat amount. On a character this
        /// client does not own the status lands on this client's copy, which that player's
        /// damage and movement never read, and one warning per key says so: use
        /// <see cref="Request"/> for another player's character.
        /// </summary>
        public static void Apply(Player target, string key, float seconds, float magnitude = 1f)
        {
            try
            {
                CharacterStatusDef def;
                if (Preflight(target, key, out def) != CharacterStatusRefusal.None) return;
                if (!(seconds > 0f) || float.IsInfinity(seconds))
                {
                    Plugin.Log.LogWarning("Ectype: Apply('" + key + "') with duration " +
                                          seconds + "s ignored.");
                    return;
                }
                if (def.Replicated) CharacterStatusSync.WarnReplicated(def);
                if (!CharacterStatusSync.Alive(target))
                {
                    if (WarnedOnce.Add("dead:" + key))
                        Plugin.Log.LogWarning("Ectype: Apply('" + key + "') on " + target.name +
                                              " refused: the character is dead, and a status put on " +
                                              "it now would outlive the respawn (logged once per status).");
                    return;
                }
                if (target.photonView != null && !target.photonView.IsMine)
                    CharacterStatusSync.WarnNotOwned(def, target);
                ApplyLocal(target, def, seconds, magnitude, 0, null, CharacterStatusSync.LocalSide(), false);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: Apply('" + key + "') failed: " + e);
            }
        }

        /// <summary>
        /// Put a status on a character, wherever it is simulated. On this client's own
        /// character this is <see cref="Apply"/>. On another player's character it sends a
        /// request to that player's client, which checks the template's opt-in
        /// (<see cref="CharacterStatusDef.AcceptsRequests"/>), both characters, Stance,
        /// MaxRange, line of sight and rate against its own view, then applies the status there
        /// or refuses it, and answers. The same checks run here first, from this client's view,
        /// so a request that would be refused is not sent.
        ///
        /// Returns a handle. A request applied here or refused here is complete before this
        /// returns; a sent one completes when the target's client answers, or with
        /// <see cref="CharacterStatusRefusal.NoAnswer"/> after
        /// <see cref="CharacterStatusSync.AnswerTimeoutSeconds"/> seconds. Seconds and magnitude
        /// above the template's MaxSeconds and MaxMagnitude are cut to them. The attacker
        /// travels as a view id and is reported by <see cref="AttackerOf"/> on the target's
        /// client; it is informational and never judged.
        /// </summary>
        public static CharacterStatusRequest Request(Player target, string key, float seconds,
                                                     float magnitude = 1f, OrbitObject attacker = null)
        {
            CharacterStatusRequest handle = new CharacterStatusRequest(target, key);
            try
            {
                CharacterStatusDef def;
                CharacterStatusRefusal pre = Preflight(target, key, out def);
                if (pre != CharacterStatusRefusal.None) handle.Complete(pre);
                else CharacterStatusSync.Request(handle, def, seconds, magnitude, attacker);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: Request('" + key + "') failed: " + e);
                handle.Complete(CharacterStatusRefusal.Failed);
            }
            return handle;
        }

        /// <summary>Remove a status early. No-op when not active. Only the client that owns
        /// the character can end its status; on another player's character this removes only
        /// a copy this client's own Apply left, and otherwise logs one warning per key.</summary>
        public static void Remove(Player target, string key)
        {
            RemoveCore(target, key, true);
        }

        /// <summary>
        /// <see cref="Remove"/>, returning why nothing was removed:
        /// <see cref="CharacterStatusRefusal.Authority"/> for a status on another player's
        /// character (that player's client owns it, and no removal travels on the network),
        /// <see cref="CharacterStatusRefusal.InvalidTarget"/> for a null target, else
        /// <see cref="CharacterStatusRefusal.None"/>, also when the status was not active.
        /// </summary>
        public static CharacterStatusRefusal TryRemove(Player target, string key)
        {
            return RemoveCore(target, key, false);
        }

        private static CharacterStatusRefusal RemoveCore(Player target, string key, bool fireAndForget)
        {
            try
            {
                if (target == null) return CharacterStatusRefusal.InvalidTarget;
                CharacterStatus live;
                if (Active.TryGetValue(MapKey(target, key), out live))
                {
                    live.End();
                    return CharacterStatusRefusal.None;
                }
                if (target.photonView != null && !target.photonView.IsMine)
                {
                    string line = "Ectype: Remove('" + key + "') on " + target.name + " refused: " +
                                  "that player's client owns the character's statuses, and only " +
                                  "it can end one.";
                    if (fireAndForget && WarnedOnce.Add("remove-authority:" + key))
                        Plugin.Log.LogWarning(line + " Logged once per status; TryRemove returns the reason.");
                    else if (Plugin.Verbose)
                        Plugin.Log.LogInfo(line);
                    return CharacterStatusRefusal.Authority;
                }
                return CharacterStatusRefusal.None;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: Remove('" + key + "') failed: " + e);
                return CharacterStatusRefusal.Failed;
            }
        }

        /// <summary>Drop every status on a character. Call this when a character is about
        /// to stop being the thing the mods were meant for - a role switch, say. Death and
        /// despawn need no call: the framework ends every status on a character when its life
        /// state leaves Alive, and the poll reaps a status whose character was destroyed.</summary>
        public static void RemoveAll(Player target)
        {
            if (target == null) return;
            // A local list: an OnRemoved that calls RemoveAll on another character must not
            // change which statuses this call ends.
            List<CharacterStatus> ending = new List<CharacterStatus>();
            foreach (KeyValuePair<string, CharacterStatus> kv in Active)
                if (kv.Value.Targets(target)) ending.Add(kv.Value);
            for (int i = 0; i < ending.Count; i++) ending[i].End();
        }

        /// <summary>True while the status with this key is active on the player, on this
        /// client. A status on another player's character is active on that player's client
        /// only; <see cref="IsPresented"/> answers for its presentation copy.</summary>
        public static bool IsActive(Player target, string key)
        {
            return target != null && Active.ContainsKey(MapKey(target, key));
        }

        /// <summary>Seconds until expiry, 0 when not active.</summary>
        public static float RemainingSeconds(Player target, string key)
        {
            CharacterStatus live = Find(target, key);
            return live != null ? live.RemainingSeconds : 0f;
        }

        /// <summary>Current stack count of the status on the player, 0 when not active.</summary>
        public static int Stacks(Player target, string key)
        {
            CharacterStatus live = Find(target, key);
            return live != null ? live.StackCount : 0;
        }

        /// <summary>True while this client runs a presentation copy of the status on another
        /// player's character: the owner announced it to this client
        /// (<see cref="CharacterStatusDef.Presented"/>) and it has not ended.</summary>
        public static bool IsPresented(Player target, string key)
        {
            return CharacterStatusCopies.IsPresented(target, key);
        }

        /// <summary>Seconds until a presentation copy ends, 0 when there is none.</summary>
        public static float PresentedRemainingSeconds(Player target, string key)
        {
            return CharacterStatusCopies.RemainingSeconds(target, key);
        }

        /// <summary>The actor number of the player whose request last applied or refreshed
        /// the status on this client, 0 when this client applied it itself or the status is
        /// not active.</summary>
        public static int RequesterOf(Player target, string key)
        {
            CharacterStatus live = Find(target, key);
            return live != null ? live.RequesterActor : 0;
        }

        /// <summary>The attacker named by the request that last applied or refreshed the
        /// status, when it still exists on this client; null otherwise. A claim of the
        /// requester's, never checked.</summary>
        public static OrbitObject AttackerOf(Player target, string key)
        {
            CharacterStatus live = Find(target, key);
            return live != null ? live.Attacker : null;
        }

        // ---------------------------------------------------------------- internals

        private static CharacterStatusRefusal Preflight(Player target, string key, out CharacterStatusDef def)
        {
            def = null;
            if (target == null || target.Stats == null)
            {
                Plugin.Log.LogWarning("Ectype: character status '" + key + "' on a null " +
                                      "character (or one with no stat collection) ignored.");
                return CharacterStatusRefusal.InvalidTarget;
            }
            if (!Defs.TryGetValue(key ?? "", out def))
            {
                if (WarnedOnce.Add("nodef:" + key))
                    Plugin.Log.LogError("Ectype: character status '" + key + "' is not " +
                                        "registered. Register the template at Awake.");
                return CharacterStatusRefusal.NotRegistered;
            }
            if (!Plugin.PatchesApplied)
            {
                // No driver means no timer: applying would be a permanent buff.
                if (WarnedOnce.Add("inert"))
                    Plugin.Log.LogWarning("Ectype: framework is inert this session; " +
                                          "character statuses are disabled.");
                return CharacterStatusRefusal.Inert;
            }
            return CharacterStatusRefusal.None;
        }

        /// <summary>The template under a received key, or null; no log.</summary>
        internal static CharacterStatusDef Lookup(string key)
        {
            CharacterStatusDef def;
            return key != null && Defs.TryGetValue(key, out def) ? def : null;
        }

        /// <summary>
        /// Applies one application on this client and returns the live status. The requester
        /// is the actor whose request was accepted (0 for this client's own call); its side
        /// joins the set the OwnForce audience reads. A requested application never shortens or
        /// weakens a running status: its end moves only later and its magnitude only up. A
        /// status on a character this client owns is then announced to its audience when the
        /// template is Presented. Returns null, applying nothing, for a dead character.
        /// </summary>
        internal static CharacterStatus ApplyLocal(Player target, CharacterStatusDef def, float seconds,
                                                   float magnitude, int requester, OrbitObject attacker,
                                                   int applierSide, bool requested)
        {
            // Nothing applied to a dead character: death ended its statuses, and one put on it
            // now would outlive the respawn.
            if (!CharacterStatusSync.Alive(target)) return null;
            string mapKey = MapKey(target, def.Key);
            CharacterStatus live;
            if (Active.TryGetValue(mapKey, out live))
                live.Reapply(seconds, magnitude, requester, attacker, applierSide, requested);
            else
            {
                live = new CharacterStatus(def, target, mapKey, magnitude, seconds, requester,
                                           attacker, applierSide);
                Active[mapKey] = live;
                live.ApplyStack();
            }
            CharacterStatusSync.Announce(live);
            return live;
        }

        /// <summary>
        /// Driven from EctypeDriver.Update. Expiry and ticks are both decided from the
        /// status's own start time, so this is safe to call more than once a frame.
        /// </summary>
        internal static void Poll()
        {
            CharacterStatusCopies.Poll();
            CharacterStatusSync.PollPending();
            if (Active.Count == 0) return;
            DriveScratch.Clear();
            foreach (KeyValuePair<string, CharacterStatus> kv in Active)
                DriveScratch.Add(kv.Value);
            for (int i = 0; i < DriveScratch.Count; i++) DriveScratch[i].Update();
        }

        internal static void Unregister(CharacterStatus live)
        {
            CharacterStatus current;
            if (Active.TryGetValue(live.MapKey, out current) && ReferenceEquals(current, live))
                Active.Remove(live.MapKey);
        }

        /// <summary>How many statuses this client runs on the character.</summary>
        internal static int CountOn(Player target)
        {
            int n = 0;
            if (target == null) return 0;
            foreach (KeyValuePair<string, CharacterStatus> kv in Active)
                if (kv.Value.Targets(target)) n++;
            return n;
        }

        internal static bool WarnOnce(string tag) { return WarnedOnce.Add(tag); }

        private static CharacterStatus Find(Player target, string key)
        {
            CharacterStatus live;
            return target != null && Active.TryGetValue(MapKey(target, key), out live)
                       ? live : null;
        }

        /// <summary>Keyed on the Unity instance id, not a view id: a status lives on one
        /// client. The game keeps the same object through death and respawn; the death hook
        /// empties it first, so a respawn starts with nothing.</summary>
        internal static string MapKey(Player target, string key)
        {
            return target.GetInstanceID() + "/" + key;
        }
    }

    /// <summary>
    /// One live application. An IModifierSource, so it IS the removal key for every
    /// StatMod it applied.
    /// </summary>
    internal sealed class CharacterStatus : IDescriptiveModifierSource
    {
        internal readonly string MapKey;
        internal int StackCount { get { return _stacks; } }

        private readonly CharacterStatusDef _def;
        private float _magnitude;
        private readonly float _startTime;
        private Player _target;
        private float _endTime;
        private int _stacks;
        private int _lastTick;
        private bool _ended;

        /// <summary>The target's actor number, kept so the end can still be announced after
        /// the character was destroyed.</summary>
        internal readonly int TargetActor;

        /// <summary>The status lives on a character this client owns and its template is
        /// Presented: this client announces it.</summary>
        internal readonly bool Announces;

        /// <summary>Actor of the request that last applied or refreshed it; 0 for this client.</summary>
        internal int RequesterActor;

        /// <summary>The attacker named by that request, when it exists here.</summary>
        internal OrbitObject Attacker;

        /// <summary>The sides of every accepted applier, for the OwnForce audience.</summary>
        internal readonly List<int> ApplierSides = new List<int>(2);

        /// <summary>Actors a presentation copy was announced to, and the end they were told.</summary>
        internal readonly List<int> AnnouncedTo = new List<int>();
        internal int AnnouncedEndTs;

        /// <summary>Unscaled time before which a moved end is not announced again, and whether
        /// one is waiting for it.</summary>
        internal float NextAnnounceAt;
        internal bool AnnounceDue;

        internal float RemainingSeconds
        {
            get { float r = _endTime - Time.time; return r > 0f ? r : 0f; }
        }

        internal CharacterStatusDef Def { get { return _def; } }
        internal Player Target { get { return _target; } }
        internal float Magnitude { get { return _magnitude; } }
        internal float EndTime { get { return _endTime; } }
        internal bool Ended { get { return _ended; } }

        internal CharacterStatus(CharacterStatusDef def, Player target, string mapKey,
                                 float magnitude, float seconds, int requester,
                                 OrbitObject attacker, int applierSide)
        {
            _def = def;
            _target = target;
            MapKey = mapKey;
            _magnitude = magnitude;
            _startTime = Time.time;
            _endTime = _startTime + seconds;
            TargetActor = Topology.ActorOf(target);
            Announces = def.Presented && target.photonView != null && target.photonView.IsMine;
            RequesterActor = requester;
            Attacker = attacker;
            NoteApplierSide(applierSide);
        }

        internal bool Targets(Player p) { return ReferenceEquals(_target, p); }

        private void NoteApplierSide(int side)
        {
            if (side >= 0 && !ApplierSides.Contains(side)) ApplierSides.Add(side);
        }

        /// <summary>A re-application. This client's own re-application refreshes to the new
        /// duration and keeps the first magnitude. A requested one only ever extends the end
        /// and raises the magnitude, so no requester can shorten or weaken a status another
        /// applier put on.</summary>
        internal void Reapply(float seconds, float magnitude, int requester, OrbitObject attacker,
                              int applierSide, bool requested)
        {
            if (_ended) return;
            RequesterActor = requester;
            Attacker = attacker;
            NoteApplierSide(applierSide);
            if (_def.RefreshOnReapply)
            {
                float end = Time.time + seconds;
                _endTime = requested ? Math.Max(_endTime, end) : end;
            }
            if (requested) RaiseMagnitude(magnitude);
            if (_stacks < _def.MaxStacks) ApplyStack();
            else if (_def.OnApplied != null) SafeCb(() => _def.OnApplied(_target, _stacks));
        }

        /// <summary>Re-applies every stack at a larger magnitude: the modifiers are removed by
        /// source and rebuilt, so the stat sees the new amount once per stack.</summary>
        private void RaiseMagnitude(float magnitude)
        {
            if (!(magnitude > _magnitude) || _target == null || _target.Stats == null) return;
            _target.Stats.RemoveModifier(this);
            _magnitude = magnitude;
            for (int i = 0; i < _stacks; i++)
            {
                bool anyMatched;
                List<StatMod> mods = StatusStats.BuildMods(_def.Entries, _magnitude, _target.Stats,
                                                           "character status", out anyMatched);
                if (mods.Count > 0) _target.Stats.ApplyModifiers(mods, this, true);
            }
        }

        /// <summary>A character that was destroyed (despawn, scene change) ends its statuses
        /// here: the target is a Unity object, so the null check catches a destroyed one, and
        /// the collection it held died with it. Death ends them earlier, from the life-state
        /// hook, because the game keeps the character object through death and respawn.</summary>
        internal void Update()
        {
            try
            {
                if (_target == null) { End(); return; }
                if (Time.time >= _endTime) { End(); return; }
                if (AnnounceDue && Time.unscaledTime >= NextAnnounceAt)
                {
                    AnnounceDue = false;
                    CharacterStatusSync.Announce(this);
                }
                Tick();
            }
            catch (Exception e)
            {
                if (CharacterStatuses.WarnOnce("tickfail:" + _def.Key))
                    Plugin.Log.LogError("Ectype: character status '" + _def.Key + "' tick " +
                                        "failed (suppressing further reports): " + e);
                End();
            }
        }

        /// <summary>Unregisters, removes the modifiers and announces the end before OnRemoved
        /// runs, so an OnRemoved that applies the same key again starts a fresh status, whose
        /// announcement follows this one's end.</summary>
        internal void End()
        {
            if (_ended) return;
            _ended = true;
            Player t = _target;
            _target = null;
            CharacterStatuses.Unregister(this);
            try
            {
                if (t != null && t.Stats != null) t.Stats.RemoveModifier(this);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: character status '" + _def.Key + "' stat " +
                                    "removal failed: " + e);
            }
            try { CharacterStatusSync.AnnounceEnd(this, Time.time < _endTime); }
            catch (Exception e)
            {
                if (CharacterStatuses.WarnOnce("announce-end:" + _def.Key))
                    Plugin.Log.LogError("Ectype: character status '" + _def.Key + "' end " +
                                        "announcement failed (reported once): " + e);
            }
            if (_def.OnRemoved != null) SafeCb(() => _def.OnRemoved(t));
        }

        /// <summary>Fresh StatMod instances per application: the game keys removal on the
        /// modifier's Source field, which applying overwrites, so instances can never be
        /// shared between targets or stacks.</summary>
        internal void ApplyStack()
        {
            if (_ended || _target == null) return;
            bool anyMatched;
            List<StatMod> mods = StatusStats.BuildMods(_def.Entries, _magnitude, _target.Stats,
                                                       "character status", out anyMatched);
            if (mods.Count > 0)
            {
                _target.Stats.ApplyModifiers(mods, this, true);
                if (!anyMatched &&
                    CharacterStatuses.WarnOnce("nomatch:" + _def.Key + ":" +
                                               _target.GetType().Name))
                    Plugin.Log.LogWarning("Ectype: character status '" + _def.Key +
                                          "' matched no registered stat on " +
                                          _target.GetType().Name + "; it has no effect there.");
            }
            _stacks++;
            if (_def.OnApplied != null) SafeCb(() => _def.OnApplied(_target, _stacks));
        }

        /// <summary>Tick-indexed against the status's own start: elapsed ticks are
        /// (now - start) / slice, and only unpaid indexes are paid, so double drives and
        /// frame hitches cannot double- or under-tick.</summary>
        private void Tick()
        {
            if (_def.OnTick == null) return;
            float slice = Math.Max(_def.TickSeconds, 0.05f);
            int due = (int)((Time.time - _startTime) / slice);
            while (_lastTick < due && !_ended)
            {
                _lastTick++;
                Action<Player, float> cb = _def.OnTick;
                Player t = _target;
                SafeCb(() => cb(t, slice));
            }
        }


        private void SafeCb(Action a)
        {
            StatusStats.SafeCb(a, CharacterStatuses.WarnOnce, "character status", _def.Key);
        }

        // ------------------------------------------------- IDescriptiveModifierSource

        public string GetHeader()
        {
            return string.IsNullOrEmpty(_def.DisplayName) ? _def.Key : _def.DisplayName;
        }

        public string GetDescription()
        {
            return _def.Entries.Count + " stat mod(s)" + (_stacks > 1 ? ", x" + _stacks : "");
        }
    }
}
