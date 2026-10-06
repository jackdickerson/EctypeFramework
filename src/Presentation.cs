using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

using UI;
using UI.Token;

namespace Ectype
{
    /// <summary>
    /// W3: presentation of more than four trees. When over four trees survive (registered
    /// classes minus suppressed vanilla trees), the authored tree row is wrapped in a
    /// horizontal ScrollView driven by the mouse wheel and a pad bridge (every controller
    /// focus change funnels through NavigatorVE.TryFocusElement; scrolling the focused tree
    /// into view keeps spatial navigation working). With four or fewer trees the row is not
    /// wrapped - but the header band still applies, so a four-tree screen and a nine-tree one
    /// carry their class names the same way.
    ///
    /// Position is shown by a pager rather than a scrollbar: one pip per class above the row,
    /// lit for the classes currently on screen. A scrollbar answers "how far along am I",
    /// which is the wrong question for a small fixed set of classes; the pager answers which
    /// classes are in view and how many exist, both without estimating a proportion. Its pips
    /// are built from the game's own sprites, tinted - the art is white silhouettes, so tint
    /// is the whole of their appearance.
    ///
    /// The scroller is the platform ScrollView, so clipping and offset clamping are built in.
    ///
    /// One controller serves two differently-authored documents (token terminal and codex),
    /// so each logs its resolved geometry the first time it lays out and
    /// ScrollExcludeDocuments opts one out by any of its logged names. Both of those - the
    /// document's identity and everything that observes rather than styles - live in
    /// PresentationDiagnostics; nothing in this file reads back what it wrote.
    /// </summary>
    internal static class Presentation
    {
        internal const string SCROLL_NAME = "EctypeTreeScroll";
        private const string IGNORE_CLASS = "picking-mode-setter-ignore";

        /// <summary>Every tunable number below comes from here, read at the point of use so a
        /// reload from disk takes effect without rebuilding anything (see DevLayout).</summary>
        private static LayoutSpec S { get { return DevLayout.Spec; } }

        /// <summary>Called at the end of the PerkTreeController ctor prefix, after injection
        /// and def filtering; liveTreeCount is the filtered def count = trees this build
        /// will actually drive.</summary>
        public static void Apply(VisualElement root, int liveTreeCount)
        {
            if (root == null) return;
            string docId = PresentationDiagnostics.DescribeDocument(root);

            bool wrapped = false;
            if (liveTreeCount > 4 &&
                EctypeConfig.EnableScroll != null && EctypeConfig.EnableScroll.Value)
            {
                if (PresentationDiagnostics.IsExcluded(docId))
                {
                    if (Plugin.Verbose)
                        Plugin.Log.LogInfo("Ectype: document [" + docId + "] is excluded from " +
                                           "the tree scroller by config; authored layout kept.");
                }
                else
                {
                    try
                    {
                        wrapped = EnsureScroll(root);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError("Ectype: tree scroller install failed in [" +
                                            docId + "]: " + e);
                    }
                }
            }

            // The class-name band is not part of the scroller: it applies to whatever trees
            // this document holds, wrapped or not.
            try { StyleTreeContent(root); StyleClassNames(root); }
            catch (Exception e)
            { Plugin.Log.LogError("Ectype: class-name styling failed: " + e); }

            if (EctypeConfig.GeometryLog == null || EctypeConfig.GeometryLog.Value)
                PresentationDiagnostics.ScheduleGeometryLog(
                    root, docId, liveTreeCount, wrapped);
        }

        /// <summary>
        /// Re-apply the whole layout to a document that is already on screen. Every pass
        /// this calls writes its properties unconditionally from the spec, so re-running is
        /// how a reloaded spec lands - and equally how a value that was tuned and then
        /// removed from the file reverts. Nothing here inserts or removes elements: the
        /// structure is the ctor prefix's job, and re-running it needs a panel rebuild.
        /// </summary>
        public static void Restyle(VisualElement root)
        {
            if (root == null) return;
            StyleTreeContent(root);
            StyleClassNames(root);

            ScrollView scroll = root.Q<ScrollView>(SCROLL_NAME);
            if (scroll == null) return;
            // Re-run rather than assume: a reload can change the pip count, art or spacing,
            // and rebuilding to match is what makes those keys hot.
            if (scroll.parent != null) EnsurePager(scroll, scroll.parent);
            SizeTrees(scroll);
            TreeCarousel carousel = scroll.userData as TreeCarousel;
            if (carousel != null) carousel.Refresh();
        }

