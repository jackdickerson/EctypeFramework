using System;
using System.Collections.Generic;
using UnityEngine;

using CG.Client.UserData;
using CG.Objects;
using CG.Ship.Modules;
using CG.Ship.Object;
using Client.Player.Interactions;
using Gameplay.CompositeWeapons;
using ResourceAssets;
using RuntimeAssets;

namespace Ectype
{
    /// <summary>What a published item is, which decides the containers it is registered in.</summary>
    public enum ContentKind
    {
        /// <summary>A composite weapon: a <c>CompositeWeaponData</c>, installed from its build
        /// box.</summary>
        WeaponModule = 1,

        /// <summary>A ship module: a prefab carrying a <c>CellModule</c>.</summary>
        Module = 2,

        /// <summary>A loose object a player picks up: a prefab carrying a
        /// <c>CarryableObject</c>.</summary>
        Carryable = 3,
    }

    /// <summary>Where a publication stands.</summary>
    public enum ContentState
    {
        /// <summary>Accepted, waiting for the game's asset containers, or retrying a failed
        /// attempt.</summary>
        Pending,

        /// <summary>Registered in every container its kind needs and recorded in
        /// <see cref="ModContent"/>.</summary>
        Registered,

        /// <summary>Refused or given up; <see cref="ContentHandle.Reason"/> says why. Nothing
        /// more happens to it this session.</summary>
        Failed,
    }

    /// <summary>One publication, as returned by <see cref="RuntimeContent.Publish"/>.</summary>
    public sealed class ContentHandle
    {
        internal ContentHandle(ContentKind kind, GUIDUnion guid, string owner)
        {
            Kind = kind;
            Guid = guid;
            Owner = owner;
        }

        /// <summary>The kind it was published as.</summary>
        public ContentKind Kind { get; private set; }

        /// <summary>The item's GUID.</summary>
        public GUIDUnion Guid { get; private set; }

        /// <summary>The publishing plugin, from the entry's <c>Owner</c>.</summary>
        public string Owner { get; private set; }

        /// <summary>Pending, Registered or Failed.</summary>
        public ContentState State { get; internal set; }

        /// <summary>Why the publication is Failed, or what the last failed attempt hit while it
        /// is Pending. Null otherwise.</summary>
        public string Reason { get; internal set; }

        /// <summary>Registration attempts made so far.</summary>
        public int Attempts { get; internal set; }

        internal ContentAssets Assets;
        internal ModContentEntry Entry;
        internal string Stage = "queued";
        internal bool ContainersDone;
        internal bool ConverterRan;
        internal object Def;

        internal string Describe()
        {
            return Kind + " " + Guid.AsHex() + " from " + Owner;
        }
    }

    /// <summary>
    /// The game-side half of a publication: the asset, its display text, and the crafting,
    /// unlock and category data the game's containers hold. Fields a kind does not use are
    /// ignored.
    /// </summary>
    public sealed class ContentAssets
    {
        /// <summary>
        /// The asset. A <c>CompositeWeaponData</c> for <see cref="ContentKind.WeaponModule"/>;
        /// for <see cref="ContentKind.Module"/> and <see cref="ContentKind.Carryable"/>, the
        /// inactive prefab GameObject, or its <c>CellModule</c> or <c>CarryableObject</c>.
        /// Publish writes the item's GUID into the asset's container GUID, which a clone
        /// otherwise inherits from its donor.
        /// </summary>
        public UnityEngine.Object Asset;

        /// <summary>Body text of the context info the game shows. The header is the entry's
        /// <c>DisplayName</c>.</summary>
        public string Description;

        /// <summary>The icon the game shows.</summary>
        public Sprite Icon;

        /// <summary>Weapon module only: the icon of its build box. Null uses
        /// <see cref="Icon"/>.</summary>
        public Sprite BuildBoxIcon;

        /// <summary>
        /// Crafting and recycling rules, registered under the item's GUID. Required for a
        /// carryable (components included) and a weapon module, and refused at publish when
        /// missing. Optional for a module, whose crafting data belongs to its build box.
        /// </summary>
        public CraftingRules? Crafting;

