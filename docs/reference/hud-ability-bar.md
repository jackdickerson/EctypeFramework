# HUD ability bar: extra ability buttons beside the role ability

`HudAbilityBar` draws a row of square buttons to the right of the role-ability widget, at
most `HudAbilityBar.MaxButtons` (4, a constant in the framework). A mod registers a button
and keeps the ability, its input and its cooldown. The framework builds the button, lays out
the row, draws the key chip and feeds the fill.

```csharp
// once, e.g. at plugin Awake; safe before the HUD exists
HudAbilityBar.Handle h = HudAbilityBar.Register(new HudButtonSpec
{
    Id = "mymod.dash",                  // unique; namespace it with your GUID
    Glyph = myTexture,                  // square, white on transparent
    ReadyColor = new Color(0.3f, 0.8f, 1f),   // null = the game's orange
    Order = 0,                          // left to right, ties on Id
    Key = () => Key.Q,                  // drawn on the chip, read every frame
    Fill = () => cooldownLeft / cooldownTotal,  // 0 = ready, 1 = just used
    Visible = () => dashInstalled,      // false hides it and closes the gap
});
h.Dispose();                            // removes it
```

`Register` returns null and logs once when the bar is full (naming the ids holding it), when
the id is taken, or when the spec has no id. A callback that throws hides its own button for
the session and logs once; the rest of the bar keeps running. The bar is presentation only:
nothing is networked or registered, so it cannot flag a session.

## What a button is

- **A copy of the HUD's authored hero element**, instantiated from the document's template and
  not cloned from the live tree. It keeps its authored name, because the stylesheets select
  its art by element ID; a marker class (`ectype-hud-bar-button`) identifies it. The game
  queries that name once, depth-first from the container, and finds its own element first.
- **The game's own state machine.** Writing `PerkBuffItemVE.PerkFill` runs the element's
  handler. It reveals the fill from the left, adds `perk-ready` below 0.01, starts the ready
  glow (flipping the glow tint's alpha every second, smoothed by the `perk-glow` stylesheet's
  1 s transition), and removes `perk-ready` above 0.99. The bar never animates anything.
- **Colour on the fill and glow.** The ready plate is `Fill` covering `PerkItem`, and the
  stock fill is white while recovering and orange under `perk-ready`. An inline tint beats
  that rule, so `ReadyColor` goes on `Fill` only while the element carries `perk-ready`, and
  on the glow once. `IdleColor` tints the dark base plate.
- **The game's chrome, 9-sliced square.** `WideHeroBtn_background`, `_border`,
  `_border_shadow` and `_glow` have chamfered ends. Each layer's resolved image is re-assigned
  inline as its backing Texture2D and sliced at 190 texels (glow 250), with
  `unitySliceScale = side / 512`, the authored widget's own scale. A Sprite would scale its
  slices by an extra `referenceSpritePixelsPerUnit / pixelsPerUnit`. Every sliced layer's
  background-size is pinned to 100%, and the plate is pinned to fill the button.
- **The glyph** on `Icon` and `IconFilled` at 76% of the button. A square glyph that size
  still reaches past the chamfer at its corners, where the white base copy shows against the
  HUD, so the bar draws a copy with everything outside the frame's inner octagon cleared (the
  chamfer's inner edge is the line x + y = 225 texels on the 512-tall border). That needs a
  readable texture, which any texture a mod creates or loads is. An unreadable one is drawn as
  given, with one warning.
- **The key chip** points at an `InputAction` bound to the button's key, so the game renders
  it. The action is constructed with no binding and the binding added after, because a binding
  path in the constructor leaves `interactions` null and the chip's hold lookup throws. The
  chip is anchored at `left: 100%; top: 100%` with a -50% translate, so its centre sits on the
  corner without the bar ever reading its size back.

## Placement

Placement follows the height-matched HUD scale: a button's side is the role-ability plate's
height, read live. Each button is preceded by one gap plus how far the previous element's key
chip reaches past its plate, halved: the chips overhang their own corner, so the full
clearance reads as too much air.

The role-ability widget's chip is moved to the same convention, centred on its plate's
bottom-right corner and in front, for stock and framework-built slots alike. The game lays
that chip out in a flex row and never writes its position, so taking it out of flow holds. Out
of flow it no longer pushes the plate right, so the widget gets a left padding of the chip's
row width, measured once while the chip was still in the row, and the plate stays where the
game put it. A framework-built role slot follows the same colour rule: its accent goes on the
glow, and on the fill while ready.

No placement reads back a size that the placement itself changes. A chip's measured width
comes out a sub-pixel different depending on where it lands, so a position computed from it
flips between two values every few frames. Chip widths are measured once, chips are anchored
with percentages, and every other write is skipped when the value is unchanged.

Sample: [examples/hud-ability-bar](../../examples/hud-ability-bar/).
