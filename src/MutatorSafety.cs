using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using HarmonyLib;
using ResourceAssets;

namespace Ectype
{
    /// <summary>
    /// Drops active mutator GUIDs this client never registered, before
    /// GameSession.InitStartingMutators dereferences them and throws a NullReferenceException
    /// on every client whose install lacks the mutator's content mod.
    ///
    /// A Prefix rather than a replacement: StartingMutators is one persistent list the
    /// original reads by index, so filtering it in place re-derives none of that method's
    /// own whitelist and already-active logic.
    /// </summary>
    [HarmonyPatch(typeof(GameSession), nameof(GameSession.InitStartingMutators))]
    internal static class Patch_GameSession_InitStartingMutators
    {
        [HarmonyPrefix]
        private static void Prefix(GameSession __instance)
        {
            try
            {
                __instance.StartingMutators.RemoveAll(guid =>
                {
                    bool known = MutatorContainer.Instance.HasItem(guid);
                    if (!known)
                    {
                        Plugin.Log.LogWarning("Ectype: mutator " + guid + " is active this " +
                            "session but not registered on this client (its mod is not " +
                            "installed here) - skipped.");
                    }
                    return !known;
                });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: mutator-safety filter failed, starting " +
                                      "mutators unfiltered this session: " + e);
            }
        }
    }

    /// <summary>
    /// Same gap on the galaxy map: the "continue" card lists the preserved session's saved
    /// mutators, and MutatorsElementListVE.UpdateMutatorsList(GUIDs) dereferences each
    /// container lookup unconditionally, so a saved run that used a mutator whose mod is now
    /// disabled throws on every click. The prefix hands the original a filtered copy; the
    /// saved list itself is left alone, so re-enabling the mod restores the selection.
    /// </summary>
    [HarmonyPatch(typeof(UI.Core.MutatorsElementListVE),
        nameof(UI.Core.MutatorsElementListVE.UpdateMutatorsList),
        new[] { typeof(ReadOnlyCollection<GUIDUnion>) })]
    internal static class Patch_MutatorsElementListVE_UpdateMutatorsList
    {
        private static readonly HashSet<string> Warned = new HashSet<string>();

        [HarmonyPrefix]
        private static void Prefix(ref ReadOnlyCollection<GUIDUnion> activeMutatorGuids)
        {
            try
            {
                if (activeMutatorGuids == null) return;
                List<GUIDUnion> kept = null;
                for (int i = 0; i < activeMutatorGuids.Count; i++)
                {
                    GUIDUnion guid = activeMutatorGuids[i];
                    if (MutatorContainer.Instance.HasItem(guid))
                    {
                        if (kept != null) kept.Add(guid);
                        continue;
                    }
                    if (kept == null)
                    {
                        kept = new List<GUIDUnion>();
                        for (int j = 0; j < i; j++) kept.Add(activeMutatorGuids[j]);
                    }
                    string id = guid.ToString();
                    if (Warned.Add(id))
                    {
                        Plugin.Log.LogWarning("Ectype: mutator " + id + KnownMutators.Describe(id) +
                            " is listed (saved or selected session) but not registered on this " +
                            "client - hidden from the mutator list until its mod is enabled.");
                    }
                }
                if (kept != null) activeMutatorGuids = kept.AsReadOnly();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: mutator-list UI filter failed, list unfiltered: " + e);
            }
        }
    }

    /// <summary>Names for this repo's own mutators, used only to make the warning actionable.</summary>
    internal static class KnownMutators
    {
        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>
        {
            { "4d1f7c2a9e6b40518c3a7fbe25d90a11", "Field Promotion, FieldPromotion.dll" },
            { "7cb9e33adfb6443888d7fe719c27e65d", "Void Tunnel Decay, VoidTunnelDecay.dll" },
            { "fef0fdfd958440729881b458666b53ca", "Insanity, Insanity.dll" },
        };

        internal static string Describe(string guid)
        {
            string key = (guid ?? "").Replace("-", "").ToLowerInvariant();
            return Names.TryGetValue(key, out string name) ? " (" + name + ")" : "";
        }
    }
}
