using System;
using System.Collections.Generic;

using Opsive.UltimateCharacterController.Character;
using Opsive.UltimateCharacterController.Character.Abilities;
using UnityEngine;

namespace Ectype
{
    /// <summary>The ways a role ability's channel can end before it runs out. Combine with |.</summary>
    [Flags]
    public enum CancelTriggers
    {
        None = 0,

        /// <summary>The ability key pressed again while the channel runs.</summary>
        AbilityKey = 1 << 0,

        /// <summary>A contact from the character's movement sweep whose approach speed (velocity
        /// against the surface normal) reaches <see cref="CancelPolicy.CollisionSpeed"/>.</summary>
        Collision = 1 << 1,

        /// <summary>Any grappling hook on the character attaching to a surface.</summary>
        GrappleAttach = 1 << 2,

        /// <summary>The character entering a ship's inertia zone.</summary>
        InertiaZoneEntry = 1 << 3,

        /// <summary>The character not flying the EVA jetpack on a tick of the channel: it landed
        /// or entered gravity. A channel started out of flight ends on its first tick.</summary>
        JetpackFlightEnd = 1 << 4,

        /// <summary>The ability's own <c>RequestCancel(reason)</c>.</summary>
        Requested = 1 << 5,
    }

    /// <summary>The cooldown a channel that ends early leaves.</summary>
    public enum CancelCooldown
    {
        /// <summary>The full cooldown, as when the channel runs out.</summary>
        Full,

        /// <summary>The cooldown times the share of the channel used: 3 s of a 4 s channel on
        /// an 8 s cooldown leaves 6 s.</summary>
        Partial,
    }

    /// <summary>
    /// Makes a role ability's channel cancelable. Assign one to
    /// <c>EctypeRoleAbility.Cancel</c> in the subclass constructor; with none the channel
    /// always runs its full duration and ends on the full cooldown.
    ///
    /// The triggers are watched on the owning client only, from the channel's start to its
    /// stop. A trigger records the cancel, and the ability stops on its own next tick, never
    /// inside the callback that reported the trigger.
    /// </summary>
    public sealed class CancelPolicy
    {
        /// <summary>Approach speed in m/s used when <see cref="CollisionSpeed"/> is null.</summary>
        public const float DefaultCollisionSpeed = 3f;

        /// <summary>Approach speeds below this are read as this: contact at zero speed would
        /// otherwise cancel on every ground check.</summary>
        public const float MinCollisionSpeed = 0.1f;

        /// <summary>What may end the channel early.</summary>
        public CancelTriggers On;

        /// <summary>The cooldown an early end leaves. Applies to every end with channel time
        /// left, including a stop from outside the policy such as a deselect.</summary>
        public CancelCooldown Cooldown = CancelCooldown.Full;

        /// <summary>Approach speed in m/s at which a contact counts as
        /// <see cref="CancelTriggers.Collision"/>. Read at every contact, so a value that changes
        /// while the game runs takes effect at once.</summary>
        public Func<float> CollisionSpeed;
    }

    /// <summary>How a role ability's channel ended. Read from <c>EctypeRoleAbility.LastEnd</c>.</summary>
    public struct ChannelEnd
    {
        internal ChannelEnd(CancelTriggers cause, CancelTriggers fired, string reason, bool early,
                            float usedSeconds, float channelSeconds, float cooldownSeconds)
        {
            Cause = cause;
            Fired = fired;
            Reason = reason;
            Early = early;
            UsedSeconds = usedSeconds;
            ChannelSeconds = channelSeconds;
            CooldownSeconds = cooldownSeconds;
        }

        /// <summary>The first trigger that fired, which is the one that ended the channel.
        /// None when the channel ran out, or when something outside the policy stopped it: a
        /// deselect, a despawn, or on a replica the owner's replicated stop.</summary>
        public CancelTriggers Cause { get; }

