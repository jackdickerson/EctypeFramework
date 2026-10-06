using UnityEngine;

// Deliberately in the global namespace, compiled into each mod that opts in from its
// csproj. A shared DLL would need every consumer to load it at the same version and in
// the same order relative to BepInEx's plugin scan; a scene marker needs neither - whichever mod's
// Awake/Update runs first finds or creates it, and every other mod's dev-key
// poll sees the same GameObject.
//
// A mod's dev-key handler (F9..F12, etc.) checks DevKeyGate.Open first and
// returns early when it is false. VoidManager's F5 settings page is not a dev
// key and never consults this gate.

/// <summary>
/// Scene-marker gate that lets one owner (a tool that takes over the keyboard) put every
/// mod's dev-key polling to sleep without either side knowing about the other's assembly.
/// </summary>
internal static class DevKeyGate
{
    /// Fixed name of the marker GameObject. Whoever closes the gate creates it (DontDestroyOnLoad);
    /// releasing destroys it.
    public const string MarkerName = "VoidCrew.DevKeyGate.Closed";

    // The marker reference is found once and cached; when the cached reference is null the scene is
    // re-probed at most every ProbeInterval seconds (unscaled), because GameObject.Find walks the
    // whole scene and this is polled from Update in several mods.
    private const float ProbeInterval = 0.5f;
    private static GameObject _marker;
    private static bool _closed;
    private static float _nextProbe;

    /// True when no owner has closed the gate. A dev-key poll checks this first and returns early
    /// when false. Never throws.
    public static bool Open
    {
        get
        {
            try
            {
                if (_marker != null) return false;
                if (Time.unscaledTime >= _nextProbe)
                {
                    _nextProbe = Time.unscaledTime + ProbeInterval;
                    _marker = GameObject.Find(MarkerName);
                    _closed = _marker != null;
                }
                return !_closed;
            }
            catch { return true; }
        }
    }

    /// Close the gate: create the marker if it does not exist. Idempotent. Never throws (log nothing; the caller logs).
    public static void Close()
    {
        try
        {
            GameObject go = GameObject.Find(MarkerName);
            if (go == null)
            {
                go = new GameObject(MarkerName);
                Object.DontDestroyOnLoad(go);
            }
            _marker = go;
            _closed = true;
        }
        catch { }
    }

    /// Open the gate: destroy the marker if it exists (find it even if this copy did not create it). Idempotent. Never throws.
    public static void Release()
    {
        try
        {
            GameObject go = _marker != null ? _marker : GameObject.Find(MarkerName);
            if (go != null) Object.Destroy(go);
            _marker = null;
            _closed = false;
            _nextProbe = 0f;
        }
        catch { }
    }
}
