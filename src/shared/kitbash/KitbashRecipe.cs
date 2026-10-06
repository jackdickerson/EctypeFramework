using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

// Deliberately in the global namespace, same reasoning as ModFlagPolicy: shared source
// is compiled into each opting-in mod, every mod has its own root namespace, and
// `internal` gives each assembly a private copy with no collisions.
//
// This file is the parser half and depends only on UnityEngine.CoreModule
// (Vector3/Transform); the applier half is KitbashCore.cs.

/// <summary>
/// One kitbash recipe: clone a donor subtree and graft it onto a target module.
///
/// Grammar: ';'-separated `key=value` fields. All float I/O is invariant-culture, so
/// recipe text is portable across locales. Unknown keys and malformed fields set
/// <see cref="ParseError"/> rather than throwing.
/// </summary>
internal sealed class KitbashRecipe
{
    public string Id = "kb";
    public string Target = "";
    public string DonorPrefab = "@self";   // "@self" or a prefab name/GUID query
    public string DonorPath = "";          // literal Find path, or "*Substring" subtree search
    public string ParentPath = null;       // null => donor's parent (@self) / target root
    public Vector3? Pos, Rot, Scale;
    public Vector3 Offset = Vector3.zero;
    public Vector3 ROffset = Vector3.zero;
    public Vector3 SFactor = Vector3.one;
    public bool Full;
    /// <summary>User-given display name, carried through save and load. Never the GameObject's
    /// name: the applier matches on the real name. Written only
    /// when non-empty. ';' and '=' are the field separators of this grammar and are therefore
    /// not representable in a caption; whatever sets one strips them.</summary>
    public string Caption = "";
    /// <summary>Id of the group record this part hangs under in a grouped asset document, or
    /// empty for a part directly under the asset root. Meaningless in a single recipe.</summary>
    public string GroupId = "";
    public string ParseError;

    /// <summary>Name given to the cloned GameObject. Also the idempotency key: a second
    /// recipe with the same id on the same module silently never applies, so ids in
    /// shareable documents should be namespaced (e.g. "mymod_port_l").</summary>
    public string CloneName { get { return "KB_" + Id; } }

    public static List<KitbashRecipe> ParseAll(string raw)
    {
        List<KitbashRecipe> list = new List<KitbashRecipe>();
        if (string.IsNullOrEmpty(raw)) return list;
        string[] chunks = raw.Split('|');
        for (int i = 0; i < chunks.Length; i++)
        {
            string chunk = chunks[i].Trim();
            if (chunk.Length == 0) continue;
            KitbashRecipe r = Parse(chunk, i);
            list.Add(r);
        }
        return list;
    }

    public static KitbashRecipe Parse(string chunk, int index)
    {
        KitbashRecipe r = new KitbashRecipe();
        r.Id = "kb" + index;
        try
        {
            string[] fields = chunk.Split(';');
            for (int i = 0; i < fields.Length; i++)
            {
                string f = fields[i].Trim();
                if (f.Length == 0) continue;
                int eq = f.IndexOf('=');
                if (eq <= 0) { r.ParseError = "field without '=': " + f; continue; }
                string key = f.Substring(0, eq).Trim().ToLowerInvariant();
                string val = f.Substring(eq + 1).Trim();

                switch (key)
                {
                    case "id": r.Id = val; break;
                    case "target": r.Target = val; break;
                    case "donor": SplitDonor(r, val); break;
                    case "parent": r.ParentPath = val; break;
                    case "pos": r.Pos = Vec(val, Vector3.zero); break;
                    case "rot": r.Rot = Vec(val, Vector3.zero); break;
                    case "scale": r.Scale = Vec(val, Vector3.one); break;
                    case "offset": r.Offset = Vec(val, Vector3.zero); break;
                    case "roffset": r.ROffset = Vec(val, Vector3.zero); break;
                    case "sfactor": r.SFactor = Vec(val, Vector3.one); break;
                    case "caption": r.Caption = val; break;
                    case "group": r.GroupId = val; break;
                    case "mode": r.Full = val.Equals("full", StringComparison.OrdinalIgnoreCase); break;
                    default: r.ParseError = "unknown key '" + key + "'"; break;
                }
            }
        }
        catch (Exception e)
        {
            r.ParseError = e.Message;
        }

        if (string.IsNullOrEmpty(r.Target)) r.ParseError = "no target";
        if (string.IsNullOrEmpty(r.DonorPath)) r.ParseError = "no donor path";
        return r;
    }

