using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;

using CG.Ship.Modules;
using ResourceAssets;

// Global namespace, internal, opt-in - see KitbashRecipe.cs.

// The option fields and PrefabResolver are assigned only by consumers that opt in
// (a consumer using the defaults never writes them), so CS0649 is noise in every
// other including project.
#pragma warning disable 649

/// <summary>Strip behaviour for visual clones. Defaults are the safe ones: no colliders
/// (a stray collider blocks interaction with the real socket behind it), no animators,
/// quiet log.</summary>
internal struct KitbashStripOptions
{
    public bool KeepColliders;
    public bool KeepAnimators;
    public bool Verbose;

    public static KitbashStripOptions Default
    {
        get { return new KitbashStripOptions(); }
    }
}

/// <summary>
/// The reusable kitbash pipeline: clone a donor subtree inert, strip it to visuals,
/// graft it onto a live module, place it.
///
/// Two rules make it work, neither obvious:
///  * clone into an INACTIVE holder, so the copy's Awake/OnEnable never run while it
///    still carries the donor's behaviours and network identity;
///  * prefer the target's own subtree as the donor ("@self") - its serialized
///    back-references already point at the right instance.
///
/// This class applies visual-only recipes. It never registers a runtime asset, never
/// touches SessionModificationEffect, and never allocates a network id - a consuming
/// mod inherits no flag posture from it.
/// </summary>
internal static class KitbashCore
{
    /// <summary>Set once by the consuming assembly (each assembly has its own copy of
    /// this static). Null-safe: unset means silent.</summary>
    public static ManualLogSource Log;

    /// <summary>
    /// Hook for resolving a non-@self donor prefab query to a prefab root. A consumer with
    /// its own prefab index installs its resolver here; when unset, a minimal built-in
    /// walk over the ResourceAssets containers is used (cheap for GUID queries,
    /// potentially a multi-second Resources.Load walk for name queries - embedded
    /// documents should prefer GUID donors).
    /// </summary>
    public static Func<string, Transform> PrefabResolver;

    private static void Info(string msg) { if (Log != null) Log.LogInfo(msg); }
    private static void Warn(string msg) { if (Log != null) Log.LogWarning(msg); }
    private static void Error(string msg) { if (Log != null) Log.LogError(msg); }

    // ---------------------------------------------------------------- apply