        /// <summary>The rarity the game colours the item's edge with. Default Common.</summary>
        public RarityType Rarity = RarityType.Common;

        /// <summary>How the item unlocks. Default <c>Session</c>: learned at the fabricator by
        /// recycling one, written to no save. <c>Always</c> writes the GUID into every player's
        /// save, where it outlives the mod.</summary>
        public UnlockCriteriaType Unlock = UnlockCriteriaType.Session;

        /// <summary>The asset's effect in the game's runtime asset register. Default
        /// <c>IsNetworkSpawned</c>, which every client of a room must share; a build that must
        /// not flag the session passes <c>None</c>.</summary>
        public SessionModificationEffect ModFlag = SessionModificationEffect.IsNetworkSpawned;

        /// <summary>Carryable and weapon module: the purchase sub-category. Null gives a
        /// carryable <c>Other_Basic</c>, as the game's own converter does, and leaves a weapon
        /// module's unset; copy a weapon's from its donor's def.</summary>
        public PurchasableItemSubCategory? ItemCategory;

        /// <summary>Module only: its category in the module lists. Default Support.</summary>
        public ECategory ModuleCategory = ECategory.Support;

        internal ContentAssets Clone()
        {
            return (ContentAssets)MemberwiseClone();
        }
    }

    /// <summary>
    /// Publishes a weapon module, module or carryable a mod built at runtime: registers it in
    /// every container its kind needs, lists it in the container's authored list in GUID order,
    /// and records its <see cref="ModContentEntry"/> in <see cref="ModContent"/>.
    ///
    /// A publication made before the game's containers exist is queued and completed by the
    /// framework's registration driver. Every step is guarded on the container already holding
    /// the GUID, so a retry resumes where an attempt failed.
    /// </summary>
    public static class RuntimeContent
    {
        private const int MaxAttempts = 200;

        private static readonly Dictionary<GUIDUnion, ContentHandle> _byGuid =
            new Dictionary<GUIDUnion, ContentHandle>();
        private static readonly List<ContentHandle> _pending = new List<ContentHandle>();

        /// <summary>Every accepted publication, in no particular order.</summary>
        public static IEnumerable<ContentHandle> Handles { get { return _byGuid.Values; } }

        /// <summary>Counts by state, for a menu or a log line.</summary>
        public static string Status
        {
            get
            {
                int r = 0, p = 0, f = 0;
                foreach (ContentHandle h in _byGuid.Values)
                {
                    if (h.State == ContentState.Registered) r++;
                    else if (h.State == ContentState.Pending) p++;
                    else f++;
                }
                return _byGuid.Count + " published (" + r + " registered, " + p + " pending, " +
                       f + " failed)";
            }
        }

        /// <summary>
        /// Publish one item. Returns a handle whose <c>State</c> is Registered when the
        /// containers already exist, Pending when they do not yet, and Failed with a
        /// <c>Reason</c> when the publication is refused. Publishing the same GUID again from
        /// the same owner returns the first handle; from another owner it is refused.
        /// Call from the main thread. Publish before the player first opens a terminal: a
        /// catalog may read the lists once per session.
        /// </summary>
        public static ContentHandle Publish(ContentKind kind, GUIDUnion guid, ContentAssets assets,
                                            ModContentEntry entry)
        {
            string owner = entry != null && !string.IsNullOrEmpty(entry.Owner) ? entry.Owner
                                                                                 : "unnamed";
            ContentHandle h = new ContentHandle(kind, guid, owner);
            try
            {
                ContentHandle existing;
                if (!guid.IsNull && _byGuid.TryGetValue(guid, out existing))
                {
                    if (existing.Owner == owner && existing.Kind == kind) return existing;
                    return Refuse(h, "GUID " + guid.AsHex() + " is already published by " +
                                     existing.Owner + " as " + existing.Kind + ".");
                }

                string why = Validate(kind, guid, assets, entry);
                if (why != null) return Refuse(h, why);

                if (EctypeConfig.Enabled != null && !EctypeConfig.Enabled.Value)
                    return Refuse(h, "the framework is disabled ([General] Enabled = false).");
                if (EctypeConfig.Enabled != null && !Plugin.PatchesApplied)
                    return Refuse(h, "the framework is inert this session (patching did not complete).");

                // Copies, so a caller changing its objects afterwards cannot change a retry.
                h.Assets = assets.Clone();
                h.Entry = entry.Clone();
                h.Entry.Guid = guid;
                h.Entry.Kind = kind.ToString();
                if (string.IsNullOrEmpty(h.Entry.Category)) h.Entry.Category = DefaultCategory(kind, assets);
                h.State = ContentState.Pending;
                _byGuid[guid] = h;
                _pending.Add(h);

                if (Plugin.PatchesApplied && Ready(kind)) Attempt(h);
                return h;
            }
            catch (Exception e)
            {
                return Refuse(h, "Publish threw: " + e);
            }
        }

