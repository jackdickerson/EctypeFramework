using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace OfflineChecks
{
    internal enum VK { Unknown, Null, Int, Str, Type, TypeArray, StrArray, Member, MemberType, Hook, Traverse }

    /// <summary>A type as the framework names it: by metadata token, or by a name string.</summary>
    internal sealed class TypeVal
    {
        public TypeReference Ref;
        public string Name;
        /// <summary>From <c>obj.GetType()</c>: the run-time type may be any subclass of this one.</summary>
        public bool Inexact;
        /// <summary>The type of a member another lookup finds (a field's type, a property's,
        /// a method's return), known only once that lookup is resolved.</summary>
        public Lookup MemberOf;
        public string MemberName;
        /// <summary>Taken from an array of that type, this many levels down.</summary>
        public int Element;
        /// <summary>Other members the value may have been read through; the first that
        /// resolves gives the type.</summary>
        public List<TypeVal> Alternates;

        public string Describe()
        {
            if (MemberOf != null)
                return "type of " + MemberOf.Describe(MemberName) + string.Concat(Enumerable.Repeat("[element]", Element));
            return Ref != null ? Format.TypeName(Ref) : Name;
        }

        public string Key()
        {
            string k = MemberOf != null ? "of(" + MemberOf.Key() + "/" + MemberName + "/" + Element + ")" :
                       Ref != null ? Ref.FullName : "name:" + Name;
            if (Alternates != null) k += "|" + string.Join("|", Alternates.Select(a => a.Key()));
            return k + (Inexact ? "+sub" : "");
        }

        public TypeVal ElementOf()
        {
            return new TypeVal
            {
                Ref = Ref, Name = Name, Inexact = true, MemberOf = MemberOf, MemberName = MemberName, Element = Element + 1,
                Alternates = Alternates?.Select(a => a.ElementOf()).ToList(),
            };
        }

        public TypeVal AsInexact()
        {
            return new TypeVal
            {
                Ref = Ref, Name = Name, Inexact = true, MemberOf = MemberOf, MemberName = MemberName, Element = Element,
                Alternates = Alternates,
            };
        }
    }

    /// <summary>One value on the interpreter's stack, in a local or in an argument.</summary>
    internal sealed class Val
    {
        public VK Kind;
        /// <summary>
        /// Derived from a parameter of the method interpreted at the top level. A lookup built
        /// from one is a helper's; its callers pass the real names and are checked instead.
        /// </summary>
        public bool FromParam;
        /// <summary>The declared type of an unknown value, when the IL says it.</summary>
        public TypeReference Static;
        public long Int;
        public string[] Strs;
        public TypeVal Type;
        public Val[] Items;
        public Lookup Member;
        public string MemberAttr;
        /// <summary>For an unknown value read through a looked-up member: that member's type.</summary>
        public TypeVal Hint;

        public static Val Unknown(TypeReference staticType = null, bool fromParam = false)
        {
            // An open generic parameter says nothing about the run-time type.
            if (staticType != null && staticType.ContainsGenericParameter) staticType = null;
            return new Val { Kind = VK.Unknown, Static = staticType, FromParam = fromParam };
        }

        /// <summary>
        /// What is known of the run-time type of an object value: the member type it was read
        /// from, else its declared type when that says more than object. Either way a subclass
        /// is possible.
        /// </summary>
        public TypeVal RuntimeType()
        {
            if (Kind != VK.Unknown) return null;
            if (Hint != null) return Hint.AsInexact();
            if (Static != null && Static.FullName != "System.Object" && Static.FullName != "System.Array" &&
                !(Static is ArrayType) && !Static.IsValueType)
                return new TypeVal { Ref = Static, Inexact = true };
            return null;
        }

        public static readonly Val Null = new Val { Kind = VK.Null };

        public static Val OfInt(long i) { return new Val { Kind = VK.Int, Int = i }; }
        public static Val OfStr(params string[] s) { return new Val { Kind = VK.Str, Strs = s }; }
        public static Val OfType(TypeVal t) { return new Val { Kind = VK.Type, Type = t }; }

        /// <summary>Worth inlining a call for: a name, a type, or a lookup's result.</summary>
        public bool Informative
        {
            get
            {
                return Kind == VK.Str || Kind == VK.Type || Kind == VK.TypeArray || Kind == VK.StrArray ||
                       Kind == VK.Member || Kind == VK.MemberType || Kind == VK.Hook || Kind == VK.Traverse ||
                       Hint != null;
            }
        }

        public string[] StringsOrNull()
        {
            if (Kind == VK.Str) return Strs;
            return null;
        }

        /// <summary>The parameter types of a Type[]; null when any slot is not known.</summary>
        public List<TypeVal> TypesOrNull()
        {
            if (Kind != VK.TypeArray) return null;
            var list = new List<TypeVal>();
            foreach (Val v in Items)
            {
                if (v == null || v.Kind != VK.Type) return null;
                list.Add(v.Type);
            }
            return list;
        }
    }

    internal enum MemberKind { Field, Method, Property, Getter, Setter, Ctor, Event }

    /// <summary>
    /// How a lookup searches: AccessTools' plain forms walk the base types and see every
    /// member of each; its Declared forms see the type's own members only; Type.GetX follows
    /// the BindingFlags it is given.
    /// </summary>
    internal enum Search { AccessTools, Declared, Reflection }

    /// <summary>A check of the found member's type: FieldType, ReturnType, PropertyType,
    /// EventHandlerType, or a delegate the member is bound to.</summary>
    internal sealed class Constraint
    {
        public string Attr;
        public bool Assignable;
        public TypeVal Type;

        public string Key() { return Attr + (Assignable ? "<:" : "==") + Type.Key(); }
    }

    /// <summary>One by-name lookup the framework makes, as recovered from its IL.</summary>
    internal sealed class Lookup
    {
        public string Api;
        public MemberKind Kind;
        public TypeVal Type;
        public bool TypeFromParam;
        public string[] Names;
        public bool NameFromParam;
        /// <summary>Null: any parameters, so exactly one member of the name may exist.</summary>
        public List<TypeVal> Params;
        public bool ParamsUnknown;
        public bool ParamsFromParam;
        public Search Search;
        public int Flags;
        public bool RequireStatic;
        public bool WalksBase;
        /// <summary>The call that made the lookup. The same name looked up more than once on
        /// the same type in one call is a fallback chain (a property, else a field; an exact
        /// overload, else any): it passes when one of them resolves.</summary>
        public int Frame;
        public string GroupKey { get { return Frame + "|" + (Type != null ? Type.Key() : "?"); } }
        public readonly List<Constraint> Constraints = new List<Constraint>();
        public readonly SortedSet<string> Sites = new SortedSet<string>(StringComparer.Ordinal);
        public MethodDefinition SiteMethod;

        public bool Suppressed
        {
            get { return TypeFromParam || NameFromParam || ParamsFromParam || Type?.MemberOf != null && Type.MemberOf.Suppressed; }
        }

        public bool Dynamic
        {
            get
            {
                return !Suppressed && (Type == null || Names == null || ParamsUnknown ||
                                       Type.MemberOf != null && (Type.MemberOf.Dynamic || !Type.MemberOf.Names.Contains(Type.MemberName)));
            }
        }

        public string Key()
        {
            return Kind + "|" + (Type != null ? Type.Key() : "?") + "|" + (Names != null ? string.Join(",", Names) : "?") + "|" +
                   (ParamsUnknown ? "?" : Params == null ? "*" : string.Join(",", Params.Select(p => p.Key()))) + "|" +
                   Search + "|" + Flags + "|" + RequireStatic + "|" + WalksBase;
        }

        /// <param name="typeName">The type as resolved, shown instead of how it was reached.</param>
        public string Describe(string name = null, string typeName = null)
        {
            string n = name ?? (Names != null ? string.Join("|", Names) : "<run-time name>");
            string t = typeName ?? (Type != null ? Type.Describe() : "<run-time type>");
            if (Type != null && Type.Inexact) t += " (or a subclass)";
            string p = ParamsUnknown ? "(<run-time types>)" :
                       Params == null ? "" : "(" + string.Join(", ", Params.Select(x => x.Describe())) + ")";
            string kind = Kind == MemberKind.Method || Kind == MemberKind.Ctor ? "" : Kind.ToString().ToLowerInvariant() + " ";
            if (Kind == MemberKind.Ctor) n = ".ctor";
            if (Kind == MemberKind.Ctor && Params == null && !ParamsUnknown) p = "(...)";
            return kind + t + "." + n + p;
        }
    }

    /// <summary>A harmony.Patch call or a patch class: a target and one patch method on it.</summary>
    internal sealed class PatchLink
    {
        public Lookup Target;
        public Lookup Hook;
        public MethodDefinition HookMethod;
        public string HookKind;
        public string Site;
    }
}