        /// <summary>Every trigger that fired before the stop landed, the cause included.</summary>
        public CancelTriggers Fired { get; }

        /// <summary>The cause in words, or the text passed to <c>RequestCancel</c>. Null with no cause.</summary>
        public string Reason { get; }

        /// <summary>True when the channel had time left as it stopped.</summary>
        public bool Early { get; }

        /// <summary>Seconds of the channel that ran.</summary>
        public float UsedSeconds { get; }

        /// <summary>The channel's full length in seconds, as it was when it started.</summary>
        public float ChannelSeconds { get; }

        /// <summary>The cooldown the end left, in seconds, on the owning client. NaN inside
        /// <c>OnRemoveEffect</c>, which runs before the cooldown is set, and on a replica.</summary>
        public float CooldownSeconds { get; }

        internal ChannelEnd WithCooldown(float seconds)
        {
            return new ChannelEnd(Cause, Fired, Reason, Early, UsedSeconds, ChannelSeconds, seconds);
        }

        /// <summary>One line for a log or a status readout.</summary>
        public override string ToString()
        {
            if (ChannelSeconds <= 0f) return "no channel yet";
            string text;
            if (!Early)
                text = "ran its full " + ChannelSeconds.ToString("0.0") + " s";
            else
                text = (Cause != CancelTriggers.None ? "cancelled" : "stopped") + " after " +
                       UsedSeconds.ToString("0.0") + " of " + ChannelSeconds.ToString("0.0") + " s" +
                       (Reason != null ? " (" + Reason + ")" : "");
            if (!float.IsNaN(CooldownSeconds))
                text += ", cooldown " + CooldownSeconds.ToString("0.0") + " s";
            return text;
        }
    }

    /// <summary>
    /// The cancel policy's moving parts: the triggers watched for one channel on the owning
    /// client, the cancel they record, and the cooldown an early end leaves.
    /// </summary>
    public abstract partial class EctypeRoleAbility
    {
        /// <summary>The channel's cancel policy. Null, the default, makes the channel run its
        /// full duration. Set it in the subclass constructor.</summary>
        protected CancelPolicy Cancel { get; set; }

        /// <summary>How the most recent channel ended. Inside <c>OnRemoveEffect</c> it describes
        /// the channel that is ending, with its cooldown not yet set.</summary>
        protected ChannelEnd LastEnd { get; private set; }

        // Owner only, from the channel's start to its stop.
        private bool _channelLive;
        private float _channelSeconds;
        private CancelTriggers _cause;
        private CancelTriggers _fired;
        private string _causeText;

        private CharacterLocomotion _collisionLoco;
        private Action<RaycastHit> _collisionHandler;
        private CharacterAtmosphericDataTracker _tracker;
        private Action<bool> _zoneHandler;
        private readonly List<ChannelGrapplingHook> _hooks = new List<ChannelGrapplingHook>();
        private Action _grappleHandler;
        private FlyJetpack _jetpack;

        private bool _loggedCancelFail;
        private bool _loggedRequestRefused;

        /// <summary>
        /// Ends the channel on the ability's next tick, with <paramref name="reason"/> as the
        /// recorded cause. Needs <see cref="CancelTriggers.Requested"/> in the policy; without
        /// it the call logs once and does nothing. Does nothing outside a channel and on a
        /// replica, whose stop is the owner's replicated stop.
        /// </summary>
        protected void RequestCancel(string reason)
        {
            if (!Allows(CancelTriggers.Requested))
            {
                if (!_loggedRequestRefused)
                {
                    _loggedRequestRefused = true;
                    Plugin.Log.LogWarning("Ectype: " + GetType().Name + ".RequestCancel ignored: " +
                                          "its CancelPolicy does not list CancelTriggers.Requested " +
                                          "(reported once).");
                }
                return;
            }
            Fire(CancelTriggers.Requested, string.IsNullOrEmpty(reason) ? "requested" : reason);
        }

