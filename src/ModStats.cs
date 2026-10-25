using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

using CG.Game.Player;
using CG.Space;
using Gameplay.Utilities;
using Newtonsoft.Json.Linq;
using UnityEngine;

using Client.Player.Interactions;

namespace Ectype
{
    /// <summary>The stat collection a declared stat is registered into.</summary>
    public enum StatCollectionKind
    {
        /// <summary>Every player character's own collection, on every replica.</summary>
        Character,
        /// <summary>Every player ship's own collection, on every replica.</summary>
        Ship,
        /// <summary>The framework's run-wide collection; see <see cref="SessionStats"/>.</summary>
        Session,
    }

    /// <summary>One stat the framework registers into a collection with a plain base value: a
    /// generic framework factor, or a stat a mod declared in its stats file.</summary>
    internal sealed class ModStatDecl
    {
        internal string Owner;
        internal string Name;
        internal int Id;
        internal bool IsFactor;
        internal float Default;
        internal StatCollectionKind Collection;
        internal string Display;
        internal string Tooltip;
        internal string DisplayType;
        internal bool LowerIsBetter;
        internal string IconFrom;
        /// <summary>The file or resource that declared it, or "framework".</summary>
        internal string DeclaredBy;
        internal bool Embedded;
        internal string SortAssembly;
        internal string SortName;

        internal string Qualified { get { return Owner + "/" + Name; } }
    }

    /// <summary>
    /// Stats owned by a mod that has no role, declared in a data file the mod ships, and the
    /// generic framework factors that have no field behind them.
    ///
    /// A mod ships a file whose name ends in <see cref="FileSuffix"/>, embedded as a resource of
    /// its plugin assembly or beside its DLL. The framework reads every such file once, with the
    /// framework's own stats, and for each stat: registers its tooltip entry in the game's stat
    /// table under the id derived from (owner, name), and registers a ModifiableFloat holding the
    /// declared default into every collection of the declared kind as it is built. The id is the
    /// same pure function of strings the framework uses for every custom stat (see
    /// <see cref="Id(string, string)"/>), so a mod that never references the framework computes
    /// the same id and reads the stat from the game's own collection by id.
    /// </summary>
    public static class ModStats
    {
        /// <summary>The suffix that marks a stats file, as a resource name or a file name.</summary>
        public const string FileSuffix = "ectype-stats.json";

        internal const int MaxFileBytes = 65536;
        internal const int MaxStatsPerFile = 256;

        private static readonly Regex NamePattern = new Regex("^[A-Za-z][A-Za-z0-9_]{0,63}\\z");

        /// <summary>The generic framework factors and the collection each lives on.</summary>
        private static readonly KeyValuePair<string, StatCollectionKind>[] FrameworkPlain =
        {
            new KeyValuePair<string, StatCollectionKind>(FrameworkStats.MeleeDamage, StatCollectionKind.Character),
            new KeyValuePair<string, StatCollectionKind>(FrameworkStats.MeleeCooldown, StatCollectionKind.Character),
            new KeyValuePair<string, StatCollectionKind>(FrameworkStats.MeleeReach, StatCollectionKind.Character),
            new KeyValuePair<string, StatCollectionKind>(FrameworkStats.AntiPersonnelDamage, StatCollectionKind.Ship),
            new KeyValuePair<string, StatCollectionKind>(FrameworkStats.HostileSpawnScale, StatCollectionKind.Session),
        };

        private static readonly List<ModStatDecl> _wired = new List<ModStatDecl>();
        private static readonly Dictionary<int, ModStatDecl> _byId = new Dictionary<int, ModStatDecl>();
        private static readonly Dictionary<string, ModStatDecl> _byName =
            new Dictionary<string, ModStatDecl>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<StatCollectionKind> _loggedSurface = new HashSet<StatCollectionKind>();
        private static readonly HashSet<string> _warnedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _discovered;

        /// <summary>Whether stats-file discovery has run.</summary>
        internal static bool Discovered { get { return _discovered; } }
        private static int _modCount;

        /// <summary>Discovery outcome for the F5 status page.</summary>
        public static string Status = "not discovered";

        // ------------------------------------------------------------------ ids

