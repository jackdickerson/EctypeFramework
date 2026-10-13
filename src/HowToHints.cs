using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

using CG.Profile;
using Client.Player.Interactions;
using ResourceAssets;

namespace Ectype
{
    /// <summary>
    /// A how-to toast in the HUD, drawn by the game's own hint system.
    ///
    /// The game's <c>HintsController</c> shows a <c>Hint</c> (a <c>ContextInfo</c> with header,
    /// body and icon, a show duration and a per-session cap) through <c>ViewEventBus.OnShowHint</c>,
    /// which the HUD's hint display draws: icon, upper-cased header, body. The player dismisses it
    /// by clicking it or with the dismiss-hint key, and a second hint stacks behind the first and
    /// is restored when it closes. Body text goes through the game's tag replacer, so it may carry
    /// <c>&lt;emphasize&gt;</c> (gold), <c>&lt;warning&gt;</c>, <c>&lt;positive&gt;</c>,
    /// <c>&lt;negative&gt;</c> and <c>&lt;keybind&gt;Fire&lt;/keybind&gt;</c>, which prints the
    /// player's own binding for the named input action.
    ///
    /// A hint built here never reaches the player's profile: the game records every shown hint's
    /// GUID in the cloud-saved seen-hint list, and the framework refuses that write for these.
    /// How often a player has seen a toast is the calling mod's own state, kept in its config.
    /// </summary>
    public static class HowToHints
    {
        private const float TickSeconds = 0.5f;

        private static readonly HashSet<Hint> _ours = new HashSet<Hint>();

        private static bool _patched;
        private static bool _forceAvailable;
        private static bool _bypass;

        // Controller internals: the expiry tick, the shown hint and the stack behind it.
        private static MethodInfo _updateHintToShow;
        private static FieldInfo _shownHint, _hintInfoHint, _stackedHints, _animationFinished;

        private static Ticker _ticker;
        private static bool _warnedNotPatched, _warnedNoController, _warnedNotOurs,
                            _warnedNoForce, _warnedBypass;

        /// <summary>
        /// Builds a hint the game's controller will show. <paramref name="guidHex"/> is a fixed
        /// GUID from the mod's own block; it identifies the hint to the controller and must not
        /// collide with a stock hint. <paramref name="maxPerSession"/> caps how often the hint
        /// shows in one process (at least 1). The hint never sets the game's once-in-lifetime flag;
        /// a lifetime cap is the mod's own state.
        /// </summary>
        public static Hint Build(string guidHex, string header, string body, Sprite icon,
                                 float seconds, int maxPerSession)
        {
            ContextInfo info = ContextInfo.Create(icon, header, body);
            info.name = "HowToHint_Info_" + header;
            info.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(info);

            Hint hint = ScriptableObject.CreateInstance<Hint>();
            hint.name = "HowToHint_" + header;
            hint.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(hint);
            hint.ContainerGuid = new GUIDUnion(guidHex);
            hint.ContextInfo = info;
            hint.ShowDuration = seconds;
            hint.ShowOnceInLifetime = false;
            hint.MaxNumberShowsPerPlaysession = Math.Max(1, maxPerSession);

            _ours.Add(hint);
            return hint;
        }

        /// <summary>True for a hint returned by <see cref="Build"/>.</summary>
        public static bool Owns(Hint hint)
        {
            return hint != null && _ours.Contains(hint);
        }

        /// <summary>
        /// Shows the hint under the player's "Show hints" setting. Returns true only when the
        /// controller put it on screen (a hint already showing is stacked behind it); false when
        /// the HUD is not up, hints are off, the hint reached its per-session cap, or it is already
        /// showing or stacked. A caller that counts shows counts the true returns.
        /// </summary>
        public static bool Show(Hint hint)
        {
            return ShowCore(hint, false);
        }

        /// <summary>
        /// Shows the hint even when the player has turned "Show hints" off, for a toast the player
        /// must see once before that setting governs it. Only the setting is bypassed: a hint
        /// suppressed by the current scenario, a hint at its per-session cap, or one already
        /// showing is refused as by <see cref="Show"/>. With the setting off the game runs no
        /// countdown for hints, so the framework runs one until the hint closes. Returns as
        /// <see cref="Show"/> does.
        /// </summary>
        public static bool ShowForced(Hint hint)
        {
            return ShowCore(hint, true);
        }