        private bool Allows(CancelTriggers trigger)
        {
            CancelPolicy p = Cancel;
            return p != null && (p.On & trigger) != 0;
        }

        /// <summary>Records a trigger. The first one is the cause; the stop waits for the tick.</summary>
        private void Fire(CancelTriggers trigger, string text)
        {
            if (!_channelLive) return;
            _fired |= trigger;
            if (_cause != CancelTriggers.None) return;
            _cause = trigger;
            _causeText = text;
        }

        private bool IsOwner
        {
            get { return LocalPlayer != null && LocalPlayer.IsMine; }
        }

        // ------------------------------------------------------------------ channel start

        private void ResetChannel()
        {
            _cause = CancelTriggers.None;
            _fired = CancelTriggers.None;
            _causeText = null;
            _channelLive = IsOwner;
        }

        private void BeginChannel()
        {
            _channelSeconds = CurrentActiveDuration;
            if (!_channelLive) return;
            if (!IsOngoing()) { _channelLive = false; return; }
            UnwatchTriggers();   // never two subscriptions, whatever path ended the last channel
            WatchTriggers();
        }

        private void WatchTriggers()
        {
            CancelPolicy p = Cancel;
            UltimateCharacterLocomotion loco = Locomotion;
            if (p == null || loco == null) return;

            if ((p.On & CancelTriggers.Collision) != 0)
            {
                if (_collisionHandler == null) _collisionHandler = OnCollisionHit;
                loco.OnCollision += _collisionHandler;
                _collisionLoco = loco;
            }

            // Every hook the character carries: the Scavenger's own and any a mod installs.
            // Each invokes OnGrapplingStart at the end of its attach-to-valid-surface handler.
            if ((p.On & CancelTriggers.GrappleAttach) != 0)
            {
                if (_grappleHandler == null) _grappleHandler = OnGrappleAttached;
                Ability[] abilities = loco.Abilities;
                for (int i = 0; abilities != null && i < abilities.Length; i++)
                {
                    ChannelGrapplingHook hook = abilities[i] as ChannelGrapplingHook;
                    if (hook == null) continue;
                    hook.OnGrapplingStart += _grappleHandler;
                    _hooks.Add(hook);
                }
            }

            if ((p.On & CancelTriggers.InertiaZoneEntry) != 0)
            {
                CustomCharacterLocomotion custom = loco as CustomCharacterLocomotion;
                CharacterAtmosphericDataTracker tracker =
                    custom != null ? custom.AtmosphereTracker : null;
                if (tracker != null)
                {
                    if (_zoneHandler == null) _zoneHandler = OnInertiaZoneChange;
                    tracker.OnInInertiaZoneChange += _zoneHandler;
                    _tracker = tracker;
                }
            }

            // Polled from Update: FlyJetpack raises no event when it stops.
            if ((p.On & CancelTriggers.JetpackFlightEnd) != 0)
                _jetpack = loco.GetAbility<FlyJetpack>(-1);
        }

        // ------------------------------------------------------------------ triggers

        private void OnCollisionHit(RaycastHit hit)
        {
            try
            {
                CharacterLocomotion loco = _collisionLoco;
                if (!_channelLive || ReferenceEquals(loco, null)) return;
                float into = Vector3.Dot(loco.Velocity, -hit.normal);
                if (into >= CollisionThreshold())
                    Fire(CancelTriggers.Collision, "a collision at " + into.ToString("0.0") + " m/s");
            }
            catch (Exception e) { CancelFailed("the collision check", e); }
        }

        private float CollisionThreshold()
        {
            CancelPolicy p = Cancel;
            Func<float> source = p != null ? p.CollisionSpeed : null;
            float v = source != null ? source() : CancelPolicy.DefaultCollisionSpeed;
            if (float.IsNaN(v)) v = CancelPolicy.DefaultCollisionSpeed;
            return Mathf.Max(CancelPolicy.MinCollisionSpeed, v);
        }

