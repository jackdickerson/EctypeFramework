using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle(Ectype.EctypePluginInfo.USERS_PLUGIN_NAME)]
[assembly: AssemblyDescription(Ectype.EctypePluginInfo.PLUGIN_DESCRIPTION)]
[assembly: AssemblyProduct(Ectype.EctypePluginInfo.PLUGIN_NAME)]
[assembly: AssemblyCompany(Ectype.EctypePluginInfo.PLUGIN_AUTHORS)]
[assembly: AssemblyVersion(Ectype.EctypePluginInfo.PLUGIN_VERSION + ".0")]
[assembly: AssemblyFileVersion(Ectype.EctypePluginInfo.PLUGIN_VERSION + ".0")]
[assembly: ComVisible(false)]

// Distribution metadata, read by packaging tooling to describe the mod.
// MPType mirrors the VoidPlugin declaration - keep the two in step.
#if NO_SESSION_FLAG
[assembly: AssemblyMetadata("ModCategories", "Mods")]
#else
[assembly: AssemblyMetadata("ModCategories", "Mods, Mod_Session Required, Disables Progression")]
#endif
[assembly: AssemblyMetadata("MPType", "All")]
[assembly: AssemblyMetadata("ProgressionFlag", ModFlagPolicy.BuildTag)]
#if ECTYPE_DEV
// A dev tools build (-p:DevTools=true). Never shipped: see Directory.Build.props.
[assembly: AssemblyMetadata("EctypeDevTools", "true")]
#endif