        /// <summary>
        /// The id of stat <paramref name="name"/> owned by <paramref name="owner"/>:
        /// <c>0x20000000 | (first four bytes of MD5(UTF-8 "ectype:stat:" + owner + "/" + name),
        /// read little-endian, &amp; 0x0FFFFFFF)</c>. Case-sensitive. A pure function: it does not
        /// need the stat to be declared or the framework to have registered anything.
        /// </summary>
        public static int Id(string owner, string name)
        {
            if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(name)) return 0;
            return Guids.StatId(owner, name);
        }

        /// <summary>
        /// The id of a stat by name: <c>"owner/Name"</c> for a stat a stats file declares (the
        /// declared spelling is found case-insensitively), <c>"ectype/Name"</c> or a bare name for
        /// a framework stat. An undeclared <c>"owner/Name"</c> with a valid owner and name still
        /// returns its derived id. 0 when the name is malformed or a bare name the framework does
        /// not own.
        /// </summary>
        public static int Id(string name)
        {
            int id;
            ModStatDecl decl;
            return Resolve(name, out id, out decl) ? id : 0;
        }

        /// <summary>True when a stats file declared <paramref name="qualifiedName"/>
        /// ("owner/Name") and it registered.</summary>
        public static bool IsDeclared(string qualifiedName)
        {
            return qualifiedName != null && _byName.ContainsKey(qualifiedName);
        }

        /// <summary>The id of a stat by its qualified name: one a stats file declared, or
        /// <c>"ectype/Name"</c> for a framework stat; false for any other name. Manifest
        /// resolution uses this.</summary>
        internal static bool TryGetDeclaredId(string qualifiedName, out int id)
        {
            id = 0;
            if (qualifiedName == null) return false;
            int slash = qualifiedName.IndexOf('/');
            if (slash <= 0) return false;
            if (string.Equals(qualifiedName.Substring(0, slash), FrameworkStats.Owner, StringComparison.OrdinalIgnoreCase))
                return FrameworkStats.TryGet(qualifiedName.Substring(slash + 1), out id);
            ModStatDecl decl;
            if (!_byName.TryGetValue(qualifiedName, out decl)) return false;
            id = decl.Id;
            return true;
        }

        /// <summary>True when <paramref name="id"/> is registered for the session collection.</summary>
        internal static bool IsSessionStat(int id)
        {
            ModStatDecl decl;
            return _byId.TryGetValue(id, out decl) && decl.Collection == StatCollectionKind.Session &&
                   _wired.Contains(decl);
        }

        private static bool Resolve(string name, out int id, out ModStatDecl decl)
        {
            id = 0;
            decl = null;
            if (string.IsNullOrEmpty(name)) return false;
            if (_byName.TryGetValue(name, out decl))
            {
                id = decl.Id;
                return true;
            }
            int slash = name.IndexOf('/');
            if (slash < 0)
            {
                if (!FrameworkStats.TryGet(name, out id)) return false;
                _byId.TryGetValue(id, out decl);
                return true;
            }
            string owner = name.Substring(0, slash), stat = name.Substring(slash + 1);
            if (string.Equals(owner, FrameworkStats.Owner, StringComparison.OrdinalIgnoreCase))
            {
                if (!FrameworkStats.TryGet(stat, out id)) return false;
                _byId.TryGetValue(id, out decl);
                return true;
            }
            if (Identifiers.OwnerFormProblem(owner) != null || !NamePattern.IsMatch(stat)) return false;
            id = Guids.StatId(owner, stat);
            return true;
        }

        // ------------------------------------------------------------------ reading

        /// <summary>
        /// This client's collection of <paramref name="kind"/>: the local player's character, the
        /// local player's crew ship (<see cref="Topology.CrewShipOf(CG.Game.Player.Player)"/>), or
        /// the session collection. Null when it does not exist yet.
        /// </summary>
        public static StatCollection LocalCollection(StatCollectionKind kind)
        {
            switch (kind)
            {
                case StatCollectionKind.Character:
                    return LocalPlayer.Instance != null ? LocalPlayer.Instance.Stats : null;
                case StatCollectionKind.Ship:
                    PlayerControlledShip ship = LocalPlayer.Instance != null
                        ? Topology.CrewShipOf(LocalPlayer.Instance) : null;
                    return ship != null ? ship.Stats : null;
                default:
                    return SessionStats.Collection;
            }
        }