        private void OnGrappleAttached()
        {
            try { Fire(CancelTriggers.GrappleAttach, "a grapple attaching"); }
            catch (Exception e) { CancelFailed("the grapple trigger", e); }
        }

        private void OnInertiaZoneChange(bool inZone)
        {
            try { if (inZone) Fire(CancelTriggers.InertiaZoneEntry, "entering an inertia zone"); }
            catch (Exception e) { CancelFailed("the inertia-zone trigger", e); }
        }

        private void PollJetpack()
        {
            if (_jetpack != null && !_jetpack.IsActive)
                Fire(CancelTriggers.JetpackFlightEnd, "jetpack flight ending");
        }

        /// <summary>From Update, owner only: the jetpack poll, then the recorded cancel's stop.</summary>
        private void TickChannel()
        {
            try
            {
                PollJetpack();
                if (_cause != CancelTriggers.None && IsActive && IsOngoing()) StopAbility();
            }
            catch (Exception e) { CancelFailed("the deferred cancel", e); }
        }

        // ------------------------------------------------------------------ channel stop

        /// <summary>Before the game's stop path: drop the watches and describe the end, so
        /// OnRemoveEffect can read it.</summary>
        private void EndChannel(float remaining)
        {
            _channelLive = false;
            UnwatchTriggers();
            float total = _channelSeconds;
            float used = total > 0f ? Mathf.Clamp(total - Mathf.Max(0f, remaining), 0f, total) : 0f;
            LastEnd = new ChannelEnd(_cause, _fired, _causeText, remaining > 0f, used, total,
                                     float.NaN);
            _cause = CancelTriggers.None;
            _fired = CancelTriggers.None;
            _causeText = null;
        }

        /// <summary>
        /// After the game's stop path, which put the ability on its full cooldown: a partial
        /// cooldown is written through the game's own setter. It never goes below one physics
        /// step, so it still ends in the game's countdown, which raises ClassAbilityCooldownEnded.
        /// </summary>
        private void FinishChannel()
        {
            if (!IsOwner) return;
            ChannelEnd end = LastEnd;
            CancelPolicy p = Cancel;
            bool partial = end.Early && end.ChannelSeconds > 0f &&
                           p != null && p.Cooldown == CancelCooldown.Partial;
            if (partial)
                RefundCooldown(Mathf.Max(Time.fixedDeltaTime,
                                         CooldownSeconds.Value * (end.UsedSeconds / end.ChannelSeconds)));
            LastEnd = end.WithCooldown(CurrentCooldown);
            if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: " + GetType().Name + " channel " + LastEnd +
                                   (partial ? ", scaled to the share used" : "") + ".");
        }

        private void UnwatchTriggers()
        {
            // Reference checks, not Unity's: a despawning character still holds the delegates.
            if (!ReferenceEquals(_collisionLoco, null))
            {
                try { _collisionLoco.OnCollision -= _collisionHandler; }
                catch (Exception e) { CancelFailed("unsubscribing the collision trigger", e); }
                _collisionLoco = null;
            }
            for (int i = 0; i < _hooks.Count; i++)
            {
                try { _hooks[i].OnGrapplingStart -= _grappleHandler; }
                catch (Exception e) { CancelFailed("unsubscribing the grapple trigger", e); }
            }
            _hooks.Clear();
            if (!ReferenceEquals(_tracker, null))
            {
                try { _tracker.OnInInertiaZoneChange -= _zoneHandler; }
                catch (Exception e) { CancelFailed("unsubscribing the inertia-zone trigger", e); }
                _tracker = null;
            }
            _jetpack = null;
        }

        private void CancelFailed(string what, Exception e)
        {
            if (_loggedCancelFail) return;
            _loggedCancelFail = true;
            Plugin.Log.LogError("Ectype: " + GetType().Name + " cancel policy: " + what +
                                " threw (reported once): " + e);
        }
    }
}
