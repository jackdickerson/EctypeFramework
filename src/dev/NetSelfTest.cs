using System;
using System.Collections.Generic;

using ExitGames.Client.Photon;

namespace Ectype
{
    /// <summary>
    /// Dev build self-test, run once at load: for every layout declared in
    /// <see cref="EctypeNet.Layouts"/>, a sample message is packed the way
    /// <see cref="EctypeNet.Send"/> packs it, serialized and deserialized with Photon's
    /// Protocol18 (PUN's wire format), and unpacked the way the receive path unpacks it. The
    /// arguments must come back with the same types and values and still match the layout,
    /// which catches a field type Photon does not carry or carries as another type. A layout
    /// added to the table is covered without touching this file. Offline: no connection is
    /// made. Nested start data carries primitives only; PUN's custom types (Vector3 and the
    /// like) are registered by PUN itself and are outside this test.
    /// </summary>
    internal static class NetSelfTest
    {
        internal static void Run()
        {
            List<string> failures = new List<string>();
            int count = 0;
            foreach (KeyValuePair<byte, Type[][]> kv in EctypeNet.Layouts)
            {
                foreach (Type[] layout in kv.Value)
                {
                    count++;
                    string name = "subtype " + kv.Key + " (" + layout.Length + " fields)";
                    object[] args = new object[layout.Length];
                    try
                    {
                        for (int i = 0; i < layout.Length; i++) args[i] = Sample(layout[i], i);
                    }
                    catch (Exception e) { failures.Add(name + ": " + e.Message); continue; }
                    Check(name, kv.Key, args, failures);
                }
            }

            if (failures.Count == 0)
                Plugin.Log.LogInfo("Ectype: dev self-test: net payload round trip passed (" +
                                   count + " layouts, Protocol18).");
            else
                Plugin.Log.LogError("Ectype: dev self-test: net payload round trip FAILED: " +
                                    string.Join("; ", failures.ToArray()));
        }

        private static object Sample(Type t, int index)
        {
            if (t == typeof(int)) return 1000 + index;
            if (t == typeof(byte)) return (byte)index;
            if (t == typeof(bool)) return index % 2 == 0;
            if (t == typeof(float)) return 1.5f + index;
            if (t == typeof(string)) return "example.mod.key" + index;
            if (t == typeof(object[])) return new object[] { 2.5f, 7, "start", true };
            throw new Exception("no sample value for field type " + t.Name +
                                "; add one to NetSelfTest.Sample");
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
                    else if (!EctypeNet.MatchesLayout(subtype, gotArgs))
                        failures.Add(name + ": the round-tripped fields match no declared layout");
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
