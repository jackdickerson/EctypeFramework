using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace OfflineChecks
{
    /// <summary>Where an assembly came from, which decides the report section a lookup lands in.</summary>
    internal enum Origin { Game, Loader, Plugin, Framework }

    /// <summary>
    /// Every assembly the checks read: the game's Managed folder, BepInEx\core, every DLL under
    /// BepInEx\plugins, and any plugin DLL named on the command line. Read as metadata only;
    /// nothing is loaded into this process.
    /// </summary>
    internal sealed class GameIndex : IAssemblyResolver
    {
        private readonly Dictionary<string, AssemblyDefinition> _assemblies =
            new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Origin> _origins = new Dictionary<string, Origin>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TypeDefinition> _byFullName = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<TypeDefinition>> _byName = new Dictionary<string, List<TypeDefinition>>(StringComparer.Ordinal);
        private readonly HashSet<string> _requiredNamespaces = new HashSet<string>(StringComparer.Ordinal);
        private readonly string _frameworkName;
        private Dictionary<string, List<TypeDefinition>> _subclasses;

        public readonly string GameDir;
        public readonly string ManagedDir;
        public readonly string PluginsDir;

        public GameIndex(string gameDir, string frameworkAssemblyName, IEnumerable<string> extraPlugins)
        {
            GameDir = gameDir;
            ManagedDir = Path.Combine(gameDir, "Void Crew_Data", "Managed");
            PluginsDir = Path.Combine(gameDir, "BepInEx", "plugins");
            _frameworkName = frameworkAssemblyName;
            if (!File.Exists(Path.Combine(ManagedDir, "Assembly-CSharp.dll")))
                throw new FileNotFoundException("Void Crew not found at '" + gameDir + "': no Void Crew_Data\\Managed\\Assembly-CSharp.dll.");

            // Command-line plugins first, so one passed explicitly wins over an installed copy.
            foreach (string p in extraPlugins) Load(p, Origin.Plugin);
            foreach (string p in Directory.GetFiles(ManagedDir, "*.dll")) Load(p, Origin.Game);
            string core = Path.Combine(gameDir, "BepInEx", "core");
            if (Directory.Exists(core))
                foreach (string p in Directory.GetFiles(core, "*.dll")) Load(p, Origin.Loader);
            if (Directory.Exists(PluginsDir))
                foreach (string p in Directory.GetFiles(PluginsDir, "*.dll", SearchOption.AllDirectories)) Load(p, Origin.Plugin);

            foreach (AssemblyDefinition a in _assemblies.Values)
                foreach (TypeDefinition t in a.MainModule.GetTypes())
                {
                    string reflName = t.FullName.Replace('/', '+');
                    if (!_byFullName.ContainsKey(reflName)) _byFullName[reflName] = t;
                    if (!_byName.TryGetValue(t.Name, out List<TypeDefinition> list)) _byName[t.Name] = list = new List<TypeDefinition>();
                    list.Add(t);
                    if (OriginOf(a.Name.Name) != Origin.Plugin) _requiredNamespaces.Add(TopNamespace(t));
                }
        }

        private void Load(string path, Origin origin)
        {
            AssemblyDefinition a;
            try
            {
                a = AssemblyDefinition.ReadAssembly(path, new ReaderParameters
                {
                    AssemblyResolver = this,
                    ReadingMode = ReadingMode.Deferred,
                    ReadSymbols = false,
                });
            }
            catch (BadImageFormatException) { return; } // a native DLL
            if (string.Equals(a.Name.Name, _frameworkName, StringComparison.OrdinalIgnoreCase)) return;
            if (_assemblies.ContainsKey(a.Name.Name)) return;
            _assemblies[a.Name.Name] = a;
            _origins[a.Name.Name] = origin;
            _paths[a.Name.Name] = path;
        }

        public Origin OriginOf(string assemblyName)
        {
            return _origins.TryGetValue(assemblyName, out Origin o) ? o : Origin.Framework;
        }

        public Origin OriginOf(TypeDefinition t)
        {
            return t.Module.Assembly.Name.Name == _frameworkName ? Origin.Framework : OriginOf(t.Module.Assembly.Name.Name);
        }

        public string PathOf(string assemblyName)
        {
            return _paths.TryGetValue(assemblyName, out string p) ? p : null;
        }

        /// <summary>
        /// A type looked up by a name string: a full name with '+' or '.' before a nested
        /// type's name, then a bare name, the order HarmonyX's TypeByName searches in.
        /// </summary>
        public TypeDefinition FindByName(string name)
        {
            if (_byFullName.TryGetValue(name, out TypeDefinition t)) return t;
            if (_byName.TryGetValue(name, out List<TypeDefinition> list)) return list[0];
            return null;
        }

        /// <summary>
        /// True when the name's top-level namespace is defined by the game or the loader, so a
        /// miss is a failure. Any other namespace belongs to a plugin, which may not be installed.
        /// </summary>
        public bool IsRequiredName(string name)
        {
            int dot = name.IndexOf('.');
            return _requiredNamespaces.Contains(dot < 0 ? "" : name.Substring(0, dot));
        }

        private static string TopNamespace(TypeDefinition t)
        {
            while (t.DeclaringType != null) t = t.DeclaringType;
            int dot = t.Namespace.IndexOf('.');
            return dot < 0 ? t.Namespace : t.Namespace.Substring(0, dot);
        }

        /// <summary>Every loaded type deriving from the given one, directly or not.</summary>
        public IEnumerable<TypeDefinition> SubclassesOf(TypeDefinition baseType)
        {
            if (_subclasses == null)
            {
                _subclasses = new Dictionary<string, List<TypeDefinition>>(StringComparer.Ordinal);
                // Keyed by base type and by each implemented interface, so an interface's
                // implementers count as its subclasses.
                foreach (TypeDefinition t in _assemblies.Values.SelectMany(a => a.MainModule.GetTypes()))
                {
                    var supers = t.Interfaces.Select(i => i.InterfaceType).ToList();
                    if (t.BaseType != null) supers.Add(t.BaseType);
                    foreach (TypeReference s in supers)
                    {
                        string b = s.GetElementType().FullName;
                        if (!_subclasses.TryGetValue(b, out List<TypeDefinition> l)) _subclasses[b] = l = new List<TypeDefinition>();
                        l.Add(t);
                    }
                }
            }
            var seen = new HashSet<TypeDefinition>();
            var queue = new Queue<string>();
            queue.Enqueue(baseType.FullName);
            while (queue.Count > 0)
                if (_subclasses.TryGetValue(queue.Dequeue(), out List<TypeDefinition> l))
                    foreach (TypeDefinition t in l)
                        if (seen.Add(t))
                        {
                            queue.Enqueue(t.FullName);
                            yield return t;
                        }
        }

        public AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            return Resolve(name, new ReaderParameters());
        }

        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            if (_assemblies.TryGetValue(name.Name, out AssemblyDefinition a)) return a;
            // The framework targets net472 and references netstandard and the class libraries
            // by name; the game ships Unity's copies of those in Managed.
            string p = Path.Combine(ManagedDir, name.Name + ".dll");
            if (File.Exists(p))
            {
                a = AssemblyDefinition.ReadAssembly(p, new ReaderParameters { AssemblyResolver = this, ReadingMode = ReadingMode.Deferred });
                _assemblies[name.Name] = a;
                _origins[name.Name] = Origin.Game;
                _paths[name.Name] = p;
                return a;
            }
            throw new AssemblyResolutionException(name);
        }

        public void Dispose()
        {
            foreach (AssemblyDefinition a in _assemblies.Values) a.Dispose();
        }
    }
}
