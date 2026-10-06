using System;
using System.Text;

using Gameplay.Perks;
using UI.Token;

namespace Ectype
{
    /// <summary>
    /// Dumps the live gene tree grids from PerkTreeUtility.PerkTreeMap - the ground truth for
    /// Row/Column placement, against which a manifest's [row][col] must exist and be
    /// unoccupied. The map is only populated once a token terminal (or the codex) has built
    /// its trees, so dump while standing at the personal loadout terminal.
    /// </summary>
    internal static class TreeDump
    {
        public static string LastSummary = "<not run>";

        public static void Dump()
        {
            try
            {
                var map = PerkTreeUtility.PerkTreeMap;
                if (map == null || map.Count == 0)
                {
                    LastSummary = "PerkTreeMap is empty - open the gene tree first.";
                    Plugin.Log.LogWarning("Ectype: " + LastSummary);
                    return;
                }

                StringBuilder sb = new StringBuilder();
                int ours = 0;
                foreach (var kv in map)
                {
                    PerkTreeVE treeVe = kv.Key;
                    string treeName;
                    try { treeName = treeVe.PerkTree.Value.ToString(); }
                    catch { treeName = "<unreadable>"; }

                    sb.Append("Ectype: tree ").Append(treeName)
                      .Append(" - ").Append(kv.Value.Count).AppendLine(" rows:");

                    for (int r = 0; r < kv.Value.Count; r++)
                    {
                        sb.Append("  row ").Append(r).Append(": ");
                        var row = kv.Value[r];
                        for (int c = 0; c < row.Count; c++)
                        {
                            PerkBuffItemVE cell = row[c];
                            PerkBuff perk = cell != null ? cell.PerkData : null;
                            string label = perk == null ? "<empty>"
                                : SafeName(perk) + "(" + perk.DisplayType + ")";
                            if (perk != null && RoleRegistry.IsOurGuid(perk.ContainerGuid))
                            {
                                label = "**" + label + "**";
                                ours++;
                            }
                            sb.Append("[").Append(c).Append(" ").Append(label).Append("] ");
                        }
                        sb.AppendLine();
                    }
                }

                Plugin.Log.LogInfo(sb.ToString());
                LastSummary = "dumped " + map.Count + " tree(s) to LogOutput.log; " +
                              ours + " framework perk cell(s) present.";
            }
            catch (Exception e)
            {
                LastSummary = "dump threw: " + e.Message;
                Plugin.Log.LogError("Ectype: tree dump threw: " + e);
            }
        }

        private static string SafeName(PerkBuff perk)
        {
            try
            {
                string s = perk.GetHeader();
                return string.IsNullOrEmpty(s) ? perk.name : s;
            }
            catch
            {
                return perk.name;
            }
        }
    }
}