        private static bool ShowCore(Hint hint, bool force)
        {
            if (hint == null) return false;
            try
            {
                if (!Owns(hint))
                {
                    if (!_warnedNotOurs)
                    {
                        _warnedNotOurs = true;
                        Plugin.Log.LogWarning("Ectype: how-to hints: '" + hint.name + "' was not " +
                                              "built by HowToHints.Build; not shown.");
                    }
                    return false;
                }
                if (!_patched)
                {
                    // Without the seen-hint prefix the hint's GUID would land in the profile.
                    if (!_warnedNotPatched)
                    {
                        _warnedNotPatched = true;
                        Plugin.Log.LogWarning("Ectype: how-to hints: patches not applied; toast '" +
                                              hint.ContextInfo.HeaderText + "' not shown.");
                    }
                    return false;
                }
                HintsController hc = HintsController.Instance;
                if (hc == null)
                {
                    if (!_warnedNoController)
                    {
                        _warnedNoController = true;
                        Plugin.Log.LogWarning("Ectype: how-to hints: no HintsController in the " +
                                              "scene; toast '" + hint.ContextInfo.HeaderText +
                                              "' not shown.");
                    }
                    return false;
                }

                if (hc.ForceDisableHints) return false;
                bool settingOn = hc.HintsEnabled();
                if (!settingOn && force && !_forceAvailable)
                {
                    if (!_warnedNoForce)
                    {
                        _warnedNoForce = true;
                        Plugin.Log.LogWarning("Ectype: how-to hints: forced show unavailable " +
                                              "(controller internals not found); toasts follow " +
                                              "the Show hints setting.");
                    }
                    return false;
                }
                if (!settingOn && !force) return false;
                // The controller's own cap compares with '>', which lets a hint show once more
                // than its cap; refusing here at '>=' holds it to the cap.
                if (hint.TimesSeenThisPlaysession >= hint.MaxNumberShowsPerPlaysession) return false;
                // Already on screen or stacked: the controller would count it without showing it.
                if (WouldNotShow(hc, hint)) return false;

                int before = hint.TimesSeenThisPlaysession;
                _bypass = !settingOn;
                try { hc.TryShowHint(hint); }
                finally { _bypass = false; }

                bool shown = hint.TimesSeenThisPlaysession > before;
                if (shown && _forceAvailable && !GameTickerRunning(hc)) EnsureTicker(hc);
                return shown;
            }
            catch (Exception e)
            {
                _bypass = false;
                Plugin.Log.LogError("Ectype: how-to hints: show failed: " + e);
                return false;
            }
        }

        // ---- Controller state, read by reflection --------------------------------------------

        private static bool WouldNotShow(HintsController hc, Hint hint)
        {
            if (!_forceAvailable) return false;
            object shown = _shownHint.GetValue(hc);
            Hint current = (Hint)_hintInfoHint.GetValue(shown);
            if (current == null || !(bool)_animationFinished.GetValue(hc)) return false;
            if (current == hint) return true;
            IEnumerable stack = _stackedHints.GetValue(hc) as IEnumerable;
            if (stack == null) return false;
            foreach (object info in stack)
                if ((Hint)_hintInfoHint.GetValue(info) == hint) return true;
            return false;
        }

        private static bool HasWork(HintsController hc)
        {
            object shown = _shownHint.GetValue(hc);
            if ((Hint)_hintInfoHint.GetValue(shown) != null) return true;
            ICollection stack = _stackedHints.GetValue(hc) as ICollection;
            return stack != null && stack.Count > 0;
        }

        /// <summary>
        /// True while any scheduler entry runs this controller's own expiry tick. The controller
        /// keeps one handle in a field, but its settings handler registers a new one without
        /// removing the old, so the scheduler's list is the record of what is running.
        /// </summary>
        private static bool GameTickerRunning(HintsController hc)
        {
            List<RepeatEventScheduler.RepeatEventHandle> list = RepeatEventScheduler.ToInvokeEvents;
            for (int i = 0; i < list.Count; i++)
            {
                Action f = list[i].Function;
                if (f != null && ReferenceEquals(f.Target, hc) && f.Method == _updateHintToShow)
                    return true;
            }
            return false;
        }

        // ---- The framework's expiry tick ------------------------------------------------------

        /// <summary>
        /// One registration with the game's repeat scheduler. Stopping clears <c>Alive</c>, and the
        /// scheduler drops the entry itself at its next due time without running it; nothing is
        /// removed from the scheduler's list while it is iterating.
        /// </summary>
        private sealed class Ticker
        {
            public bool Alive = true;
            public HintsController Controller;
            public Action Update;
        }