    /// <summary>
    /// Apply one visual recipe to one module: idempotency check, donor resolve, inert
    /// clone, strip, parent, place, activate. Returns the placed clone, or null when
    /// the clone already exists, a stage failed, or stopAfterStage truncated the run.
    /// Stage names go into every error message. Never lets an exception escape.
    /// `mode=full` is not this layer's business - the recipe's Full flag is ignored and
    /// the clone is always stripped to visuals.
    /// </summary>
    public static GameObject ApplyVisual(CellModule module, KitbashRecipe recipe,
                                         KitbashStripOptions opts, int stopAfterStage = 0)
    {
        if (module == null || recipe == null || recipe.ParseError != null) return null;
        string stage = "start";
        GameObject clone = null;
        GameObject holder = null;
        try
        {
            // --- 0. idempotency ------------------------------------------------------------
            stage = "0-existing";
            if (FindExistingClone(module.transform, recipe.CloneName) != null) return null;

            // --- 1. donor ------------------------------------------------------------------
            stage = "1-donor";
            Transform donor = ResolveDonor(module, recipe.DonorPrefab, recipe.DonorPath);
            if (donor == null)
            {
                Warn("Kitbash [" + recipe.Id + "]: donor '" + recipe.DonorPrefab + ":" +
                     recipe.DonorPath + "' not found on " + SafeName(module));
                return null;
            }
            if (stopAfterStage == 1) return null;

            // --- 2. clone, inert -----------------------------------------------------------
            stage = "2-clone";
            bool donorWasActive = donor.gameObject.activeSelf;
            clone = CloneVisualInert(donor, recipe.CloneName, recipe.Id, opts, out holder);
            if (clone == null) { Cleanup(ref holder, ref clone); return null; }
            if (stopAfterStage == 2 || stopAfterStage == 3) { Cleanup(ref holder, ref clone); return null; }

            // --- 4. parent -----------------------------------------------------------------
            stage = "4-parent";
            Transform parent = ResolveParent(module, recipe.ParentPath, recipe.DonorPrefab, donor);
            clone.transform.SetParent(parent, false);
            if (stopAfterStage == 4) { Cleanup(ref holder, ref clone); return null; }

            // --- 5. place ------------------------------------------------------------------
            stage = "5-place";
            Vector3 pos = recipe.Pos.HasValue ? recipe.Pos.Value : donor.localPosition;
            Vector3 rot = recipe.Rot.HasValue ? recipe.Rot.Value : donor.localEulerAngles;
            Vector3 scl = recipe.Scale.HasValue ? recipe.Scale.Value : donor.localScale;
            clone.transform.localPosition = pos + recipe.Offset;
            clone.transform.localEulerAngles = rot + recipe.ROffset;
            clone.transform.localScale = Vector3.Scale(scl, recipe.SFactor);
            if (stopAfterStage == 5) { Cleanup(ref holder, ref clone); return null; }

            // --- 6. activate ---------------------------------------------------------------
            stage = "6-activate";
            UnityEngine.Object.DestroyImmediate(holder);
            holder = null;
            clone.SetActive(donorWasActive);

            Info("Kitbash [" + recipe.Id + "]: placed on " + SafeName(module) +
                 " under " + FullPath(parent) + " at " + clone.transform.localPosition + "  (visual)");
            return clone;
        }
        catch (Exception e)
        {
            Error("Kitbash [" + recipe.Id + "] failed at stage " + stage + " on " +
                  SafeName(module) + ": " + e);
            Cleanup(ref holder, ref clone);          // leave nothing half-built on a real module
            return null;
        }
    }

    // ---------------------------------------------------------------- clone core

    /// <summary>
    /// Clone a donor subtree, strip it to visuals, and hand it back STILL INACTIVE under
    /// a holder. The caller parents it, places it, then calls <see cref="FinishInert"/>.
    /// Instantiating into an INACTIVE parent is what keeps Awake/OnEnable from running
    /// with the donor's behaviours and network identity still attached.
    /// </summary>
    public static GameObject CloneVisualInert(Transform donor, string cloneName, string logId,
                                              KitbashStripOptions opts, out GameObject holder)
    {
        holder = null;
        if (donor == null) return null;
        holder = new GameObject("KitbashHolder");
        holder.SetActive(false);
        GameObject clone = UnityEngine.Object.Instantiate(donor.gameObject, holder.transform, false);
        clone.name = cloneName;
        StripToVisual(clone, logId, opts);
        VerifyVisuals(clone, logId);
        return clone;
    }

    /// <summary>Parent, place, drop the holder, activate. Mirrors stages 4-6 of the pipeline.</summary>
    public static void FinishInert(GameObject clone, Transform parent, Vector3 pos, Vector3 rot,
                                   Vector3 scale, ref GameObject holder, bool activate)
    {
        if (clone == null) return;
        clone.transform.SetParent(parent, false);
        clone.transform.localPosition = pos;
        clone.transform.localEulerAngles = rot;
        clone.transform.localScale = scale;
        if (holder != null) { UnityEngine.Object.DestroyImmediate(holder); holder = null; }
        clone.SetActive(activate);
    }

    private static void Cleanup(ref GameObject holder, ref GameObject clone)
    {
        try { if (clone != null) UnityEngine.Object.DestroyImmediate(clone); } catch { }
        try { if (holder != null) UnityEngine.Object.DestroyImmediate(holder); } catch { }
        clone = null;
        holder = null;
    }

    // ---------------------------------------------------------------- stripping