    private static void SplitDonor(KitbashRecipe r, string val)
    {
        int colon = val.IndexOf(':');
        if (colon < 0) { r.DonorPrefab = "@self"; r.DonorPath = val; return; }
        r.DonorPrefab = val.Substring(0, colon).Trim();
        r.DonorPath = val.Substring(colon + 1).Trim();
        if (r.DonorPrefab.Length == 0) r.DonorPrefab = "@self";
    }

    /// <summary>
    /// A single number means "all three axes" - handy for sfactor=0.5.
    /// </summary>
    private static Vector3 Vec(string s, Vector3 fallback)
    {
        if (string.IsNullOrEmpty(s)) return fallback;
        string[] parts = s.Split(',');
        float x, y, z;
        if (parts.Length == 1)
        {
            if (F(parts[0], out x)) return new Vector3(x, x, x);
            return fallback;
        }
        if (parts.Length < 3) return fallback;
        if (!F(parts[0], out x)) return fallback;
        if (!F(parts[1], out y)) return fallback;
        if (!F(parts[2], out z)) return fallback;
        return new Vector3(x, y, z);
    }

    private static bool F(string s, out float v)
    {
        return float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    /// <summary>
    /// Serialize exactly the fields this recipe carries - the inverse of Parse, used by
    /// document save. Defaults (zero offset, unit sfactor, visual mode, unset TRS) are
    /// omitted so a parsed recipe round-trips to the same line.
    /// </summary>
    public string FieldLine()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("id=").Append(Id);
        sb.Append("; target=").Append(Target);
        sb.Append("; donor=").Append(DonorPrefab).Append(':').Append(DonorPath);
        if (ParentPath != null) sb.Append("; parent=").Append(ParentPath);
        if (Pos.HasValue) sb.Append("; pos=").Append(V(Pos.Value));
        if (Rot.HasValue) sb.Append("; rot=").Append(V(Rot.Value));
        if (Scale.HasValue) sb.Append("; scale=").Append(V(Scale.Value));
        if (Offset != Vector3.zero) sb.Append("; offset=").Append(V(Offset));
        if (ROffset != Vector3.zero) sb.Append("; roffset=").Append(V(ROffset));
        if (SFactor != Vector3.one) sb.Append("; sfactor=").Append(V(SFactor));
        if (!string.IsNullOrEmpty(Caption)) sb.Append("; caption=").Append(Caption);
        if (!string.IsNullOrEmpty(GroupId)) sb.Append("; group=").Append(GroupId);
        if (Full) sb.Append("; mode=full");
        return sb.ToString();
    }

    /// <summary>
    /// Recipe text describing a clone's CURRENT transform - what the nudge keys emit.
    /// The TRS is the clone's LOCAL transform, i.e. measured in its parent's frame;
    /// pass <paramref name="parentPath"/> (the parent's path under the target module)
    /// so the emitted line names the frame it was measured in.
    /// </summary>
    public string ToRecipeString(Transform t, string parentPath)
    {
        string donor = DonorPrefab + ":" + DonorPath;
        string parent = parentPath != null ? parentPath : ParentPath;
        return "id=" + Id + "; target=" + Target + "; donor=" + donor +
               (!string.IsNullOrEmpty(parent) ? "; parent=" + parent : "") +
               "; pos=" + V(t.localPosition) + "; rot=" + V(t.localEulerAngles) +
               "; scale=" + V(t.localScale) +
               (Full ? "; mode=full" : "");
    }

    public string ToRecipeString(Transform t) { return ToRecipeString(t, null); }

    private static string V(Vector3 v)
    {
        return v.x.ToString("0.####", CultureInfo.InvariantCulture) + "," +
               v.y.ToString("0.####", CultureInfo.InvariantCulture) + "," +
               v.z.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
