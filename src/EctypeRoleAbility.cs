using System;

using Opsive.UltimateCharacterController.Character;

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
    /// </summary>
    public abstract partial class EctypeRoleAbility : FiniteDurationClassAbility
    {
        private bool _applyFailed;
        private bool _removeFailed;

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
        /// deselected, or the character despawns - including after a failed apply.</summary>
        protected abstract void OnRemoveEffect();
    }
}