    /// <summary>
    /// Keep the things that draw, remove everything else. A visual clone that still
    /// carries a PhotonView or a socket behaviour is not a decoration, it is a second
    /// live object competing with the real one.
    /// </summary>
    public static void StripToVisual(GameObject clone, string id, KitbashStripOptions opts)
    {
        List<Component> doomed = new List<Component>();
        Component[] all = clone.GetComponentsInChildren<Component>(true);
        for (int i = 0; i < all.Length; i++)
        {
            Component c = all[i];
            if (c == null) continue;
            if (c is Transform) continue;
            if (IsVisual(c, opts)) continue;
            doomed.Add(c);
        }

        // PhotonView last: MonoBehaviourPun components require it, and Unity refuses to
        // remove a component something else depends on.
        doomed.Sort(delegate (Component a, Component b)
        {
            int pa = (a is PhotonView) ? 1 : 0;
            int pb = (b is PhotonView) ? 1 : 0;
            return pa.CompareTo(pb);
        });

        int removed = 0;
        for (int pass = 0; pass < 8 && doomed.Count > 0; pass++)
        {
            List<Component> stubborn = new List<Component>();
            for (int i = 0; i < doomed.Count; i++)
            {
                Component c = doomed[i];
                if (c == null) continue;
                string tn = c.GetType().FullName;
                try { UnityEngine.Object.DestroyImmediate(c); }
                catch (Exception e)
                {
                    if (opts.Verbose)
                        Warn("Kitbash [" + id + "]: removing " + tn + " failed: " + e.Message);
                }
                // Unity logs and returns instead of throwing when a dependency blocks
                // removal, so "did it actually go" is the only reliable check.
                if (c == null) { removed++; if (opts.Verbose) Info("Kitbash [" + id + "]: removed " + tn); }
                else stubborn.Add(c);
            }
            if (stubborn.Count == doomed.Count)
            {
                // No progress this pass - disable what is left rather than fight it.
                for (int i = 0; i < stubborn.Count; i++)
                {
                    Behaviour b = stubborn[i] as Behaviour;
                    if (b != null) { try { b.enabled = false; } catch { } }
                }
                Warn("Kitbash [" + id + "]: " + stubborn.Count +
                     " components could not be removed and were disabled instead (first: " +
                     stubborn[0].GetType().FullName + ").");
                break;
            }
            doomed = stubborn;
        }

        Info("Kitbash [" + id + "]: stripped " + removed + " components to visual-only.");
    }

    private static bool IsVisual(Component c, KitbashStripOptions opts)
    {
        if (c is Renderer) return true;          // Mesh/Skinned/Trail/Line/Particle/VFXRenderer
        if (c is MeshFilter) return true;
        if (c is LODGroup) return true;
        if (c is Light) return true;
        if (c is ParticleSystem) return true;
        if (c is Animator) return opts.KeepAnimators;
        if (c is Collider) return opts.KeepColliders;

        // Name-matched rather than type-referenced: these live in HDRP / VFX / Sprite
        // modules that are a nuisance to add to the reference set, and the name is
        // unambiguous.
        string n = c.GetType().Name;
        if (n == "VisualEffect") return true;                 // drives a VFXRenderer; useless alone
        if (n == "SortingGroup") return true;
        // A UnityEngine.Light with no HDAdditionalLightData does not light anything in
        // HDRP - the Light component is only half of an HDRP light.
        if (n == "HDAdditionalLightData") return true;
        if (n == "HDAdditionalReflectionData") return true;
        return false;
    }

