using System;
using System.Collections.Generic;

using ResourceAssets;

namespace Ectype
{
    /// <summary>
    /// Keeps runtime entries of a container's authored list in one order on every client.
    ///
    /// The authored entries keep their places. Entries added at runtime form the tail of the
    /// list, and each is inserted before the first tail entry whose key sorts after its own,
    /// by ordinal comparison. A tail built only through this insert is therefore sorted by key,
    /// whatever order the inserts ran in, so two clients that publish the same set in different
    /// orders end with identical lists.
    /// </summary>
    internal static class AssetDescriptionOrder
    {
        /// <summary>
        /// Inserts <paramref name="item"/> into the runtime tail of <paramref name="list"/>.
        /// <paramref name="isRuntime"/> marks the tail: it starts after the last entry for which
        /// it returns false. Returns the index, or -1 when the item, or another entry with the
        /// same key, is already listed.
        /// </summary>
        internal static int Insert<T>(List<T> list, T item, Func<T, string> keyOf,
                                      Func<T, bool> isRuntime) where T : class
        {
            if (list == null || item == null) return -1;
            if (list.Contains(item)) return -1;
            string key = keyOf(item);

            int tail = list.Count;
            while (tail > 0 && list[tail - 1] != null && isRuntime(list[tail - 1])) tail--;

            int at = list.Count;
            for (int i = tail; i < list.Count; i++)
            {
                int c = string.CompareOrdinal(keyOf(list[i]), key);
                if (c == 0) return -1;
                if (c > 0) { at = i; break; }
            }
            list.Insert(at, item);
            return at;
        }

        /// <summary>A container def keyed by its GUID's hex form; the tail is the defs whose
        /// <paramref name="refOf"/> is a runtime ref.</summary>
        internal static int InsertDef<T>(List<T> list, T def, Func<T, ResourceAssetRef> refOf)
            where T : class, IResourceAsset
        {
            return Insert(list, def, d => d.AssetGuid.AsHex(), d =>
            {
                ResourceAssetRef r = refOf(d);
                return (object)r != null && r.IsRuntime;
            });
        }
    }
}