        // -------------------------------------------------------------- the class-name band

        /// <summary>
        /// A PerkTreeVE's frame and its contents are SIBLINGS, not nested ("Title",
        /// "Background", then "Outer" and "Inner"), so scaling only these two frees space
        /// inside the frame - scaling the PerkTreeVE would shrink the frame with it.
        /// </summary>
        private static readonly string[] CONTENT_ELEMENTS = { "Outer", "Inner" };

        /// <summary>
        /// Shrink each tree's content about the tree's top edge and push it down by the band.
        /// Each content element sits at a different authored y, so <c>y0*(scale-1)</c> moves
        /// it to where a scale about the TREE's top would have put it and the band is added
        /// on top, making the content behave as one block.
        /// </summary>
        private static void StyleTreeContent(VisualElement root)
        {
            foreach (PerkTreeVE tree in root.Query<PerkTreeVE>().ToList())
                foreach (string name in CONTENT_ELEMENTS)
                {
                    // Direct children only: the names are generic enough that a descendant
                    // search would eventually find a nested one and transform it twice.
                    VisualElement el = null;
                    foreach (VisualElement child in tree.Children())
                        if (child.name == name) { el = child; break; }
                    if (el == null) continue;

                    BandTransform(el);

                    // Registered on the element whose layout is being read, not an ancestor;
                    // the class is an inert marker keeping registration idempotent without
                    // writing to a game element's userData.
                    if (!el.ClassListContains(BAND_MARKER))
                    {
                        el.AddToClassList(BAND_MARKER);
                        VisualElement captured = el;
                        el.RegisterCallback<GeometryChangedEvent>(
                            delegate
                            {
                                try { BandTransform(captured); }
                                catch (Exception e) { Plugin.Log.LogError("Ectype: band transform failed: " + e); }
                            });
                    }
                }
        }

        private const string BAND_MARKER = "ectype-band-tracked";

        /// <summary>
        /// One content element's band transform. Writes nothing until its layout has resolved,
        /// because an offset baked from an unresolved <c>NaN</c> is never corrected.
        /// </summary>
        private static void BandTransform(VisualElement el)
        {
            float y0 = el.layout.y;
            if (float.IsNaN(y0)) return;

            LayoutSpec s = S;
            float scale = s.treeScale > 0f ? s.treeScale : 1f;
            el.style.transformOrigin =
                new TransformOrigin(new Length(50f, LengthUnit.Percent), 0f, 0f);
            el.style.scale = new Scale(new Vector2(scale, scale));
            el.style.translate =
                new Translate(0f, y0 * (scale - 1f) + s.headerBandPx, 0f);
        }

        /// <summary>
        /// Put each tree's class name in the band the content just vacated, driving the
        /// game's own "ClassName" label rather than adding a parallel one.
        ///
        /// Placement is written to the label's PARENT ("Title"), the absolutely-positioned
        /// element the UXML authored at a negative y; positioning the label instead only
        /// moves it within Title, leaving the name outside the frame at any value.
        /// Appearance still belongs to the label, and every property is written only when
        /// the spec names it.
        /// </summary>
        private static void StyleClassNames(VisualElement root)
        {
            LayoutSpec s = S;
            if (s.showClassName == null && s.classNameFontSize == null &&
                s.classNameTopPx == null && s.classNameHeightPx == null &&
                string.IsNullOrEmpty(s.classNameColorHex)) return;

            foreach (PerkTreeVE tree in root.Query<PerkTreeVE>().ToList())
            {
                Label label = tree.Q<Label>("ClassName");
                if (label == null) continue;

                IStyle st = label.style;
                if (s.showClassName != null)
                    st.display = s.showClassName.Value ? DisplayStyle.Flex : DisplayStyle.None;
                if (s.classNameFontSize != null) st.fontSize = s.classNameFontSize.Value;
                if (!string.IsNullOrEmpty(s.classNameColorHex))
                    st.color = LayoutSpec.ParseColor(s.classNameColorHex, Color.white);

                if (s.classNameTopPx == null && s.classNameHeightPx == null) continue;

                // Title is authored width-to-content, so stretching it edge to edge and
                // centring its children is what actually centres the name in the tree - the
                // authored centring is a left offset computed for the old text width.
                VisualElement title = label.parent ?? label;
                IStyle ts = title.style;
                ts.position = Position.Absolute;
                ts.left = 0f;
                ts.right = 0f;
                ts.alignItems = Align.Center;
                ts.justifyContent = Justify.Center;
                if (s.classNameTopPx != null) ts.top = s.classNameTopPx.Value;
                if (s.classNameHeightPx != null) ts.height = s.classNameHeightPx.Value;
            }
        }