    /// <summary>
    /// The two ways a visual clone comes out looking wrong, both of them silent.
    /// </summary>
    public static void VerifyVisuals(GameObject clone, string id)
    {
        Renderer[] rs = clone.GetComponentsInChildren<Renderer>(true);
        if (rs.Length == 0)
        {
            Warn("Kitbash [" + id + "]: clone has no renderers and will be invisible " +
                 "(donor node is logic-only? try a GFX_* child).");
            return;
        }

        // A SkinnedMeshRenderer keeps its bone array. Instantiate only remaps bones that
        // were INSIDE the copied subtree - clone below the common ancestor of mesh and
        // skeleton and the copy silently deforms with, and renders at, the original.
        int strays = 0;
        for (int i = 0; i < rs.Length; i++)
        {
            SkinnedMeshRenderer smr = rs[i] as SkinnedMeshRenderer;
            if (smr == null) continue;
            Transform[] bones = smr.bones;
            if (bones == null) continue;
            for (int b = 0; b < bones.Length; b++)
            {
                if (bones[b] == null) continue;
                if (!bones[b].IsChildOf(clone.transform)) { strays++; break; }
            }
            if (smr.rootBone != null && !smr.rootBone.IsChildOf(clone.transform)) strays++;
        }
        if (strays > 0)
            Warn("Kitbash [" + id + "]: " + strays + " SkinnedMeshRenderer(s) bound to bones outside " +
                 "the clone; they follow the original. Clone a node containing the mesh and its joints.");

        Info("Kitbash [" + id + "]: " + rs.Length + " renderer(s) survived the strip.");
    }

    // ---------------------------------------------------------------- resolution

    public static bool MatchesTarget(CellModule module, string query)
    {
        if (module == null) return false;
        if (string.IsNullOrEmpty(query)) return false;
        if (query == "*") return true;
        try
        {
            string goName = StripClone(module.gameObject.name);
            if (Contains(goName, query)) return true;
            if (Contains(module.GetType().Name, query)) return true;

            object guid = GetAssetGuid(module);
            if (guid == null) return false;
            if (Contains(guid.ToString(), query)) return true;
            // The catalogue header is the name the player actually sees ("Charge Station
            // MkIII"), so it is the one they will type into a recipe.
            if (Contains(HeaderForGuid(guid), query)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>Resolve a recipe's donor: a child of the module for "@self", otherwise a
    /// child of the prefab the query names (via PrefabResolver or the built-in walk).</summary>
    public static Transform ResolveDonor(CellModule module, string donorPrefab, string donorPath)
    {
        if (donorPrefab == "@self")
        {
            if (module == null) return null;
            return FindChild(module.transform, donorPath, true);
        }
        Transform prefabRoot = null;
        if (PrefabResolver != null) prefabRoot = PrefabResolver(donorPrefab);
        else prefabRoot = ResolvePrefabFallback(donorPrefab);
        if (prefabRoot == null)
        {
            Warn("Kitbash: no prefab matches donor query '" + donorPrefab + "'.");
            return null;
        }
        return FindChild(prefabRoot, donorPath, false);
    }

    public static Transform ResolveParent(CellModule module, string parentPath,
                                          string donorPrefab, Transform donor)
    {
        if (!string.IsNullOrEmpty(parentPath))
        {
            Transform p = FindChild(module.transform, parentPath, false);
            if (p != null) return p;
            Warn("Kitbash: parent '" + parentPath + "' not found; using the module root.");
            return module.transform;
        }
        // For a @self donor, sitting beside the original is almost always what you want.
        if (donorPrefab == "@self" && donor != null && donor.parent != null &&
            donor.IsChildOf(module.transform))
            return donor.parent;
        return module.transform;
    }

    /// <summary>
    /// "A/B/C" is a literal Transform.Find path. "*Foo" searches the whole subtree for
    /// the first node whose name - or the type name of one of its components - contains
    /// Foo. The second form is what makes a recipe survive the game renaming a node.
    /// </summary>
    public static Transform FindChild(Transform root, string query, bool skipClones)
    {
        if (root == null || string.IsNullOrEmpty(query)) return null;

        if (query[0] != '*')
        {
            Transform direct = root.Find(query);
            if (direct != null) return direct;
            // fall through to a subtree search on the last path element
            int slash = query.LastIndexOf('/');
            query = "*" + (slash >= 0 ? query.Substring(slash + 1) : query);
        }

        string needle = query.Substring(1);
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t == root) continue;
            if (skipClones && IsKitbashClone(t)) continue;
            if (Contains(t.name, needle)) return t;
        }
        // second sweep: match by component type instead of node name
        for (int i = 0; i < all.Length; i++)
        {
            Transform t = all[i];
            if (t == root) continue;
            if (skipClones && IsKitbashClone(t)) continue;
            Component[] comps = t.GetComponents<Component>();
            for (int j = 0; j < comps.Length; j++)
            {
                if (comps[j] == null) continue;
                if (Contains(comps[j].GetType().Name, needle)) return t;
            }
        }
        return null;
    }

    public static bool IsKitbashClone(Transform t)
    {
        Transform cur = t;
        int guard = 0;
        while (cur != null && guard++ < 64)
        {
            if (cur.name.StartsWith("KB_", StringComparison.Ordinal)) return true;
            cur = cur.parent;
        }
        return false;
    }

    public static Transform FindExistingClone(Transform root, string cloneName)
    {
        Transform[] all = root.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < all.Length; i++)
            if (all[i].name == cloneName) return all[i];
        return null;
    }

