using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;

using VoidManager;
using VoidManager.CustomGUI;

namespace ExampleSettingsPage
{
    /// <summary>
    /// Puts <see cref="SettingsPage"/> into VoidManager's F5 menu when VoidManager is loaded,
    /// and touches no VoidManager type when it is not.
    ///
    /// The rule that keeps a soft dependency soft: no compiled type in this assembly derives
    /// from a VoidManager type, or names one in a field, property, parameter or return type.
    /// Such a type cannot load without VoidManager, and <c>Assembly.GetTypes()</c> on this
    /// assembly then throws <c>ReflectionTypeLoadException</c>, which HarmonyX's
    /// <c>AccessTools.TypeByName</c> and <c>PatchAll</c> log on every call and which breaks any
    /// other mod that scans loaded assemblies. A VoidManager type may appear only inside a
    /// method body that runs after VoidManager is known to be loaded: the runtime resolves the
    /// types a method body names when it compiles that method, on its first call. Each such
    /// method here is marked <c>NoInlining</c> so that it is never compiled into a caller that
    /// runs without VoidManager.
    ///
    /// VoidManager collects <c>ModSettingsMenu</c> subclasses only from the assembly of a mod
    /// that declares a <c>VoidPlugin</c>, and a <c>VoidPlugin</c> is a VoidManager-derived type
    /// that would make the dependency hard. So the page class is built at runtime, once
    /// VoidManager is known to be loaded, and inserted into VoidManager's page list by a
    /// postfix on <c>PluginHandler.DiscoverPlugins</c>, the method that builds that list.
    /// VoidManager runs it from its postfix on the game's <c>PlayerProfileLoader.Awake</c>,
    /// after every plugin's Awake, so a patch applied in Awake is in place in time.
    /// </summary>
    internal static class VoidManagerPage
    {
        /// <summary>VoidManager's BepInEx plugin GUID.</summary>
        internal const string VoidManagerGuid = "VoidCrewModdingTeam.VoidManager";

        private static Type _pageType;

        /// <summary>
        /// Call from Awake. Returns false, having named no VoidManager type, when VoidManager is
        /// not loaded. The soft dependency makes BepInEx load VoidManager first, so its entry is
        /// already registered when this runs.
        /// </summary>
        internal static bool TryInstall(Harmony harmony)
        {
            PluginInfo info;
            if (!Chainloader.PluginInfos.TryGetValue(VoidManagerGuid, out info) || info.Instance == null)
                return false;
            PatchDiscovery(harmony);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PatchDiscovery(Harmony harmony)
        {
            // PluginHandler is internal to VoidManager, so it is found by name in the assembly
            // of a public VoidManager type.
            Type handler = typeof(GUIMain).Assembly.GetType("VoidManager.PluginHandler");
            MethodInfo discover = handler != null ? AccessTools.Method(handler, "DiscoverPlugins") : null;
            if (discover == null)
            {
                Plugin.Log.LogWarning("VoidManager.PluginHandler.DiscoverPlugins not found " +
                                      "(VoidManager update?); the F5 page is missing.");
                return;
            }
            harmony.Patch(discover, postfix: new HarmonyMethod(typeof(VoidManagerPage),
                                                               nameof(AfterDiscoverPlugins)));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AfterDiscoverPlugins()
        {
            try
            {
                List<ModSettingsMenu> pages = AccessTools.Field(typeof(GUIMain), "settings")
                    .GetValue(GUIMain.Instance) as List<ModSettingsMenu>;
                if (pages == null) throw new Exception("VoidManager's menu has no page list");

                if (_pageType == null) _pageType = BuildPageType();
                // Once per list: a second discovery into the same list adds nothing.
                foreach (ModSettingsMenu existing in pages)
                    if (existing != null && existing.GetType() == _pageType) return;
                ModSettingsMenu page = (ModSettingsMenu)Activator.CreateInstance(_pageType);

                // F5 lists a page under a mod's "Settings menus" when the page's owner is that
                // mod's entry. A mod without a VoidPlugin has an entry VoidManager generated.
                Type handler = typeof(GUIMain).Assembly.GetType("VoidManager.PluginHandler");
                Dictionary<string, VoidPlugin> generated = AccessTools.Property(handler, "GeneratedVoidPlugins")
                    .GetValue(null, null) as Dictionary<string, VoidPlugin>;
                VoidPlugin owner;
                if (generated != null && generated.TryGetValue(Plugin.PLUGIN_GUID, out owner))
                    AccessTools.Field(typeof(ModSettingsMenu), "MyVoidPlugin").SetValue(page, owner);

                // VoidManager keeps its own page first and sorts the rest by name.
                int at = pages.Count;
                for (int i = 1; i < pages.Count; i++)
                {
                    if (string.Compare(pages[i].Name(), page.Name(), StringComparison.CurrentCulture) > 0)
                    {
                        at = i;
                        break;
                    }
                }
                pages.Insert(at, page);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Adding the F5 page to VoidManager failed: " + e);
            }
        }

        /// <summary>
        /// Builds <c>ExampleSettingsPage.Page : ModSettingsMenu</c> in a dynamic assembly, with
        /// <c>Name</c> and <c>Draw</c> calling <see cref="SettingsPage"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Type BuildPageType()
        {
            Type menuBase = typeof(ModSettingsMenu);
            AssemblyName name = new AssemblyName("ExampleSettingsPage.VoidManagerPage");
            AssemblyBuilder asm = AppDomain.CurrentDomain.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);
            ModuleBuilder module = asm.DefineDynamicModule(name.Name);
            TypeBuilder type = module.DefineType("ExampleSettingsPage.Page",
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, menuBase);
            Override(type, menuBase, "Name", typeof(SettingsPage).GetMethod("Name"));
            Override(type, menuBase, "Draw", typeof(SettingsPage).GetMethod("Draw"));
            type.DefineDefaultConstructor(MethodAttributes.Public);
            return type.CreateType();
        }

        /// <summary>A public virtual method with the base method's name and signature, which
        /// overrides it by the runtime's ordinary slot matching, and whose body calls
        /// <paramref name="body"/>.</summary>
        private static void Override(TypeBuilder type, Type menuBase, string name, MethodInfo body)
        {
            MethodInfo slot = menuBase.GetMethod(name, Type.EmptyTypes);
            if (slot == null || body == null) throw new Exception("ModSettingsMenu." + name + " not found");
            MethodBuilder m = type.DefineMethod(name,
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
                slot.ReturnType, Type.EmptyTypes);
            ILGenerator il = m.GetILGenerator();
            il.Emit(OpCodes.Call, body);
            il.Emit(OpCodes.Ret);
        }
    }
}