        // ------------------------------------------------------------------ the scroller

        private static bool EnsureScroll(VisualElement root)
        {
            if (root.Q<ScrollView>(SCROLL_NAME) != null) return true;   // idempotent

            PerkTreeVE anyTree = root.Q<PerkTreeVE>();
            if (anyTree == null || anyTree.parent == null)
            {
                Plugin.Log.LogWarning("Ectype: no PerkTreeVE in this document - nothing " +
                                      "to wrap.");
                return false;
            }

            VisualElement row = anyTree.parent;
            List<PerkTreeVE> toMove = new List<PerkTreeVE>();
            foreach (VisualElement child in row.Children())
            {
                PerkTreeVE t = child as PerkTreeVE;
                if (t != null) toMove.Add(t);
            }

            ScrollView scroll = new ScrollView(ScrollViewMode.Horizontal);
            scroll.name = SCROLL_NAME;
            // Both platform scrollbars stay hidden: position is shown by the pager instead.
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            scroll.style.flexGrow = 1;
            // The content container must SIZE TO ITS CHILDREN horizontally or scrollableWidth
            // computes as ~0; cross-axis is left at the default stretch, because collapsing
            // it breaks the height chain the trees' authored layout relies on.
            scroll.contentContainer.style.flexDirection = FlexDirection.Row;
            scroll.contentContainer.style.flexGrow = 0;
            scroll.contentContainer.style.flexShrink = 0;
            // Explicit NoWrap: something in the resolved style chain otherwise wraps the
            // content row at the viewport width.
            scroll.contentContainer.style.flexWrap = Wrap.NoWrap;
            // Position + the sweep-exempt class so WheelEvents bubbling from the trees find
            // a pickable scroller (PickingModeController forces everything else to Ignore).
            scroll.pickingMode = PickingMode.Position;
            scroll.AddToClassList(IGNORE_CLASS);

            row.Insert(row.IndexOf(toMove[0]), scroll);
            foreach (PerkTreeVE t in toMove)
            {
                row.Remove(t);
                scroll.Add(t);   // lands in contentContainer
            }

            EnsurePager(scroll, row);

            // The carousel owns all scrolling from here: the wheel steps a quarter of a
            // viewport (one tree) per notch and pad focus scrolls into view, both animating
            // through the same eased tween with the drawer slide/collapse pass. Stored on
            // userData so the NavigatorVE bridge finds it.
            TreeCarousel carousel = new TreeCarousel(scroll, row);
            scroll.userData = carousel;

            // Tree sizing waits for the viewport to resolve: a world-space panel lays out
            // long after this ctor runs, and only the live viewport width says what
            // "authored size" is in this document. Re-runs on every viewport geometry
            // change (resize, rebuild); idempotent per width.
            scroll.contentViewport.RegisterCallback<GeometryChangedEvent>(
                delegate { SizeTrees(scroll); carousel.Refresh(); });
            scroll.contentContainer.RegisterCallback<GeometryChangedEvent>(
                delegate { carousel.Refresh(); });
            SizeTrees(scroll);
            carousel.Refresh();

            if (Plugin.Verbose) Plugin.Log.LogInfo("Ectype: tree scroller installed (" + toMove.Count +
                                                   " trees wrapped).");
            return true;
        }

        // -------------------------------------------------------------------- the pager

        private const string PAGER_NAME = "EctypeTreePager";
        private const string STACK_NAME = "EctypeTreeStack";

        /// <summary>Sprites are looked up among those the process has already loaded, by
        /// name, and cached - misses included, so a wrong name costs one warning rather than
        /// a full scan every refresh.</summary>
        private static readonly Dictionary<string, Sprite> _sprites =
            new Dictionary<string, Sprite>();