    // ---------------------------------------------------------------- small helpers

    public static bool Contains(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;
        return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static string StripClone(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        int idx = name.IndexOf("(Clone)", StringComparison.Ordinal);
        return idx >= 0 ? name.Substring(0, idx) : name;
    }

    public static string SafeName(CellModule m)
    {
        try { return m == null ? "<null>" : StripClone(m.gameObject.name) + " (" + m.GetType().Name + ")"; }
        catch { return "<unnamed>"; }
    }

    public static string FullPath(Transform t)
    {
        if (t == null) return "<null>";
        string p = t.name;
        Transform cur = t.parent;
        int guard = 0;
        while (cur != null && guard++ < 64) { p = cur.name + "/" + p; cur = cur.parent; }
        return p;
    }

    // Silent, cached field/property reflection for the module's assetGuid. Deliberately
    // not AccessTools: a miss that is expected would log twice per module per match.
    private static FieldInfo _assetGuidField;
    private static PropertyInfo _assetGuidProp;
    private static Type _assetGuidOwner;

    public static object GetAssetGuid(CellModule module)
    {
        if (module == null) return null;
        try
        {
            Type t = module.GetType();
            if (_assetGuidOwner == null || !_assetGuidOwner.IsAssignableFrom(t))
            {
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                Type cur = t;
                int guard = 0;
                while (cur != null && cur != typeof(object) && guard++ < 32)
                {
                    FieldInfo fi = cur.GetField("assetGuid", F | BindingFlags.DeclaredOnly);
                    if (fi != null) { _assetGuidField = fi; _assetGuidProp = null; _assetGuidOwner = cur; break; }
                    PropertyInfo pi = cur.GetProperty("assetGuid", F | BindingFlags.DeclaredOnly);
                    if (pi != null && pi.CanRead) { _assetGuidProp = pi; _assetGuidField = null; _assetGuidOwner = cur; break; }
                    cur = cur.BaseType;
                }
            }
            if (_assetGuidField != null) return _assetGuidField.GetValue(module);
            if (_assetGuidProp != null) return _assetGuidProp.GetValue(module, null);
        }
        catch { }
        return null;
    }

    private static readonly Dictionary<string, string> _headers = new Dictionary<string, string>();

    /// <summary>Catalogue header for a boxed GUIDUnion, cached. Cheap enough to call
    /// from a module-start postfix - GetContext is a dictionary lookup, not a resolve.</summary>
    public static string HeaderForGuid(object boxedGuid)
    {
        if (boxedGuid == null) return "";
        string key = boxedGuid.ToString();
        string cached;
        if (_headers.TryGetValue(key, out cached)) return cached;

        string header = "";
        try
        {
            ResourceAssetContainerRegister reg = ResourceAssetContainerRegister.Instance;
            if (reg != null && boxedGuid is GUIDUnion)
            {
                IResourceAssetContextInfo ctx = reg.GetContext((GUIDUnion)boxedGuid);
                if (ctx != null && ctx.HeaderText != null) header = ctx.HeaderText;
            }
        }
        catch { }

        _headers[key] = header;
        return header;
    }

    // ---------------------------------------------------------------- fallback prefab resolve

    private static readonly Dictionary<string, Transform> _resolvedPrefabs =
        new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Minimal donor-prefab resolution over the ResourceAssets containers, for consumers
    /// without their own prefab index. GUID queries are cheap (GUID comparison,
    /// one asset resolve). Name queries have to resolve assets one by one until a name
    /// matches - that is a Resources.Load walk and can hitch for seconds, which is why
    /// embedded documents should carry GUID donors.
    /// </summary>
    private static Transform ResolvePrefabFallback(string query)
    {
        if (string.IsNullOrEmpty(query)) return null;
        Transform cached;
        if (_resolvedPrefabs.TryGetValue(query, out cached) && cached != null) return cached;

        Transform found = null;
        try
        {
            ResourceAssetContainerRegister reg = ResourceAssetContainerRegister.Instance;
            if (reg == null) return null;
            IEnumerable containers = GetContainers(reg);
            if (containers == null) return null;

            // Pass 1: match by GUID string - no asset resolution until the hit.
            foreach (object cObj in containers)
            {
                IResourceAssetContainer c = cObj as IResourceAssetContainer;
                if (c == null) continue;
                IReadOnlyList<IResourceAsset> items = null;
                try { items = c.GetAllItems(); } catch { continue; }
                if (items == null) continue;
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i] == null) continue;
                    string g = null;
                    try { g = items[i].AssetGuid.ToString(); } catch { }
                    if (g == null || !string.Equals(g, query, StringComparison.OrdinalIgnoreCase)) continue;
                    found = PrefabRootOf(items[i]);
                    if (found != null) break;
                }
                if (found != null) break;
            }

