using System;
using System.Collections;
using System.Reflection;
using System.Reflection.Emit;

using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;

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
    /// VoidManager finds a mod's <c>VoidPlugin</c> and <c>ModSettingsMenu</c> only by scanning
    /// that mod's own assembly, so the two are provided another way when VoidManager is loaded:
    ///  * Multiplayer type. VoidManager gives a plugin without a <c>VoidPlugin</c> a generated
    ///    entry whose type comes from its "Unspecified Mod Overrides" table, rebuilt by
    ///    <c>Configs.LoadModListOverride</c> at the start of plugin discovery. A postfix adds
    ///    this plugin's GUID as <c>All</c> unless the player already listed the framework by
    ///    GUID or name. The generated entry carries the plugin's version and DLL hash, so
    ///    VoidManager's join check and its modded-session flag treat the framework exactly as
    ///    a declared <c>All</c> mod.
    ///  * Settings page. A <c>ModSettingsMenu</c> subclass is emitted at runtime, after
    ///    VoidManager's assembly is known to be loaded, and appended to VoidManager's menu
    ///    list by a postfix on <c>PluginHandler.DiscoverPlugins</c>. Its <c>Name</c> and
    ///    <c>Draw</c> call <see cref="SettingsPage"/>.
    /// Each binding resolves by name and logs one line naming what is lost when it misses.
    /// </summary>
    internal static class VoidManagerBridge
    {
        /// <summary>VoidManager's BepInEx plugin GUID.</summary>
        internal const string Guid = "VoidCrewModdingTeam.VoidManager";

        private static Assembly _vm;
        private static FieldInfo _overrides;
        private static Type _mpType;
        private static bool _menuAdded;

        /// <summary>True when BepInEx loaded VoidManager. Plugins load in dependency order, so
        /// the soft dependency puts VoidManager's entry here before this plugin's Awake.</summary>
        internal static bool Present
        {
            get { return Chainloader.PluginInfos.ContainsKey(Guid); }
        }

        /// <summary>Called from the plugin's Awake, before the Enabled switch is read: VoidManager
        /// lists the framework, and the F5 page reports its state, whatever that switch says.</summary>
        internal static void Install(Harmony harmony)
        {
            PluginInfo info;
            if (!Chainloader.PluginInfos.TryGetValue(Guid, out info) || info.Instance == null)
            {
                Plugin.Log.LogInfo("Ectype: settings menu unavailable: VoidManager not loaded.");
                return;
            }
            _vm = info.Instance.GetType().Assembly;

            try { PatchModType(harmony); }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: VoidManager mod-type hook failed, so VoidManager " +
                                    "lists the framework as Unmanaged: " + e);
            }
            try { PatchMenu(harmony); }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: VoidManager settings-page hook failed, so the F5 " +
                                    "page is missing: " + e);
            }
        }

        // ------------------------------------------------------------- multiplayer type

        private static void PatchModType(Harmony harmony)
        {
            Type configs = _vm.GetType("VoidManager.Configs");
            MethodInfo load = configs != null ? AccessTools.Method(configs, "LoadModListOverride") : null;
            _overrides = configs != null ? AccessTools.Field(configs, "ModOverrideDictionary") : null;
            _mpType = _vm.GetType("VoidManager.MPModChecks.MultiplayerType");
            if (load == null || _overrides == null || _mpType == null)
            {
                Plugin.Log.LogWarning("Ectype: VoidManager's mod-override table not found " +
                                      "(VoidManager update?). VoidManager lists the framework " +
                                      "as Unmanaged and its join check skips the framework; " +
                                      "the game's asset check still refuses a peer on another " +
                                      "network contract.");
                return;
            }
            harmony.Patch(load, postfix: new HarmonyMethod(typeof(VoidManagerBridge),
                                                           nameof(AfterLoadModListOverride)));
        }

        private static void AfterLoadModListOverride()
        {
            try
            {
                IDictionary table = _overrides.GetValue(null) as IDictionary;
                if (table == null) return;
                // VoidManager looks the GUID up first, then the name: a player's own entry
                // under either wins.
                if (table.Contains(EctypePluginInfo.PLUGIN_GUID) ||
                    table.Contains(EctypePluginInfo.USERS_PLUGIN_NAME)) return;
                table.Add(EctypePluginInfo.PLUGIN_GUID, Enum.Parse(_mpType, "All"));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: declaring the framework's VoidManager mod type " +
                                    "failed: " + e);
            }
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
            if (_menuAdded) return;
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

                object menu = Activator.CreateInstance(EmitMenuType(menuBase));

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
                _menuAdded = true;
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
