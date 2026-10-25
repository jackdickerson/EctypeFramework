using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace OfflineChecks
{
    /// <summary>
    /// Offline checks for EctypeFramework.dll against an installed game, without starting it:
    ///  1. every Harmony patch target and every by-name reflection lookup the framework makes
    ///     resolves in the game's assemblies, with the parameter and member types the framework
    ///     expects, and every patch method's injected parameters fit its target;
    ///  2. the public API matches the checked-in snapshot;
    ///  3. the members the framework binds by reflection in other plugins (Void Crew Fleet's
    ///     FleetApi, VoidManager) exist with the probed signatures, when the plugin is present.
    /// Exit code 0 when every check passes, 1 on a failure, 2 on a usage error.
    /// </summary>
    internal static class Program
    {
        private const string Usage =
            "usage: OfflineChecks --framework <EctypeFramework.dll> --game <Void Crew folder>\n" +
            "                     [--plugin <dll>]... [--targets <manual-targets.txt>] [--snapshot <file>]\n" +
            "                     [--update-snapshot] [--verbose]";

        private static int Main(string[] argv)
        {
            string framework = null, game = null, snapshot = null, targets = null;
            bool update = false, verbose = false;
            var plugins = new List<string>();
            for (int i = 0; i < argv.Length; i++)
            {
                string a = argv[i];
                string Next() => i + 1 < argv.Length ? argv[++i] : throw new ArgumentException(a + " needs a value");
                try
                {
                    switch (a)
                    {
                        case "--framework": framework = Next(); break;
                        case "--game": game = Next(); break;
                        case "--plugin": plugins.Add(Next()); break;
                        case "--snapshot": snapshot = Next(); break;
                        case "--targets": targets = Next(); break;
                        case "--update-snapshot": update = true; break;
                        case "--verbose": verbose = true; break;
                        default: throw new ArgumentException("unknown argument " + a);
                    }
                }
                catch (ArgumentException e)
                {
                    Console.Error.WriteLine(e.Message + "\n" + Usage);
                    return 2;
                }
            }
            if (framework == null || game == null)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }
            foreach (string p in plugins.Concat(new[] { framework }))
                if (!File.Exists(p))
                {
                    Console.Error.WriteLine("not found: " + p);
                    return 2;
                }

            string frameworkName = Path.GetFileNameWithoutExtension(framework);
            using var index = new GameIndex(game, frameworkName, plugins);
            using ModuleDefinition module = ModuleDefinition.ReadModule(framework, new ReaderParameters
            {
                AssemblyResolver = index,
                ReadSymbols = false,
            });

            Console.WriteLine("Offline checks: " + Path.GetFullPath(framework));
            Console.WriteLine("Game: " + index.GameDir);
            int failures = 0;

            // ---- recover and resolve every lookup
            var interp = new Interpreter(module);
            interp.RunAll();
            var lookups = new List<Lookup>(interp.Lookups);
            var links = new List<PatchLink>(interp.Links);
            PatchClasses.Collect(module, interp, lookups, links);
            if (targets != null)
            {
                List<Lookup> manual = ReadTargets(targets, out string error);
                if (manual == null)
                {
                    Console.Error.WriteLine(error);
                    return 2;
                }
                lookups.AddRange(manual);
            }

            var canon = new Dictionary<string, Lookup>();
            var alias = new Dictionary<Lookup, Lookup>();
            var dynamic = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Lookup l in lookups)
            {
                if (l.Suppressed) continue;
                if (l.Dynamic)
                {
                    foreach (string site in l.Sites) dynamic.Add(site + ": " + l.Api + " " + l.Describe());
                    continue;
                }
                string key = l.Key();
                if (canon.TryGetValue(key, out Lookup c))
                {
                    foreach (string s in l.Sites) c.Sites.Add(s);
                    c.Constraints.AddRange(l.Constraints);
                    if (c.Api != l.Api && !c.Api.Contains(l.Api)) c.Api += ", " + l.Api;
                    alias[l] = c;
                }
                else
                {
                    canon[key] = l;
                    alias[l] = l;
                }
            }

            var checker = new Checker(index);
            checker.CheckLookups(canon.Values);
            WaiveFallbacks(lookups, alias, checker);
            foreach (PatchLink link in links)
            {
                if (link.Target == null || !alias.TryGetValue(link.Target, out Lookup t)) continue;
                link.Target = t;
                if (link.Hook != null && alias.TryGetValue(link.Hook, out Lookup h)) link.Hook = h;
                checker.CheckPatch(link);
            }

            // ---- 1. the game
            var gameOutcomes = checker.Outcomes.Where(o => o.MissingPlugin == null && o.Origin != Origin.Plugin).ToList();
            Console.WriteLine();
            Console.WriteLine("== 1. Patch targets and by-name lookups");
            failures += Print(gameOutcomes, verbose);
            foreach (string f in checker.PatchFailures)
            {
                Console.WriteLine("  FAIL  injected parameter: " + f);
                failures++;
            }
            int patchTargets = gameOutcomes.Count(o => o.Lookup.Api == "[HarmonyPatch]" || links.Any(k => k.Target == o.Lookup));
            Console.WriteLine("  " + gameOutcomes.Count(o => o.Origin != Origin.Framework) + " game members (" + patchTargets +
                              " of them patch targets), " + gameOutcomes.Count(o => o.Origin == Origin.Framework) +
                              " of the framework's own members, " + checker.PatchesChecked + " patch method signatures; " +
                              (gameOutcomes.Count(o => !o.Ok) + checker.PatchFailures.Count) + " failed");
            if (dynamic.Count > 0)
            {
                Console.WriteLine("  Not checked: the type or name is known only at run time (" + dynamic.Count + "):");
                foreach (string d in dynamic) Console.WriteLine("    " + d);
            }

            // ---- 2. the public API
            Console.WriteLine();
            Console.WriteLine("== 2. Public API snapshot");
            if (snapshot == null) Console.WriteLine("  skipped: no --snapshot file given");
            else failures += CheckSnapshot(module, snapshot, update);

            // ---- 3. other plugins
            Console.WriteLine();
            Console.WriteLine("== 3. Bindings into other plugins");
            var pluginOutcomes = checker.Outcomes.Where(o => o.MissingPlugin != null || o.Origin == Origin.Plugin).ToList();
            foreach (var group in pluginOutcomes.Where(o => o.MissingPlugin == null).GroupBy(o => o.Assembly).OrderBy(g => g.Key))
            {
                Console.WriteLine("  " + group.Key + " (" + index.PathOf(group.Key) + ")");
                failures += Print(group.ToList(), verbose);
                Console.WriteLine("    " + group.Count() + " members checked, " + group.Count(o => !o.Ok) + " failed");
                ApiVersions(group.ToList(), module, ref failures);
            }
            foreach (var group in pluginOutcomes.Where(o => o.MissingPlugin != null).GroupBy(o => o.MissingPlugin).OrderBy(g => g.Key))
                Console.WriteLine("  skipped " + group.Key + ": " + group.First().Message +
                                  "; pass its DLL as a plugin (-Plugin, or --plugin) to check " + group.Count() + " member(s)");
            if (pluginOutcomes.Count == 0) Console.WriteLine("  the framework binds no other plugin by name");

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : failures + " FAILURE(S)");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>The checked-in table of members the IL cannot name: one lookup per line.</summary>
        private static List<Lookup> ReadTargets(string path, out string error)
        {
            error = null;
            var result = new List<Lookup>();
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                string[] parts = line.Split((char[])null, 3, StringSplitOptions.RemoveEmptyEntries);
                MemberKind kind;
                switch (parts.Length == 3 ? parts[0] : "")
                {
                    case "field": kind = MemberKind.Field; break;
                    case "method": kind = MemberKind.Method; break;
                    case "property": kind = MemberKind.Property; break;
                    case "event": kind = MemberKind.Event; break;
                    default:
                        error = path + ":" + (i + 1) + ": expected '<field|method|property|event> <type> <member>'";
                        return null;
                }
                string member = parts[2].Replace(" ", "");
                List<TypeVal> parameters = null;
                int paren = member.IndexOf('(');
                if (paren >= 0)
                {
                    string inner = member.Substring(paren + 1).TrimEnd(')');
                    parameters = inner.Length == 0
                        ? new List<TypeVal>()
                        : inner.Split(',').Select(p => new TypeVal { Name = p }).ToList();
                    member = member.Substring(0, paren);
                }
                var l = new Lookup
                {
                    Api = "manual target",
                    Kind = kind,
                    Type = new TypeVal { Name = parts[1] },
                    Names = new[] { member },
                    Params = parameters,
                    Search = Search.Declared,
                };
                l.Sites.Add(Path.GetFileName(path) + ":" + (i + 1));
                result.Add(l);
            }
            return result;
        }

        /// <summary>
        /// The same name looked up more than once on one type within one call is a fallback
        /// chain: a property, else a field; an exact overload, else any. A miss in the chain is
        /// waived when another link resolves, at every place the missing lookup is made.
        /// </summary>
        private static void WaiveFallbacks(List<Lookup> lookups, Dictionary<Lookup, Lookup> alias, Checker checker)
        {
            // Frame 0: from an attribute or the manual table, not from a call.
            var inCalls = lookups.Where(l => l.Frame > 0 && alias.ContainsKey(l)).ToList();
            var groups = inCalls.GroupBy(l => l.GroupKey).ToDictionary(g => g.Key, g => g.ToList());
            var waived = new Dictionary<Outcome, Outcome>();
            foreach (Lookup l in inCalls)
                foreach (string name in l.Names)
                {
                    Outcome own = checker.Get(alias[l], name);
                    if (own == null || own.Ok || own.MissingPlugin != null) continue;
                    Outcome other = groups[l.GroupKey]
                        .Where(g => alias[g] != alias[l] && g.Names.Contains(name))
                        .Select(g => checker.Get(alias[g], name))
                        .FirstOrDefault(o => o != null && o.Ok);
                    waived[own] = (!waived.TryGetValue(own, out Outcome w) || w != null) ? other : null;
                }
            foreach (KeyValuePair<Outcome, Outcome> kv in waived)
                if (kv.Value != null)
                {
                    kv.Key.Ok = true;
                    kv.Key.Message = "not found, but the fallback " + kv.Value.Lookup.Describe(kv.Key.Name) +
                                     " in the same call resolves: " + kv.Value.Message;
                }
        }

        private static int Print(List<Outcome> outcomes, bool verbose)
        {
            int failures = 0;
            foreach (Outcome o in outcomes.OrderBy(o => o.Describe(), StringComparer.Ordinal))
            {
                if (o.Ok && !verbose) continue;
                if (!o.Ok) failures++;
                Console.WriteLine("  " + (o.Blocked ? "skip" : o.Ok ? "ok  " : "FAIL") + "  " + o.Describe() + ": " + o.Message);
                Console.WriteLine("        " + o.Lookup.Api + " at " + string.Join(", ", o.Lookup.Sites));            }
            return failures;
        }

        /// <summary>
        /// A plugin's version constant read by the framework: a const field the framework looks
        /// up by name, with a framework const named Supported plus that field's name in the
        /// looking type or a type enclosing it. A different value means the framework stops
        /// binding at run time.
        /// </summary>
        private static void ApiVersions(List<Outcome> outcomes, ModuleDefinition module, ref int failures)
        {
            foreach (Outcome o in outcomes)
            {
                if (!o.Ok || !(o.Found is FieldDefinition f) || !f.HasConstant || o.Lookup.SiteMethod == null) continue;
                FieldDefinition supported = null;
                for (TypeDefinition t = o.Lookup.SiteMethod.DeclaringType; t != null && supported == null; t = t.DeclaringType)
                    supported = t.Fields.FirstOrDefault(x => x.HasConstant && x.Name == "Supported" + f.Name);
                if (supported == null) continue;
                bool same = Equals(supported.Constant, f.Constant);
                Console.WriteLine("    " + (same ? "ok  " : "FAIL") + "  " + Format.TypeName(f.DeclaringType) + "." + f.Name + " = " +
                                  f.Constant + "; the framework reads " + supported.Constant);
                if (!same) failures++;
            }
        }

        private static int CheckSnapshot(ModuleDefinition module, string path, bool update)
        {
            List<string> now = ApiSnapshot.Build(module);
            if (update)
            {
                ApiSnapshot.Write(path, now);
                Console.WriteLine("  wrote " + now.Count + " lines to " + path);
                return 0;
            }
            if (!File.Exists(path))
            {
                Console.WriteLine("  FAIL  " + path + " does not exist; create it with -UpdateSnapshot");
                return 1;
            }
            List<string> before = ApiSnapshot.Read(path);
            var removed = before.Except(now, StringComparer.Ordinal).ToList();
            var added = now.Except(before, StringComparer.Ordinal).ToList();
            if (removed.Count == 0 && added.Count == 0)
            {
                Console.WriteLine("  ok    " + now.Count + " lines match " + path);
                return 0;
            }
            Console.WriteLine("  FAIL  the public API differs from " + path + " (" + removed.Count + " removed, " + added.Count + " added):");
            foreach (string l in removed) Console.WriteLine("  - " + l);
            foreach (string l in added) Console.WriteLine("  + " + l);
            Console.WriteLine("  If the change is intended, rewrite the snapshot with -UpdateSnapshot and commit it with the change.");
            return 1;
        }
    }
}
