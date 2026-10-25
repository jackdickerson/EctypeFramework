using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace OfflineChecks
{
    /// <summary>The verdict on one name of one lookup.</summary>
    internal sealed class Outcome
    {
        public Lookup Lookup;
        public string Name;
        public bool Ok;
        public string Message;
        public IMemberDefinition Found;
        public Origin Origin;
        public string Assembly;
        /// <summary>Set when the type belongs to a plugin that is not installed: not a failure.</summary>
        public string MissingPlugin;
        /// <summary>Its type comes from another lookup that failed, which is reported instead.</summary>
        public bool Blocked;
        public string TypeName;

        public string Describe() { return Lookup.Describe(Name, TypeName); }
    }

    /// <summary>
    /// Resolves each recovered lookup against the installed assemblies with the search rules of
    /// the API that makes it, then checks every patch method's injected parameters against
    /// the target it was matched to.
    /// </summary>
    internal sealed class Checker
    {
        private const int DeclaredOnly = 2, Instance = 4, Static = 8, Public = 16, NonPublic = 32, FlattenHierarchy = 64;

        private readonly GameIndex _index;
        public readonly List<Outcome> Outcomes = new List<Outcome>();
        public readonly List<string> PatchFailures = new List<string>();
        public int PatchesChecked;
        private readonly Dictionary<Lookup, List<Outcome>> _byLookup = new Dictionary<Lookup, List<Outcome>>();

        public Checker(GameIndex index)
        {
            _index = index;
        }

        // ------------------------------------------------------------------ lookups

        public void CheckLookups(IEnumerable<Lookup> lookups)
        {
            foreach (Lookup l in lookups)
            {
                var list = new List<Outcome>();
                _byLookup[l] = list;
                foreach (string name in l.Names)
                {
                    Outcome o = CheckOne(l, name);
                    list.Add(o);
                    Outcomes.Add(o);
                }
            }
        }

        private Outcome CheckOne(Lookup l, string name)
        {
            var o = new Outcome { Lookup = l, Name = name };
            TypeDefinition t = ResolveType(l.Type, out string error, out string missingPlugin);
            if (t == null)
            {
                o.MissingPlugin = missingPlugin;
                o.Blocked = error != null && error.StartsWith("blocked", StringComparison.Ordinal);
                o.Ok = missingPlugin != null || o.Blocked;
                o.Message = error;
                o.Origin = missingPlugin != null ? Origin.Plugin : Origin.Game;
                return o;
            }
            o.Origin = _index.OriginOf(t);
            o.Assembly = t.Module.Assembly.Name.Name;
            o.TypeName = Format.TypeName(t);

            List<IMemberDefinition> found = Find(t, l, name);
            string where = null;
            if (found.Count == 0 && l.Type.Inexact)
                foreach (TypeDefinition sub in _index.SubclassesOf(t))
                {
                    found = Find(sub, l, name);
                    if (found.Count > 0) { where = " (declared on subclass " + Format.TypeName(sub) + ")"; break; }
                }
            if (found.Count == 0)
            {
                o.Message = "not found" + Hint(t, l, name);
                return o;
            }
            if (found.Count > 1 && l.Params == null && (l.Kind == MemberKind.Method || l.Kind == MemberKind.Ctor))
            {
                o.Message = "ambiguous: " + found.Count + " overloads match a lookup without parameter types, which throws " +
                            "AmbiguousMatchException: " + string.Join("; ", found.Select(Describe));
                return o;
            }
            o.Found = found[0];
            var problems = new List<string>();
            foreach (Constraint c in l.Constraints.GroupBy(c => c.Key()).Select(g => g.First()))
            {
                string p = CheckConstraint(o.Found, c);
                if (p != null) problems.Add(p);
            }
            if (l.RequireStatic && !IsStatic(o.Found)) problems.Add("is not static");
            o.Ok = problems.Count == 0;
            o.Message = (o.Ok ? Describe(o.Found) : string.Join("; ", problems)) + (where ?? "");
            return o;
        }

        private string Hint(TypeDefinition t, Lookup l, string name)
        {
            // Name the near misses: same name with other parameters, or another kind of member.
            var same = new List<string>();
            for (TypeDefinition cur = t; cur != null; cur = Base(cur))
            {
                same.AddRange(cur.Methods.Where(m => m.Name == name).Select(m => Describe(m)));
                same.AddRange(cur.Fields.Where(f => f.Name == name).Select(f => Describe(f)));
                same.AddRange(cur.Properties.Where(p => p.Name == name).Select(p => Describe(p)));
                if (l.Search == Search.Declared) break;
            }
            if (same.Count == 0) return "";
            return "; " + Format.TypeName(t) + " has " + string.Join("; ", same.Distinct().Take(4)) +
                   (l.Search == Search.Declared ? " (a Declared lookup sees the type's own members only)" : "");
        }

        private TypeDefinition ResolveType(TypeVal tv, out string error, out string missingPlugin)
        {
            error = null;
            missingPlugin = null;
            if (tv.MemberOf != null && tv.Alternates != null)
            {
                string firstError = null, firstMissing = null;
                foreach (TypeVal alt in new[] { tv }.Concat(tv.Alternates))
                {
                    TypeVal single = alt == tv ? new TypeVal { MemberOf = tv.MemberOf, MemberName = tv.MemberName, Element = tv.Element } : alt;
                    TypeDefinition td = ResolveType(single, out string e, out string m);
                    if (td != null) return td;
                    if (firstError == null) { firstError = e; firstMissing = m; }
                }
                error = firstError;
                missingPlugin = firstMissing;
                return null;
            }
            if (tv.MemberOf != null)
            {
                Outcome of = CheckOne(tv.MemberOf, tv.MemberName);
                TypeReference mt = BindToLookup(MemberType(of.Found), tv.MemberOf);
                for (int i = 0; mt != null && i < tv.Element; i++)
                    mt = mt is ArrayType at ? at.ElementType :
                         mt is GenericInstanceType g && g.GenericArguments.Count == 1 ? g.GenericArguments[0] : null;
                if (of.MissingPlugin != null)
                {
                    missingPlugin = of.MissingPlugin;
                    error = of.Message;
                    return null;
                }
                if (mt == null)
                {
                    error = "blocked: its type comes from " + tv.MemberOf.Describe(tv.MemberName) + ", which " +
                            (of.Found == null ? "does not resolve" : "is not an array");
                    return null;
                }
                TypeDefinition td = null;
                try { td = mt is GenericParameter ? null : mt.GetElementType().Resolve(); }
                catch (AssemblyResolutionException) { }
                if (td == null) error = "blocked: the type of " + tv.MemberOf.Describe(tv.MemberName) + ", " + Format.TypeName(mt) +
                                        ", does not resolve to a definition";
                return td;
            }
            if (tv.Ref != null)
            {
                TypeReference r = tv.Ref.GetElementType();
                try
                {
                    TypeDefinition td = r.Resolve();
                    if (td != null) return td;
                }
                catch (AssemblyResolutionException) { }
                error = "type " + Format.TypeName(tv.Ref) + " not found in " + r.Scope.Name;
                return null;
            }
            TypeDefinition byName = _index.FindByName(tv.Name);
            if (byName != null) return byName;
            if (!_index.IsRequiredName(tv.Name))
            {
                int dot = tv.Name.IndexOf('.');
                missingPlugin = dot < 0 ? tv.Name : tv.Name.Substring(0, dot);
                error = "no assembly under BepInEx\\plugins defines " + tv.Name;
                return null;
            }
            error = "no installed assembly defines a type named \"" + tv.Name + "\"";
            return null;
        }

        private static TypeDefinition Base(TypeDefinition t)
        {
            if (t.BaseType == null) return null;
            try { return t.BaseType.Resolve(); }
            catch (AssemblyResolutionException) { return null; }
        }

        private List<IMemberDefinition> Find(TypeDefinition t, Lookup l, string name)
        {
            var result = new List<IMemberDefinition>();
            int all = Instance | Static | Public | NonPublic;
            switch (l.Search)
            {
                case Search.Declared:
                    result.AddRange(Declared(t, l, name, all, false));
                    break;
                case Search.AccessTools:
                    // HarmonyX's FindIncludingBaseTypes: the first type up the chain with a match.
                    for (TypeDefinition cur = t; cur != null && result.Count == 0; cur = Base(cur))
                        result.AddRange(Declared(cur, l, name, all, false));
                    break;
                case Search.Reflection when l.WalksBase:
                    for (TypeDefinition cur = t; cur != null && result.Count == 0; cur = Base(cur))
                        result.AddRange(Declared(cur, l, name, l.Flags, false));
                    break;
                case Search.Reflection:
                    result.AddRange(Declared(t, l, name, l.Flags, false));
                    if ((l.Flags & DeclaredOnly) == 0 && l.Kind != MemberKind.Ctor)
                        for (TypeDefinition cur = Base(t); cur != null; cur = Base(cur))
                            foreach (IMemberDefinition m in Declared(cur, l, name, l.Flags, true))
                                if (!(m is MethodDefinition md && result.OfType<MethodDefinition>().Any(r => SameSignature(r, md))))
                                    result.Add(m);
                    break;
            }
            return result;
        }

        /// <summary>
        /// The type's own members of the lookup's kind and name that the flags admit. An
        /// inherited member, as Type.GetX sees one: never private, and static only with
        /// FlattenHierarchy.
        /// </summary>
        private IEnumerable<IMemberDefinition> Declared(TypeDefinition t, Lookup l, string name, int flags, bool inherited)
        {
            bool Admit(bool isStatic, bool isPublic, bool isPrivate)
            {
                if ((flags & (isStatic ? Static : Instance)) == 0) return false;
                if ((flags & (isPublic ? Public : NonPublic)) == 0) return false;
                if (inherited && isPrivate) return false;
                if (inherited && isStatic && (flags & FlattenHierarchy) == 0) return false;
                return true;
            }

            switch (l.Kind)
            {
                case MemberKind.Field:
                    return t.Fields.Where(f => f.Name == name && Admit(f.IsStatic, f.IsPublic, f.IsPrivate));
                case MemberKind.Event:
                    return t.Events.Where(e => e.Name == name && e.AddMethod != null &&
                                               Admit(e.AddMethod.IsStatic, e.AddMethod.IsPublic, e.AddMethod.IsPrivate));
                case MemberKind.Property:
                    return t.Properties.Where(p => p.Name == name && Accessors(p).Any(a => Admit(a.IsStatic, a.IsPublic, a.IsPrivate)));
                case MemberKind.Getter:
                    return t.Properties.Where(p => p.Name == name && p.GetMethod != null).Select(p => (IMemberDefinition)p.GetMethod);
                case MemberKind.Setter:
                    return t.Properties.Where(p => p.Name == name && p.SetMethod != null).Select(p => (IMemberDefinition)p.SetMethod);
                case MemberKind.Ctor:
                    return Overloads(t.Methods.Where(m => m.IsConstructor && !m.IsStatic &&
                                                          Admit(false, m.IsPublic, m.IsPrivate)), l.Params);
                default:
                    return Overloads(t.Methods.Where(m => m.Name == name && !m.IsConstructor &&
                                                          Admit(m.IsStatic, m.IsPublic, m.IsPrivate)), l.Params);
            }
        }

        private static IEnumerable<MethodDefinition> Accessors(PropertyDefinition p)
        {
            if (p.GetMethod != null) yield return p.GetMethod;
            if (p.SetMethod != null) yield return p.SetMethod;
        }

        /// <summary>
        /// The overloads a Type[] selects. Type.GetMethod's default binder takes an exact match,
        /// else one whose parameters accept the given types.
        /// </summary>
        private IEnumerable<IMemberDefinition> Overloads(IEnumerable<MethodDefinition> methods, List<TypeVal> parameters)
        {
            List<MethodDefinition> list = methods.ToList();
            if (parameters == null) return list;
            var arity = list.Where(m => m.Parameters.Count == parameters.Count).ToList();
            var exact = arity.Where(m => m.Parameters.Select((p, i) => SameType(p.ParameterType, parameters[i])).All(x => x)).ToList();
            if (exact.Count > 0) return exact;
            return arity.Where(m => m.Parameters.Select((p, i) => parameters[i].Ref != null &&
                                                               AssignableTo(parameters[i].Ref, p.ParameterType)).All(x => x));
        }

        private static bool SameSignature(MethodDefinition a, MethodDefinition b)
        {
            return a.Name == b.Name && a.Parameters.Count == b.Parameters.Count &&
                   a.Parameters.Select((p, i) => p.ParameterType.FullName == b.Parameters[i].ParameterType.FullName).All(x => x);
        }

        private static bool SameType(TypeReference a, TypeVal b)
        {
            return b.Ref != null ? a.FullName == b.Ref.FullName : a.FullName.Replace('/', '+') == b.Name;
        }

        private static bool IsStatic(IMemberDefinition m)
        {
            switch (m)
            {
                case FieldDefinition f: return f.IsStatic;
                case MethodDefinition md: return md.IsStatic;
                case PropertyDefinition p: return Accessors(p).Any(a => a.IsStatic);
                case EventDefinition e: return e.AddMethod != null && e.AddMethod.IsStatic;
            }
            return false;
        }

        /// <summary>True when a value of type <paramref name="from"/> can be used as <paramref name="to"/>.</summary>
        public static bool AssignableTo(TypeReference from, TypeReference to)
        {
            if (from is ByReferenceType fb) from = fb.ElementType;
            if (to is ByReferenceType tb) to = tb.ElementType;
            if (from.FullName == to.FullName) return true;
            if (to.FullName == "System.Object") return !from.IsValueType;
            TypeDefinition cur;
            try { cur = from.Resolve(); }
            catch (AssemblyResolutionException) { return false; }
            var seen = new HashSet<string>();
            var queue = new Queue<TypeDefinition>();
            if (cur != null) queue.Enqueue(cur);
            string target = to.GetElementType().FullName;
            while (queue.Count > 0)
            {
                TypeDefinition t = queue.Dequeue();
                if (!seen.Add(t.FullName)) continue;
                if (t.FullName == target) return true;
                var next = new List<TypeReference>();
                if (t.BaseType != null) next.Add(t.BaseType);
                next.AddRange(t.Interfaces.Select(i => i.InterfaceType));
                foreach (TypeReference n in next)
                {
                    if (n.GetElementType().FullName == target) return true;
                    try
                    {
                        TypeDefinition d = n.Resolve();
                        if (d != null) queue.Enqueue(d);
                    }
                    catch (AssemblyResolutionException) { }
                }
            }
            return false;
        }

        /// <summary>A member type that is the declaring type's generic parameter, bound to the
        /// type argument the lookup named: ServiceBase&lt;InputService&gt;._instance is an InputService.</summary>
        private static TypeReference BindToLookup(TypeReference t, Lookup l)
        {
            if (t is GenericParameter gp && gp.Type == GenericParameterType.Type &&
                l.Type?.Ref is GenericInstanceType git && gp.Position < git.GenericArguments.Count)
                return git.GenericArguments[gp.Position];
            return t;
        }

        private static TypeReference MemberType(IMemberDefinition m)
        {
            switch (m)
            {
                case FieldDefinition f: return f.FieldType;
                case MethodDefinition md: return md.ReturnType;
                case PropertyDefinition p: return p.PropertyType;
                case EventDefinition e: return e.EventType;
            }
            return null;
        }

        private string CheckConstraint(IMemberDefinition m, Constraint c)
        {
            if (c.Attr == "Delegate") return CheckDelegate(m, c.Type);
            TypeReference actual = MemberType(m);
            if (actual == null || c.Type.MemberOf != null) return null;
            bool ok = c.Assignable && c.Type.Ref != null ? AssignableTo(actual, c.Type.Ref) : SameType(actual, c.Type);
            if (ok) return null;
            return "its " + c.Attr + " is " + Format.TypeName(actual) + "; the framework requires " +
                   (c.Assignable ? "one assignable to " : "") + c.Type.Describe();
        }

        /// <summary>A delegate bound to the member must match it: Invoke's parameters are the
        /// method's, preceded by the instance for an open instance method.</summary>
        private string CheckDelegate(IMemberDefinition m, TypeVal delegateType)
        {
            var md = m as MethodDefinition;
            if (md == null || delegateType.Ref == null) return null;
            TypeDefinition d;
            try { d = delegateType.Ref.Resolve(); }
            catch (AssemblyResolutionException) { return null; }
            MethodDefinition invoke = d?.Methods.FirstOrDefault(x => x.Name == "Invoke");
            if (invoke == null) return null;
            var git = delegateType.Ref as GenericInstanceType;
            TypeReference Bind(TypeReference t)
            {
                return t is GenericParameter gp && git != null && gp.Type == GenericParameterType.Type
                    ? git.GenericArguments[gp.Position] : t;
            }
            var wanted = invoke.Parameters.Select(p => Bind(p.ParameterType)).ToList();
            var given = md.Parameters.Select(p => p.ParameterType).ToList();
            if (!md.IsStatic) given.Insert(0, md.DeclaringType);
            bool ok = wanted.Count == given.Count &&
                      wanted.Select((w, i) => w.FullName == given[i].FullName || AssignableTo(given[i], w)).All(x => x) &&
                      (Bind(invoke.ReturnType).FullName == md.ReturnType.FullName || AssignableTo(md.ReturnType, Bind(invoke.ReturnType)));
            if (ok) return null;
            return "bound to " + delegateType.Describe() + " but its signature is " + Describe(md);
        }

        // ------------------------------------------------------------------ patches

        public Outcome Get(Lookup l, string name)
        {
            return _byLookup.TryGetValue(l, out List<Outcome> outs) ? outs.FirstOrDefault(o => o.Name == name) : null;
        }

        /// <summary>The target methods a lookup resolved to, each name separately.</summary>
        public IEnumerable<MethodDefinition> Targets(Lookup l)
        {
            if (!_byLookup.TryGetValue(l, out List<Outcome> outs)) yield break;
            foreach (Outcome o in outs)
                if (o.Ok && o.Found is MethodDefinition md) yield return md;
        }

        public MethodDefinition HookMethod(PatchLink link)
        {
            if (link.HookMethod != null) return link.HookMethod;
            return link.Hook != null ? Targets(link.Hook).FirstOrDefault() : null;
        }

        public void CheckPatch(PatchLink link)
        {
            MethodDefinition hook = HookMethod(link);
            if (hook == null || link.HookKind == "transpiler" || link.HookKind == "ilmanipulator") return;
            foreach (MethodDefinition target in Targets(link.Target))
            {
                PatchesChecked++;
                string what = link.HookKind + " " + Format.TypeName(hook.DeclaringType) + "." + hook.Name + " on " + Describe(target) +
                              " [" + link.Site + "]";
                foreach (string problem in Injected(hook, target))
                    PatchFailures.Add(what + ": " + problem);
            }
        }

        /// <summary>
        /// Harmony fills a patch method's parameters by name: __instance, __result, __state,
        /// ___field for a field of the patched type, __N for the Nth argument, and otherwise the
        /// target's parameter of the same name. A name with no match fails when the patch is
        /// applied; a type that does not fit makes bad code that fails when the method runs.
        /// </summary>
        private IEnumerable<string> Injected(MethodDefinition hook, MethodDefinition target)
        {
            if (!hook.IsStatic) yield return "the patch method is not static";
            foreach (ParameterDefinition p in hook.Parameters)
            {
                string name = p.Name;
                CustomAttribute arg = p.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == "HarmonyLib.HarmonyArgument");
                if (arg != null && arg.ConstructorArguments.Count > 0)
                {
                    object v = arg.ConstructorArguments[0].Value;
                    name = v is int i ? "__" + i : (string)v;
                }
                bool byRef = p.ParameterType is ByReferenceType;
                switch (name)
                {
                    case "__state": case "__originalMethod": case "__runOriginal": case "__exception": case "__args":
                        continue;
                    case "__instance":
                        if (target.IsStatic) yield return "__instance on a static method";
                        else if (!Fits(target.DeclaringType, p.ParameterType, byRef))
                            yield return "__instance is " + Format.TypeName(p.ParameterType) + ", the target is declared on " +
                                         Format.TypeName(target.DeclaringType);
                        continue;
                    case "__result":
                        if (target.ReturnType.FullName == "System.Void") yield return "__result on a method that returns void";
                        else if (!Fits(target.ReturnType, p.ParameterType, byRef))
                            yield return "__result is " + Format.TypeName(p.ParameterType) + ", the target returns " +
                                         Format.TypeName(target.ReturnType);
                        continue;
                }
                if (name.StartsWith("___", StringComparison.Ordinal))
                {
                    string field = name.Substring(3);
                    FieldDefinition f = null;
                    for (TypeDefinition cur = target.DeclaringType; cur != null && f == null; cur = Base(cur))
                        f = cur.Fields.FirstOrDefault(x => x.Name == field);
                    if (f == null) yield return name + ": " + Format.TypeName(target.DeclaringType) + " has no field " + field;
                    else if (!Fits(f.FieldType, p.ParameterType, byRef))
                        yield return name + " is " + Format.TypeName(p.ParameterType) + ", the field is " + Format.TypeName(f.FieldType);
                    continue;
                }
                if (name.StartsWith("__", StringComparison.Ordinal) && int.TryParse(name.Substring(2), out int index))
                {
                    if (index >= target.Parameters.Count)
                        yield return name + ": the target has " + target.Parameters.Count + " parameter(s)";
                    else if (!Fits(target.Parameters[index].ParameterType, p.ParameterType, byRef))
                        yield return name + " is " + Format.TypeName(p.ParameterType) + ", the target's argument " + index + " is " +
                                     Format.TypeName(target.Parameters[index].ParameterType);
                    continue;
                }
                if (name.StartsWith("__", StringComparison.Ordinal)) continue; // Harmony's other specials
                ParameterDefinition tp = target.Parameters.FirstOrDefault(x => x.Name == name);
                if (tp == null)
                    yield return "parameter " + name + " matches nothing; the target's parameters are (" +
                                 string.Join(", ", target.Parameters.Select(x => Format.TypeName(x.ParameterType) + " " + x.Name)) + ")";
                else if (!Fits(tp.ParameterType, p.ParameterType, byRef))
                    yield return "parameter " + name + " is " + Format.TypeName(p.ParameterType) + ", the target's is " +
                                 Format.TypeName(tp.ParameterType);
            }
        }

        private static bool Fits(TypeReference actual, TypeReference patch, bool patchByRef)
        {
            TypeReference a = actual is ByReferenceType ab ? ab.ElementType : actual;
            TypeReference p = patch is ByReferenceType pb ? pb.ElementType : patch;
            if (a.FullName == p.FullName) return true;
            if (patchByRef) return false;
            if (p.FullName == "System.Object") return true;
            return AssignableTo(a, p);
        }

        // ------------------------------------------------------------------ text

        public static string Describe(IMemberDefinition m)
        {
            switch (m)
            {
                case MethodDefinition md:
                    return (md.IsStatic ? "static " : "") + Format.TypeName(md.ReturnType) + " " +
                           Format.TypeName(md.DeclaringType) + "." + md.Name + "(" +
                           string.Join(", ", md.Parameters.Select(p => (p.ParameterType is ByReferenceType ? (p.IsOut ? "out " : "ref ") : "") +
                                                                     Format.TypeName(p.ParameterType))) + ")";
                case FieldDefinition f:
                    return (f.IsStatic ? "static " : "") + Format.TypeName(f.FieldType) + " " + Format.TypeName(f.DeclaringType) + "." + f.Name;
                case PropertyDefinition p:
                    return Format.TypeName(p.PropertyType) + " " + Format.TypeName(p.DeclaringType) + "." + p.Name +
                           " {" + (p.GetMethod != null ? " get;" : "") + (p.SetMethod != null ? " set;" : "") + " }";
                case EventDefinition e:
                    return "event " + Format.TypeName(e.EventType) + " " + Format.TypeName(e.DeclaringType) + "." + e.Name;
            }
            return m.FullName;
        }
    }
}
