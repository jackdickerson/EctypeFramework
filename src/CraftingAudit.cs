using System;
using System.Collections.Generic;

using ResourceAssets;

namespace Ectype
{
    /// <summary>
    /// Warns about carryables registered at runtime without crafting data.
    ///
    /// The game's recycled-alloy count walks every registered carryable sitting on the player ship
    /// and reads the crafting rules of each one's crafting definition, with no null check. A
    /// carryable the carryable container knows and the crafting-data container does not therefore
    /// throws there, in a vanilla-rules room too. The count runs on the host at session start and
    /// at the end of a run, while the run's rewards are computed.
    ///
    /// Runtime assets are registered by many mods at their own times, so after the framework's
    /// own roles have settled, each registration pass checks the ids it has not seen before. Each
    /// offender is reported once.
    /// </summary>
    internal static class CraftingAudit
    {
        private static readonly HashSet<GUIDUnion> Checked = new HashSet<GUIDUnion>();
        private static int _lastCount = -1;
        private static bool _failed;

        /// <summary>From the registration pass, once every role has settled.</summary>
        internal static void Run()
        {
            if (_failed) return;
            try
            {
                RuntimeAssetsRegister reg = RuntimeAssetsRegister.Instance;
                CarryableContainer carryables = CarryableContainer.Instance;
                CraftingDataContainer crafting = CraftingDataContainer.Instance;
                if (reg == null || carryables == null || crafting == null) return;

                List<GUIDUnion> ids = reg.GetAllIds();
                if (ids.Count == _lastCount) return;
                _lastCount = ids.Count;

                foreach (GUIDUnion id in ids)
                {
                    if (!Checked.Add(id)) continue;
                    if (!carryables.HasItem(id) || crafting.HasItem(id)) continue;
                    UnityEngine.Object asset;
                    string name = reg.TryGetAsset(id, out asset) && asset != null ? asset.name : "(unnamed)";
                    Plugin.Log.LogWarning("Ectype: carryable '" + name + "' (" + id.AsHex() + ") is " +
                                          "registered with no crafting data; it breaks the end-of-run " +
                                          "alloy tally, which reads the crafting rules of every " +
                                          "carryable on the player ship and throws on this one. " +
                                          "Register a crafting definition under the same GUID." +
                                          ComponentNote(id));
                }
            }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Log.LogWarning("Ectype: crafting-data audit failed and is off for this session: " + e);
            }
        }

        /// <summary>
        /// A component is an inner part, kept out of loose-item lists, and is audited like any
        /// other carryable: it still sits on the ship inside its parent, where the tally reads
        /// it. The only way one reaches this check is a publication that failed part-way, since
        /// a completed one always registers crafting data.
        /// </summary>
        private static string ComponentNote(GUIDUnion id)
        {
            ContentHandle h = RuntimeContent.Find(id);
            if (h == null) return "";
            return " It was published by " + h.Owner + (h.Entry != null && h.Entry.IsComponent
                       ? " as a component; a component is kept out of item lists, not out of the tally"
                       : "") +
                   (h.State == ContentState.Failed ? ", and the publication failed: " + h.Reason : "") + ".";
        }
    }
}
