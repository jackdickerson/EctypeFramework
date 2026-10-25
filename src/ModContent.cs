using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using ResourceAssets;

namespace Ectype
{
    /// <summary>
    /// What a catalog needs to know about one item a mod published through
    /// <see cref="RuntimeContent.Publish"/>, beyond what the game's own containers hold.
    ///
    /// Every field is a BCL type or the game's <see cref="GUIDUnion"/>, so a mod that does not
    /// reference this assembly can read an entry by reflection. Field names and types are part
    /// of <see cref="ModContent.ApiVersion"/>: they are only ever added to.
    /// </summary>
    public sealed class ModContentEntry
    {
        /// <summary>The item's GUID. Set by <see cref="RuntimeContent.Publish"/>; any value the
        /// publisher writes is replaced.</summary>
        public GUIDUnion Guid;

        /// <summary>The <see cref="ContentKind"/> name: "WeaponModule", "Module" or
        /// "Carryable". Set by <see cref="RuntimeContent.Publish"/>.</summary>
        public string Kind;

        /// <summary>The name shown for the item. Required; also the header of the context info
        /// the game shows.</summary>
        public string DisplayName;

        /// <summary>The publishing plugin's BepInEx GUID. Required.</summary>
        public string Owner;

        /// <summary>A category hint for a catalog's tabs. Empty means the game's own category:
        /// the purchase sub-category name for a carryable, the module category name for a
        /// module, "Weapons" for a weapon module. A value naming a member of the game's
        /// <c>PurchasableItemSubCategory</c> or <c>ECategory</c> maps onto the game's tabs.</summary>
        public string Category;

        /// <summary>The item's tier, 1 for a Mk I, 2 for a Mk II, 3 for a Mk III, independent of
        /// the language the name is shown in. 0 means unranked.</summary>
        public int Tier;

        /// <summary>
        /// True for an inner part of another item, such as a payload inside a battery: it is
        /// registered in every container so it spawns, but it is never listed as a loose item
        /// and never added to a loot roll on its own. A component still needs crafting data.
        /// </summary>
        public bool IsComponent;

        /// <summary>False keeps the item out of player-versus-player matches for a mod that runs
        /// them. Default true.</summary>
        public bool PvpLegal = true;

        /// <summary>
        /// Whether the item may appear in this room, asked by catalogs and loot rolls. Must give
        /// the same answer on every client of a room. Null means always; the stored entry holds
        /// a predicate that returns true. An item that belongs to a role passes
        /// <c>() => RoleRegistry.IsAvailableInRoom(roleId)</c>.
        /// </summary>
        public Func<bool> AvailableInRoom;

        /// <summary>Free text for a catalog that draws its own icons. The game's icon is the
        /// one published with the item.</summary>
        public string IconHint;

        internal ModContentEntry Clone()
        {
            return (ModContentEntry)MemberwiseClone();
        }
    }

    /// <summary>
    /// The registry of published mod content: every item that reached
    /// <see cref="ContentState.Registered"/> through <see cref="RuntimeContent.Publish"/>.
    ///
    /// A catalog binds it by reflection: probe <see cref="ApiVersion"/>, then call
    /// <see cref="TryGet"/> or read <see cref="All"/>, and rebuild its lists on
    /// <see cref="Changed"/>. Readers receive copies; writing one changes nothing here.
    /// </summary>
    public static class ModContent
    {
        /// <summary>The shape of this class and of <see cref="ModContentEntry"/>. Raised only
        /// when a member is removed or changes meaning; added members leave it at 1.</summary>
        public const int ApiVersion = 1;

        private static readonly Func<bool> Always = () => true;
        private static readonly Dictionary<GUIDUnion, ModContentEntry> _byGuid =
            new Dictionary<GUIDUnion, ModContentEntry>();
        private static readonly List<ModContentEntry> _ordered = new List<ModContentEntry>();
        private static ReadOnlyCollection<ModContentEntry> _snapshot;

        /// <summary>Raised on the main thread after an entry is added. Each handler runs in its
        /// own try block; one that throws is logged and the rest still run.</summary>
        public static event Action Changed;

        /// <summary>Copies of every entry, ordered by GUID.</summary>
        public static IReadOnlyList<ModContentEntry> All
        {
            get
            {
                if (_snapshot == null)
                {
                    List<ModContentEntry> copies = new List<ModContentEntry>(_ordered.Count);
                    foreach (ModContentEntry e in _ordered) copies.Add(e.Clone());
                    _snapshot = copies.AsReadOnly();
                }
                return _snapshot;
            }
        }

        /// <summary>A copy of the entry published under <paramref name="guid"/>.</summary>
        public static bool TryGet(GUIDUnion guid, out ModContentEntry entry)
        {
            ModContentEntry e;
            if (_byGuid.TryGetValue(guid, out e)) { entry = e.Clone(); return true; }
            entry = null;
            return false;
        }

        /// <summary>
        /// False when <paramref name="guid"/> has an entry whose <c>AvailableInRoom</c> returns
        /// false or throws; true otherwise, including for a GUID nobody published here, which
        /// this registry does not restrict.
        /// </summary>
        public static bool IsAvailable(GUIDUnion guid)
        {
            ModContentEntry e;
            return !_byGuid.TryGetValue(guid, out e) || Available(e);
        }

        /// <summary>The stored entry itself, for the framework's own per-roll reads.</summary>
        internal static ModContentEntry Find(GUIDUnion guid)
        {
            ModContentEntry e;
            return _byGuid.TryGetValue(guid, out e) ? e : null;
        }

        internal static bool Available(ModContentEntry e)
        {
            try { return e.AvailableInRoom == null || e.AvailableInRoom(); }
            catch { return false; }
        }

        /// <summary>Stores a copy of <paramref name="entry"/>, which must carry its GUID and
        /// kind, then raises <see cref="Changed"/>.</summary>
        internal static void Add(ModContentEntry entry)
        {
            ModContentEntry e = entry.Clone();
            if (e.AvailableInRoom == null) e.AvailableInRoom = Always;
            if (_byGuid.ContainsKey(e.Guid)) return;
            _byGuid[e.Guid] = e;
            string key = e.Guid.AsHex();
            int at = _ordered.Count;
            for (int i = 0; i < _ordered.Count; i++)
                if (string.CompareOrdinal(_ordered[i].Guid.AsHex(), key) > 0) { at = i; break; }
            _ordered.Insert(at, e);
            _snapshot = null;
            Raise();
        }

        /// <summary>Removes an entry and raises <see cref="Changed"/>. Used only by the dev
        /// build's self-test; published content is never withdrawn.</summary>
        internal static bool Remove(GUIDUnion guid)
        {
            ModContentEntry e;
            if (!_byGuid.TryGetValue(guid, out e)) return false;
            _byGuid.Remove(guid);
            _ordered.Remove(e);
            _snapshot = null;
            Raise();
            return true;
        }

        private static void Raise()
        {
            Action handlers = Changed;
            if (handlers == null) return;
            foreach (Delegate d in handlers.GetInvocationList())
            {
                try { ((Action)d)(); }
                catch (Exception ex)
                {
                    if (Plugin.Log != null)
                        Plugin.Log.LogError("Ectype: content: ModContent.Changed subscriber " +
                                            d.Method.DeclaringType + "." + d.Method.Name +
                                            " threw: " + ex);
                }
            }
        }
    }
}
