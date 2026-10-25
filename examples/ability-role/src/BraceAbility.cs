using System;

namespace ExampleAbilityRole
{
    /// <summary>
    /// A role ability that cuts all incoming damage while it is active. The framework
    /// installs it, selects it through the hero perk, binds the ability key, and runs the
    /// cooldown and duration. The subclass supplies the effect.
    /// </summary>
    public class BraceAbility : Ectype.EctypeRoleAbility
    {
        private const string StatusKey = Plugin.PLUGIN_GUID + "/brace";

        private bool _loggedFailure;

        public BraceAbility(float cooldownSeconds, float durationSeconds)
            : base(cooldownSeconds, durationSeconds)
        {
        }

        /// <summary>Registers the status template once, from the plugin's Awake. A status
        /// template is registry-wide.</summary>
        internal static void RegisterStatus()
        {
            Ectype.CharacterStatuses.Register(
                new Ectype.CharacterStatusDef(StatusKey) { DisplayName = "Brace" }
                    .AddAllVulnerabilities("flat", -0.75f));
        }

        /// <summary>Runs when the channel starts. The base class catches and logs an
        /// exception thrown here; this catch only keeps the log to one line per failure.</summary>
        protected override void OnApplyEffect()
        {
            try
            {
                if (LocalPlayer == null) return;
                Ectype.CharacterStatuses.Apply(LocalPlayer, StatusKey, FiniteTimeDuration);
            }
            catch (Exception e)
            {
                if (_loggedFailure) return;
                _loggedFailure = true;
                Plugin.Log.LogError("Brace: could not apply the status (reported once): " + e);
            }
        }

        /// <summary>Runs when the channel ends, the ability is deselected, or the character
        /// despawns, including after a failed apply.</summary>
        protected override void OnRemoveEffect()
        {
            try
            {
                if (LocalPlayer == null) return;
                Ectype.CharacterStatuses.Remove(LocalPlayer, StatusKey);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Brace: could not remove the status: " + e);
            }
        }
    }
}