        /// <summary>
        /// The checks a publication must pass before anything is registered, in this order:
        /// entry and GUID, crafting data, asset type. Null when they pass, else the reason.
        /// </summary>
        internal static string Validate(ContentKind kind, GUIDUnion guid, ContentAssets assets,
                                        ModContentEntry entry)
        {
            if (kind != ContentKind.WeaponModule && kind != ContentKind.Module &&
                kind != ContentKind.Carryable)
                return "unknown content kind " + (int)kind + ".";
            if (guid.IsNull) return "the GUID is null.";
            if (entry == null) return "no ModContentEntry.";
            if (string.IsNullOrEmpty(entry.Owner))
                return "the entry has no Owner (the publishing plugin's GUID).";
            if (string.IsNullOrEmpty(entry.DisplayName)) return "the entry has no DisplayName.";
            if (assets == null) return "no ContentAssets.";

            if (!assets.Crafting.HasValue)
            {
                if (kind == ContentKind.Carryable)
                    return (entry.IsComponent ? "component carryable " : "carryable ") +
                           guid.AsHex() + " has no crafting data (ContentAssets.Crafting). The " +
                           "game's end-of-run alloy tally reads the crafting rules of every " +
                           "carryable on the player ship with no null check and throws on one " +
                           "without them, and the fabricator cannot recycle it." +
                           (entry.IsComponent ? " A component is kept out of item lists, not " +
                                                "out of the tally." : "");
                if (kind == ContentKind.WeaponModule)
                    return "weapon module " + guid.AsHex() + " has no crafting data " +
                           "(ContentAssets.Crafting). Recycling, blank-box inscription and the " +
                           "end-of-run alloy tally all read the crafting rules under the weapon's " +
                           "GUID.";
            }

            if (assets.Asset == null) return "ContentAssets.Asset is null.";
            switch (kind)
            {
                case ContentKind.WeaponModule:
                    if (!(assets.Asset is CompositeWeaponData))
                        return "a weapon module's asset must be a CompositeWeaponData, not " +
                               assets.Asset.GetType().Name + ".";
                    break;
                case ContentKind.Module:
                    if (ComponentOf<CellModule>(assets.Asset) == null)
                        return "a module's asset must be a prefab carrying a CellModule; " +
                               assets.Asset.name + " carries none.";
                    break;
                case ContentKind.Carryable:
                    if (ComponentOf<CarryableObject>(assets.Asset) == null)
                        return "a carryable's asset must be a prefab carrying a CarryableObject; " +
                               assets.Asset.name + " carries none.";
                    break;
            }
            return null;
        }

        /// <summary>
        /// Completes queued publications. Called from the registration driver once the
        /// framework's patches applied and the containers it registers into exist.
        /// </summary>
        internal static void Pump()
        {
            if (_pending.Count == 0) return;
            foreach (ContentHandle h in _pending.ToArray())
            {
                if (h.State != ContentState.Pending) continue;
                if (!Ready(h.Kind)) continue;
                Attempt(h);
            }
            _pending.RemoveAll(h => h.State != ContentState.Pending);
        }

