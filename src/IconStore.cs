using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Ectype
{
    /// <summary>
    /// Loads icons from PNGs embedded in a class mod's assembly. Sprites are created once and
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

        /// <summary>The framework's placeholder for a slot the class supplies nothing for:
        /// 512x512 for square slots, 2048x512 for wide ones. Built exactly like a class icon
        /// (centre pivot, 100 PPU, no mip chain) so it scales the same.</summary>
        public static Sprite Placeholder(bool wide)
        {
            return Get(typeof(IconStore).Assembly,
                       wide ? "placeholder-wide" : "placeholder-square");
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