        private static Sprite FindSprite(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Sprite found;
            if (_sprites.TryGetValue(name, out found)) return found;

            Sprite source = null;
            foreach (Sprite candidate in Resources.FindObjectsOfTypeAll<Sprite>())
                if (candidate != null && candidate.name == name) { source = candidate; break; }
            if (source == null)
                Plugin.Log.LogWarning("Ectype: no loaded sprite named '" + name + "' - that " +
                                      "pip layer is left blank.");
            else
            {
                // Log what was found, not that something was: a right-named sprite can still
                // be the wrong atlas region, and border/mesh type decide how it draws.
                Plugin.Log.LogInfo("Ectype: pip sprite '" + name + "' rect=" +
                                   source.rect + " texRect=" + source.textureRect +
                                   " border=" + source.border + " tex=" +
                                   (source.texture != null ? source.texture.name : "null"));
                found = Unsliced(source);
            }
            _sprites[name] = found;
            return found;
        }

        /// <summary>
        /// A copy of the sprite with no nine-slice border and a full-rect mesh, rebuilt from
        /// the same texture region so it works on an atlas and needs no readable texture.
        /// </summary>
        private static Sprite Unsliced(Sprite source)
        {
            try
            {
                if (source.texture == null) return source;
                if (source.border == Vector4.zero &&
                    source.packingMode == SpritePackingMode.Rectangle)
                    return source;

                Sprite copy = Sprite.Create(source.texture, source.textureRect,
                                            new Vector2(0.5f, 0.5f), source.pixelsPerUnit,
                                            0, SpriteMeshType.FullRect);
                copy.name = "Ectype_" + source.name + "_unsliced";
                UnityEngine.Object.DontDestroyOnLoad(copy);
                copy.hideFlags = HideFlags.HideAndDontSave;
                return copy;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: could not rebuild '" + source.name +
                                      "' without its border, using it as shipped: " +
                                      e.Message);
                return source;
            }
        }

        /// <summary>
        /// Build the pager to match the current tree count, and place it above the row.
        /// Rebuilt whole whenever the count changes rather than diffed, so a rebuild cannot
        /// leave a stale pip behind for a class that is no longer registered.
        ///
        /// The pager is IN FLOW, in a column whose width is the scroller's own, and centred
        /// by the layout engine. That is the whole of its placement.
        /// </summary>
        private static void EnsurePager(ScrollView scroll, VisualElement host)
        {
            LayoutSpec s = S;

            int trees = 0;
            foreach (VisualElement child in scroll.contentContainer.Children())
                if (child is PerkTreeVE) trees++;

            VisualElement stack = EnsureStack(scroll, host);
            VisualElement pager = stack.Q(PAGER_NAME);
            if (!s.showPager || trees == 0)
            {
                if (pager != null) pager.RemoveFromHierarchy();
                return;
            }

            if (pager == null)
            {
                pager = new VisualElement { name = PAGER_NAME };
                pager.pickingMode = PickingMode.Ignore;
                pager.style.flexDirection = FlexDirection.Row;
                pager.style.alignItems = Align.Center;
                // The centring, in one property: the column is exactly as wide as the
                // scroller, so its cross-axis centre is the centre of the viewport.
                pager.style.alignSelf = Align.Center;
                pager.style.flexShrink = 0;
                stack.Insert(0, pager);          // above the scroller in the column
            }
            pager.style.height = s.pagerPipHeightPx;
            pager.style.marginTop = s.pagerTopPx;
            pager.style.marginBottom = s.pagerGapPx;

            // The seam between the two middle trees sits off row centre by
            // (marginLeft - marginRight)/2 - derived, not measured, since every other term
            // cancels - and shifting a cross-axis-centred item left by d costs 2d of
            // trailing margin, hence the doubling.
            pager.style.marginRight = s.pagerSeamNudgePx;
            pager.style.marginLeft = 0f;

            if (pager.childCount != trees)
            {
                pager.Clear();
                for (int i = 0; i < trees; i++) pager.Add(MakePip());
            }

            Sprite fill = FindSprite(s.pagerFillSprite);
            Sprite outline = FindSprite(s.pagerOutlineSprite);
            for (int i = 0; i < pager.childCount; i++)
            {
                VisualElement pip = pager[i];
                pip.style.width = s.pagerPipWidthPx;
                pip.style.height = s.pagerPipHeightPx;
                pip.style.marginLeft = i == 0 ? 0f : s.pagerPipGapPx * 0.5f;
                pip.style.marginRight = i == pager.childCount - 1
                                            ? 0f : s.pagerPipGapPx * 0.5f;
                ApplySprite(pip, fill);
                if (pip.childCount > 0) ApplySprite(pip[0], outline);
            }
        }