        /// <summary>
        /// The composed value of stat <paramref name="name"/> (see <see cref="Id(string)"/>) in
        /// <paramref name="collection"/>. False when the collection does not hold it; the value is
        /// then the declared default (1 for a factor), or 0 for a stat with no declaration.
        /// </summary>
        public static bool TryGet(StatCollection collection, string name, out float value)
        {
            int id;
            ModStatDecl decl;
            value = 0f;
            if (!Resolve(name, out id, out decl))
            {
                if (_warnedNames.Add(name ?? ""))
                    Plugin.Log.LogWarning("Ectype: mod stats: '" + name + "' names no stat (expected " +
                                          "\"owner/Name\" or a framework stat name); it reads 0.");
                return false;
            }
            if (decl != null) value = decl.Default;
            StatBase stat;
            if (collection == null || collection.Stats == null || !collection.Stats.TryGetValue(id, out stat))
                return false;
            ModifiableFloat f = stat.Data as ModifiableFloat;
            if (f == null) return false;
            value = f.Value;
            return true;
        }

        /// <summary>The value <see cref="TryGet(StatCollection, string, out float)"/> reports.</summary>
        public static float Get(StatCollection collection, string name)
        {
            float v;
            TryGet(collection, name, out v);
            return v;
        }

        /// <summary><see cref="TryGet(StatCollection, string, out float)"/> on
        /// <see cref="LocalCollection"/>(<paramref name="kind"/>).</summary>
        public static bool TryGet(StatCollectionKind kind, string name, out float value)
        {
            return TryGet(LocalCollection(kind), name, out value);
        }

        /// <summary><see cref="Get(StatCollection, string)"/> on
        /// <see cref="LocalCollection"/>(<paramref name="kind"/>).</summary>
        public static float Get(StatCollectionKind kind, string name)
        {
            return Get(LocalCollection(kind), name);
        }

        // ------------------------------------------------------------------ declared set

        /// <summary>The stats registered for <paramref name="kind"/>, in registration order.</summary>
        internal static List<ModStatDecl> DeclaredFor(StatCollectionKind kind)
        {
            List<ModStatDecl> list = new List<ModStatDecl>();
            foreach (ModStatDecl d in _wired)
                if (d.Collection == kind) list.Add(d);
            return list;
        }

        internal static bool TryGetDecl(int id, out ModStatDecl decl)
        {
            return _byId.TryGetValue(id, out decl);
        }

        // ------------------------------------------------------------------ discovery

        private sealed class Source
        {
            internal string DeclaredBy;
            internal string Text;
            internal bool Embedded;
            /// <summary>The declaring assembly's simple name; for a loose file, the smallest
            /// simple name among the plugins whose folder holds it.</summary>
            internal string SortAssembly;
            /// <summary>The resource name or the file name, never a path.</summary>
            internal string SortName;
            /// <summary>The GUIDs the file's owner may be: the plugins its assembly declares, or,
            /// for a loose file, the plugins whose DLL is in its folder.</summary>
            internal HashSet<string> Plugins;
            /// <summary>Where <see cref="Plugins"/> come from, completing "a plugin ...".</summary>
            internal string PluginsFrom;
        }

