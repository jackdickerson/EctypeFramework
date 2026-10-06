using System;
using System.Collections.Generic;

using UnityEngine;

// Deliberately in the global namespace, same reasoning as ModFlagPolicy: this file is
// compiled into each mod that opts in from its csproj, and each mod has its own root
// namespace. `internal` means every assembly gets its own private copy, so there is no
// collision between mods and nothing here is part of any mod's public surface.

/// <summary>
/// IMGUI building blocks for a mod's VoidManager F5 settings page.
///
/// The rule is ONE ModSettingsMenu per mod. VoidManager instantiates every subclass it
/// finds, unconditionally, so a second menu class is always a second button in the Mod
/// Settings list - top-level entries are the scarce resource, and a "- Dev" sibling per
/// mod doubles the list. Dev tooling therefore lives INSIDE the single page, in a
/// DevZone at the bottom of Draw(): invisible (not even a hint line) unless
/// VoidManager's own DebugMode is on, and a collapsed foldout even when it is, so the
/// player-facing controls stay above the fold in every state. DebugMode is re-read
/// every Draw(), so flipping it in Void Manager Settings takes effect immediately.
/// </summary>
internal static class MenuKit
{
    private static readonly Dictionary<string, bool> foldouts = new Dictionary<string, bool>();
    private static readonly HashSet<string> loggedFailures = new HashSet<string>();

    /// <summary>Bold section header with breathing room above. Group related controls
    /// under these instead of relying on blank lines.</summary>
    public static void Header(string text)
    {
        GUILayout.Space(10f);
        GUILayout.Label("<b>" + text + "</b>");
    }

    /// <summary>A dim 1px horizontal rule.</summary>
    public static void Rule()
    {
        GUILayout.Space(6f);
        Rect r = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true));
        Color prev = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, 0.25f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = prev;
        GUILayout.Space(6f);
    }

    /// <summary>Collapsible section. `id` keys the open/closed state for the process
    /// lifetime; prefix it with the mod name so mods never collide.</summary>
    public static bool Foldout(string id, string label)
    {
        bool open;
        foldouts.TryGetValue(id, out open);
        if (GUILayout.Button((open ? "[-] " : "[+] ") + label))
        {
            open = !open;
            foldouts[id] = open;
        }
        return open;
    }

    /// <summary>
    /// The dev surface. Call last in Draw(). Renders nothing when VoidManager's
    /// DebugMode is off; when on, a rule plus a collapsed "Developer tools" foldout
    /// whose body is boxed and exception-isolated (a throwing dump button degrades to
    /// an error label instead of truncating the whole page - and the box is closed in
    /// finally, so the layout stack stays balanced).
    /// </summary>
    public static void DevZone(string id, Action body)
    {
        if (!VoidManager.Configs.IsDebugMode) return;

        GUILayout.Space(12f);
        Rule();
        if (!Foldout(id + ".dev", "Developer tools")) return;

        GUILayout.BeginVertical(GUI.skin.box);
        try
        {
            body();
        }
        catch (Exception e)
        {
            GUILayout.Label("Dev section failed: " + e.Message + " (see log)");
            if (loggedFailures.Add(id))
                Debug.LogError("[MenuKit] dev zone '" + id + "' failed: " + e);
        }
        finally
        {
            GUILayout.EndVertical();
        }
    }
}
