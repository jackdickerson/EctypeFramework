using System;

using Opsive.UltimateCharacterController.Character;
using UnityEngine.InputSystem;

namespace Ectype
{
    /// <summary>
    /// Base class for a framework role's ability. Subclass it, implement the two effect
    /// hooks, and return an instance from the factory passed to
    /// RoleRegistry.RegisterAbilityFactory; the framework installs it and the game's own
    /// selection lifecycle then drives it (perk investment selects, the shared
    /// UseClassAbility key starts, cooldown and duration tick natively).
    ///
    /// Effect-hook exceptions are caught and logged once per method rather than propagating
    /// into Opsive's update loop; write OnRemoveEffect to run safely after a half-failed
    /// OnApplyEffect, because the game guarantees the stop path runs, not the start path.
    ///
    /// CooldownSeconds and FiniteTimeDuration are ModifiableFloats: assign whole floats,
    /// never SetBaseValue.
    ///
    /// Two optional behaviours, both decided on the owning client: a cancel policy
    /// (<see cref="Cancel"/>), and a usability predicate (<see cref="OwnerCanUse"/>). An
    /// override of a lifecycle method below calls its base, or the policy stops working.
    /// </summary>
    public abstract partial class EctypeRoleAbility : FiniteDurationClassAbility
    {
        private bool _applyFailed;
        private bool _removeFailed;
        private bool _loggedUsableFail;

        /// <summary>Sets the ability's base cooldown and active duration in seconds, and makes it start and stop only when told to.</summary>
        protected EctypeRoleAbility(float cooldownSeconds, float durationSeconds)
        {
            CooldownSeconds = cooldownSeconds;
            FiniteTimeDuration = durationSeconds;

            // A code-constructed Opsive ability defaults to StartType Automatic, which
            // self-starts the instant every cooldown ends; vanilla role abilities are
            // prefab-serialized to Manual, so match that.
            StartType = AbilityStartType.Manual;
            StopType = AbilityStopType.Manual;
        }

        /// <summary>The Opsive locomotion this ability is installed on (protected in the
        /// Opsive base; surfaced for effect code that reads or writes movement state).</summary>
        protected UltimateCharacterLocomotion Locomotion
        {
            get { return m_CharacterLocomotion as UltimateCharacterLocomotion; }
        }

        /// <summary>Runs <c>OnApplyEffect</c> inside a try/catch that logs the first failure.</summary>
        protected sealed override void ApplyClassAbilityEffect()
        {
            try
            {
                OnApplyEffect();
            }
            catch (Exception e)
            {
                if (!_applyFailed)
                {
                    _applyFailed = true;
                    Plugin.Log.LogError("Ectype: " + GetType().Name + ".OnApplyEffect " +
                                        "threw (reported once): " + e);
                }
            }
        }

        /// <summary>Runs <c>OnRemoveEffect</c> inside a try/catch that logs the first failure.</summary>
        protected sealed override void UnapplyClassAbilityEffect()
        {
            try
            {
                OnRemoveEffect();
            }
            catch (Exception e)
            {
                if (!_removeFailed)
                {
                    _removeFailed = true;
                    Plugin.Log.LogError("Ectype: " + GetType().Name + ".OnRemoveEffect " +
                                        "threw (reported once): " + e);
                }
            }
        }

        /// <summary>The ability's effect, applied when the channel starts.</summary>
        protected abstract void OnApplyEffect();

        /// <summary>Revert the effect. Runs when the channel ends, the ability is
        /// deselected, or the character despawns - including after a failed apply.
        /// <see cref="LastEnd"/> says how the channel ended.</summary>
        protected abstract void OnRemoveEffect();

        /// <summary>
        /// Whether the ability may start now. Evaluated on the owning client every tick the
        /// ability is selected and idle; the result goes to the game's AbilityUsable, which
        /// gates the key and drives the HUD's unusable styling. A replica is always usable:
        /// its start is the owner's replicated start, and its own copy of the character's
        /// state can be stale. Default true. Role code never writes AbilityUsable itself.
        /// </summary>
        protected virtual bool OwnerCanUse()
        {
            return true;
        }

        // ------------------------------------------------------------------ lifecycle

        /// <summary>Starts the channel: OnApplyEffect, then the owner's cancel triggers.</summary>
        protected override void AbilityStarted()
        {
            ResetChannel();
            base.AbilityStarted();   // OnApplyEffect, then the channel's countdown is set
            try { BeginChannel(); }
            catch (Exception e) { CancelFailed("starting the channel's triggers", e); }
        }

        /// <summary>
        /// Stops the channel. The game's stop path runs OnRemoveEffect, puts the ability on its
        /// full cooldown, then zeroes the channel's remaining time, so the remaining time is
        /// read before it and a partial cooldown written after it.
        /// </summary>
        protected override void AbilityStopped(bool force)
        {
            float remaining = CurrentActiveDuration;
            try { EndChannel(remaining); }
            catch (Exception e) { CancelFailed("ending the channel's triggers", e); }
            base.AbilityStopped(force);
            try { FinishChannel(); }
            catch (Exception e) { CancelFailed("the early-end cooldown", e); }
            DiagStopped(remaining, force);
        }

        /// <summary>The ability key: a cancel while the channel runs and the policy allows
        /// it, otherwise the game's own start, which refuses while a channel runs.</summary>
        protected override void ActivationKeyPressed(InputAction.CallbackContext obj)
        {
            bool wasActive = IsActive;
            bool cancelled = false;
            try
            {
                if (_channelLive && IsOngoing() && Allows(CancelTriggers.AbilityKey))
                {
                    Fire(CancelTriggers.AbilityKey, "the ability key");
                    cancelled = true;
                }
            }
            catch (Exception e) { CancelFailed("the ability-key cancel", e); }
            if (cancelled) return;
            base.ActivationKeyPressed(obj);
            DiagKeyPressed(wasActive);
        }

        /// <summary>The channel's tick. The base counts the channel down and stops it when it
        /// runs out; a recorded cancel stops it here too.</summary>
        public override void Update()
        {
            base.Update();
            if (_channelLive) TickChannel();
        }

        /// <summary>The idle tick: the cooldown counts down, and the owner re-decides
        /// usability.</summary>
        public override void InactiveUpdate()
        {
            base.InactiveUpdate();
            RefreshUsable();
        }

        /// <summary>Stops the ability, as the game does on destroy, and drops any trigger
        /// still watched.</summary>
        public override void OnDestroy()
        {
            base.OnDestroy();
            try
            {
                _channelLive = false;
                UnwatchTriggers();
            }
            catch (Exception e) { CancelFailed("dropping triggers on destroy", e); }
        }

        /// <summary>The setter publishes the HUD's usable-changed event, so it runs only on
        /// a change.</summary>
        private void RefreshUsable()
        {
            try
            {
                if (LocalPlayer == null) return;
                bool usable = !LocalPlayer.IsMine || OwnerCanUse();
                if (usable != AbilityUsable) AbilityUsable = usable;
            }
            catch (Exception e)
            {
                if (_loggedUsableFail) return;
                _loggedUsableFail = true;
                Plugin.Log.LogError("Ectype: " + GetType().Name + ".OwnerCanUse threw " +
                                    "(reported once; usability left as it was): " + e);
            }
        }

        // The dev tools build implements these (src/dev); a release build has no body for
        // them, and the compiler drops the calls.
        partial void DiagStopped(float remaining, bool force);
        partial void DiagKeyPressed(bool wasActive);
    }
}
