using ResourceAssets;

// Deliberately in the global namespace: this file is compiled into each mod that
// opts in from its csproj, and each mod has its own root namespace. `internal`
// means every assembly gets its own private copy, so there is no collision.
//
// A project that loads without VoidManager defines VOIDMANAGER_OPTIONAL, which leaves out
// the one member typed by VoidManager: no type in such an assembly may name a VoidManager
// type, because a type that fails to load makes Assembly.GetTypes() throw for the whole
// assembly.

/// <summary>
/// Compile-time policy for whether this build flags the session as modded.
///
/// Default (no switch): flags normally. Build with -p:NoProgressionFlag=true and
/// every site below flips, in ONE build, without editing source.
///
/// What the switch gives up:
///
/// The modded flag and joiner mod-matching are the SAME piece of state. The game
/// publishes R_Mod = mod_session when RuntimeAssetsRegister.HasAnyRequired() is
/// true, and HasAnyRequired() tests the required mask (94) over the very same
/// _assetsEffects dictionary that GameSessionManager.ValidateMods diffs to decide
/// whether a joiner is missing a mod. Dropping an asset out of the required set to
/// clear the flag also drops it out of the mod check.
///
/// So for a mod that registers network-spawned content (a weapon module, say), a
/// no-flag build CANNOT stop a crewmate without the mod from joining. They will
/// not be bounced with "MISSING MODS"; they will join and then meet spawned
/// objects their client has no asset for. Everyone needs the same build.
///
/// A mod with no runtime assets loses nothing but the version check, since it
/// had nothing in the required set to begin with.
///
/// The switch only reaches assemblies that compile this file. A normal build of the Ectype
/// framework registers its network-contract marker with RequiredEffect, so a no-flag build
/// of a mod that depends on it still produces a flagged session.
///
/// For a mod that does not depend on the framework, a no-flag build means an unmodded
/// player gets no signal that the room is modded, and VoidManager raises no session level
/// for it. That is the purpose of the switch. The flag itself does not stop progression:
/// the game never reads R_Mod for that, and VoidManager's progression switch reacts only
/// to a mod calling its DisableProgression.
///
/// The build records which way it went in AssemblyMetadata("ProgressionFlag"),
/// so a DLL can always be identified after the fact.
/// </summary>
internal static class ModFlagPolicy
{
#if NO_SESSION_FLAG
    /// <summary>False in a -p:NoProgressionFlag=true build.</summary>
    internal const bool FlagsSession = false;
#else
    /// <summary>True in a normal build: sessions are flagged as modded.</summary>
    internal const bool FlagsSession = true;
#endif

#if !VOIDMANAGER_OPTIONAL
    /// <summary>
    /// What the VoidPlugin declares. VoidManager's UpdateHighestLevelOfMPMods sets
    /// SessionModdingType = mod_session for Session or All, and does nothing for
    /// Client - so this alone decides one of the three flag routes.
    /// </summary>
    internal const VoidManager.MPModChecks.MultiplayerType MPType =
        FlagsSession ? VoidManager.MPModChecks.MultiplayerType.Session
                     : VoidManager.MPModChecks.MultiplayerType.Client;
#endif

    /// <summary>
    /// Effect to register network-spawned runtime assets with. IsNetworkSpawned is
    /// inside the required mask, so it flags every hosted room by itself; None keeps
    /// the asset out of the required set entirely. See the class remarks for the
    /// mod-matching consequence.
    /// </summary>
    internal const SessionModificationEffect NetworkSpawnedEffect =
        FlagsSession ? SessionModificationEffect.IsNetworkSpawned : SessionModificationEffect.None;

    /// <summary>
    /// Effect to register non-spawned but run-changing runtime assets with (perks, trees,
    /// loadouts). Required is inside the required mask, so it flags every hosted room by
    /// itself and enters joiner mod-matching; None keeps the asset out of both.
    /// </summary>
    internal const SessionModificationEffect RequiredEffect =
        FlagsSession ? SessionModificationEffect.Required : SessionModificationEffect.None;

    /// <summary>Value stamped into AssemblyMetadata("ProgressionFlag").</summary>
    internal const string BuildTag = FlagsSession ? "enabled" : "disabled-at-build";
}