        private static void EnsureTicker(HintsController hc)
        {
            if (_ticker != null && _ticker.Alive && _ticker.Controller == hc) return;
            StopTicker();
            Ticker t = new Ticker
            {
                Controller = hc,
                Update = (Action)Delegate.CreateDelegate(typeof(Action), hc, _updateHintToShow),
            };
            _ticker = t;
            RepeatEventScheduler.RegisterEvent(TickSeconds, () => t.Alive && t.Controller != null,
                                               () => Tick(t), 0f);
            if (Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: how-to hints: expiry tick started (Show hints off).");
        }

        private static void StopTicker()
        {
            if (_ticker != null) _ticker.Alive = false;
            _ticker = null;
        }

        private static void Tick(Ticker t)
        {
            try
            {
                if (!t.Alive) return;
                HintsController hc = t.Controller;
                if (hc == null || hc != HintsController.Instance) { StopTicker(); return; }
                // The game's own tick is running (the setting turned on, or a dismissal registered
                // it): it counts down from here, and running both would halve the show time.
                if (GameTickerRunning(hc)) { StopTicker(); return; }
                t.Update();
                if (!HasWork(hc)) StopTicker();
            }
            catch (Exception e)
            {
                StopTicker();
                Plugin.Log.LogError("Ectype: how-to hints: expiry tick failed, stopped: " + e);
            }
        }

        // ---- Patches --------------------------------------------------------------------------

        internal static void ApplyPatches(Harmony harmony)
        {
            const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            MethodInfo setSeen = typeof(PlayerProfileData).GetMethod("SetHintSeen", I, null,
                                                                     new[] { typeof(Hint) }, null);
            MethodInfo enabled = typeof(HintsController).GetMethod("HintsEnabled", I, null,
                                                                   Type.EmptyTypes, null);
            if (setSeen == null)
            {
                Plugin.Log.LogError("Ectype: how-to hints: PlayerProfileData.SetHintSeen(Hint) not " +
                                    "found (game update?); how-to toasts are off.");
                return;
            }
            harmony.Patch(setSeen, prefix: new HarmonyMethod(typeof(HowToHints).GetMethod(
                "SetHintSeenPrefix", BindingFlags.Static | BindingFlags.NonPublic)));
            _patched = true;

            MethodInfo veShow = typeof(UI.HUD.HintDisplayVE).GetMethod("Show", I, null,
                                                                     new[] { typeof(ContextInfo) }, null);
            if (veShow != null)
                harmony.Patch(veShow, postfix: new HarmonyMethod(typeof(HowToHints).GetMethod(
                    "HintDisplayShowPostfix", BindingFlags.Static | BindingFlags.NonPublic)));
            else
                Plugin.Log.LogWarning("Ectype: how-to hints: HintDisplayVE.Show(ContextInfo) not " +
                                      "found (game update?); a hint built without an icon shows an " +
                                      "empty icon slot.");

            _updateHintToShow = typeof(HintsController).GetMethod("UpdateHintToShow", I, null,
                                                                  Type.EmptyTypes, null);
            _shownHint = typeof(HintsController).GetField("shownHint", I);
            _stackedHints = typeof(HintsController).GetField("stackedHints", I);
            _animationFinished = typeof(HintsController).GetField("animationFinishedPlaying", I);
            _hintInfoHint = _shownHint != null ? _shownHint.FieldType.GetField("Hint", I) : null;

            if (enabled == null || _updateHintToShow == null || _shownHint == null ||
                _stackedHints == null || _animationFinished == null || _hintInfoHint == null)
            {
                Plugin.Log.LogError("Ectype: how-to hints: HintsController internals not found " +
                                    "(game update?); forced shows follow the Show hints setting.");
                return;
            }
            harmony.Patch(enabled, postfix: new HarmonyMethod(typeof(HowToHints).GetMethod(
                "HintsEnabledPostfix", BindingFlags.Static | BindingFlags.NonPublic)));
            _forceAvailable = true;
            Plugin.Log.LogInfo("Ectype: how-to hints: patched.");
        }

        /// <summary>
        /// Keeps a hint built here out of the profile's cloud-saved seen-hint list. Stock hints go
        /// through untouched.
        /// </summary>
        private static bool SetHintSeenPrefix(Hint hint, ref bool __result)
        {
            try
            {
                if (!Owns(hint)) return true;
                __result = false;
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: how-to hints: SetHintSeen prefix failed: " + e);
                return true;
            }
        }

        /// <summary>
        /// Collapses the icon slot of a hint that has no icon. The display sets the icon as a
        /// background image and leaves its element in the layout, so a null icon would draw an
        /// empty square. The display's hint elements are pooled and reused for every hint, stock
        /// ones included, so the slot is restored whenever the hint has an icon.
        /// </summary>
        private static void HintDisplayShowPostfix(UI.HUD.HintDisplayVE __instance, ContextInfo info)
        {
            try
            {
                VisualElement icon = __instance != null ? __instance.Q<VisualElement>("Icon") : null;
                if (icon == null || info == null) return;
                icon.style.display = info.Icon == null ? new StyleEnum<DisplayStyle>(DisplayStyle.None)
                                                       : new StyleEnum<DisplayStyle>(StyleKeyword.Null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: how-to hints: HintDisplayVE.Show postfix failed: " + e);
            }
        }

        /// <summary>
        /// Opens the Show-hints gate for the duration of a forced show only, so stock hints keep
        /// obeying the setting. A scenario's own suppression still closes it.
        /// </summary>
        private static void HintsEnabledPostfix(HintsController __instance, ref bool __result)
        {
            try
            {
                if (_bypass && !__result && __instance != null && !__instance.ForceDisableHints)
                    __result = true;
            }
            catch (Exception e)
            {
                if (!_warnedBypass)
                {
                    _warnedBypass = true;
                    Plugin.Log.LogError("Ectype: how-to hints: HintsEnabled postfix failed: " + e);
                }
            }
        }
    }
}
