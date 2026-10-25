using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace OfflineChecks
{
    /// <summary>
    /// The patch classes Harmony.PatchAll applies: every type carrying [HarmonyPatch]. Their
    /// targets come from the attributes, merged class first and then method, or from the
    /// class's TargetMethod/TargetMethods, whose returned values the interpreter recovered.
    /// HarmonyX resolves an attribute target with the Declared lookups: the type's own
    /// members, never an inherited one.
    /// </summary>
    internal static class PatchClasses
    {
        private static readonly string[] HookNames = { "Prefix", "Postfix", "Finalizer", "Transpiler", "ILManipulator" };

        private sealed class Info
        {
            public TypeReference Type;
            public string TypeName;
            public string Method;
            public string MethodType;
            public List<TypeReference> Args;

            public Info Merge(Info over)
            {
                return new Info
                {
                    Type = over.Type ?? Type,
                    TypeName = over.TypeName ?? TypeName,
                    Method = over.Method ?? Method,
                    MethodType = over.MethodType ?? MethodType,
                    Args = over.Args ?? Args,
                };
            }
        }

        public static void Collect(ModuleDefinition module, Interpreter interp, List<Lookup> lookups, List<PatchLink> links)
        {
            foreach (TypeDefinition t in module.GetTypes())
            {
                List<CustomAttribute> attrs = PatchAttrs(t).ToList();
                if (attrs.Count == 0) continue;
                Info classInfo = Read(attrs);
                string site = t.FullName.Replace('/', '+');

                List<Lookup> fromCode = null;
                MethodDefinition targetMethod = t.Methods.FirstOrDefault(m =>
                    m.Name == "TargetMethod" || m.Name == "TargetMethods" ||
                    m.CustomAttributes.Any(a => a.AttributeType.FullName == "HarmonyLib.HarmonyTargetMethod" ||
                                                a.AttributeType.FullName == "HarmonyLib.HarmonyTargetMethods"));
                if (targetMethod != null) fromCode = Returned(targetMethod, interp);

                foreach (MethodDefinition hook in t.Methods)
                {
                    string kind = HookKind(hook);
                    if (kind == null) continue;
                    var targets = new List<Lookup>();
                    if (fromCode != null) targets.AddRange(fromCode);
                    else
                    {
                        Info info = classInfo.Merge(Read(PatchAttrs(hook).ToList()));
                        Lookup l = ToLookup(info, site);
                        if (l == null) continue;
                        lookups.Add(l);
                        targets.Add(l);
                    }
                    foreach (Lookup target in targets)
                        links.Add(new PatchLink { Target = target, HookMethod = hook, HookKind = kind, Site = site });
                }
            }
        }

        private static IEnumerable<CustomAttribute> PatchAttrs(ICustomAttributeProvider p)
        {
            return p.CustomAttributes.Where(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
        }

        private static string HookKind(MethodDefinition m)
        {
            foreach (CustomAttribute a in m.CustomAttributes)
                switch (a.AttributeType.FullName)
                {
                    case "HarmonyLib.HarmonyPrefix": return "prefix";
                    case "HarmonyLib.HarmonyPostfix": return "postfix";
                    case "HarmonyLib.HarmonyFinalizer": return "finalizer";
                    case "HarmonyLib.HarmonyTranspiler": return "transpiler";
                    case "HarmonyLib.HarmonyILManipulator": return "ilmanipulator";
                }
            return HookNames.Contains(m.Name) ? m.Name.ToLowerInvariant() : null;
        }

        private static Info Read(List<CustomAttribute> attrs)
        {
            var info = new Info();
            foreach (CustomAttribute a in attrs)
            {
                var strings = new List<string>();
                foreach (CustomAttributeArgument arg in a.ConstructorArguments)
                {
                    switch (arg.Type.FullName)
                    {
                        case "System.Type": info.Type = (TypeReference)arg.Value; break;
                        case "System.String": strings.Add((string)arg.Value); break;
                        case "System.Type[]":
                            info.Args = ((CustomAttributeArgument[])arg.Value).Select(x => (TypeReference)x.Value).ToList();
                            break;
                        case "HarmonyLib.MethodType": info.MethodType = EnumName(arg); break;
                    }
                }
                if (strings.Count == 1) info.Method = strings[0];
                else if (strings.Count == 2)
                {
                    info.TypeName = strings[0];
                    info.Method = strings[1];
                }
            }
            return info;
        }

        private static string EnumName(CustomAttributeArgument arg)
        {
            TypeDefinition e = arg.Type.Resolve();
            FieldDefinition f = e?.Fields.FirstOrDefault(x => x.HasConstant && Convert.ToInt64(x.Constant) == Convert.ToInt64(arg.Value));
            return f != null ? f.Name : arg.Value.ToString();
        }

        private static Lookup ToLookup(Info info, string site)
        {
            if (info.Type == null && info.TypeName == null) return null;
            var l = new Lookup
            {
                Api = "[HarmonyPatch]",
                Type = info.Type != null ? new TypeVal { Ref = info.Type } : new TypeVal { Name = info.TypeName },
                Search = Search.Declared,
                Params = info.Args?.Select(a => new TypeVal { Ref = a }).ToList(),
            };
            switch (info.MethodType ?? "Normal")
            {
                case "Getter": l.Kind = MemberKind.Getter; break;
                case "Setter": l.Kind = MemberKind.Setter; break;
                case "Constructor": l.Kind = MemberKind.Ctor; break;
                default: l.Kind = MemberKind.Method; break;
            }
            l.Names = l.Kind == MemberKind.Ctor ? new[] { ".ctor" } : info.Method != null ? new[] { info.Method } : null;
            if (l.Names == null) return null;
            l.Sites.Add(site);
            return l;
        }

        /// <summary>The method lookups a TargetMethod(s) returns or, for an iterator, yields.</summary>
        private static List<Lookup> Returned(MethodDefinition m, Interpreter interp)
        {
            MethodDefinition body = m;
            CustomAttribute sm = m.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName ==
                                                                        "System.Runtime.CompilerServices.IteratorStateMachineAttribute");
            if (sm != null)
            {
                TypeDefinition machine = ((TypeReference)sm.ConstructorArguments[0].Value).Resolve();
                body = machine?.Methods.FirstOrDefault(x => x.Name == "MoveNext") ?? m;
            }
            var result = new List<Lookup>();
            if (interp.Results.TryGetValue(body, out List<Val> values))
                foreach (Val v in values)
                    if (v.Kind == VK.Member && !result.Contains(v.Member)) result.Add(v.Member);
            return result;
        }
    }
}
