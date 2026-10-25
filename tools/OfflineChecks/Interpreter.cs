using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace OfflineChecks
{
    /// <summary>
    /// Recovers the framework's by-name lookups from its compiled IL. Every method is run
    /// abstractly once: constants (strings, typeof, Type[] and string[] initialisers, ints)
    /// are tracked through the stack, locals and arguments; a call into AccessTools,
    /// Type.GetX, Assembly.GetType, HarmonyMethod or Harmony.Patch records what it names.
    /// A call to another framework method with a constant argument is followed into that
    /// method with the constants bound, so a helper such as Patch(type, "Name", ...) is
    /// checked once per call site with the names its callers pass. Branches are followed in
    /// IL order, which C# compiler output allows: the stack at a join is the one a jump left.
    /// </summary>
    internal sealed class Interpreter
    {
        private const int MaxDepth = 6;
        private const int Budget = 400000;

        private readonly ModuleDefinition _module;
        public readonly List<Lookup> Lookups = new List<Lookup>();
        public readonly List<PatchLink> Links = new List<PatchLink>();
        /// <summary>Per top-level method: the values it returns, and for an iterator's
        /// MoveNext the values it yields.</summary>
        public readonly Dictionary<MethodDefinition, List<Val>> Results = new Dictionary<MethodDefinition, List<Val>>();

        private int _steps;
        private int _frames;
        private MethodDefinition _site;

        public Interpreter(ModuleDefinition module)
        {
            _module = module;
        }

        public void RunAll()
        {
            foreach (TypeDefinition t in _module.GetTypes())
                foreach (MethodDefinition m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    _site = m;
                    _steps = 0;
                    var args = new List<Val>();
                    if (m.HasThis) args.Add(Val.Unknown(t, true));
                    foreach (ParameterDefinition p in m.Parameters) args.Add(Val.Unknown(p.ParameterType, true));
                    var stack = new HashSet<MethodDefinition>();
                    Results[m] = Run(m, args.ToArray(), null, 0, stack);
                }
        }

        private sealed class Frame
        {
            public MethodDefinition Method;
            public Val[] Args;
            public Val[] Locals;
            public List<Val> Stack = new List<Val>();
            public Dictionary<string, TypeVal> Generics;
            public List<Val> Returns = new List<Val>();
            public Dictionary<string, Val> Fields = new Dictionary<string, Val>();
            public int Depth;
            public int Id;
            public bool WalksBase;
            public HashSet<MethodDefinition> Active;

            public Val Pop()
            {
                if (Stack.Count == 0) return Val.Unknown();
                Val v = Stack[Stack.Count - 1];
                Stack.RemoveAt(Stack.Count - 1);
                return v;
            }

            public void Push(Val v) { Stack.Add(v ?? Val.Unknown()); }
        }

        private List<Val> Run(MethodDefinition m, Val[] args, Dictionary<string, TypeVal> generics, int depth,
                              HashSet<MethodDefinition> active)
        {
            var f = new Frame
            {
                Method = m,
                Args = args,
                Locals = new Val[m.Body.Variables.Count],
                Generics = generics,
                Depth = depth,
                Id = ++_frames,
                WalksBase = m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "get_BaseType" &&
                                                         r.DeclaringType.FullName == "System.Type"),
                Active = active,
            };
            active.Add(m);
            var saved = new Dictionary<Instruction, List<Val>>();
            foreach (ExceptionHandler eh in m.Body.ExceptionHandlers)
            {
                if (eh.HandlerType == ExceptionHandlerType.Catch) saved[eh.HandlerStart] = new List<Val> { Val.Unknown() };
                else if (eh.HandlerType == ExceptionHandlerType.Filter)
                {
                    saved[eh.FilterStart] = new List<Val> { Val.Unknown() };
                    saved[eh.HandlerStart] = new List<Val> { Val.Unknown() };
                }
                else saved[eh.HandlerStart] = new List<Val>();
            }

            // Targets of backward branches (loop heads) are reached later in IL order than the
            // code that follows them; everything else unreachable from what ran is skipped, so
            // the untaken arm of a branch on a known value does not run.
            var loopHeads = new HashSet<Instruction>();
            foreach (Instruction ins in m.Body.Instructions)
                foreach (Instruction t in Targets(ins))
                    if (t.Offset <= ins.Offset) loopHeads.Add(t);

            bool dead = false;
            foreach (Instruction ins in m.Body.Instructions)
            {
                if (++_steps > Budget) break;
                if (saved.TryGetValue(ins, out List<Val> s))
                {
                    f.Stack = dead ? new List<Val>(s) : Merge(f.Stack, s);
                    dead = false;
                }
                else if (dead)
                {
                    if (!loopHeads.Contains(ins)) continue;
                    f.Stack = new List<Val>();
                    dead = false;
                }
                dead = Step(f, ins, saved);
            }
            active.Remove(m);
            return f.Returns;
        }

        private static IEnumerable<Instruction> Targets(Instruction ins)
        {
            if (ins.Operand is Instruction t) yield return t;
            else if (ins.Operand is Instruction[] ts)
                foreach (Instruction x in ts) yield return x;
        }

        /// <summary>True or false when the value's truth is known, null when it is not.</summary>
        private static bool? Truth(Val v)
        {
            switch (v.Kind)
            {
                case VK.Null: return false;
                case VK.Int: return v.Int != 0;
                case VK.Str: case VK.Type: case VK.TypeArray: case VK.StrArray: case VK.Hook: case VK.Traverse:
                    return true;
            }
            return null;
        }

        private static List<Val> Merge(List<Val> a, List<Val> b)
        {
            if (a.Count != b.Count) return a;
            var r = new List<Val>(a.Count);
            for (int i = 0; i < a.Count; i++)
            {
                // x ?? y, each read through a different member: either may give the type.
                if (a[i].Kind == VK.Unknown && b[i].Kind == VK.Unknown && a[i].Hint != null && b[i].Hint != null &&
                    a[i].Hint.MemberOf != null && b[i].Hint.MemberOf != null && a[i].Hint.Key() != b[i].Hint.Key())
                {
                    TypeVal h = a[i].Hint.AsInexact();
                    h.Alternates = (h.Alternates ?? new List<TypeVal>()).Concat(new[] { b[i].Hint }).ToList();
                    r.Add(new Val { Kind = VK.Unknown, Hint = h, Static = a[i].Static });
                    continue;
                }
                r.Add(a[i].Informative || !b[i].Informative ? a[i] : b[i]);
            }
            return r;
        }

        private static void Save(Dictionary<Instruction, List<Val>> saved, Instruction target, List<Val> stack)
        {
            saved[target] = saved.TryGetValue(target, out List<Val> existing) ? Merge(existing, stack) : new List<Val>(stack);
        }

        private int ArgIndex(Frame f, Instruction ins)
        {
            switch (ins.OpCode.Code)
            {
                case Code.Ldarg_0: return 0;
                case Code.Ldarg_1: return 1;
                case Code.Ldarg_2: return 2;
                case Code.Ldarg_3: return 3;
            }
            var p = (ParameterDefinition)ins.Operand;
            return p.Index + (f.Method.HasThis ? 1 : 0);
        }

        private static int LocalIndex(Instruction ins)
        {
            switch (ins.OpCode.Code)
            {
                case Code.Ldloc_0: case Code.Stloc_0: return 0;
                case Code.Ldloc_1: case Code.Stloc_1: return 1;
                case Code.Ldloc_2: case Code.Stloc_2: return 2;
                case Code.Ldloc_3: case Code.Stloc_3: return 3;
            }
            return ((VariableDefinition)ins.Operand).Index;
        }

        /// <summary>Executes one instruction; returns true when control does not fall through.</summary>
        private bool Step(Frame f, Instruction ins, Dictionary<Instruction, List<Val>> saved)
        {
            switch (ins.OpCode.Code)
            {
                case Code.Ldarg_0: case Code.Ldarg_1: case Code.Ldarg_2: case Code.Ldarg_3:
                case Code.Ldarg: case Code.Ldarg_S:
                {
                    int i = ArgIndex(f, ins);
                    f.Push(i < f.Args.Length ? f.Args[i] : Val.Unknown());
                    return false;
                }
                case Code.Starg: case Code.Starg_S:
                {
                    int i = ArgIndex(f, ins);
                    Val v = f.Pop();
                    if (i < f.Args.Length) f.Args[i] = v;
                    return false;
                }
                case Code.Ldloc_0: case Code.Ldloc_1: case Code.Ldloc_2: case Code.Ldloc_3:
                case Code.Ldloc: case Code.Ldloc_S:
                {
                    int i = LocalIndex(ins);
                    f.Push(f.Locals[i] ?? Val.Unknown(f.Method.Body.Variables[i].VariableType));
                    return false;
                }
                case Code.Stloc_0: case Code.Stloc_1: case Code.Stloc_2: case Code.Stloc_3:
                case Code.Stloc: case Code.Stloc_S:
                {
                    int i = LocalIndex(ins);
                    Val v = f.Pop();
                    if (v.Kind == VK.Unknown && v.Static == null)
                    {
                        Val typed = Val.Unknown(f.Method.Body.Variables[i].VariableType, v.FromParam);
                        typed.Hint = v.Hint;
                        v = typed;
                    }
                    f.Locals[i] = v;
                    return false;
                }
                case Code.Ldstr: f.Push(Val.OfStr((string)ins.Operand)); return false;
                case Code.Ldnull: f.Push(Val.Null); return false;
                case Code.Ldc_I4_M1: f.Push(Val.OfInt(-1)); return false;
                case Code.Ldc_I4_0: f.Push(Val.OfInt(0)); return false;
                case Code.Ldc_I4_1: f.Push(Val.OfInt(1)); return false;
                case Code.Ldc_I4_2: f.Push(Val.OfInt(2)); return false;
                case Code.Ldc_I4_3: f.Push(Val.OfInt(3)); return false;
                case Code.Ldc_I4_4: f.Push(Val.OfInt(4)); return false;
                case Code.Ldc_I4_5: f.Push(Val.OfInt(5)); return false;
                case Code.Ldc_I4_6: f.Push(Val.OfInt(6)); return false;
                case Code.Ldc_I4_7: f.Push(Val.OfInt(7)); return false;
                case Code.Ldc_I4_8: f.Push(Val.OfInt(8)); return false;
                case Code.Ldc_I4_S: f.Push(Val.OfInt((sbyte)ins.Operand)); return false;
                case Code.Ldc_I4: f.Push(Val.OfInt((int)ins.Operand)); return false;
                case Code.Ldtoken:
                {
                    var tr = ins.Operand as TypeReference;
                    if (tr == null) { f.Push(Val.Unknown()); return false; }
                    f.Push(TypeOf(f, tr));
                    return false;
                }
                case Code.Dup:
                {
                    Val v = f.Pop();
                    f.Push(v);
                    f.Push(v);
                    return false;
                }
                case Code.Pop: f.Pop(); return false;
                case Code.Newarr:
                {
                    Val n = f.Pop();
                    string elem = ((TypeReference)ins.Operand).FullName;
                    if (n.Kind == VK.Int && n.Int >= 0 && n.Int < 256 && (elem == "System.Type" || elem == "System.String"))
                        f.Push(new Val { Kind = elem == "System.Type" ? VK.TypeArray : VK.StrArray, Items = new Val[n.Int] });
                    else
                        f.Push(Val.Unknown(new ArrayType((TypeReference)ins.Operand)));
                    return false;
                }
                case Code.Stelem_Ref: case Code.Stelem_Any:
                {
                    Val v = f.Pop();
                    Val i = f.Pop();
                    Val a = f.Pop();
                    if ((a.Kind == VK.TypeArray || a.Kind == VK.StrArray) && i.Kind == VK.Int && i.Int >= 0 && i.Int < a.Items.Length)
                        a.Items[i.Int] = v;
                    return false;
                }
                case Code.Ldelem_Ref: case Code.Ldelem_Any:
                {
                    f.Pop();
                    Val a = f.Pop();
                    if (a.Kind == VK.StrArray && a.Items.All(x => x != null && x.Kind == VK.Str))
                        f.Push(Val.OfStr(a.Items.SelectMany(x => x.Strs).Distinct().ToArray()));
                    else if (a.Kind == VK.TypeArray)
                        f.Push(Val.Unknown());
                    else
                        f.Push(Val.Unknown(null, a.FromParam));
                    return false;
                }
                case Code.Ldsfld:
                {
                    var fr = (FieldReference)ins.Operand;
                    if (fr.DeclaringType.FullName == "System.Type" && fr.Name == "EmptyTypes")
                        f.Push(new Val { Kind = VK.TypeArray, Items = new Val[0] });
                    else
                        f.Push(f.Fields.TryGetValue(fr.FullName, out Val sv) ? sv : Val.Unknown(Bind(fr.FieldType, fr.DeclaringType)));
                    return false;
                }
                case Code.Ldfld:
                {
                    var fr = (FieldReference)ins.Operand;
                    Val o = f.Pop();
                    // A field written earlier in the same method reads back what was written: an
                    // iterator's locals live in fields of its state machine.
                    f.Push(f.Fields.TryGetValue(fr.FullName, out Val fv) ? fv : Val.Unknown(Bind(fr.FieldType, fr.DeclaringType), o.FromParam));
                    return false;
                }
                case Code.Stsfld:
                {
                    var fr = (FieldReference)ins.Operand;
                    f.Fields[fr.FullName] = f.Pop();
                    return false;
                }
                case Code.Stfld:
                {
                    var fr = (FieldReference)ins.Operand;
                    Val v = f.Pop();
                    f.Pop();
                    if (fr.Name == "<>2__current") f.Returns.Add(v);
                    else f.Fields[fr.FullName] = v;
                    return false;
                }
                case Code.Castclass: case Code.Isinst:
                {
                    Val v = f.Pop();
                    if (v.Kind == VK.Unknown)
                    {
                        Val cast = Val.Unknown((TypeReference)ins.Operand, v.FromParam);
                        cast.Hint = v.Hint;
                        v = cast;
                    }
                    f.Push(v);
                    return false;
                }
                case Code.Call: case Code.Callvirt: case Code.Newobj:
                    Call(f, ins);
                    return false;
                case Code.Ret:
                    if (f.Method.ReturnType.FullName != "System.Void") f.Returns.Add(f.Pop());
                    return true;
                case Code.Br: case Code.Br_S:
                    Save(saved, (Instruction)ins.Operand, f.Stack);
                    return true;
                case Code.Leave: case Code.Leave_S:
                    Save(saved, (Instruction)ins.Operand, new List<Val>());
                    return true;
                case Code.Throw: case Code.Rethrow: case Code.Endfinally: case Code.Endfilter: case Code.Jmp:
                    if (ins.OpCode.Code == Code.Throw || ins.OpCode.Code == Code.Endfilter) f.Pop();
                    return true;
                case Code.Brtrue: case Code.Brtrue_S: case Code.Brfalse: case Code.Brfalse_S:
                {
                    bool? truth = Truth(f.Pop());
                    bool onTrue = ins.OpCode.Code == Code.Brtrue || ins.OpCode.Code == Code.Brtrue_S;
                    bool? jumps = truth == null ? (bool?)null : truth == onTrue;
                    if (jumps != false) Save(saved, (Instruction)ins.Operand, f.Stack);
                    return jumps == true;
                }
                case Code.Switch:
                    f.Pop();
                    foreach (Instruction t in (Instruction[])ins.Operand) Save(saved, t, f.Stack);
                    return false;
            }

            // Everything else: pop and push by the opcode's stack behaviour.
            bool fromParam = false;
            int pops = Pops(ins);
            if (pops < 0) f.Stack.Clear();
            for (int i = 0; i < pops; i++) fromParam |= f.Pop().FromParam;
            if (ins.OpCode.FlowControl == FlowControl.Cond_Branch)
                Save(saved, (Instruction)ins.Operand, f.Stack);
            int pushes = Pushes(ins);
            for (int i = 0; i < pushes; i++) f.Push(Val.Unknown(null, fromParam));
            return false;
        }

        private static int Pops(Instruction ins)
        {
            switch (ins.OpCode.StackBehaviourPop)
            {
                case StackBehaviour.Pop0: return 0;
                case StackBehaviour.Pop1: case StackBehaviour.Popi: case StackBehaviour.Popref: return 1;
                case StackBehaviour.Pop1_pop1: case StackBehaviour.Popi_pop1: case StackBehaviour.Popi_popi:
                case StackBehaviour.Popi_popi8: case StackBehaviour.Popi_popr4: case StackBehaviour.Popi_popr8:
                case StackBehaviour.Popref_pop1: case StackBehaviour.Popref_popi: return 2;
                case StackBehaviour.Popi_popi_popi: case StackBehaviour.Popref_popi_popi: case StackBehaviour.Popref_popi_popi8:
                case StackBehaviour.Popref_popi_popr4: case StackBehaviour.Popref_popi_popr8: case StackBehaviour.Popref_popi_popref:
                    return 3;
                case StackBehaviour.PopAll: return -1;
                case StackBehaviour.Varpop:
                    if (ins.Operand is CallSite cs) return cs.Parameters.Count + (cs.HasThis ? 1 : 0) + 1;
                    return 0;
            }
            return 0;
        }

        private static int Pushes(Instruction ins)
        {
            switch (ins.OpCode.StackBehaviourPush)
            {
                case StackBehaviour.Push0: return 0;
                case StackBehaviour.Push1_push1: return 2;
                case StackBehaviour.Varpush:
                    if (ins.Operand is CallSite cs) return cs.ReturnType.FullName == "System.Void" ? 0 : 1;
                    return 0;
            }
            return 1;
        }

        private Val TypeOf(Frame f, TypeReference tr)
        {
            TypeVal t = Subst(f, tr);
            return t != null ? Val.OfType(t) : Val.Unknown(null, true);
        }

        /// <summary>Binds a generic parameter to the type argument of the call being followed.
        /// Unbound (at the top level) it counts as a parameter: the callers supply it.</summary>
        private static TypeVal Subst(Frame f, TypeReference tr)
        {
            if (tr is GenericParameter gp)
            {
                string key = (gp.Type == GenericParameterType.Method ? "M" : "T") + gp.Position;
                return f.Generics != null && f.Generics.TryGetValue(key, out TypeVal bound) ? bound : null;
            }
            if (tr.ContainsGenericParameter) return null;
            return new TypeVal { Ref = tr };
        }

        // ------------------------------------------------------------------ calls

        private void Call(Frame f, Instruction ins)
        {
            var mr = (MethodReference)ins.Operand;
            bool newobj = ins.OpCode.Code == Code.Newobj;
            var a = new Val[mr.Parameters.Count];
            for (int i = a.Length - 1; i >= 0; i--) a[i] = f.Pop();
            Val self = mr.HasThis && !newobj ? f.Pop() : null;

            Val result = Dispatch(f, mr, self, a);
            bool fromParam = (self != null && self.FromParam) || a.Any(x => x.FromParam);
            if (newobj) f.Push(result ?? Val.Unknown(mr.DeclaringType, fromParam));
            else if (mr.ReturnType.FullName != "System.Void")
                f.Push(result ?? Val.Unknown(Bind(mr.ReturnType, mr.DeclaringType, mr as GenericInstanceMethod), fromParam));
        }

        /// <summary>A member's declared type with the type arguments of the reference applied:
        /// DataTable&lt;CsTagTable&gt;.Instance is a CsTagTable.</summary>
        private static TypeReference Bind(TypeReference t, TypeReference declaring, GenericInstanceMethod method = null)
        {
            if (t is GenericParameter gp)
            {
                if (gp.Type == GenericParameterType.Type && declaring is GenericInstanceType git && gp.Position < git.GenericArguments.Count)
                    return git.GenericArguments[gp.Position];
                if (gp.Type == GenericParameterType.Method && method != null && gp.Position < method.GenericArguments.Count)
                    return method.GenericArguments[gp.Position];
            }
            return t;
        }

        private Val Dispatch(Frame f, MethodReference mr, Val self, Val[] a)
        {
            string type = mr.DeclaringType.GetElementType().FullName;
            string name = mr.Name;
            switch (type)
            {
                case "System.Type":
                    switch (name)
                    {
                        case "GetTypeFromHandle": return a[0];
                        case "op_Equality":
                        case "op_Inequality":
                            Constrain(a[0], a[1], false);
                            Constrain(a[1], a[0], false);
                            return null;
                        case "IsAssignableFrom":
                            // typeof(F).IsAssignableFrom(member.FieldType): the member's type must fit F.
                            if (self != null) Constrain(a[0], self, true);
                            return null;
                        case "GetField": return ReflectionLookup(f, mr, self, a, MemberKind.Field);
                        case "GetMethod": return ReflectionLookup(f, mr, self, a, MemberKind.Method);
                        case "GetProperty": return ReflectionLookup(f, mr, self, a, MemberKind.Property);
                        case "GetEvent": return ReflectionLookup(f, mr, self, a, MemberKind.Event);
                        case "GetConstructor": return ReflectionLookup(f, mr, self, a, MemberKind.Ctor);
                        case "GetType":
                            if (!mr.HasThis && a.Length > 0 && a[0].Kind == VK.Str && a[0].Strs.Length == 1)
                                return Val.OfType(new TypeVal { Name = a[0].Strs[0] });
                            return null;
                    }
                    return null;
                case "System.Object":
                    if (name == "GetType" && self != null)
                    {
                        // A declared type that says more than object holds whoever the caller
                        // is, so a lookup on it is checked even inside a helper.
                        TypeVal rt = self.RuntimeType();
                        if (rt != null) return Val.OfType(rt);
                        return Val.Unknown(null, self.FromParam);
                    }
                    return null;
                case "System.Array":
                    if (name == "GetValue" && self != null && self.Hint != null)
                        return new Val { Kind = VK.Unknown, Hint = self.Hint.ElementOf(), FromParam = self.FromParam };
                    return null;
                case "HarmonyLib.Traverse":
                case "HarmonyLib.Traverse`1":
                    return Traverse(f, mr, self, a);
                case "System.Reflection.Assembly":
                    if (name == "GetType" && a.Length > 0 && a[0].Kind == VK.Str && a[0].Strs.Length == 1)
                        return Val.OfType(new TypeVal { Name = a[0].Strs[0] });
                    return null;
                case "System.Reflection.FieldInfo":
                case "System.Reflection.MethodInfo":
                case "System.Reflection.PropertyInfo":
                case "System.Reflection.EventInfo":
                    if (self != null && self.Kind == VK.Member &&
                        (name == "get_FieldType" || name == "get_ReturnType" || name == "get_PropertyType" || name == "get_EventHandlerType"))
                        return new Val { Kind = VK.MemberType, Member = self.Member, MemberAttr = name.Substring(4) };
                    // A value read through a looked-up member has that member's type.
                    if (self != null && self.Kind == VK.Member && name == "GetValue" &&
                        self.Member.Names != null && self.Member.Names.Length == 1)
                        return new Val
                        {
                            Kind = VK.Unknown,
                            Hint = new TypeVal { MemberOf = self.Member, MemberName = self.Member.Names[0], Inexact = true },
                        };
                    return null;
                case "System.Delegate":
                    if (name == "CreateDelegate" && a.Length >= 2 && a[0].Kind == VK.Type && a[1].Kind == VK.Member)
                        a[1].Member.Constraints.Add(new Constraint { Attr = "Delegate", Type = a[0].Type });
                    return null;
                case "HarmonyLib.AccessTools":
                    return AccessTools(f, mr, a);
                case "HarmonyLib.HarmonyMethod":
                    if (name == ".ctor" && a.Length > 0)
                    {
                        if (a[0].Kind == VK.Member) return new Val { Kind = VK.Hook, Member = a[0].Member };
                        if (a[0].Kind == VK.Type && a.Length > 1)
                        {
                            Lookup l = MakeLookup(f, "HarmonyMethod", MemberKind.Method, a[0], a[1],
                                                  a.Length > 2 ? a[2] : Val.Null, Search.AccessTools, 0);
                            return l == null ? null : new Val { Kind = VK.Hook, Member = l };
                        }
                    }
                    return null;
                case "HarmonyLib.Harmony":
                    if (name == "Patch" && a.Length > 0 && a[0].Kind == VK.Member)
                        for (int i = 1; i < a.Length; i++)
                            if (a[i].Kind == VK.Hook)
                                Links.Add(new PatchLink
                                {
                                    Target = a[0].Member,
                                    Hook = a[i].Member,
                                    HookKind = mr.Parameters[i].Name,
                                    Site = Site(),
                                });
                    return null;
            }

            // foreach over, or indexing into, a collection read through a looked-up member: the
            // element has the collection's element type.
            if (self != null && self.Hint != null)
            {
                if (name == "GetEnumerator" || name == "get_Item")
                    return new Val { Kind = VK.Unknown, Hint = self.Hint.ElementOf() };
                if (name == "get_Current")
                    return new Val { Kind = VK.Unknown, Hint = self.Hint };
            }

            // A framework method: follow it when a constant goes in.
            if (f.Depth < MaxDepth && (self != null && self.Informative || a.Any(x => x.Informative)))
            {
                MethodDefinition md = null;
                try { md = mr.Resolve(); }
                catch (AssemblyResolutionException) { }
                if (md != null && md.Module == _module && md.HasBody && !f.Active.Contains(md))
                {
                    var args = new List<Val>();
                    if (md.HasThis) args.Add(self ?? Val.Unknown());
                    args.AddRange(a);
                    Dictionary<string, TypeVal> generics = null;
                    if (mr is GenericInstanceMethod gim)
                    {
                        generics = new Dictionary<string, TypeVal>();
                        for (int i = 0; i < gim.GenericArguments.Count; i++)
                        {
                            TypeVal t = Subst(f, gim.GenericArguments[i]);
                            if (t != null) generics["M" + i] = t;
                        }
                    }
                    List<Val> returns = Run(md, args.ToArray(), generics, f.Depth + 1, f.Active).Where(r => r.Informative).ToList();
                    // Several returns each read through a different member (a property, else a
                    // field): the value's type is whichever of them resolves.
                    if (returns.Count > 1 && returns.All(r => r.Kind == VK.Unknown && r.Hint != null))
                        return new Val
                        {
                            Kind = VK.Unknown,
                            Hint = new TypeVal
                            {
                                MemberOf = returns[0].Hint.MemberOf,
                                MemberName = returns[0].Hint.MemberName,
                                Element = returns[0].Hint.Element,
                                Inexact = true,
                                Alternates = returns.Skip(1).Select(r => r.Hint).ToList(),
                            },
                        };
                    return returns.FirstOrDefault();
                }
            }
            return null;
        }

        private string Site()
        {
            return _site.DeclaringType.FullName.Replace('/', '+') + "::" + _site.Name;
        }

        private static void Constrain(Val member, Val type, bool assignable)
        {
            if (member.Kind == VK.MemberType && type.Kind == VK.Type)
                member.Member.Constraints.Add(new Constraint { Attr = member.MemberAttr, Assignable = assignable, Type = type.Type });
        }

        private Lookup MakeLookup(Frame f, string api, MemberKind kind, Val type, Val name, Val parameters, Search search, int flags)
        {
            // A null name or type is the branch of a conditional that never makes the call.
            type = AsType(type);
            if (type.Kind == VK.Null || name != null && name.Kind == VK.Null) return null;
            var l = new Lookup { Api = api, Kind = kind, Search = search, Flags = flags, SiteMethod = _site, Frame = f.Id };
            // A method that walks Type.BaseType repeats its lookups up the chain; the IL is run
            // once, so the lookup is checked against the whole chain instead.
            if (search == Search.Reflection && f.WalksBase) l.WalksBase = true;
            l.Sites.Add(Site());
            if (type.Kind == VK.Type)
            {
                l.Type = type.Type;
                l.TypeFromParam = type.FromParam;
            }
            else l.TypeFromParam = type.FromParam;
            if (name != null)
            {
                l.Names = name.StringsOrNull();
                l.NameFromParam = l.Names == null && name.FromParam;
            }
            else if (kind == MemberKind.Ctor) l.Names = new[] { ".ctor" };
            if (parameters == null || parameters.Kind == VK.Null) l.Params = null;
            else
            {
                l.Params = parameters.TypesOrNull();
                if (l.Params == null)
                {
                    l.ParamsUnknown = true;
                    l.ParamsFromParam = parameters.FromParam;
                }
            }
            Lookups.Add(l);
            return l;
        }

        /// <summary>
        /// HarmonyX's Traverse: Create(obj) or Create(type), then Field/Property by name, each
        /// step a lookup on the type the previous step found. GetValue() carries that type on,
        /// so a chain Create(x).Field("a").GetValue() -> Create(..).Field("b") checks b on a's type.
        /// </summary>
        private Val Traverse(Frame f, MethodReference mr, Val self, Val[] a)
        {
            switch (mr.Name)
            {
                case "Create":
                {
                    TypeVal t = null;
                    bool fromParam = a.Length > 0 && a[0].FromParam;
                    if (mr is GenericInstanceMethod gim && a.Length == 0) t = Subst(f, gim.GenericArguments[0]);
                    else if (a.Length == 1 && a[0].Kind == VK.Type) t = a[0].Type;
                    else if (a.Length == 1) t = a[0].RuntimeType();
                    return new Val { Kind = VK.Traverse, Type = t, FromParam = t == null && fromParam };
                }
                case "Field":
                case "Property":
                {
                    if (self == null || a.Length == 0) return null;
                    Val type = self.Kind == VK.Traverse && self.Type != null ? Val.OfType(self.Type) : Val.Unknown(null, self.FromParam);
                    Lookup l = MakeLookup(f, "Traverse." + mr.Name, mr.Name == "Field" ? MemberKind.Field : MemberKind.Property,
                                          type, a[0], Val.Null, Search.AccessTools, 0);
                    if (l == null || l.Names == null || l.Names.Length != 1)
                        return new Val { Kind = VK.Traverse, FromParam = self.FromParam || a[0].FromParam };
                    return new Val
                    {
                        Kind = VK.Traverse,
                        Type = new TypeVal { MemberOf = l, MemberName = l.Names[0], Inexact = true },
                        FromParam = self.FromParam,
                    };
                }
                case "GetValue":
                    if (self != null && self.Kind == VK.Traverse && self.Type != null && !(mr is GenericInstanceMethod))
                        return new Val { Kind = VK.Unknown, Hint = self.Type, FromParam = self.FromParam };
                    return null;
                case "SetValue":
                    return self;
            }
            return null;
        }

        private static Val AsType(Val v)
        {
            if (v != null && v.Kind == VK.MemberType && v.Member.Names != null && v.Member.Names.Length == 1)
                return Val.OfType(new TypeVal { MemberOf = v.Member, MemberName = v.Member.Names[0], Inexact = true });
            return v;
        }

        private Val ReflectionLookup(Frame f, MethodReference mr, Val self, Val[] a, MemberKind kind)
        {
            self = AsType(self);
            if (self == null) return null;
            Val name = null, parameters = null;
            int flags = kind == MemberKind.Ctor ? 16 | 4 : 16 | 4 | 8; // Public | Instance (| Static)
            for (int i = 0; i < a.Length; i++)
            {
                string pt = mr.Parameters[i].ParameterType.FullName;
                if (pt == "System.String" && name == null) name = a[i];
                else if (pt == "System.Type[]" && parameters == null) parameters = a[i];
                else if (pt == "System.Reflection.BindingFlags")
                    flags = a[i].Kind == VK.Int ? (int)a[i].Int : 4 | 8 | 16 | 32;
            }
            if (kind == MemberKind.Method && parameters == null) parameters = Val.Null;
            Lookup l = MakeLookup(f, "Type." + mr.Name, kind, self, name, parameters ?? Val.Null, Search.Reflection, flags);
            return l == null ? null : new Val { Kind = VK.Member, Member = l };
        }

        private Val AccessTools(Frame f, MethodReference mr, Val[] a)
        {
            string name = mr.Name;
            if (name == "TypeByName")
                return a.Length > 0 && a[0].Kind == VK.Str && a[0].Strs.Length == 1
                    ? Val.OfType(new TypeVal { Name = a[0].Strs[0] })
                    : Val.Unknown(null, a.Length > 0 && a[0].FromParam);

            var generics = new List<TypeVal>();
            if (mr is GenericInstanceMethod gim)
                foreach (TypeReference g in gim.GenericArguments) generics.Add(Subst(f, g));

            Val type = null, member = null, typesA = null;
            Val nameV = null;
            for (int i = 0; i < a.Length; i++)
            {
                string pt = mr.Parameters[i].ParameterType.FullName;
                if (pt == "System.Type" && type == null) type = a[i];
                else if (pt == "System.String" && nameV == null) nameV = a[i];
                else if (pt == "System.Type[]" && typesA == null) typesA = a[i];
                else if (pt.StartsWith("System.Reflection.", StringComparison.Ordinal) && member == null) member = a[i];
            }

            if (name == "MethodDelegate")
            {
                if (member != null && member.Kind == VK.Member && generics.Count > 0 && generics[0] != null)
                    member.Member.Constraints.Add(new Constraint { Attr = "Delegate", Type = generics[0] });
                return null;
            }

            bool fieldRef = name == "FieldRefAccess" || name == "StaticFieldRefAccess";
            if (fieldRef && member != null)
            {
                // FieldRefAccess<T, F>(FieldInfo): the field's type must fit F.
                if (member.Kind == VK.Member && generics.Count > 0 && generics[generics.Count - 1] != null)
                    member.Member.Constraints.Add(new Constraint { Attr = "FieldType", Assignable = true, Type = generics[generics.Count - 1] });
                return null;
            }

            // "Type:Member" in one string, the form with no Type argument.
            if (type == null && nameV != null && nameV.Kind == VK.Str && nameV.Strs.Length == 1 && nameV.Strs[0].Contains(':') && !fieldRef)
            {
                string[] parts = nameV.Strs[0].Split(':');
                type = Val.OfType(new TypeVal { Name = parts[0] });
                nameV = Val.OfStr(parts[1]);
            }
            if (fieldRef && type == null && generics.Count == 2)
                type = generics[0] != null ? Val.OfType(generics[0]) : Val.Unknown(null, true);
            if (type == null) return null;

            MemberKind kind;
            string bare = name.StartsWith("Declared", StringComparison.Ordinal) ? name.Substring(8) : name;
            switch (bare)
            {
                case "Field": case "FieldRefAccess": case "StaticFieldRefAccess": kind = MemberKind.Field; break;
                case "Method": kind = MemberKind.Method; break;
                case "Property": kind = MemberKind.Property; break;
                case "PropertyGetter": kind = MemberKind.Getter; break;
                case "PropertySetter": kind = MemberKind.Setter; break;
                case "Constructor": kind = MemberKind.Ctor; break;
                default: return null;
            }
            Search search = name.StartsWith("Declared", StringComparison.Ordinal) || kind == MemberKind.Ctor
                ? Search.Declared : Search.AccessTools;
            Val parameters = kind == MemberKind.Method || kind == MemberKind.Ctor ? typesA ?? Val.Null : Val.Null;
            Lookup l = MakeLookup(f, "AccessTools." + name, kind, type, kind == MemberKind.Ctor ? null : nameV, parameters, search, 0);
            if (l == null) return null;
            if (name == "StaticFieldRefAccess") l.RequireStatic = true;
            if (fieldRef && generics.Count > 0 && generics[generics.Count - 1] != null)
                l.Constraints.Add(new Constraint { Attr = "FieldType", Assignable = true, Type = generics[generics.Count - 1] });
            return new Val { Kind = VK.Member, Member = l };
        }
    }
}
