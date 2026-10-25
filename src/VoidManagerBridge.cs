using System;
using System.Collections;
using System.Reflection;
using System.Reflection.Emit;

using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;

using Hashtable = ExitGames.Client.Photon.Hashtable;

namespace Ectype
{
    /// <summary>
    /// Everything the framework does with VoidManager, all of it optional. VoidManager is a
    /// soft dependency: with it absent the framework runs in full and only the F5 page is
    /// missing.
    ///
    /// No compiled type in this assembly derives from, or has a field or signature naming, a
    /// VoidManager type. A type whose base class lives in a missing assembly cannot load, and
    /// <c>Assembly.GetTypes()</c> then throws <c>ReflectionTypeLoadException</c> for the whole
    /// assembly. HarmonyX's <c>AccessTools.TypeByName</c>, <c>AllTypes</c> and <c>PatchAll</c>
    /// enumerate every loaded assembly that way and log that exception as a warning on each
    /// call, so a single VoidManager-derived type would spam the log of every player without
    /// VoidManager, and break any other mod that scans assemblies without catching it.
    ///
    /// The framework declares no multiplayer type to VoidManager, so VoidManager lists it as
    /// <c>Unmanaged</c>: shown in its mod list, never compared at join, never raising its
    /// session level. VoidManager's host kicks a joiner without VoidManager whenever its own
    /// highest declared type is <c>All</c>, so a framework declared <c>All</c> would make every
    /// VoidManager host refuse every player without VoidManager. <c>Unmanaged</c> rather than
    /// <c>Client</c>: the Void Crew Mod Launcher takes a mod's need from VoidManager's type
    /// when one is declared and would advertise a <c>Client</c> framework as client-side
    /// only; for <c>Unmanaged</c> it falls back to the package's <c>All Required</c> category.
    /// What keeps framework peers compatible is the contract marker and the role mods'
    /// required assets, both compared by the game itself.
    ///
    /// With VoidManager loaded, two hooks, each resolved by name and logging one line naming
    /// what is lost when it misses:
    ///  * Join check. VoidManager's client-side check reads the host's mod list from the room
    ///    property <c>Mods</c> whenever the room is tagged modded, and throws when a host
    ///    without VoidManager tagged the room through the game's own property alone. A prefix
    ///    treats a tagged room with no <c>Mods</c> property as a host without VoidManager and
    ///    passes the check; the game's asset check still runs.
    ///  * Settings page. VoidManager finds a <c>ModSettingsMenu</c> only by scanning a mod's
    ///    own assembly, so a subclass is emitted at runtime, after VoidManager is known to be
    ///    loaded, and inserted into VoidManager's menu list by a postfix on
    ///    <c>PluginHandler.DiscoverPlugins</c>. That method runs from VoidManager's postfix on
    ///    the game's <c>PlayerProfileLoader.Awake</c>, after every plugin's Awake, so the hook
    ///    is in place in time. Its <c>Name</c> and <c>Draw</c> call <see cref="SettingsPage"/>.
    /// </summary>
    internal static class VoidManagerBridge
    {
        /// <summary>VoidManager's BepInEx plugin GUID.</summary>
        internal const string Guid = "VoidCrewModdingTeam.VoidManager";

        private static Assembly _vm;
        private static Type _menuType;
        private static bool _loggedHostWithoutVoidManager;

        /// <summary>True once <see cref="Install"/> found VoidManager loaded.</summary>
        internal static bool Loaded { get { return _vm != null; } }

