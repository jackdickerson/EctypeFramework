using System;
using System.Collections;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ectype
{
    /// <summary>
    /// Reads a compiled StyleSheet back into readable USS. No public API reports what a
    /// rule contains: a StyleSheet is a ScriptableObject of plain serialized arrays, and
    /// every value in a declaration is stored as a typed index into one of them - floats,
    /// dimensions, colors, assets and strings each in their own array. Decoding a
    /// declaration therefore means following the handle's type to the right array, which is
    /// the same traversal the palette clone path performs when it writes one.
    ///
    /// Every member here is a pure read over an asset. Nothing in this file touches a live
    /// element, so it can be exercised against any loaded sheet without a terminal open.
    /// </summary>
    internal static class UssDecoder
    {
        /// <summary>
        /// The properties a matched rule actually sets, and what they resolve to - a selector
        /// alone only says a rule COULD apply, and cannot tell a rule that sets the art from
        /// one that sets a margin, or a live asset reference from a dead one.
        /// </summary>
        internal static void AppendDeclarations(StringBuilder sb, StyleSheet sheet, object cs)
        {
            try
            {
                object rule = Traverse.Create(cs).Property("rule").GetValue();
                if (rule == null) return;
                Array props = Traverse.Create(rule).Field("m_Properties").GetValue() as Array;
                for (int i = 0; props != null && i < props.Length; i++)
                {
                    Traverse pr = Traverse.Create(props.GetValue(i));
                    string name = (string)pr.Field("m_Name").GetValue();
                    Array handles = pr.Field("m_Values").GetValue() as Array;
                    sb.Append("          ").Append(name).Append(':');
                    for (int h = 0; handles != null && h < handles.Length; h++)
                        sb.Append(' ').Append(ValueText(sheet, handles.GetValue(h)));
                    sb.AppendLine();
                }
            }
            catch (Exception e)
            {
                sb.Append("          <declarations unreadable: ")
                  .Append(e.Message).AppendLine(">");
            }
        }

        private static string ValueText(StyleSheet sheet, object handle)
        {
            try
            {
                Traverse th = Traverse.Create(handle);
                // Property first, backing field second: which one a style value handle
                // carries varies by engine version, and reading only one turns every value
                // into "<unreadable>".
                object rawType = Member(th, "valueType", "m_ValueType");
                object rawIdx = Member(th, "valueIndex", "m_ValueIndex");
                if (rawType == null || rawIdx == null)
                    return "<no value member; handle has " + FieldNames(handle) + ">";
                string type = rawType.ToString();
                int idx = Convert.ToInt32(rawIdx);
                Traverse ts = Traverse.Create(sheet);
                switch (type)
                {
                    case "Float":
                        return Fmt(ArrayAt<float>(ts, "floats", idx));
                    case "Dimension":
                    {
                        object d = ArrayAt<object>(ts, "dimensions", idx);
                        return d != null ? d.ToString() : "dimension?";
                    }
                    case "Color":
                        return "#" + ColorUtility.ToHtmlStringRGBA(
                                         ArrayAt<Color>(ts, "colors", idx));
                    case "AssetReference":
                    {
                        UnityEngine.Object o =
                            ArrayAt<UnityEngine.Object>(ts, "assets", idx);
                        return o != null
                            ? "asset(" + o.name + " : " + o.GetType().Name + ")"
                            : "asset(MISSING)";
                    }
                    case "ResourcePath":
                        return "resource(" + ArrayAt<string>(ts, "strings", idx) + ")";
                    case "Variable":
                        return "var(" + ArrayAt<string>(ts, "strings", idx) + ")";
                    case "String":
                    case "Enum":
                        return ArrayAt<string>(ts, "strings", idx);
                    case "Keyword":
                        return "keyword:" + idx;
                    default:
                        return type + "[" + idx + "]";
                }
            }
            catch { return "<unreadable>"; }
        }

        /// <summary>First of the named members that actually resolves, property or field.</summary>
        private static object Member(Traverse t, params string[] names)
        {
            foreach (string n in names)
            {
                try
                {
                    object v = t.Property(n).GetValue();
                    if (v != null) return v;
                }
                catch { }
                try
                {
                    object v = t.Field(n).GetValue();
                    if (v != null) return v;
                }
                catch { }
            }
            return null;
        }

        /// <summary>What the type does carry, so a failed lookup names its own fix instead
        /// of costing another round trip through the game.</summary>
        private static string FieldNames(object o)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (FieldInfo f in o.GetType().GetFields(
                             BindingFlags.Public | BindingFlags.NonPublic |
                             BindingFlags.Instance))
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(f.Name);
                }
                foreach (PropertyInfo p in o.GetType().GetProperties(
                             BindingFlags.Public | BindingFlags.NonPublic |
                             BindingFlags.Instance))
                    sb.Append(", ").Append(p.Name).Append("()");
                return sb.ToString();
            }
            catch { return "?"; }
        }

        private static T ArrayAt<T>(Traverse sheet, string field, int index)
        {
            Array a = sheet.Field(field).GetValue() as Array;
            if (a == null || index < 0 || index >= a.Length) return default(T);
            object v = a.GetValue(index);
            return v is T ? (T)v : default(T);
        }

        /// <summary>One complex selector rendered back into something USS-shaped, so a
        /// reader can match it against the class list on a dumped element.</summary>
        internal static string SelectorText(object complexSelector)
        {
            try
            {
                IEnumerable sels = Traverse.Create(complexSelector).Field("m_Selectors")
                                           .GetValue() as IEnumerable;
                if (sels == null) return null;
                StringBuilder sb = new StringBuilder();
                foreach (object sel in sels)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    Array parts = Traverse.Create(sel).Field("m_Parts").GetValue() as Array;
                    for (int i = 0; parts != null && i < parts.Length; i++)
                    {
                        Traverse tp = Traverse.Create(parts.GetValue(i));
                        string type = tp.Field("m_Type").GetValue().ToString();
                        string value = (string)tp.Field("m_Value").GetValue();
                        switch (type)
                        {
                            case "Class":       sb.Append('.').Append(value); break;
                            case "ID":          sb.Append('#').Append(value); break;
                            case "PseudoClass": sb.Append(':').Append(value); break;
                            case "Wildcard":    sb.Append('*'); break;
                            default:            sb.Append(value); break;
                        }
                    }
                }
                return sb.ToString();
            }
            catch { return null; }
        }

        /// <summary>Shared by every dump that prints a number: NaN is a real and common
        /// answer from an unresolved layout, and must survive into the text rather than
        /// formatting as something that looks like a measurement.</summary>
        internal static string Fmt(float f)
        {
            return float.IsNaN(f) ? "NaN" : f.ToString("0.##");
        }
    }
}