        /// <summary>
        /// The column that holds the pager above the scroller, so the pager is centred on the
        /// SCROLLER's width rather than the wider tree row's - a column's default cross-axis
        /// stretch makes the two the same width by construction.
        /// </summary>
        private static VisualElement EnsureStack(ScrollView scroll, VisualElement host)
        {
            VisualElement existing = scroll.parent;
            if (existing != null && existing.name == STACK_NAME) return existing;

            VisualElement stack = new VisualElement { name = STACK_NAME };
            stack.style.flexDirection = FlexDirection.Column;
            stack.style.flexGrow = 1;
            stack.pickingMode = PickingMode.Ignore;

            VisualElement parent = scroll.parent ?? host;
            int at = parent.IndexOf(scroll);
            scroll.RemoveFromHierarchy();
            parent.Insert(Mathf.Clamp(at, 0, parent.childCount), stack);
            stack.Add(scroll);
            return stack;
        }

        /// <summary>A pip: the fill sprite, with the outline sprite laid over it at the same
        /// size. Two elements rather than one so each can be tinted independently, which is
        /// what lets a lit pip read as a filled diamond and an unlit one as an empty
        /// outline.</summary>
        private static VisualElement MakePip()
        {
            VisualElement pip = new VisualElement();
            pip.pickingMode = PickingMode.Ignore;
            VisualElement outline = new VisualElement();
            outline.pickingMode = PickingMode.Ignore;
            outline.style.position = Position.Absolute;
            outline.style.left = 0f;
            outline.style.right = 0f;
            outline.style.top = 0f;
            outline.style.bottom = 0f;
            pip.Add(outline);
            return pip;
        }

        /// <summary>
        /// A pip's background, scaled whole. These sprites are diamonds authored with a border
        /// of exactly half their width and height, so slicing one renders it as a square;
        /// slices are zeroed at the sprite (see Unsliced), never in style.
        /// </summary>
        private static void ApplySprite(VisualElement el, Sprite sprite)
        {
            if (sprite == null) return;
            el.style.backgroundImage = new StyleBackground(sprite);
            el.style.backgroundSize =
                new StyleBackgroundSize(new BackgroundSize(BackgroundSizeType.Contain));
        }