        /// <summary>
        /// Reads every stats file and registers what it declares. Runs once, from the registration
        /// driver, after the framework's own stats and before any role registers a perk. Returns
        /// without running while BepInEx is still loading plugins or before the framework's own
        /// stats are registered, so an early call cannot latch on a partial plugin list. Each file
        /// is parsed in its own try/catch: a file that throws is refused by name and the others
        /// still register.
        /// </summary>
        internal static void DiscoverAndRegister()
        {
            if (_discovered) return;
            if (!FrameworkStats.Registered) return;
            if (!PluginLoad.ReadyFor("stats-file discovery")) return;
            _discovered = true;

            foreach (KeyValuePair<string, StatCollectionKind> kv in FrameworkPlain)
            {
                int id = FrameworkStats.Id(kv.Key);
                if (id == 0) continue;
                ModStatDecl d = new ModStatDecl
                {
                    Owner = FrameworkStats.Owner, Name = kv.Key, Id = id, IsFactor = true, Default = 1f,
                    Collection = kv.Value, DeclaredBy = "framework",
                };
                _wired.Add(d);
                _byId[id] = d;
                _byName[d.Qualified] = d;
            }

            // Role ids are owners a stats file may not use, so the roles must be known first.
            // Discovery of roles is idempotent and runs on the same pass right after this anyway.
            try { RoleRegistry.DiscoverOnce(); }
            catch (Exception e) { Plugin.Log.LogError("Ectype: mod stats: role discovery failed: " + e); }

            List<string> errors = new List<string>();
            List<string> warnings = new List<string>();
            List<ModStatDecl> parsed = new List<ModStatDecl>();
            int files = 0;
            foreach (Source src in CollectSources(errors))
            {
                files++;
                List<ModStatDecl> found;
                try
                {
                    Source file = src;
                    found = Parse(src.Text, src.DeclaredBy, errors, warnings,
                                  o => RoleOwnerProblem(o) ??
                                       Identifiers.OwnerProblem(o, null, file.Plugins, file.PluginsFrom));
                }
                catch (Exception e)
                {
                    errors.Add(src.DeclaredBy + " rejected: " + e.Message.TrimEnd('.'));
                    continue;
                }
                foreach (ModStatDecl d in found)
                {
                    d.Embedded = src.Embedded;
                    d.SortAssembly = src.SortAssembly;
                    d.SortName = src.SortName;
                }
                parsed.AddRange(found);
            }
            List<ModStatDecl> accepted = Merge(Order(parsed), _byId, _byName, errors);

            int sessionCount = 0;
            foreach (ModStatDecl d in _wired) if (d.Collection == StatCollectionKind.Session) sessionCount++;

            List<string> summary = new List<string>();
            foreach (ModStatDecl d in accepted)
            {
                if (d.Collection == StatCollectionKind.Session && sessionCount >= SessionStats.MaxStats)
                {
                    errors.Add("'" + d.Qualified + "' from " + d.DeclaredBy + " skipped: the session collection " +
                               "holds at most " + SessionStats.MaxStats + " stats");
                    _byId.Remove(d.Id);
                    _byName.Remove(d.Qualified);
                    continue;
                }
                try
                {
                    StatRegistry.RegisterCustomStat(d.Owner, new CustomStatSpec
                    {
                        name = d.Name,
                        displayName = string.IsNullOrEmpty(d.Display) ? d.Name : d.Display,
                        desc = d.Tooltip ?? "",
                        lowerIsBetter = d.LowerIsBetter,
                        displayType = d.DisplayType,
                    }, BorrowIcon(d), d.DeclaredBy);
                }
                catch (Exception e)
                {
                    errors.Add("registering '" + d.Qualified + "' from " + d.DeclaredBy + " failed, skipped: " +
                               e.Message);
                    _byId.Remove(d.Id);
                    _byName.Remove(d.Qualified);
                    continue;
                }
                if (d.Collection == StatCollectionKind.Session) sessionCount++;
                _wired.Add(d);
                _modCount++;
                summary.Add(d.Qualified + "=" + d.Id + " (" + KindName(d.Collection) + ")");
            }

            foreach (string w in warnings) Plugin.Log.LogWarning("Ectype: mod stats: " + w + ".");
            foreach (string e in errors) Plugin.Log.LogError("Ectype: mod stats: " + e + ".");
            if (_modCount > 0)
                Plugin.Log.LogInfo("Ectype: mod stats: " + _modCount + " declared in " + files + " file(s): " +
                                   string.Join(", ", summary.ToArray()) + ".");
            else if (files > 0 || Plugin.Verbose)
                Plugin.Log.LogInfo("Ectype: mod stats: none declared (" + files + " file(s) read).");
            Status = _modCount + " mod-declared" + (errors.Count > 0 ? ", " + errors.Count + " error(s)" : "");
        }

        /// <summary>Null, or why <paramref name="owner"/> may not be a stats file's owner because a
        /// discovered role has that id.</summary>
        private static string RoleOwnerProblem(string owner)
        {
            RoleRecord rec = RoleRegistry.Find(owner);
            if (rec == null) return null;
            return "owner '" + owner + "' is the role id of '" + rec.Manifest.roleName + "' (" +
                   rec.SourceAssembly.GetName().Name + "); a role's stats come from its manifest's customStats";
        }

