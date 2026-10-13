using System;
using System.Text;

using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// Evidence for "an ability went dead after it was cut short, until the loadout
    /// changed". A role switch deselects and reselects, which re-runs every piece of state
    /// the lifecycle owns: the input binding, the Opsive Enabled flag, the active slot, the
    /// cooldown. The log has to say which one was wrong, so each line below is one of those
    /// facts at the moment it matters, on the owning client only.
    ///
    ///  - a channel stopped with time left: who stopped it (a short stack), and the state after;
    ///  - input bound and unbound, with a running count, so a missing or doubled binding shows;
    ///  - the ability disabled while still selected;
    ///  - a key press the locomotion refused for any reason other than the ordinary two
    ///    (cooling down, or already running).
    ///
    /// Each line is an event a player causes, never a per-frame poll, and refusals are
    /// rate-limited. The overrides here call their base first; the stop and key-press lines
    /// are partial methods the main class calls after the game's own path. Each swallows only
    /// its own logging failure.
    /// </summary>
    public abstract partial class EctypeRoleAbility
    {
        private const float EARLY_STOP_SECONDS = 0.1f;
        private const float REFUSAL_LOG_INTERVAL = 2f;

        private int _inputBindings;
        private float _lastRefusalLog = -999f;

        private bool DiagMine
        {
            get { return LocalPlayer != null && LocalPlayer.IsMine; }
        }

        /// <summary>Binds the ability's input, and logs when the binding is doubled.</summary>
        protected override void EnableInput()
        {
            base.EnableInput();
            try
            {
                if (!DiagMine) return;
                _inputBindings++;
                Diag("input bound (bindings " + _inputBindings + ")" +
                     (_inputBindings > 1 ? " - DOUBLE-BOUND: one press will fire twice" : ""));
            }
            catch (Exception e) { DiagFailed(e); }
        }

        /// <summary>Unbinds the ability's input.</summary>
        protected override void DisableInput()
        {
            base.DisableInput();
            try
            {
                if (!DiagMine || _inputBindings == 0) return;
                _inputBindings--;
                Diag("input unbound (bindings " + _inputBindings + ")");
            }
            catch (Exception e) { DiagFailed(e); }
        }

        /// <summary>Enables or disables the ability, and logs disabling it while it is still selected.</summary>
        protected override void SetEnabled(bool enabled)
        {
            base.SetEnabled(enabled);
            try
            {
                if (!enabled && IsSelectedAbility && DiagMine)
                    Diag("DISABLED while still selected - it will neither tick its cooldown " +
                         "nor start until reselected. " + DiagState());
            }
            catch (Exception e) { DiagFailed(e); }
        }

        /// <summary>After the stop path: logs a channel cut short with time remaining.</summary>
        partial void DiagStopped(float remaining, bool force)
        {
            try
            {
                if (remaining <= EARLY_STOP_SECONDS || !DiagMine) return;
                Diag("channel cut short with " + remaining.ToString("0.0") + "s left (force " +
                     force + "). After: " + DiagState() + ". Stopped from:" + Caller());
            }
            catch (Exception e) { DiagFailed(e); }
        }

        /// <summary>After the game's start path ran for a key press: logs a press the ability
        /// refused for an unexpected reason.</summary>
        partial void DiagKeyPressed(bool wasActive)
        {
            if (wasActive || IsActive || !DiagMine) return;
            try
            {
                bool routine = IsSelectedAbility && Enabled && AbilityUsable &&
                               (IsOnCooldown() || IsOngoing());
                if (routine) return;
                if (Time.time - _lastRefusalLog < REFUSAL_LOG_INTERVAL) return;
                _lastRefusalLog = Time.time;
                Diag("key press REFUSED with no ordinary reason. " + DiagState() +
                     ". Running on the character: " + ActiveNames());
            }
            catch (Exception e) { DiagFailed(e); }
        }

        private string DiagState()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("selected=").Append(IsSelectedAbility)
              .Append(" enabled=").Append(Enabled)
              .Append(" usable=").Append(AbilityUsable)
              .Append(" active=").Append(IsActive).Append("(slot ").Append(ActiveIndex).Append(')')
              .Append(" cooldown=").Append(CurrentCooldown.ToString("0.0"))
              .Append('/').Append(CooldownSeconds.Value.ToString("0.0"))
              .Append(" channel=").Append(CurrentActiveDuration.ToString("0.0"))
              .Append(" bindings=").Append(_inputBindings);
            if (m_CharacterLocomotion != null)
                sb.Append(" alive=").Append(m_CharacterLocomotion.Alive);
            return sb.ToString();
        }

        private string ActiveNames()
        {
            if (m_CharacterLocomotion == null || m_CharacterLocomotion.ActiveAbilities == null)
                return "(no locomotion)";
            StringBuilder sb = new StringBuilder();
            int n = m_CharacterLocomotion.ActiveAbilityCount;
            for (int i = 0; i < n && i < m_CharacterLocomotion.ActiveAbilities.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                var a = m_CharacterLocomotion.ActiveAbilities[i];
                sb.Append(a == null ? "null" : a.GetType().Name);
                if (ReferenceEquals(a, this)) sb.Append("(this)");
            }
            return n == 0 ? "nothing" : sb.ToString();
        }

        /// <summary>The stack above AbilityStopped: skips this method, DiagStopped and AbilityStopped.</summary>
        private static string Caller()
        {
            try
            {
                string[] lines = new System.Diagnostics.StackTrace(3, false).ToString()
                    .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < lines.Length && i < 8; i++)
                    sb.Append("\n    ").Append(lines[i].Trim());
                return sb.ToString();
            }
            catch (Exception) { return " (no stack)"; }
        }

        // A shared budget over every Diag line, so a lifecycle that thrashes (a select /
        // deselect loop, a stuck refusal) cannot flood the log; the count of dropped lines
        // is carried on the next line that fits.
        private const float DIAG_WINDOW_SECONDS = 10f;
        private const int DIAG_LINES_PER_WINDOW = 20;
        private static float _diagWindowStart = -999f;
        private static int _diagLinesInWindow;
        private static int _diagDropped;

        private void Diag(string message)
        {
            float now = Time.unscaledTime;
            if (now - _diagWindowStart >= DIAG_WINDOW_SECONDS)
            {
                _diagWindowStart = now;
                _diagLinesInWindow = 0;
            }
            if (_diagLinesInWindow >= DIAG_LINES_PER_WINDOW) { _diagDropped++; return; }
            _diagLinesInWindow++;
            string dropped = _diagDropped > 0
                ? " [" + _diagDropped + " earlier diagnostic line(s) rate-limited]" : "";
            _diagDropped = 0;
            Plugin.Log.LogInfo("Ectype: " + GetType().Name + " " + message + dropped);
        }

        private static bool _diagFailureLogged;

        private static void DiagFailed(Exception e)
        {
            if (_diagFailureLogged) return;
            _diagFailureLogged = true;
            try { Plugin.Log.LogWarning("Ectype: ability diagnostics threw (reported once): " + e); }
            catch (Exception) { }
        }
    }
}
