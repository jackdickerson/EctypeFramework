using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

using HarmonyLib;
using UI.Core;
using UI.Token;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Ectype
{
    /// <summary>What a mod supplies for one button on the HUD ability bar.</summary>
    public sealed class HudButtonSpec
    {
        /// <summary>Unique across mods; namespace it with the registering mod's GUID.</summary>
        public string Id;

        /// <summary>Square glyph, white on transparent. Drawn white on the dark plate while
        /// recovering and black on the fill once ready, as the stock button does.</summary>
        public Texture2D Glyph;

        /// <summary>Fill and glow colour while ready. Null keeps the game's orange.</summary>
        public Color? ReadyColor;

        /// <summary>Base plate colour. Null keeps the game's dark grey.</summary>
        public Color? IdleColor;

        /// <summary>Left to right; ties break on <see cref="Id"/>.</summary>
        public int Order;

        /// <summary>The key drawn on the chip. Read every frame.</summary>
        public Func<Key> Key;

        /// <summary>0 = ready, 1 = just used; the game's own fill semantics. Read every
        /// frame. Null draws the button permanently ready.</summary>
        public Func<float> Fill;

        /// <summary>False hides the button and closes its gap without unregistering it.
        /// Null means always visible.</summary>
        public Func<bool> Visible;
    }

    /// <summary>
    /// A row of square ability buttons to the right of the class-ability widget. A mod
    /// registers a <see cref="HudButtonSpec"/>; the bar builds the button, lays out the row,
    /// draws the key chip, feeds the cooldown fill and tears it all down. Input, cooldown and
    /// the ability itself stay with the mod. Presentation only: nothing here is networked or
    /// touches what a run contains.
    ///
    /// Each button is a copy of the HUD document's authored hero element, so the game's own
    /// <c>PerkBuffItemVE</c> does the fill reveal, the <c>perk-ready</c> class and the ready
    /// glow once the bar writes <c>PerkFill</c>. The bar owns the square chrome, the glyph,
    /// the colours and the placement. The class-ability widget's own key chip is moved to
    /// the same bottom-right corner convention.
    /// </summary>
    public static class HudAbilityBar
    {
        /// <summary>Most buttons the bar holds. A registration past it is refused.</summary>
        public const int MaxButtons = 4;

        /// <summary>Returned by <see cref="Register"/>. Dispose to remove the button.</summary>
        public sealed class Handle : IDisposable
        {
            internal HudButton Button;
            /// <summary>The registered button's id, or null once it is gone.</summary>
            public string Id { get { return Button != null ? Button.Spec.Id : null; } }
            /// <summary>Removes the button from the bar.</summary>
            public void Dispose() { Unregister(this); }
        }

        /// <summary>
        /// Adds a button. Returns null, and logs once, when the bar is full, the id is already
        /// registered, or the spec has no id. Safe to call before the HUD exists; the button
        /// is built when it appears and rebuilt whenever the HUD is.
        /// </summary>
        public static Handle Register(HudButtonSpec spec)
        {
            if (spec == null || string.IsNullOrEmpty(spec.Id))
            {
                Plugin.Log.LogWarning("Ectype: HUD bar refused a button with no id.");
                return null;
            }
            foreach (HudButton b in Buttons)
            {
                if (b.Spec.Id == spec.Id)
                {
                    Plugin.Log.LogWarning("Ectype: HUD bar refused '" + spec.Id +
                                          "': that id is already registered.");
                    return null;
                }
            }
            if (Buttons.Count >= MaxButtons)
            {
                var holders = new List<string>();
                foreach (HudButton b in Buttons) holders.Add(b.Spec.Id);
                Plugin.Log.LogWarning("Ectype: HUD bar refused '" + spec.Id + "': the bar holds " +
                                      MaxButtons + " buttons and they are taken by " +
                                      string.Join(", ", holders.ToArray()) + ".");
                return null;
            }

            var button = new HudButton(spec);
            var handle = new Handle { Button = button };
            Buttons.Add(button);
            Buttons.Sort((a, b) => a.Spec.Order != b.Spec.Order
                ? a.Spec.Order.CompareTo(b.Spec.Order)
                : string.CompareOrdinal(a.Spec.Id, b.Spec.Id));
            if (Plugin.Verbose) Plugin.Log.LogInfo("Ectype: HUD bar registered '" + spec.Id + "' (" +
                               Buttons.Count + "/" + MaxButtons + ").");
            return handle;
        }

        private static void Unregister(Handle handle)
        {
            if (handle == null || handle.Button == null) return;
            HudButton button = handle.Button;
            handle.Button = null;
            button.Teardown();
            Buttons.Remove(button);
            if (Plugin.Verbose) Plugin.Log.LogInfo("Ectype: HUD bar removed '" + button.Spec.Id + "'.");
        }

        // ------------------------------------------------------------------ state

        private const float Gap = 6f;

        // The padding before each button: one gap plus how far the previous element's key
        // chip reaches past its plate, halved - the chips overhang their own corner, so the
        // full clearance reads as too much air between buttons.
        private const float PaddingScale = 0.5f;
        private const float PlaceInterval = 0.25f;

        // 9-slice geometry of the hero chrome, in texels. background/border/shadow are
        // 2048x512 with chamfered ends ~170 texels deep, so 190 puts every slice line on a
        // straight edge. The glow is 2048x656, drawn at GlowScale of the plate's texel scale
        // and GlowPad texels past the plate on each side, so its inner cutout sits on the
        // plate's edge.
        private const float ChromeTextureHeight = 512f;
        private const int PlateSlice = 190;
        private const int GlowSlice = 250;
        private const float GlowPad = 60f;
        private const float GlowScale = 0.8f;

        // The square glyph's share of the plate, so its edges clear the chamfer.
        private const float GlyphSize = 0.76f;

        internal const string MarkerClass = "ectype-hud-bar-button";

        // Looked up under the PerkBuffItemVE: a KeyBindVE also has a child named "Fill".
        private static readonly string[] ChromeLayers =
            { "GlowContainer", "PerkItem", "Fill", "Shadow", "Frame" };

        private static readonly FieldInfo ContainerF =
            AccessTools.Field(typeof(HUDElement), "container");
        private static readonly FieldInfo ClassAbilityVEF =
            AccessTools.Field(typeof(ClassAbilityDisplay), "classAbilityVE");
        private static readonly FieldInfo BoundInputF =
            AccessTools.Field(typeof(KeyBindVE), "_boundInput");
        private static readonly MethodInfo UpdateKeybindM =
            AccessTools.Method(typeof(KeyBindVE), "UpdateKeybind");

        internal static readonly List<HudButton> Buttons = new List<HudButton>();

        /// <summary>A framework-built class slot's accent, which goes on its fill only while
        /// the slot is ready. Written when the slot is built.</summary>
        internal static readonly ConditionalWeakTable<VisualElement, StrongBox<Color>> SlotAccents =
            new ConditionalWeakTable<VisualElement, StrongBox<Color>>();

        private static ClassAbilityDisplay _display;
        private static VisualElement _container;
        private static float _nextSearch, _nextPlace;
        private static bool _templateWarned, _tickFailed;
        private static float _side = 68f, _top;

        private static Handle _demoHandle;

        // ------------------------------------------------------------------ tick

        /// <summary>Driven every frame by the framework driver. Never throws.</summary>
        internal static void Tick()
        {
            try
            {
                if (!Plugin.PatchesApplied) return;
                SyncDemo();
                if (!EnsureDisplay()) return;

                foreach (HudButton b in Buttons) b.Update(_container, _display);
                SyncClassSlotAccent();

                if (Time.unscaledTime >= _nextPlace)
                {
                    _nextPlace = Time.unscaledTime + PlaceInterval;
                    Place();
                }
            }
            catch (Exception e)
            {
                if (!_tickFailed)
                {
                    _tickFailed = true;
                    Plugin.Log.LogError("Ectype: HUD bar tick failed (reported once; the bar " +
                                        "keeps trying): " + e);
                }
            }
        }

        /// <summary>The class-ability display and its container, re-found whenever the HUD
        /// is rebuilt. A rebuilt HUD drops every built button so it is rebuilt with it.</summary>
        private static bool EnsureDisplay()
        {
            bool alive = _display != null && _container != null && _container.panel != null;
            if (alive) return true;

            if (_container != null)
            {
                foreach (HudButton b in Buttons) b.Teardown();
                _container = null;
                _display = null;
            }
            if (Time.unscaledTime < _nextSearch) return false;
            _nextSearch = Time.unscaledTime + 1f;

            _display = UnityEngine.Object.FindObjectOfType<ClassAbilityDisplay>();
            if (_display == null) return false;
            _container = ContainerF != null ? ContainerF.GetValue(_display) as VisualElement : null;
            if (_container == null)
            {
                _display = null;
                return false;
            }
            _nextPlace = 0f;
            return true;
        }

        // ------------------------------------------------------------------ placement

        /// <summary>
        /// Moves the class-ability chip to its plate's corner, then lays the visible buttons
        /// out to the right of that plate. Re-run a few times a second so a resolution change,
        /// a class change or a HUD rebuild is followed. Every write is absolute, computed from
        /// the current layout, so nothing compounds.
        /// </summary>
        private static void Place()
        {
            Rect box = _container.worldBound;
            if (float.IsNaN(box.x)) return;

            VisualElement main = ClassAbilityVEF != null
                ? ClassAbilityVEF.GetValue(_display) as VisualElement : null;
            PerkBuffItemVE mainPerk = main != null ? main.Q<PerkBuffItemVE>() : null;
            float chipReach = 0f;
            float x;

            if (mainPerk != null && main.resolvedStyle.display != DisplayStyle.None)
            {
                Rect plate = mainPerk.worldBound;
                // A plate still settling (seen on a joining client) can have a finite x and
                // a NaN width; every value placement reads has to be finite.
                if (!IsFinite(plate.xMax) || !IsFinite(plate.yMin) ||
                    !(plate.height > 0f) || !IsFinite(plate.height)) return;
                _side = plate.height;
                _top = plate.yMin - box.yMin;
                chipReach = CornerMainChip(main, mainPerk);
                x = plate.xMax - box.xMin;
            }
            else
            {
                x = -Gap;   // no class ability showing: the bar starts at the container's edge
            }

            foreach (HudButton b in Buttons)
            {
                if (!b.Shown) continue;
                x += (Gap + chipReach) * PaddingScale;
                b.Place(x, _top, _side);
                chipReach = b.ChipReach;
                x += _side;
            }
        }

        /// <summary>
        /// Puts a key chip's centre on its parent's bottom-right corner and draws it in front.
        /// The anchor is a percentage of the parent and the -50% translate is a percentage of
        /// the chip's own size, so nothing is measured: a position computed from the chip's
        /// measured width feeds back, because the width comes out a sub-pixel different
        /// wherever the chip lands, and the chip then flips between two positions.
        /// </summary>
        internal static void AnchorChipToCorner(KeyBindVE chip)
        {
            if (chip == null) return;
            chip.style.position = Position.Absolute;
            chip.style.left = Length.Percent(100f);
            chip.style.top = Length.Percent(100f);
            chip.style.translate = new Translate(Length.Percent(-50f), Length.Percent(-50f));
            chip.BringToFront();
        }

        // The class-ability chip's in-flow width, measured once while it still sat in the row.
        private static readonly ConditionalWeakTable<KeyBindVE, StrongBox<float>> ChipRowWidth =
            new ConditionalWeakTable<KeyBindVE, StrongBox<float>>();

        /// <summary>
        /// Moves the class-ability chip onto its plate's bottom-right corner. The widget lays
        /// the chip out in a flex row to the left of the plate and never writes its position,
        /// so taking it out of flow holds. Out of flow, the chip no longer pushes the plate
        /// right, so the widget gets a left padding of the chip's measured row width, once,
        /// and the plate stays where the game put it. The chip is then pinned to the plate's
        /// corner in the plate's own layout coordinates, which the chip no longer affects.
        /// Returns how far the chip reaches past the plate's right edge.
        /// </summary>
        private static float CornerMainChip(VisualElement main, PerkBuffItemVE plate)
        {
            KeyBindVE chip = main.Q<KeyBindVE>();
            if (chip == null || chip.parent != plate.parent) return 0f;

            StrongBox<float> row;
            if (!ChipRowWidth.TryGetValue(chip, out row))
            {
                IResolvedStyle rs = chip.resolvedStyle;
                float w = chip.layout.width + rs.marginLeft + rs.marginRight;
                if (float.IsNaN(w) || w <= 0f) return 0f;
                row = new StrongBox<float>(w);
                ChipRowWidth.Add(chip, row);

                if (chip.layout.x < plate.layout.x)
                    chip.parent.style.paddingLeft = chip.parent.resolvedStyle.paddingLeft + w;
                chip.style.position = Position.Absolute;
                chip.style.translate = new Translate(Length.Percent(-50f), Length.Percent(-50f));
                chip.BringToFront();
            }

            Rect p = plate.layout;
            if (float.IsNaN(p.xMax) || p.height <= 0f) return row.Value * 0.5f;
            SetIfChanged(chip.style, p.xMax, p.yMax);
            return row.Value * 0.5f;
        }

        private static void SetIfChanged(IStyle st, float left, float top)
        {
            if (NeedsWrite(st.left, left)) st.left = left;
            if (NeedsWrite(st.top, top)) st.top = top;
        }

        /// <summary>
        /// True when a finite target differs from the inline value. A stored NaN always
        /// counts as different: every comparison against NaN is false, so a plain delta
        /// test would never overwrite one and the element would stay at 0 for the session.
        /// </summary>
        private static bool NeedsWrite(StyleLength current, float target)
        {
            if (!IsFinite(target)) return false;
            if (current.keyword != StyleKeyword.Undefined) return true;
            float value = current.value.value;
            return !IsFinite(value) || Mathf.Abs(value - target) > 0.01f;
        }

        private static bool IsFinite(float f)
        {
            return !float.IsNaN(f) && !float.IsInfinity(f);
        }

        // --------------------------------------------------------- class-slot accent

        /// <summary>
        /// A framework-built class slot carries its class accent on the glow, and on the fill
        /// only while the game has marked it ready: the stock fill is white while recovering
        /// and orange under <c>perk-ready</c>, and an inline tint would beat that rule.
        /// </summary>
        private static void SyncClassSlotAccent()
        {
            VisualElement main = ClassAbilityVEF != null
                ? ClassAbilityVEF.GetValue(_display) as VisualElement : null;
            StrongBox<Color> accent;
            if (main == null || !SlotAccents.TryGetValue(main, out accent)) return;
            PerkBuffItemVE perk = main.Q<PerkBuffItemVE>();
            if (perk == null) return;
            SetFillTint(perk.Q("Fill"), perk.ClassListContains("perk-ready"), accent.Value);
        }

        /// <summary>Writes the fill tint only when it differs from what is there.</summary>
        internal static void SetFillTint(VisualElement fill, bool ready, Color color)
        {
            if (fill == null) return;
            StyleColor current = fill.style.unityBackgroundImageTintColor;
            bool colored = current.keyword == StyleKeyword.Undefined;
            if (ready == colored && (!ready || current.value == color)) return;
            fill.style.unityBackgroundImageTintColor =
                ready ? (StyleColor)color : (StyleColor)StyleKeyword.Null;
        }

        // ------------------------------------------------------------------ build

        /// <summary>A pristine copy of the authored hero element from the HUD document's
        /// template, or null (logged once) if the document no longer has it.</summary>
        internal static VisualElement InstantiateHeroElement(ClassAbilityDisplay display)
        {
            UIDocument doc = display.GetComponent<UIDocument>();
            if (doc == null || doc.visualTreeAsset == null) return null;

            TemplateContainer fresh = doc.visualTreeAsset.Instantiate();
            VisualElement abilities = fresh.Q("ClassAbilityContainer");
            VisualElement donor = abilities != null ? abilities.Q("Scavenger") : null;
            if (donor == null)
            {
                if (!_templateWarned)
                {
                    _templateWarned = true;
                    Plugin.Log.LogWarning("Ectype: HUD document has no ClassAbilityContainer/" +
                                          "Scavenger element - HUD bar buttons are skipped " +
                                          "this session (game update?).");
                }
                return null;
            }
            donor.RemoveFromHierarchy();
            return donor;
        }

        // ------------------------------------------------------------------ one button

        internal sealed class HudButton
        {
            internal readonly HudButtonSpec Spec;
            internal VisualElement Root;
            internal bool Shown;
            internal float ChipReach;

            private PerkBuffItemVE _perk;
            private VisualElement _plate, _glow, _fill;
            private KeyBindVE _chip;
            private InputAction _chipAction;
            private Key _chipKey = UnityEngine.InputSystem.Key.None;
            private bool _chromeSliced, _failed, _geometryLogged;
            private float _chipReach = -1f;
            private float _geometryAt;

            internal HudButton(HudButtonSpec spec) { Spec = spec; }

            internal void Update(VisualElement container, ClassAbilityDisplay display)
            {
                if (_failed) return;
                try
                {
                    bool visible = Spec.Visible == null || Spec.Visible();
                    if (Root == null)
                    {
                        if (!visible) { Shown = false; return; }
                        if (!Build(container, display)) return;
                    }
                    Shown = visible;
                    Root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
                    if (!visible) return;

                    _perk.PerkFill.Value = Spec.Fill != null ? Mathf.Clamp01(Spec.Fill()) : 0f;
                    if (Spec.ReadyColor.HasValue)
                        SetFillTint(_fill, _perk.ClassListContains("perk-ready"), Spec.ReadyColor.Value);
                    UpdateChip(Spec.Key != null ? Spec.Key() : UnityEngine.InputSystem.Key.None);

                    if (_chromeSliced && !_geometryLogged && Time.unscaledTime >= _geometryAt)
                    {
                        _geometryLogged = true;
                        if (Plugin.Verbose) Plugin.Log.LogInfo("Ectype: HUD bar '" + Spec.Id + "' laid out: " +
                                                               Geometry());
                    }
                }
                catch (Exception e)
                {
                    _failed = true;
                    if (Root != null) Root.style.display = DisplayStyle.None;
                    Shown = false;
                    Plugin.Log.LogError("Ectype: HUD bar button '" + Spec.Id + "' threw and is " +
                                        "hidden for the rest of this session: " + e);
                }
            }

            private bool Build(VisualElement container, ClassAbilityDisplay display)
            {
                VisualElement donor = InstantiateHeroElement(display);
                if (donor == null) return false;

                // The element keeps its authored name: the stylesheets select its art by ID,
                // and a renamed copy comes out blank. The game reaches for that name once,
                // with a depth-first Q from the container, and the authored element is
                // earlier in child order than this copy, so it still finds its own.
                donor.AddToClassList(MarkerClass);
                donor.style.position = Position.Absolute;
                donor.style.display = DisplayStyle.Flex;
                donor.style.visibility = Visibility.Visible;
                donor.style.opacity = 1f;
                // The chip straddles the corner and the glow bleeds past every edge.
                donor.style.overflow = Overflow.Visible;

                container.Add(donor);
                Root = donor;
                _perk = donor.Q<PerkBuffItemVE>();
                _chip = donor.Q<KeyBindVE>();
                _plate = donor.Q("PerkItem");
                _glow = donor.Q("GlowContainer");
                _fill = _perk != null ? _perk.Q("Fill") : null;
                if (_perk == null)
                {
                    Teardown();
                    throw new InvalidOperationException("the hero element has no PerkBuffItemVE");
                }

                FillBox(_perk);
                FillBox(_plate);
                AnchorChipToCorner(_chip);
                _chipReach = -1f;
                foreach (string name in new[] { "Icon", "IconFilled" })
                {
                    VisualElement icon = donor.Q(name);
                    if (icon == null || Spec.Glyph == null) continue;
                    icon.style.backgroundImage = new StyleBackground(TrimmedGlyph(Spec.Glyph));
                    icon.style.backgroundSize = new BackgroundSize(
                        Length.Percent(GlyphSize * 100f), Length.Percent(GlyphSize * 100f));
                }
                // The glow shows only when ready, so its colour is set once; the game's
                // ready animation rewrites only its alpha.
                if (Spec.ReadyColor.HasValue && _glow != null)
                    _glow.style.unityBackgroundImageTintColor = Spec.ReadyColor.Value;
                if (Spec.IdleColor.HasValue && _plate != null)
                    _plate.style.unityBackgroundImageTintColor = Spec.IdleColor.Value;

                _chromeSliced = false;
                _geometryLogged = false;
                _chipKey = UnityEngine.InputSystem.Key.None;
                if (Plugin.Verbose) Plugin.Log.LogInfo("Ectype: HUD bar built '" + Spec.Id + "'.");
                return true;
            }

            internal void Place(float left, float top, float side)
            {
                if (Root == null) return;
                SetIfChanged(Root.style, left, top);
                if (NeedsWrite(Root.style.width, side)) Root.style.width = side;
                if (NeedsWrite(Root.style.height, side)) Root.style.height = side;

                if (_chipReach < 0f && _chip != null)
                {
                    float w = _chip.layout.width;
                    if (!float.IsNaN(w) && w > 0f) _chipReach = w * 0.5f;
                }
                ChipReach = Mathf.Max(0f, _chipReach);
                PlaceGlow(side);
                SliceChrome(side);
            }

            /// <summary>Sizes the glow off the square side instead of the stylesheet's
            /// percentages, which were tuned for the wide box. <c>perk-glow</c> transitions
            /// every property over 1 s, so this settles a second after a change.</summary>
            private void PlaceGlow(float side)
            {
                if (_glow == null) return;
                float inset = -GlowPad * GlowScale * side / ChromeTextureHeight;
                _glow.style.position = Position.Absolute;
                _glow.style.width = side - 2f * inset;
                _glow.style.height = side - 2f * inset;
                _glow.style.left = inset;
                _glow.style.top = inset;
            }

            /// <summary>
            /// 9-slices the game's own chrome into the square. Each layer's resolved image is
            /// re-assigned inline as its backing Texture2D, because UI Toolkit scales a
            /// Sprite's slices by an extra referencePixelsPerUnit / pixelsPerUnit factor and a
            /// Texture2D's by <c>unitySliceScale</c> alone. The scale is side / 512, the
            /// texels-per-point the authored widget draws at, so the corners match it. The
            /// swap waits for the copy's stylesheets to resolve, then runs once; the scale
            /// follows the side on every placement.
            /// </summary>
            private void SliceChrome(float side)
            {
                if (!_chromeSliced)
                {
                    if (_plate == null || ChromeTexture(_plate.resolvedStyle.backgroundImage) == null)
                        return;
                    foreach (string name in ChromeLayers)
                    {
                        VisualElement ve = _perk.Q(name);
                        Texture2D tex = ve != null ? ChromeTexture(ve.resolvedStyle.backgroundImage) : null;
                        if (tex == null) continue;
                        ve.style.backgroundImage = new StyleBackground(tex);
                        int slice = ReferenceEquals(ve, _glow) ? GlowSlice : PlateSlice;
                        ve.style.unitySliceLeft = slice;
                        ve.style.unitySliceTop = slice;
                        ve.style.unitySliceRight = slice;
                        ve.style.unitySliceBottom = slice;
                        // Slices lay out inside the background-size box, and the authored
                        // sheets do not set 100% on every chrome layer.
                        ve.style.backgroundSize = new BackgroundSize(Length.Percent(100f), Length.Percent(100f));
                        ve.style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
                        ve.style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
                        ve.style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
                    }
                    _chromeSliced = true;
                    _geometryAt = Time.unscaledTime + 2f;   // past the glow's 1 s transition
                }

                float scale = side / ChromeTextureHeight;
                foreach (string name in ChromeLayers)
                {
                    VisualElement ve = _perk.Q(name);
                    if (ve != null)
                        ve.style.unitySliceScale = ReferenceEquals(ve, _glow) ? scale * GlowScale : scale;
                }
            }

            /// <summary>
            /// The chip renders whatever InputAction sits in its private bound-input field.
            /// Swap in one bound to the button's key and call the chip's own refresh. The
            /// chip's hold lookup parses <c>action.interactions</c> and throws on null;
            /// passing a binding path to the constructor routes the interactions argument to
            /// the binding instead, so construct with none and add the binding after.
            /// </summary>
            private void UpdateChip(Key key)
            {
                if (_chip == null || BoundInputF == null || UpdateKeybindM == null) return;
                if (key == _chipKey) return;
                _chipKey = key;
                if (_chipAction != null) { _chipAction.Disable(); _chipAction.Dispose(); }
                _chipAction = new InputAction(Spec.Id, InputActionType.Button, null, "press");
                if (key != UnityEngine.InputSystem.Key.None)
                    _chipAction.AddBinding("<Keyboard>/" + BindingPathFor(key));
                _chipAction.Enable();   // lets the chip's press animation react to the key
                BoundInputF.SetValue(_chip, _chipAction);
                UpdateKeybindM.Invoke(_chip, null);
            }

            internal void Teardown()
            {
                if (Root != null) Root.RemoveFromHierarchy();
                if (_chipAction != null) { _chipAction.Disable(); _chipAction.Dispose(); }
                Root = null;
                _perk = null;
                _plate = _glow = _fill = null;
                _chip = null;
                _chipAction = null;
                _chipKey = UnityEngine.InputSystem.Key.None;
                _chromeSliced = false;
                Shown = false;
            }

            internal string Geometry()
            {
                if (Root == null) return "not built";
                var sb = new StringBuilder();
                sb.Append("button ").Append(Fmt(Root.worldBound));
                if (_glow != null)
                    sb.Append(" glow ").Append(Fmt(_glow.worldBound)).Append(' ')
                      .Append(DescribeBackground(_glow.resolvedStyle.backgroundImage));
                if (_plate != null)
                    sb.Append(" plate ").Append(DescribeBackground(_plate.resolvedStyle.backgroundImage));
                if (_chip != null) sb.Append(" chip ").Append(Fmt(_chip.worldBound));
                return sb.ToString();
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>Pins an element to its parent's full box; the authored sheets size the
        /// plate for the wide widget.</summary>
        private static void FillBox(VisualElement ve)
        {
            if (ve == null) return;
            ve.style.position = Position.Absolute;
            ve.style.left = 0f;
            ve.style.top = 0f;
            ve.style.width = Length.Percent(100f);
            ve.style.height = Length.Percent(100f);
            ve.style.marginLeft = ve.style.marginTop = ve.style.marginRight = ve.style.marginBottom = 0f;
        }

        // The frame's inner chamfer edge is the line x + y = 225 texels on the 512-tall border
        // texture, measured at every row of the chamfer. As a fraction of the button's side:
        private const float InnerChamfer = 225f / 512f;

        private static readonly Dictionary<Texture2D, Texture2D> _trimmed =
            new Dictionary<Texture2D, Texture2D>();
        private static bool _unreadableWarned;

        /// <summary>
        /// A copy of the glyph with everything outside the frame's inner octagon cleared.
        /// A square glyph at <see cref="GlyphSize"/> reaches past the chamfer at its corners,
        /// and the white base glyph shows there against the HUD. An unreadable texture (not
        /// created or loaded by a mod) is used as given, with one warning.
        /// </summary>
        private static Texture2D TrimmedGlyph(Texture2D glyph)
        {
            if (glyph == null) return null;
            Texture2D done;
            if (_trimmed.TryGetValue(glyph, out done)) return done;
            if (!glyph.isReadable)
            {
                if (!_unreadableWarned)
                {
                    _unreadableWarned = true;
                    Plugin.Log.LogWarning("Ectype: HUD bar glyph '" + glyph.name + "' is not " +
                                          "readable, so its corners are not trimmed to the " +
                                          "frame; content there may show outside it.");
                }
                _trimmed[glyph] = glyph;
                return glyph;
            }

            int w = glyph.width, h = glyph.height;
            Color32[] px = glyph.GetPixels32();
            float margin = (1f - GlyphSize) * 0.5f;
            float feather = 1.5f * GlyphSize / Mathf.Max(w, h);   // 1.5 glyph pixels, in sides
            for (int y = 0; y < h; y++)
            {
                float v = margin + GlyphSize * (y + 0.5f) / h;
                for (int x = 0; x < w; x++)
                {
                    float u = margin + GlyphSize * (x + 0.5f) / w;
                    // Distance inside the nearest of the four chamfer lines.
                    float d = Mathf.Min(Mathf.Min(u + v, (1f - u) + v),
                                        Mathf.Min(u + (1f - v), (1f - u) + (1f - v))) - InnerChamfer;
                    if (d >= feather) continue;
                    int i = y * w + x;
                    float keep = Mathf.Clamp01(d / feather);
                    px[i].a = (byte)(px[i].a * keep);
                }
            }
            var copy = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = glyph.name + ".trimmed",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = glyph.filterMode,
            };
            copy.SetPixels32(px);
            copy.Apply();
            _trimmed[glyph] = copy;
            return copy;
        }

        /// <summary>The hero-chrome texture behind a resolved background, or null.</summary>
        private static Texture2D ChromeTexture(Background bg)
        {
            Texture2D tex = bg.texture != null ? bg.texture
                : bg.sprite != null ? bg.sprite.texture : null;
            if (tex == null || tex.name.IndexOf("WideHeroBtn", StringComparison.OrdinalIgnoreCase) < 0)
                return null;
            return tex;
        }

        /// <summary>Keyboard control-path segment for a Key ("2", "h", "numpad5", "f4").</summary>
        private static string BindingPathFor(Key k)
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                var control = keyboard[k];
                if (control != null) return control.name;
            }
            string name = k.ToString();
            if (name.StartsWith("Digit")) return name.Substring(5);
            return name.ToLowerInvariant();
        }

        private static string Fmt(Rect r)
        {
            return string.Format("({0:F1},{1:F1} {2:F1}x{3:F1})", r.x, r.y, r.width, r.height);
        }

        private static string DescribeBackground(Background bg)
        {
            if (bg.texture != null)
                return "tex:" + bg.texture.name + " " + bg.texture.width + "x" + bg.texture.height;
            if (bg.sprite != null)
                return "sprite:" + bg.sprite.name + " rect=" + bg.sprite.rect +
                    " (atlas=" + bg.sprite.texture.width + "x" + bg.sprite.texture.height + ")";
            if (bg.vectorImage != null) return "vector:" + bg.vectorImage.name;
            if (bg.renderTexture != null) return "rt:" + bg.renderTexture.name;
            return "none";
        }

        // ------------------------------------------------------------------ demo

        /// <summary>
        /// A dev-only button ([Dev] DemoHudButton) that exercises the bar with no consumer
        /// mod installed: a procedural ring glyph, key F9 on the chip, and an 8 s cycle of
        /// 3 s ready then a 5 s cooldown.
        /// </summary>
        private static void SyncDemo()
        {
            bool want = EctypeConfig.DemoHudButton != null && EctypeConfig.DemoHudButton.Value;
            if (want && _demoHandle == null)
            {
                _demoHandle = Register(new HudButtonSpec
                {
                    Id = EctypePluginInfo.PLUGIN_GUID + ".demo",
                    Glyph = DemoGlyph(),
                    ReadyColor = new Color(0.25f, 0.8f, 0.95f),
                    Order = int.MaxValue,
                    Key = () => UnityEngine.InputSystem.Key.F9,
                    Fill = () =>
                    {
                        float t = Time.unscaledTime % 8f;
                        return t < 3f ? 0f : 1f - (t - 3f) / 5f;
                    },
                });
            }
            else if (!want && _demoHandle != null)
            {
                _demoHandle.Dispose();
                _demoHandle = null;
            }
        }

        private static Texture2D _demoGlyph;

        private static Texture2D DemoGlyph()
        {
            if (_demoGlyph != null) return _demoGlyph;
            const int n = 128;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
            {
                name = "EctypeHudDemoGlyph",
                wrapMode = TextureWrapMode.Clamp,
            };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float dx = x - n / 2f + 0.5f, dy = y - n / 2f + 0.5f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(1f - Mathf.Abs(r - n * 0.32f) / (n * 0.08f));
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            _demoGlyph = tex;
            return tex;
        }

        // ------------------------------------------------------------------ diagnostics

        /// <summary>
        /// Every button's element tree, resolved style beside what the bar wrote inline, in
        /// the format <c>vcm/scripts/hud-badge.py --dump</c> reads. Called from a dev button.
        /// </summary>
        public static void DumpTree()
        {
            var sb = new StringBuilder("Ectype HUD bar TREE DUMP\n");
            if (Buttons.Count == 0) sb.AppendLine("no buttons registered.");
            foreach (HudButton b in Buttons)
            {
                if (b.Root == null)
                {
                    sb.AppendLine("'" + b.Spec.Id + "': not built.");
                    continue;
                }
                sb.AppendLine("TREE DUMP '" + b.Spec.Id + "'");
                Describe(sb, "root", b.Root);
                DumpChildren(sb, b.Root, "  ", 0);
            }
            Plugin.Log.LogWarning(sb.ToString());
        }

        private static void DumpChildren(StringBuilder sb, VisualElement ve, string indent, int depth)
        {
            if (ve == null || depth > 4) return;
            foreach (VisualElement child in ve.Children())
            {
                Describe(sb, indent + "-", child);
                DumpChildren(sb, child, indent + "  ", depth + 1);
            }
        }

        private static void Describe(StringBuilder sb, string label, VisualElement ve)
        {
            IResolvedStyle rs = ve.resolvedStyle;
            IStyle st = ve.style;
            sb.AppendLine(string.Format(
                "{0}: <{1}> name='{2}' classes=[{3}] display={4} opacity={5:F2} " +
                "worldBound={6} bg={7} bgSize={8} tint={9} inline=(w:{10} h:{11} l:{12} t:{13}) " +
                "transition=({14} / {15})",
                label, ve.GetType().Name, ve.name, string.Join(",", ve.GetClasses()),
                rs.display, rs.opacity, ve.worldBound, DescribeBackground(rs.backgroundImage),
                rs.backgroundSize, rs.unityBackgroundImageTintColor,
                st.width, st.height, st.left, st.top,
                string.Join(",", rs.transitionProperty), string.Join(",", rs.transitionDuration)));
        }
    }
}