        /// <summary>The handle for <paramref name="guid"/>, in any state, or null.</summary>
        internal static ContentHandle Find(GUIDUnion guid)
        {
            ContentHandle h;
            return _byGuid.TryGetValue(guid, out h) ? h : null;
        }

        // ------------------------------------------------------------------ attempts

        /// <summary>A refusal that a retry cannot fix.</summary>
        private sealed class Refusal : Exception
        {
            public Refusal(string message) : base(message) { }
        }

        private static ContentHandle Refuse(ContentHandle h, string why)
        {
            h.State = ContentState.Failed;
            h.Reason = why;
            Log(2, "Ectype: content: refused " + h.Describe() + ": " + why);
            return h;
        }

        private static void Attempt(ContentHandle h)
        {
            h.Attempts++;
            try
            {
                switch (h.Kind)
                {
                    case ContentKind.WeaponModule: RegisterWeapon(h); break;
                    case ContentKind.Module: RegisterModule(h); break;
                    case ContentKind.Carryable: RegisterCarryable(h); break;
                }
                h.Stage = "verify";
                Verify(h);

                h.State = ContentState.Registered;
                h.Reason = null;
                h.Stage = "registered";
                Log(0, "Ectype: content: " + h.Describe() + " '" + h.Entry.DisplayName +
                       "' registered (" + Containers(h.Kind) + "; " +
                       (h.Entry.IsComponent ? "component, not listed" : "listed") + ").");
                ModContent.Add(h.Entry);
            }
            catch (Refusal r)
            {
                Refuse(h, r.Message);
            }
            catch (Exception e)
            {
                h.Reason = "attempt " + h.Attempts + " failed at [" + h.Stage + "]: " + e.Message;
                if (h.Attempts == 1 || h.Attempts % 10 == 0)
                    Log(1, "Ectype: content: " + h.Describe() + " " + h.Reason);
                if (h.Attempts >= MaxAttempts)
                {
                    h.State = ContentState.Failed;
                    Log(2, "Ectype: content: giving up on " + h.Describe() + ". Last error: " + e);
                }
            }
        }

        /// <summary>The containers each kind registers in, for the registered line.</summary>
        private static string Containers(ContentKind kind)
        {
            switch (kind)
            {
                case ContentKind.WeaponModule:
                    return "weapon, crafting and unlock containers";
                case ContentKind.Module:
                    return "module, spawnable-object and unlock containers";
                default:
                    return "carryable, spawnable-object, crafting and unlock containers";
            }
        }