        /// <summary>
        /// The order in which duplicates are decided, independent of where the game is installed:
        /// by qualified name ignoring case, then embedded before loose, then by the declaring
        /// assembly's simple name, then by resource or file name. Stable for equal keys.
        /// </summary>
        internal static List<ModStatDecl> Order(List<ModStatDecl> decls)
        {
            List<KeyValuePair<int, ModStatDecl>> indexed = new List<KeyValuePair<int, ModStatDecl>>();
            for (int i = 0; i < decls.Count; i++) indexed.Add(new KeyValuePair<int, ModStatDecl>(i, decls[i]));
            indexed.Sort((a, b) =>
            {
                ModStatDecl x = a.Value, y = b.Value;
                int c = string.Compare(x.Qualified, y.Qualified, StringComparison.OrdinalIgnoreCase);
                if (c == 0) c = string.CompareOrdinal(x.Qualified, y.Qualified);
                if (c == 0) c = (x.Embedded ? 0 : 1).CompareTo(y.Embedded ? 0 : 1);
                if (c == 0) c = string.CompareOrdinal(x.SortAssembly ?? "", y.SortAssembly ?? "");
                if (c == 0) c = string.CompareOrdinal(x.SortName ?? "", y.SortName ?? "");
                return c != 0 ? c : a.Key.CompareTo(b.Key);
            });
            List<ModStatDecl> ordered = new List<ModStatDecl>();
            foreach (KeyValuePair<int, ModStatDecl> kv in indexed) ordered.Add(kv.Value);
            return ordered;
        }