            // Pass 2: match by object name - resolves assets until a match. Expensive.
            if (found == null)
            {
                Warn("Kitbash: resolving donor prefab '" + query + "' by name (slow, can " +
                     "hitch; use a GUID donor).");
                foreach (object cObj in containers)
                {
                    IResourceAssetContainer c = cObj as IResourceAssetContainer;
                    if (c == null) continue;
                    IReadOnlyList<IResourceAsset> items = null;
                    try { items = c.GetAllItems(); } catch { continue; }
                    if (items == null) continue;
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (items[i] == null) continue;
                        Transform root = PrefabRootOf(items[i]);
                        if (root == null) continue;
                        if (string.Equals(root.name, query, StringComparison.OrdinalIgnoreCase))
                        { found = root; break; }
                    }
                    if (found != null) break;
                }
            }
        }
        catch (Exception e)
        {
            Warn("Kitbash: prefab resolve for '" + query + "' failed: " + e.Message);
        }

        if (found != null) _resolvedPrefabs[query] = found;
        return found;
    }

    private static IEnumerable GetContainers(ResourceAssetContainerRegister reg)
    {
        try
        {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type t = reg.GetType();
            PropertyInfo pi = t.GetProperty("Containers", F);
            if (pi != null) return pi.GetValue(reg, null) as IEnumerable;
            FieldInfo fi = t.GetField("Containers", F);
            if (fi != null) return fi.GetValue(reg) as IEnumerable;
        }
        catch { }
        return null;
    }

    private static Transform PrefabRootOf(IResourceAsset item)
    {
        try
        {
            UnityEngine.Object asset = item.Asset;
            GameObject go = asset as GameObject;
            if (go == null)
            {
                // A ModuleDef resolves to the CellModule COMPONENT, not the GameObject.
                Component comp = asset as Component;
                if (comp != null) go = comp.gameObject;
            }
            return go != null ? go.transform : null;
        }
        catch { return null; }
    }
}