        /// <summary>
        /// Pin every tree to exactly viewport/treesPerPage (minus its own margins), with
        /// flexGrow/flexShrink 0 so authored grow=1 cannot stretch the set. Never a hardcoded
        /// width: a tree's internals size proportionally to its own width, so a
        /// document-foreign constant re-scales every tree.
        /// </summary>
        private static void SizeTrees(ScrollView scroll)
        {
            try
            {
                float vw = scroll.contentViewport.layout.width;
                if (float.IsNaN(vw) || vw < 100f) return;   // not laid out yet

                int perPage = Mathf.Max(1, S.treesPerPage);
                foreach (VisualElement child in scroll.contentContainer.Children())
                {
                    PerkTreeVE t = child as PerkTreeVE;
                    if (t == null) continue;
                    float margins = 0f;
                    float ml = t.resolvedStyle.marginLeft, mr = t.resolvedStyle.marginRight;
                    if (!float.IsNaN(ml)) margins += ml;
                    if (!float.IsNaN(mr)) margins += mr;
                    // Unrounded: flooring discards up to perPage-1 px, which desymmetrises the
                    // trees about the viewport centre and breaks the pager's seam offset.
                    t.style.width = vw / perPage - margins;
                    t.style.flexGrow = 0;
                    t.style.flexShrink = 0;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: tree sizing failed: " + e);
            }
        }

        /// <summary>
        /// The animated scroll driver for one document's tree row. All inputs land on one
        /// eased tween: the wheel steps a quarter of a viewport (one tree) per notch, and pad
        /// focus scrolls the focused tree into view. Each frame of motion also runs the
        /// drawer pass, sliding a tree shut as it leaves the viewport and open as it enters.
        /// </summary>
        internal sealed class TreeCarousel
        {
            private readonly ScrollView _scroll;
            private IVisualElementScheduledItem _ticker;
            private float _target;
            private float _animStart;                // offset the current tween left from
            private float _animStartTime;            // Time.unscaledTime at tween start

            public TreeCarousel(ScrollView scroll, VisualElement host)
            {
                _scroll = scroll;
                _target = scroll.scrollOffset.x;

                // TrickleDown so this sees the wheel before the ScrollView's own handler
                // (which would jump in raw pixel deltas with no animation).
                _scroll.RegisterCallback<WheelEvent>(OnWheel, TrickleDown.TrickleDown);
            }

            private void OnWheel(WheelEvent evt)
            {
                try
                {
                    float step = S.wheelStepPages;
                    if (evt.delta.y > 0f) Page(step);
                    else if (evt.delta.y < 0f) Page(-step);
                    evt.StopImmediatePropagation();
                    evt.PreventDefault();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Ectype: wheel step failed: " + e);
                }
            }

            private float Viewport()
            {
                float vw = _scroll.contentViewport.layout.width;
                return float.IsNaN(vw) ? 0f : vw;
            }

            private float MaxOffset()
            {
                float cw = _scroll.contentContainer.layout.width;
                if (float.IsNaN(cw)) cw = 0f;
                return Mathf.Max(0f, cw - Viewport());
            }

            /// <summary>1.0 = one full viewport; 0.25 = one tree (the wheel step).</summary>
            public void Page(float pages)
            {
                float vw = Viewport();
                ScrollToOffset(_target + pages * (vw > 1f ? vw : 400f));
            }

            public void ScrollToOffset(float x)
            {
                _animStart = _scroll.scrollOffset.x;
                _animStartTime = Time.unscaledTime;
                _target = Mathf.Clamp(x, 0f, MaxOffset());
                if (_ticker == null)
                    _ticker = _scroll.schedule.Execute((Action)Tick).Every(16);
                else
                    _ticker.Resume();
            }

            /// <summary>y of the easing bezier at horizontal position p (0..1). x(t) is
            /// solved by Newton with a bisection guard - the standard CSS timing-function
            /// evaluation.</summary>
            private static float Ease(float p)
            {
                if (p <= 0f) return 0f;
                if (p >= 1f) return 1f;
                LayoutSpec s = S;
                // Coefficients for x(t) = ((ax*t + bx)*t + cx)*t, same for y.
                float cx = 3f * s.easeP1x, bx = 3f * (s.easeP2x - s.easeP1x) - cx,
                      ax = 1f - cx - bx;
                float cy = 3f * s.easeP1y, by = 3f * (s.easeP2y - s.easeP1y) - cy,
                      ay = 1f - cy - by;
                float t = p;
                for (int i = 0; i < 8; i++)
                {
                    float xt = ((ax * t + bx) * t + cx) * t - p;
                    if (Mathf.Abs(xt) < 0.0005f) break;
                    float dx = (3f * ax * t + 2f * bx) * t + cx;
                    if (Mathf.Abs(dx) < 0.000001f) break;
                    t = Mathf.Clamp01(t - xt / dx);
                }
                return ((ay * t + by) * t + cy) * t;
            }

            /// <summary>Pad bridge: smallest motion that makes the focused tree fully
            /// visible, through the same animation.</summary>
            public void ScrollToElement(VisualElement el)
            {
                PerkTreeVE tree = (el as PerkTreeVE) ?? el.GetFirstAncestorOfType<PerkTreeVE>();
                if (tree == null) return;
                float lo = tree.layout.x - 8f;
                float hi = tree.layout.x + tree.layout.width + 8f;
                if (lo < _target) ScrollToOffset(lo);
                else if (hi > _target + Viewport()) ScrollToOffset(hi - Viewport());
            }

            /// <summary>Geometry changed (sizing pass, tree count, resize): re-clamp the
            /// target and re-dress at the current offset.</summary>
            public void Refresh()
            {
                try
                {
                    _target = Mathf.Clamp(_target, 0f, MaxOffset());
                    UpdateVisuals(_scroll.scrollOffset.x);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Ectype: carousel refresh failed: " + e);
                }
            }

            private void Tick()
            {
                try
                {
                    float duration = S.scrollDurationSec;
                    float p = duration <= 0f ? 1f
                        : Mathf.Clamp01((Time.unscaledTime - _animStartTime) / duration);
                    float next = Mathf.Lerp(_animStart, _target, Ease(p));
                    if (p >= 1f)
                    {
                        next = _target;
                        if (_ticker != null) _ticker.Pause();
                    }
                    _scroll.scrollOffset = new Vector2(next, 0f);
                    UpdateVisuals(next);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Ectype: carousel tick failed: " + e);
                    if (_ticker != null) _ticker.Pause();
                }
            }

            /// <summary>
            /// The drawer pass: an exiting tree slides ahead of the scroll and collapses
            /// horizontally by its hidden fraction; an entering one does the reverse.
            /// Visibility is computed from layout coordinates plus the scroll offset - never
            /// worldBound, which already contains the translate and would compound it.
            /// This owns the PerkTreeVE's own transform and nothing else's; the header band
            /// is a transform on the tree's CONTENT children, written by a separate pass.
            /// Every property is written unconditionally on every pass, so transforms cannot
            /// accumulate across rewires.
            /// </summary>
            private void UpdateVisuals(float offset)
            {
                float vw = Viewport();
                if (vw < 1f) return;
                LayoutSpec s = S;
                VisualElement pager = _scroll.parent != null
                    ? _scroll.parent.Q(PAGER_NAME) : null;
                int index = 0;
                foreach (VisualElement child in _scroll.contentContainer.Children())
                {
                    PerkTreeVE t = child as PerkTreeVE;
                    if (t == null) continue;
                    float w = Mathf.Max(1f, t.layout.width);
                    float lo = t.layout.x - offset;
                    float hi = lo + w;
                    float visible = Mathf.Clamp(Mathf.Min(hi, vw) - Mathf.Max(lo, 0f), 0f, w);
                    float f = visible / w;
                    float eased = f * f * (3f - 2f * f);   // smoothstep
                    float dir = (lo + hi) * 0.5f < vw * 0.5f ? -1f : 1f;
                    float drawerX = s.drawerMinScaleX + (1f - s.drawerMinScaleX) * eased;

                    t.style.transformOrigin =
                        new TransformOrigin(new Length(50f, LengthUnit.Percent), 0f, 0f);
                    t.style.opacity = 1f;
                    t.style.translate = new Translate(
                        (1f - eased) * s.drawerSlidePx * dir, 0f, 0f);
                    t.style.scale = new Scale(new Vector2(drawerX, 1f));

                    // The pip is lit from the same visible fraction the drawer pass just
                    // computed; a second pass recomputing it could disagree mid-tween.
                    if (pager != null && index < pager.childCount)
                        SetPipLit(pager[index], f >= s.pagerLitFraction, s);
                    index++;
                }
            }

            /// <summary>Both tints written unconditionally from the state, so a pip cannot
            /// keep a stale colour when the fraction crosses back.</summary>
            private static void SetPipLit(VisualElement pip, bool lit, LayoutSpec s)
            {
                pip.style.unityBackgroundImageTintColor = LayoutSpec.ParseColor(
                    lit ? s.pagerLitFillHex : s.pagerUnlitFillHex, Color.grey);
                if (pip.childCount > 0)
                    pip[0].style.unityBackgroundImageTintColor = LayoutSpec.ParseColor(
                        lit ? s.pagerLitOutlineHex : s.pagerUnlitOutlineHex, Color.grey);
            }

        }
    }

    /// <summary>
    /// Pad support. Controller users never scroll - they move the game's spatial navigator,
    /// and every focus change funnels through NavigatorVE.TryFocusElement. When focus lands
    /// inside the tree scroller, scroll the owning tree column into view. Mouse users
    /// scroll with the wheel.
    /// </summary>
    [HarmonyPatch(typeof(NavigatorVE), "TryFocusElement")]
    internal static class Patch_NavigatorVE_TryFocusElement
    {
        // POSITIONAL binding (__0), never the game's parameter name - which has been
        // renamed before ("element" -> "ve"), and a mismatch aborts PatchAll.
        [HarmonyPostfix]
        private static void Postfix(VisualElement __0, bool __result)
        {
            try
            {
                if (!__result || __0 == null) return;
                VisualElement element = __0;
                ScrollView scroll = element.GetFirstAncestorOfType<ScrollView>();
                if (scroll == null || scroll.name != Presentation.SCROLL_NAME) return;
                Presentation.TreeCarousel carousel =
                    scroll.userData as Presentation.TreeCarousel;
                if (carousel != null) carousel.ScrollToElement(element);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: focus scroll-into-view failed: " + e);
            }
        }
    }
}
