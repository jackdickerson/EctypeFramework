using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// Loads icons from PNGs embedded in a role mod's assembly. Sprites are created once and
    /// cached per (assembly, key); a missing resource degrades to null so callers fall back
    /// to a donor icon or to the framework's own placeholder art rather than a blank cell.
    /// </summary>
    internal static class IconStore
    {
        private static readonly Dictionary<string, Sprite> _cache =
            new Dictionary<string, Sprite>();

        public static Sprite Get(Assembly asm, string key)
        {
            if (asm == null || string.IsNullOrEmpty(key)) return null;
            string cacheKey = asm.GetName().Name + "|" + key;
            Sprite s;
            if (_cache.TryGetValue(cacheKey, out s)) return s;
            s = Load(asm, key);
            _cache[cacheKey] = s;   // cache misses too - one log line, not one per retry
            return s;
        }

        /// <summary>The framework's placeholder for a slot the role supplies nothing for:
        /// 512x512 for square slots, 2048x512 for wide ones. Built exactly like a role icon
        /// (centre pivot, 100 PPU, no mip chain) so it scales the same.</summary>
        public static Sprite Placeholder(bool wide)
        {
            return Get(typeof(IconStore).Assembly,
                       wide ? "placeholder-wide" : "placeholder-square");
        }

        /// <summary>The stock role icons' glyphs span 0.61-0.71 of their canvas (155-182px of
        /// 256), and the crew HUD draws that canvas scale-to-fit into a 16px-tall box. A role
        /// glyph is fitted to the top of that range.</summary>
        internal const float RoleGlyphFill = 0.70f;

        /// <summary>
        /// A role icon: the same PNG as <see cref="Get"/>, but the sprite rect is a square
        /// centred on the glyph's alpha bounds and sized so the glyph spans
        /// <see cref="RoleGlyphFill"/> of it. The slots that draw a role icon scale the sprite
        /// rect, not the glyph, so a glyph drawn small inside its canvas draws small on the HUD;
        /// fitting the rect makes it draw at the stock role icons' scale whatever margin it was
        /// authored with. A glyph already at or above that fill keeps the full rect.
        /// </summary>
        public static Sprite GetRoleIcon(Assembly asm, string key)
        {
            if (asm == null || string.IsNullOrEmpty(key)) return null;
            string cacheKey = asm.GetName().Name + "|" + key + "|role";
            Sprite s;
            if (_cache.TryGetValue(cacheKey, out s)) return s;
            Sprite full = Get(asm, key);
            s = full != null ? FitToGlyph(full, key) : null;
            _cache[cacheKey] = s;
            return s;
        }

        private static Sprite FitToGlyph(Sprite full, string key)
        {
            try
            {
                Texture2D tex = full.texture;
                int w = tex.width, h = tex.height;
                Color32[] px = tex.GetPixels32();
                int x0 = w, y0 = h, x1 = -1, y1 = -1;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        if (px[y * w + x].a > 8)
                        {
                            if (x < x0) x0 = x;
                            if (x > x1) x1 = x;
                            if (y < y0) y0 = y;
                            if (y > y1) y1 = y;
                        }
                if (x1 < 0) return full;   // fully transparent: nothing to fit

                int extent = Math.Max(x1 - x0 + 1, y1 - y0 + 1);
                int side = Mathf.CeilToInt(extent / RoleGlyphFill);
                if (side >= Math.Min(w, h)) return full;

                // Centre on the glyph, then slide inside the texture if it overhangs an edge.
                int rx = Mathf.Clamp(Mathf.RoundToInt((x0 + x1 + 1) * 0.5f - side * 0.5f), 0, w - side);
                int ry = Mathf.Clamp(Mathf.RoundToInt((y0 + y1 + 1) * 0.5f - side * 0.5f), 0, h - side);
                Sprite fitted = Sprite.Create(tex, new Rect(rx, ry, side, side),
                                              new Vector2(0.5f, 0.5f), 100f);
                fitted.name = full.name + "_role";
                UnityEngine.Object.DontDestroyOnLoad(fitted);
                fitted.hideFlags = HideFlags.HideAndDontSave;
                Plugin.Log.LogInfo("Ectype: role icon '" + key + "' glyph spans " + extent +
                                   "px of its " + w + "x" + h + " canvas; sprite rect fitted to " +
                                   side + "x" + side + " so it draws at the stock role icons' scale.");
                return fitted;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: fitting role icon '" + key + "' failed, using the " +
                                      "full canvas: " + e.Message);
                return full;
            }
        }

        private static Sprite Load(Assembly asm, string key)
        {
            try
            {
                string wanted = key + ".png";
                string resName = null;
                foreach (string candidate in asm.GetManifestResourceNames())
                    if (candidate.EndsWith(wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        resName = candidate;
                        break;
                    }
                if (resName == null)
                {
                    Plugin.Log.LogWarning("Ectype: embedded icon '" + wanted + "' not found " +
                                          "in " + asm.GetName().Name +
                                          " - falling back.");
                    return null;
                }

                byte[] bytes;
                using (Stream st = asm.GetManifestResourceStream(resName))
                using (MemoryStream ms = new MemoryStream())
                {
                    st.CopyTo(ms);
                    bytes = ms.ToArray();
                }

                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(bytes))
                {
                    Plugin.Log.LogWarning("Ectype: could not decode icon '" + wanted + "'.");
                    return null;
                }
                tex.name = "Ectype_" + key;
                tex.wrapMode = TextureWrapMode.Clamp;
                UnityEngine.Object.DontDestroyOnLoad(tex);
                tex.hideFlags = HideFlags.HideAndDontSave;

                Sprite sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                                              new Vector2(0.5f, 0.5f), 100f);
                sprite.name = tex.name;
                UnityEngine.Object.DontDestroyOnLoad(sprite);
                sprite.hideFlags = HideFlags.HideAndDontSave;
                return sprite;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: icon load '" + key + "' failed: " + e.Message);
                return null;
            }
        }
    }
}