        /// <summary>Called from the plugin's Awake, before the Enabled switch is read, so the F5
        /// page reports the framework's state whatever that switch says. The soft dependency
        /// makes BepInEx load VoidManager first, so its entry is already registered here.</summary>
        internal static void Install(Harmony harmony)
        {
            PluginInfo info;
            if (!Chainloader.PluginInfos.TryGetValue(Guid, out info) || info.Instance == null)
            {
                Plugin.Log.LogInfo("Ectype: settings menu unavailable: VoidManager not loaded.");
                return;
            }
            _vm = info.Instance.GetType().Assembly;

            try { PatchJoinCheck(harmony); }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: VoidManager join-check guard failed; joining a host " +
                                    "without VoidManager will fail in VoidManager's check: " + e);
            }
            try { PatchMenu(harmony); }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: VoidManager settings-page hook failed, so the F5 " +
                                    "page is missing: " + e);
            }
        }

        // ------------------------------------------------------------------ join check

        private static void PatchJoinCheck(Harmony harmony)
        {
            Type manager = _vm.GetType("VoidManager.MPModChecks.MPModCheckManager");
            MethodInfo check = manager != null
                ? AccessTools.Method(manager, "ModChecksClientside",
                                     new[] { typeof(Hashtable), typeof(bool) })
                : null;
            if (check == null)
            {
                Plugin.Log.LogWarning("Ectype: VoidManager.MPModChecks.MPModCheckManager." +
                                      "ModChecksClientside not found (VoidManager update?); " +
                                      "joining a host without VoidManager may fail in " +
                                      "VoidManager's own check.");
                return;
            }
            harmony.Patch(check, prefix: new HarmonyMethod(typeof(VoidManagerBridge),
                                                           nameof(BeforeClientJoinCheck)));
        }

        /// <summary>
        /// A room tagged modded with no <c>Mods</c> property was hosted without VoidManager:
        /// there is no host mod list to compare, so the check passes. Any other room goes to
        /// VoidManager's check unchanged.
        /// </summary>
        private static bool BeforeClientJoinCheck(Hashtable __0, ref bool __result)
        {
            try
            {
                if (__0 == null || __0.ContainsKey("Mods") || !TaggedModded(__0)) return true;
                if (!_loggedHostWithoutVoidManager)
                {
                    _loggedHostWithoutVoidManager = true;
                    Plugin.Log.LogInfo("Ectype: the host runs no VoidManager (modded room with no " +
                                       "mod list); VoidManager's join check is skipped and the " +
                                       "game's asset check decides.");
                }
                __result = true;
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: VoidManager join-check guard threw: " + e);
                return true;
            }
        }

        /// <summary>The game's modded tag: room property <c>R_Mod</c> equal to
        /// <c>ModdingType.mod_session</c>.</summary>
        internal static bool TaggedModded(Hashtable roomProperties)
        {
            object v;
            if (roomProperties == null || !roomProperties.TryGetValue("R_Mod", out v) || v == null)
                return false;
            try { return Convert.ToInt32(v) == (int)ModdingType.mod_session; }
            catch { return false; }
        }

        // --------------------------------------------------------------- settings page

        private static void PatchMenu(Harmony harmony)
        {
            Type handler = _vm.GetType("VoidManager.PluginHandler");
            MethodInfo discover = handler != null ? AccessTools.Method(handler, "DiscoverPlugins") : null;
            if (discover == null)
            {
                Plugin.Log.LogWarning("Ectype: VoidManager.PluginHandler.DiscoverPlugins not " +
                                      "found (VoidManager update?); the F5 page is missing.");
                return;
            }
            harmony.Patch(discover, postfix: new HarmonyMethod(typeof(VoidManagerBridge),
                                                               nameof(AfterDiscoverPlugins)));
        }

        private static void AfterDiscoverPlugins()
        {
            try
            {
                Type menuBase = _vm.GetType("VoidManager.CustomGUI.ModSettingsMenu");
                Type guiType = _vm.GetType("VoidManager.CustomGUI.GUIMain");
                Type handler = _vm.GetType("VoidManager.PluginHandler");
                if (menuBase == null || guiType == null || handler == null)
                    throw new Exception("a VoidManager menu type is missing");

                object gui = AccessTools.Property(guiType, "Instance").GetValue(null, null);
                IList settings = gui != null
                    ? AccessTools.Field(guiType, "settings").GetValue(gui) as IList : null;
                if (settings == null) throw new Exception("GUIMain has no settings list");

                // Once per list: a second discovery into the same list adds nothing, one into
                // a new list adds the page again.
                if (_menuType == null) _menuType = EmitMenuType(menuBase);
                foreach (object existing in settings)
                    if (existing != null && existing.GetType() == _menuType) return;
                object menu = Activator.CreateInstance(_menuType);

                // The per-mod "Settings menus" button matches by this reference.
                object plugin = FindVoidPlugin(handler, "ActiveVoidPlugins") ??
                                FindVoidPlugin(handler, "GeneratedVoidPlugins");
                FieldInfo owner = AccessTools.Field(menuBase, "MyVoidPlugin");
                if (plugin != null && owner != null) owner.SetValue(menu, plugin);

                // VoidManager keeps its own page first and sorts the rest by name.
                int at = settings.Count;
                for (int i = 1; i < settings.Count; i++)
                    if (string.Compare(NameOf(settings[i]), SettingsPage.Name(),
                                       StringComparison.CurrentCulture) > 0) { at = i; break; }
                settings.Insert(at, menu);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: adding the F5 page to VoidManager failed: " + e);
            }
        }

        private static object FindVoidPlugin(Type handler, string property)
        {
            PropertyInfo p = AccessTools.Property(handler, property);
            IDictionary plugins = p != null ? p.GetValue(null, null) as IDictionary : null;
            return plugins != null && plugins.Contains(EctypePluginInfo.PLUGIN_GUID)
                ? plugins[EctypePluginInfo.PLUGIN_GUID] : null;
        }

        private static string NameOf(object menu)
        {
            try
            {
                MethodInfo name = menu != null ? menu.GetType().GetMethod("Name", Type.EmptyTypes) : null;
                return name != null ? name.Invoke(menu, null) as string ?? "" : "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// Builds <c>Ectype.EctypeSettingsMenu : ModSettingsMenu</c> in a dynamic assembly. Each
        /// override loads a public static delegate field and invokes it; the delegates point at
        /// <see cref="SettingsPage"/>, whose methods are internal, and a delegate call is not
        /// subject to the cross-assembly access check a direct call from emitted code is.
        /// </summary>
        private static Type EmitMenuType(Type menuBase)
        {
            AssemblyName name = new AssemblyName("Ectype.VoidManagerMenu");
            AssemblyBuilder asm = AppDomain.CurrentDomain.DefineDynamicAssembly(
                name, AssemblyBuilderAccess.Run);
            ModuleBuilder module = asm.DefineDynamicModule(name.Name);
            TypeBuilder type = module.DefineType("Ectype.EctypeSettingsMenu",
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, menuBase);

            FieldBuilder nameFn = type.DefineField("NameFn", typeof(Func<string>),
                                                   FieldAttributes.Public | FieldAttributes.Static);
            FieldBuilder drawFn = type.DefineField("DrawFn", typeof(Action),
                                                   FieldAttributes.Public | FieldAttributes.Static);
            Forward(type, menuBase.GetMethod("Name", Type.EmptyTypes), nameFn,
                    typeof(Func<string>).GetMethod("Invoke"));
            Forward(type, menuBase.GetMethod("Draw", Type.EmptyTypes), drawFn,
                    typeof(Action).GetMethod("Invoke"));
            type.DefineDefaultConstructor(MethodAttributes.Public);

            Type built = type.CreateType();
            built.GetField("NameFn").SetValue(null, new Func<string>(SettingsPage.Name));
            built.GetField("DrawFn").SetValue(null, new Action(SettingsPage.Draw));
            return built;
        }

        /// <summary>A public virtual method with the base method's name and signature, which
        /// overrides it by the runtime's ordinary slot matching.</summary>
        private static void Forward(TypeBuilder type, MethodInfo target, FieldInfo fn,
                                    MethodInfo invoke)
        {
            if (target == null) throw new Exception("ModSettingsMenu member not found");
            MethodBuilder m = type.DefineMethod(target.Name,
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                target.ReturnType, Type.EmptyTypes);
            ILGenerator il = m.GetILGenerator();
            il.Emit(OpCodes.Ldsfld, fn);
            il.Emit(OpCodes.Callvirt, invoke);
            il.Emit(OpCodes.Ret);
        }
    }
}
