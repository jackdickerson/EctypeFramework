using System;
using System.Collections.Generic;

using ExitGames.Client.Photon;

namespace Ectype
{
    /// <summary>
    /// Dev build self-test, run once at load: each framework message layout is packed the way
    /// <see cref="EctypeNet.Send"/> packs it, serialized and deserialized with Photon's
    /// Protocol18 (PUN's wire format), and unpacked the way the receive path unpacks it. The
    /// arguments must come back with the same types and values, which catches a field whose
    /// type Photon does not carry or carries as another type. Offline: no connection is made.
    /// </summary>
    internal static class NetSelfTest
    {
        internal static void Run()
        {
            List<string> failures = new List<string>();
            Check("ability-active", EctypeNet.AbilityActive, new object[]
            {
                1001, "example.mod.dash", true,
                new object[] { 2.5f, 7, "start" }, new object[0], false
            }, failures);
            Check("enemy-status apply", EctypeNet.EnemyStatus, new object[]
            {
                (byte)0, 2002, "example.mod.burn", 123456789, 8000, 1.5f, 3003
            }, failures);
            Check("enemy-status remove", EctypeNet.EnemyStatus, new object[]
            {
                (byte)1, 2002, "example.mod.burn"
            }, failures);

            if (failures.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: net payload round trip passed " +
                                   "(3 layouts, Protocol18).");
            else
                Plugin.Log.LogError("Ectype: dev self-test: net payload round trip FAILED: " +
                                    string.Join("; ", failures.ToArray()));
        }

        private static void Check(string name, byte subtype, object[] args, List<string> failures)
        {
            try
            {
                Protocol18 protocol = new Protocol18();
                object back = protocol.Deserialize(protocol.Serialize(EctypeNet.Pack(subtype, args)));
                byte gotSubtype;
                object[] gotArgs;
                if (!EctypeNet.TryUnpack(back, out gotSubtype, out gotArgs))
                    failures.Add(name + ": no subtype byte after the round trip");
                else if (gotSubtype != subtype)
                    failures.Add(name + ": subtype " + subtype + " came back as " + gotSubtype);
                else
                {
                    string diff = Diff(args, gotArgs, "");
                    if (diff != null) failures.Add(name + ": " + diff);
                }
            }
            catch (Exception e)
            {
                failures.Add(name + ": threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>Null when equal in type and value, element by element; otherwise the first
        /// difference, with its index path.</summary>
        private static string Diff(object want, object got, string path)
        {
            object[] wantArr = want as object[];
            if (wantArr != null)
            {
                object[] gotArr = got as object[];
                if (gotArr == null)
                    return "[" + path + "] object[] came back as " + TypeName(got);
                if (gotArr.Length != wantArr.Length)
                    return "[" + path + "] length " + wantArr.Length + " came back as " + gotArr.Length;
                for (int i = 0; i < wantArr.Length; i++)
                {
                    string d = Diff(wantArr[i], gotArr[i], path.Length == 0 ? i.ToString() : path + "." + i);
                    if (d != null) return d;
                }
                return null;
            }
            if (want == null) return got == null ? null : "[" + path + "] null came back as " + TypeName(got);
            if (got == null || got.GetType() != want.GetType())
                return "[" + path + "] " + TypeName(want) + " came back as " + TypeName(got);
            return want.Equals(got) ? null : "[" + path + "] " + want + " came back as " + got;
        }

        private static string TypeName(object o) { return o == null ? "null" : o.GetType().Name; }
    }
}
