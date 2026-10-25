using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Mono.Cecil;

namespace OfflineChecks
{
    /// <summary>C#-style names for types and signatures, shared by the report and the API snapshot.</summary>
    internal static class Format
    {
        private static readonly Dictionary<string, string> Keywords = new Dictionary<string, string>
        {
            { "System.Void", "void" }, { "System.Boolean", "bool" }, { "System.Byte", "byte" },
            { "System.SByte", "sbyte" }, { "System.Int16", "short" }, { "System.UInt16", "ushort" },
            { "System.Int32", "int" }, { "System.UInt32", "uint" }, { "System.Int64", "long" },
            { "System.UInt64", "ulong" }, { "System.Single", "float" }, { "System.Double", "double" },
            { "System.Decimal", "decimal" }, { "System.Char", "char" }, { "System.String", "string" },
            { "System.Object", "object" },
        };

        public static string TypeName(TypeReference t)
        {
            switch (t)
            {
                case ByReferenceType br: return TypeName(br.ElementType);
                case PointerType pt: return TypeName(pt.ElementType) + "*";
                case ArrayType at: return TypeName(at.ElementType) + "[" + new string(',', at.Rank - 1) + "]";
                case GenericParameter gp: return gp.Name;
                case RequiredModifierType rm: return TypeName(rm.ElementType);
                case OptionalModifierType om: return TypeName(om.ElementType);
                case GenericInstanceType git:
                    if (git.ElementType.FullName == "System.Nullable`1") return TypeName(git.GenericArguments[0]) + "?";
                    return Plain(git.ElementType) + "<" + string.Join(", ", git.GenericArguments.Select(TypeName)) + ">";
            }
            if (Keywords.TryGetValue(t.FullName, out string k)) return k;
            if (t.HasGenericParameters)
                return Plain(t) + "<" + string.Join(", ", t.GenericParameters.Select(g => g.Name)) + ">";
            return Plain(t);
        }

        /// <summary>Namespace, then declaring types and name, without the `N arity suffixes.</summary>
        private static string Plain(TypeReference t)
        {
            string name = StripArity(t.Name);
            if (t.DeclaringType != null) return Plain(t.DeclaringType) + "." + name;
            return string.IsNullOrEmpty(t.Namespace) ? name : t.Namespace + "." + name;
        }

        public static string StripArity(string name)
        {
            int tick = name.IndexOf('`');
            return tick < 0 ? name : name.Substring(0, tick);
        }

        public static string Parameter(ParameterDefinition p, bool isExtensionThis)
        {
            string mod = "";
            if (p.ParameterType is ByReferenceType)
                mod = p.IsOut ? "out " : p.CustomAttributes.Any(a => a.AttributeType.Name == "IsReadOnlyAttribute") ? "in " : "ref ";
            if (p.CustomAttributes.Any(a => a.AttributeType.FullName == "System.ParamArrayAttribute")) mod = "params " + mod;
            if (isExtensionThis) mod = "this " + mod;
            string s = mod + TypeName(p.ParameterType) + " " + p.Name;
            if (p.HasConstant) s += " = " + Constant(p.Constant, p.ParameterType);
            else if (p.IsOptional) s += " = default";
            return s;
        }

        public static string Constant(object value, TypeReference type)
        {
            if (value == null) return type.IsValueType ? "default" : "null";
            switch (value)
            {
                case string s: return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
                case bool b: return b ? "true" : "false";
                case char c: return "'" + c + "'";
                case float fl: return fl.ToString("R", CultureInfo.InvariantCulture) + "f";
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case IFormattable fm: return fm.ToString(null, CultureInfo.InvariantCulture);
            }
            return value.ToString();
        }
    }
}