        private static Sprite BorrowIcon(ModStatDecl d)
        {
            if (string.IsNullOrEmpty(d.IconFrom)) return null;
            try
            {
                int donor = StatRegistry.Resolve(d.IconFrom, null);
                ContextInfo info = DataTable<StatTable>.Instance.GetContext(donor);
                return info != null ? info.Icon : null;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: mod stats: '" + d.Qualified + "' iconFrom '" + d.IconFrom +
                                      "' gave no icon: " + e.Message + ".");
                return null;
            }
        }

        /// <summary>Embedded resources of every loaded plugin assembly, and loose files in the
        /// folder of every loaded plugin DLL (each file read once). A plugin that did not load
        /// contributes neither.</summary>
        private static List<Source> CollectSources(List<string> errors)
        {
            List<Source> sources = new List<Source>();
            List<KeyValuePair<string, Assembly>> assemblies = new List<KeyValuePair<string, Assembly>>();
            HashSet<Assembly> seenAsm = new HashSet<Assembly>();
            // folder -> smallest simple name among the loaded plugins in it
            Dictionary<string, string> dirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // assembly, or folder -> the GUIDs of the plugins it holds
            Dictionary<Assembly, HashSet<string>> asmGuids = new Dictionary<Assembly, HashSet<string>>();
            Dictionary<string, HashSet<string>> dirGuids =
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, BepInEx.PluginInfo> kv in BepInEx.Bootstrap.Chainloader.PluginInfos)
            {
                BepInEx.PluginInfo info = kv.Value;
                if (info == null || info.Instance == null) continue;
                Assembly asm = info.Instance.GetType().Assembly;
                string simple = asm.GetName().Name;
                if (seenAsm.Add(asm)) assemblies.Add(new KeyValuePair<string, Assembly>(simple, asm));
                string guid = info.Metadata != null ? info.Metadata.GUID : kv.Key;
                HashSet<string> set;
                if (!asmGuids.TryGetValue(asm, out set))
                    asmGuids[asm] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(guid);
                try
                {
                    if (string.IsNullOrEmpty(info.Location)) continue;
                    string dir = Path.GetFullPath(Path.GetDirectoryName(info.Location));
                    string prior;
                    if (!dirs.TryGetValue(dir, out prior) || string.CompareOrdinal(simple, prior) < 0)
                        dirs[dir] = simple;
                    if (!dirGuids.TryGetValue(dir, out set))
                        dirGuids[dir] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(guid);
                }
                catch { }
            }

            foreach (KeyValuePair<string, Assembly> a in assemblies)
            {
                string[] names;
                try { names = a.Value.GetManifestResourceNames(); }
                catch { continue; }
                foreach (string res in names)
                {
                    if (!res.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    string by = a.Key + " resource " + res;
                    try
                    {
                        using (Stream st = a.Value.GetManifestResourceStream(res))
                        {
                            if (st == null) continue;
                            if (st.Length > MaxFileBytes)
                            {
                                errors.Add(by + " rejected: larger than " + MaxFileBytes + " bytes");
                                continue;
                            }
                            using (StreamReader rd = new StreamReader(st, Encoding.UTF8))
                                sources.Add(new Source
                                {
                                    DeclaredBy = by, Text = rd.ReadToEnd(), Embedded = true,
                                    SortAssembly = a.Key, SortName = res,
                                    Plugins = asmGuids[a.Value], PluginsFrom = "its assembly declares",
                                });
                        }
                    }
                    catch (Exception e) { errors.Add(by + " rejected: unreadable: " + e.Message); }
                }
            }

            string root = SafePluginRoot();
            HashSet<string> seenPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> dir in dirs)
            {
                string[] files;
                try { files = Directory.GetFiles(dir.Key, "*" + FileSuffix, SearchOption.TopDirectoryOnly); }
                catch { continue; }
                foreach (string path in files)
                {
                    if (!seenPath.Add(path)) continue;
                    string by = Relative(root, path);
                    try
                    {
                        if (new FileInfo(path).Length > MaxFileBytes)
                        {
                            errors.Add(by + " rejected: larger than " + MaxFileBytes + " bytes");
                            continue;
                        }
                        sources.Add(new Source
                        {
                            DeclaredBy = by, Text = File.ReadAllText(path, Encoding.UTF8), Embedded = false,
                            SortAssembly = dir.Value, SortName = Path.GetFileName(path),
                            Plugins = dirGuids[dir.Key], PluginsFrom = "in its folder",
                        });
                    }
                    catch (Exception e) { errors.Add(by + " rejected: unreadable: " + e.Message); }
                }
            }
            return sources;
        }

        private static string SafePluginRoot()
        {
            try { return Path.GetFullPath(BepInEx.Paths.PluginPath); }
            catch { return null; }
        }

        private static string Relative(string root, string path)
        {
            if (root != null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return "plugins" + path.Substring(root.Length).Replace('\\', '/');
            return path;
        }

        // ------------------------------------------------------------------ parse and validate

        private static readonly HashSet<string> TopKeys =
            new HashSet<string>(StringComparer.Ordinal) { "owner", "stats", "$schema", "$comment" };
        private static readonly HashSet<string> StatKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "name", "kind", "default", "collection", "display", "tooltip", "lowerIsBetter", "displayType",
            "iconFrom", "$comment",
        };

        /// <summary>A problem that refuses the whole file from inside one stat.</summary>
        private sealed class FileRefusal : Exception
        {
            internal FileRefusal(string message) : base(message) { }
        }

        /// <summary>
        /// The stats one file declares. A file-level problem (not JSON, no valid owner, a reserved
        /// owner, an owner <paramref name="ownerProblem"/> refuses, no stats array, a number no
        /// float can hold) adds one error and yields nothing; a stat-level problem skips that stat
        /// with one error. Unknown keys are warnings. Ids are filled in. Never throws.
        /// </summary>
        internal static List<ModStatDecl> Parse(string json, string declaredBy, List<string> errors,
                                                List<string> warnings, Func<string, string> ownerProblem = null)
        {
            List<ModStatDecl> found = new List<ModStatDecl>();
            List<string> fileErrors = new List<string>();
            List<string> fileWarnings = new List<string>();
            try
            {
                ParseInto(json, declaredBy, found, fileErrors, fileWarnings, ownerProblem);
            }
            catch (FileRefusal r)
            {
                found.Clear();
                fileErrors.Clear();
                fileErrors.Add(declaredBy + " rejected: " + r.Message);
            }
            catch (Exception e)
            {
                found.Clear();
                fileErrors.Clear();
                fileErrors.Add(declaredBy + " rejected: " + e.Message.TrimEnd('.'));
            }
            errors.AddRange(fileErrors);
            warnings.AddRange(fileWarnings);
            return found;
        }

        private static void ParseInto(string json, string declaredBy, List<ModStatDecl> found, List<string> errors,
                                      List<string> warnings, Func<string, string> ownerProblem)
        {
            JObject root;
            try
            {
                root = JToken.Parse(json ?? "") as JObject;
            }
            catch (Exception e)
            {
                throw new FileRefusal("not JSON: " + e.Message.TrimEnd('.'));
            }
            if (root == null) throw new FileRefusal("the document is not a JSON object");
            foreach (JProperty p in root.Properties())
                if (!TopKeys.Contains(p.Name)) warnings.Add(declaredBy + ": unknown key '" + p.Name + "' ignored");

            string owner = Str(root["owner"]);
            string problem = OwnerProblem(owner);
            if (problem == null && ownerProblem != null) problem = ownerProblem(owner);
            if (problem != null) throw new FileRefusal(problem);
            JArray stats = root["stats"] as JArray;
            if (stats == null || stats.Count == 0) throw new FileRefusal("no \"stats\" array, or it is empty");
            if (stats.Count > MaxStatsPerFile)
                throw new FileRefusal(stats.Count + " stats (at most " + MaxStatsPerFile + ")");

            for (int i = 0; i < stats.Count; i++)
            {
                JObject s = stats[i] as JObject;
                string shortLabel = "stat #" + (i + 1);
                string label = declaredBy + " " + shortLabel;
                if (s == null)
                {
                    errors.Add(label + " skipped: not a JSON object");
                    continue;
                }
                string name = Str(s["name"]);
                if (name != null)
                {
                    shortLabel += " ('" + name + "')";
                    label = declaredBy + " " + shortLabel;
                }
                foreach (JProperty p in s.Properties())
                    if (!StatKeys.Contains(p.Name)) warnings.Add(label + ": unknown key '" + p.Name + "' ignored");

                ModStatDecl d;
                string why = ParseStat(owner, name, s, declaredBy, shortLabel, out d);
                if (why != null)
                {
                    errors.Add(label + " skipped: " + why);
                    continue;
                }
                found.Add(d);
            }
        }

        private static string ParseStat(string owner, string name, JObject s, string declaredBy, string label,
                                        out ModStatDecl d)
        {
            d = null;
            if (name == null) return "no \"name\"";
            if (!NamePattern.IsMatch(name))
                return "name '" + name + "' must start with a letter and hold only letters, digits and " +
                       "underscores, at most 64 characters";

            string kind = Str(s["kind"]);
            bool factor;
            if (kind == "factor") factor = true;
            else if (kind == "field") factor = false;
            else return "\"kind\" must be \"field\" or \"factor\"" + (kind != null ? ", not '" + kind + "'" : "");

            float def = 1f;
            JToken defToken = s["default"];
            if (defToken != null)
            {
                if (defToken.Type != JTokenType.Float && defToken.Type != JTokenType.Integer)
                    return "\"default\" must be a number";
                double value = Number(defToken, label);
                if (factor && value != 1.0)
                    return "a factor's \"default\" is 1 (got " + value.ToString("R", CultureInfo.InvariantCulture) + ")";
                def = (float)value;
            }
            else if (!factor) return "a field needs a \"default\"";

            StatCollectionKind collection;
            string col = Str(s["collection"]);
            if (col == "session") collection = StatCollectionKind.Session;
            else if (col == "ship") collection = StatCollectionKind.Ship;
            else if (col == "character") collection = StatCollectionKind.Character;
            else return "\"collection\" must be \"session\", \"ship\" or \"character\"" +
                        (col != null ? ", not '" + col + "'" : "");

            string displayType = Str(s["displayType"]);
            if (displayType != null && displayType != "value" && displayType != "percentage")
                return "\"displayType\" must be \"value\" or \"percentage\", not '" + displayType + "'";

            foreach (string key in new[] { "display", "tooltip", "iconFrom", "displayType" })
            {
                JToken t = s[key];
                if (t != null && t.Type != JTokenType.String) return "\"" + key + "\" must be a string";
            }

            JToken lib = s["lowerIsBetter"];
            if (lib != null && lib.Type != JTokenType.Boolean) return "\"lowerIsBetter\" must be true or false";

            d = new ModStatDecl
            {
                Owner = owner,
                Name = name,
                Id = Guids.StatId(owner, name),
                IsFactor = factor,
                Default = def,
                Collection = collection,
                Display = Str(s["display"]),
                Tooltip = Str(s["tooltip"]),
                DisplayType = displayType ?? (factor ? "percentage" : "value"),
                LowerIsBetter = lib != null && lib.Value<bool>(),
                IconFrom = Str(s["iconFrom"]),
                DeclaredBy = declaredBy,
            };
            return null;
        }

        /// <summary>
        /// A JSON number as a double, read from its text so an integer too large for a 64-bit type
        /// still reads. One that no float can hold refuses the whole file: a number the framework
        /// would round to infinity or to a different stat base is a file another reader would read
        /// differently.
        /// </summary>
        private static double Number(JToken token, string label)
        {
            string text = token.ToString(Newtonsoft.Json.Formatting.None);
            double value;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > float.MaxValue)
                throw new FileRefusal(label + ": \"default\" " + text + " is not a number a float can hold");
            return value;
        }

        /// <summary>Null, or why a file's owner is refused before anything asks which plugins
        /// are loaded: missing, reserved, or not of a plugin GUID's form.</summary>
        internal static string OwnerProblem(string owner)
        {
            if (owner == null) return "no \"owner\"";
            return Identifiers.OwnerFormProblem(owner);
        }

        /// <summary>
        /// Admits each declaration in order unless an earlier one (already in the maps, or
        /// earlier in the list) has the same id or the same qualified name ignoring case; each
        /// refusal is one error naming both declarers. Returns the admitted ones, already added to
        /// the maps.
        /// </summary>
        internal static List<ModStatDecl> Merge(List<ModStatDecl> incoming, Dictionary<int, ModStatDecl> byId,
                                                Dictionary<string, ModStatDecl> byName, List<string> errors)
        {
            List<ModStatDecl> accepted = new List<ModStatDecl>();
            foreach (ModStatDecl d in incoming)
            {
                ModStatDecl prior;
                if (!byId.TryGetValue(d.Id, out prior)) byName.TryGetValue(d.Qualified, out prior);
                if (prior != null)
                {
                    errors.Add(d.DeclaredBy + " declares '" + d.Qualified + "' (id " + d.Id + "), already " +
                               "declared as '" + prior.Qualified + "' (id " + prior.Id + ") by " + prior.DeclaredBy +
                               "; the later declaration is skipped");
                    continue;
                }
                byId[d.Id] = d;
                byName[d.Qualified] = d;
                accepted.Add(d);
            }
            return accepted;
        }

        private static string Str(JToken t)
        {
            return t != null && t.Type == JTokenType.String ? (string)t : null;
        }

        internal static string KindName(StatCollectionKind kind)
        {
            switch (kind)
            {
                case StatCollectionKind.Character: return "character";
                case StatCollectionKind.Ship: return "ship";
                default: return "session";
            }
        }

        // ------------------------------------------------------------------ wiring

        /// <summary>Registers every character stat into a player's own collection.</summary>
        internal static void WireCharacter(Player player)
        {
            if (player != null) Wire(player.Stats, StatCollectionKind.Character, player.GetType().Name);
        }

        /// <summary>Registers every ship stat into a player ship's own collection.</summary>
        internal static void WireShip(PlayerControlledShip ship)
        {
            if (ship != null) Wire(ship.Stats, StatCollectionKind.Ship, ship.GetType().Name);
        }

        /// <summary>
        /// Registers a ModifiableFloat at the declared default for every stat of
        /// <paramref name="kind"/> the collection does not already hold. Skips an id already
        /// present, so it is safe on every spawn-time pass. Never throws: callers sit in Awake
        /// postfixes. Returns how many it registered.
        /// </summary>
        internal static int Wire(StatTagCollection stats, StatCollectionKind kind, string label)
        {
            try
            {
                if (stats == null || stats.Stats == null) return 0;
                // Register on demand: a character spawning before the registration driver has run
                // must not miss out.
                if (!_discovered)
                {
                    // The stat table loads after the first characters can spawn; the registration
                    // driver retries, and a later spawn pass wires them.
                    try { FrameworkStats.EnsureRegistered(); DiscoverAndRegister(); }
                    catch { return 0; }
                }
                int n = 0;
                List<string> names = null;
                foreach (ModStatDecl d in _wired)
                {
                    if (d.Collection != kind || stats.Stats.ContainsKey(d.Id)) continue;
                    ModifiableFloat f = new ModifiableFloat(d.Default);
                    stats.RegisterStat(d.Id, ref f);
                    n++;
                    if (!_loggedSurface.Contains(kind))
                    {
                        if (names == null) names = new List<string>();
                        names.Add(d.Qualified + "=" + d.Default.ToString("0.###", CultureInfo.InvariantCulture));
                    }
                }
                if (names != null && _loggedSurface.Add(kind))
                    Plugin.Log.LogInfo("Ectype: mod stats: " + KindName(kind) + " surface installed on " + label +
                                       " (" + string.Join(", ", names.ToArray()) + ").");
                return n;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: mod stats: registering " + KindName(kind) + " stats onto " + label +
                                    " failed - readers get the declared defaults: " + e);
                return 0;
            }
        }
    }
}