        private static bool Ready(ContentKind kind)
        {
            try
            {
                if (ResourceAssetContainerRegister.Instance == null) return false;
                if (RuntimeAssetsRegister.Instance == null) return false;
                if (UnlockContainer.Instance == null) return false;
                if (CraftingDataContainer.Instance == null) return false;
                switch (kind)
                {
                    case ContentKind.WeaponModule:
                        return CompositeWeaponDataContainer.Instance != null;
                    case ContentKind.Module:
                        return ModuleContainer.Instance != null &&
                               CloneStarObjectContainer.Instance != null;
                    default:
                        return CarryableContainer.Instance != null &&
                               CloneStarObjectContainer.Instance != null;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Publish owns every container entry for its GUID. An entry that exists before the
        /// first attempt belongs to another registration, so the publication is refused rather
        /// than adopting data it did not write.
        /// </summary>
        private static void ClaimContainers(ContentHandle h, params KeyValuePair<string, bool>[] held)
        {
            if (h.ContainersDone) return;
            foreach (KeyValuePair<string, bool> kv in held)
                if (kv.Value)
                    throw new Refusal("GUID " + h.Guid.AsHex() + " is already in the " + kv.Key +
                                      ", registered by something other than this publication. " +
                                      "Publish registers every container itself; remove the " +
                                      "mod's own registration.");
        }

        private static KeyValuePair<string, bool> Held(string name, bool has)
        {
            return new KeyValuePair<string, bool>(name, has);
        }

        private static void RegisterAsset(ContentHandle h, UnityEngine.Object asset)
        {
            h.Stage = "runtime asset";
            RuntimeAssetsRegister reg = RuntimeAssetsRegister.Instance;
            if (reg.HasAsset(h.Guid)) return;
            reg.RegisterAsset(h.Guid, asset, h.Assets.ModFlag, new RuntimeAssetInfo
            {
                Name = asset.name,
                DisplayName = h.Entry.DisplayName,
            });
        }

        private static void RegisterUnlock(ContentHandle h, ResourceAssetRef itemRef)
        {
            h.Stage = "unlock";
            UnlockContainer c = UnlockContainer.Instance;
            UnlockItemDef d;
            if (!c.TryGetByGuid(h.Guid, out d) || d == null)
            {
                if ((object)itemRef == null) throw new Exception("the unlock def did not register");
                // ResourceAssetDef<T>.Ref is typed as the base ref, so the item's own ref fits
                // and makes the unlock's Asset resolve to the item.
                d = new UnlockItemDef();
                d.Ref = itemRef;
                c.RegisterRuntimeAsset(h.Guid, d);
            }
            d.UnlockOptions = new UnlockOptions { UnlockCriteria = h.Assets.Unlock };
            d.rarity = h.Assets.Rarity;
        }

        private static void RegisterCrafting(ContentHandle h)
        {
            h.Stage = "crafting";
            if (!h.Assets.Crafting.HasValue) return;
            CraftingDataContainer c = CraftingDataContainer.Instance;
            CraftableItemDef d;
            if (!c.TryGetByGuid(h.Guid, out d) || d == null)
            {
                // Never set this def's ContextInfo: its setter resolves the presentable owner of
                // the same GUID, and once a def that overrides only the getter is published
                // there, the setter calls itself until the stack overflows.
                d = new CraftableItemDef();
                d.Ref = new CraftableItemRef(h.Guid, "") { IsRuntime = true };
                c.RegisterRuntimeAsset(h.Guid, d);
            }
            d.SetCraftingRules(h.Assets.Crafting.Value);
        }

        private static ContextInfo BuildContext(ContentHandle h)
        {
            ContextInfo ctx = ContextInfo.Create(h.Assets.Icon, h.Entry.DisplayName,
                                                 h.Assets.Description ?? "");
            EctypeAssets.Keep(ctx);
            return ctx;
        }

        private static void RegisterWeapon(ContentHandle h)
        {
            CompositeWeaponDataContainer weapons = CompositeWeaponDataContainer.Instance;
            h.Stage = "claim";
            ClaimContainers(h,
                Held("weapon container", weapons.HasItem(h.Guid)),
                Held("crafting container", CraftingDataContainer.Instance.HasItem(h.Guid)),
                Held("unlock container", UnlockContainer.Instance.HasItem(h.Guid)));
            h.ContainersDone = true;

            CompositeWeaponData data = (CompositeWeaponData)h.Assets.Asset;
            data.ContainerGuid = h.Guid;
            EctypeAssets.Keep(data);
            RegisterAsset(h, data);
            RegisterCrafting(h);

            h.Stage = "weapon def";
            CompositeWeaponDataDef def = h.Def as CompositeWeaponDataDef;
            if (def == null)
            {
                def = new CompositeWeaponDataDef();
                def.Ref = new CompositeWeaponDataRef(h.Guid, "") { IsRuntime = true };
                // Field writes: the ContextInfo property's setter is the recursive one.
                if (!Refl.SetFieldLogged(def, "contextInfo", BuildContext(h)))
                    throw new Exception("could not set CompositeWeaponDataDef.contextInfo");
                if (h.Assets.BuildBoxIcon != null)
                    Refl.SetFieldLogged(def, "buildBoxIcon", h.Assets.BuildBoxIcon);
                if (h.Assets.ItemCategory.HasValue) def.SetPurchaseCategory(h.Assets.ItemCategory.Value);
                h.Def = def;
            }
            if (!weapons.HasItem(h.Guid)) weapons.RegisterRuntimeAsset(h.Guid, def);

            RegisterUnlock(h, new CompositeWeaponDataRef(h.Guid, "") { IsRuntime = true });

            h.Stage = "listing";
            if (!h.Entry.IsComponent) AssetDescriptionOrder.InsertDef(weapons.AssetDescriptions, def, d => d.Ref);
        }

        private static void RegisterModule(ContentHandle h)
        {
            ModuleContainer modules = ModuleContainer.Instance;
            CloneStarObjectContainer spawnable = CloneStarObjectContainer.Instance;
            h.Stage = "claim";
            ClaimContainers(h,
                Held("module container", modules.HasItem(h.Guid)),
                Held("spawnable-object container", spawnable.HasItem(h.Guid)),
                Held("crafting container", h.Assets.Crafting.HasValue &&
                                           CraftingDataContainer.Instance.HasItem(h.Guid)),
                Held("unlock container", UnlockContainer.Instance.HasItem(h.Guid)));
            h.ContainersDone = true;

            CellModule module = ComponentOf<CellModule>(h.Assets.Asset);
            module.ContainerGuid = h.Guid;
            RegisterAsset(h, module.gameObject);

            h.Stage = "spawnable-object def";
            if (!spawnable.HasItem(h.Guid))
                spawnable.RegisterRuntimeAsset(h.Guid, new CloneStarObjectDef
                {
                    Ref = new CloneStarObjectRef { IsRuntime = true, AssetGuid = h.Guid },
                    // A plain field write on this def type.
                    ContextInfo = BuildContext(h),
                });

            h.Stage = "module def";
            ModuleDef def = h.Def as ModuleDef;
            if (def == null)
            {
                // ModuleDef's ContextInfo reads and writes the spawnable-object def above.
                def = new ModuleDef();
                def.Ref = new ModuleRef(h.Guid, "") { IsRuntime = true };
                def.Category = h.Assets.ModuleCategory;
                h.Def = def;
            }
            if (!modules.HasItem(h.Guid)) modules.RegisterRuntimeAsset(h.Guid, def);

            RegisterCrafting(h);
            RegisterUnlock(h, new ModuleRef(h.Guid, "") { IsRuntime = true });

            h.Stage = "listing";
            if (!h.Entry.IsComponent) AssetDescriptionOrder.InsertDef(modules.AssetDescriptions, def, d => d.Ref);
        }

        private static void RegisterCarryable(ContentHandle h)
        {
            CarryableContainer carryables = CarryableContainer.Instance;
            CloneStarObjectContainer spawnable = CloneStarObjectContainer.Instance;
            CraftingDataContainer crafting = CraftingDataContainer.Instance;
            UnlockContainer unlocks = UnlockContainer.Instance;

            h.Stage = "claim";
            ClaimContainers(h,
                Held("carryable container", carryables.HasItem(h.Guid)),
                Held("spawnable-object container", spawnable.HasItem(h.Guid)),
                Held("crafting container", crafting.HasItem(h.Guid)),
                Held("unlock container", unlocks.HasItem(h.Guid)));
            h.ContainersDone = true;

            CarryableObject obj = ComponentOf<CarryableObject>(h.Assets.Asset);
            obj.ContainerGuid = h.Guid;
            RegisterAsset(h, obj.gameObject);

            h.Stage = "containers";
            if (!h.ConverterRan)
            {
                // The game's own registration for a converted carryable: one spawnable-object,
                // carryable, unlock and crafting def each. It is unguarded, so it runs once;
                // a throw part-way leaves entries a second call would collide with.
                h.ConverterRan = true;
                try
                {
                    RuntimeAssetConverter.RegisterCarryableToContainers(
                        h.Guid, h.Entry.DisplayName, h.Assets.Description ?? "", h.Assets.Icon);
                }
                catch (Exception e)
                {
                    throw new Refusal("the game's carryable registration threw part-way, so " +
                                      "a retry would collide with what it registered: " + e.Message);
                }
            }

            h.Stage = "context info";
            CloneStarObjectDef star;
            if (spawnable.TryGetByGuid(h.Guid, out star) && star != null)
                EctypeAssets.Keep(star.ContextInfo as ContextInfo);

            RegisterCrafting(h);

            h.Stage = "purchase category";
            CarryableDef def;
            if (!carryables.TryGetByGuid(h.Guid, out def) || def == null)
                throw new Exception("the carryable def did not register");
            def.SetPurchaseCategory(h.Assets.ItemCategory ?? PurchasableItemSubCategory.Other_Basic);
            h.Def = def;

            RegisterUnlock(h, null);

            h.Stage = "listing";
            if (!h.Entry.IsComponent) AssetDescriptionOrder.InsertDef(carryables.AssetDescriptions, def, d => d.Ref);
        }

        /// <summary>Resolves back out of every container what the attempt registered, so a
        /// silent misregistration fails here, by name, and is retried.</summary>
        private static void Verify(ContentHandle h)
        {
            if (!RuntimeAssetsRegister.Instance.HasAsset(h.Guid))
                throw new Exception("not in the runtime asset register");
            UnlockItemDef unlock;
            if (!UnlockContainer.Instance.TryGetByGuid(h.Guid, out unlock) || unlock == null)
                throw new Exception("no unlock def");
            CraftableItemDef craft;
            if (h.Assets.Crafting.HasValue &&
                (!CraftingDataContainer.Instance.TryGetByGuid(h.Guid, out craft) || craft == null))
                throw new Exception("no crafting def");
            switch (h.Kind)
            {
                case ContentKind.WeaponModule:
                {
                    CompositeWeaponDataDef d;
                    if (!CompositeWeaponDataContainer.Instance.TryGetByGuid(h.Guid, out d) || d == null)
                        throw new Exception("not in the weapon container");
                    if (!h.Entry.IsComponent &&
                        !CompositeWeaponDataContainer.Instance.AssetDescriptions.Contains(d))
                        throw new Exception("not listed in the weapon container");
                    break;
                }
                case ContentKind.Module:
                {
                    ModuleDef d;
                    if (!ModuleContainer.Instance.TryGetByGuid(h.Guid, out d) || d == null)
                        throw new Exception("not in the module container");
                    if (!CloneStarObjectContainer.Instance.HasItem(h.Guid))
                        throw new Exception("not in the spawnable-object container");
                    if (!h.Entry.IsComponent && !ModuleContainer.Instance.AssetDescriptions.Contains(d))
                        throw new Exception("not listed in the module container");
                    break;
                }
                default:
                {
                    CarryableDef d;
                    if (!CarryableContainer.Instance.TryGetByGuid(h.Guid, out d) || d == null)
                        throw new Exception("not in the carryable container");
                    if (!CloneStarObjectContainer.Instance.HasItem(h.Guid))
                        throw new Exception("not in the spawnable-object container");
                    if (!h.Entry.IsComponent && !CarryableContainer.Instance.AssetDescriptions.Contains(d))
                        throw new Exception("not listed in the carryable container");
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        private static string DefaultCategory(ContentKind kind, ContentAssets assets)
        {
            switch (kind)
            {
                case ContentKind.WeaponModule: return "Weapons";
                case ContentKind.Module: return assets.ModuleCategory.ToString();
                default:
                    return (assets.ItemCategory ?? PurchasableItemSubCategory.Other_Basic).ToString();
            }
        }

        private static T ComponentOf<T>(UnityEngine.Object o) where T : Component
        {
            T t = o as T;
            if (t != null) return t;
            GameObject go = o as GameObject;
            if (go == null)
            {
                Component c = o as Component;
                if (c != null) go = c.gameObject;
            }
            return go != null ? go.GetComponent<T>() : null;
        }

        /// <summary>0 info, 1 warning, 2 error. Safe before the framework's Awake.</summary>
        private static void Log(int level, string line)
        {
            if (Plugin.Log == null) return;
            if (level == 0) Plugin.Log.LogInfo(line);
            else if (level == 1) Plugin.Log.LogWarning(line);
            else Plugin.Log.LogError(line);
        }
    }
}
