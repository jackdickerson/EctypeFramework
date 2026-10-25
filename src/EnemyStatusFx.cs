using System;
using System.Collections.Generic;

using CG.Game;
using CG.Ship.TargetLocking;
using CG.Space;
using Photon.Pun;
using UI.ObjectMarker;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ectype
{
    /// <summary>
    /// Presentation for enemy statuses: a marker on the affected object with the game's own
    /// countdown circle. Local-only cosmetics driven by the replicated status lifecycle in
    /// EnemyStatus.cs - nothing here touches the wire or the sim.
    ///
    /// The marker rides the game's own object-marker pipeline (IL-verified): an
    /// ObjectMarkerData with MarkerType Ability and AbilityChannelTimeMS set is exactly
    /// what the game builds for an NPC's reinforcement-call channel, and the marker
    /// viewer runs the countdown circle from that field on its own. Markers enter and
    /// leave through ObjectMarkerRepository.Add/Remove - the repository's public seam,
    /// which fires the viewer's add/remove events and disposes removed data itself (the
    /// provider registry around it is private and only feeds those same two calls).
    /// The repository is per game scene, so this layer re-resolves the instance and
    /// rebuilds markers for statuses still running when a new one appears.
    /// </summary>
    internal static class EnemyStatusMarkers
    {
        private sealed class Entry
        {
            public ObjectMarkerData Data;
            public int ShownEndTs;     // server ts the displayed circle runs out at
            public float ShownAt;      // unscaled time the entity was (re)created
        }

        private static ObjectMarkerRepository _repo;
        private static TargetLockComponent _lock;
        /// <summary>The player-ship reference _lock was resolved against.</summary>
        private static OrbitObject _lockShip;
        private static float _nextLockResolve;
        private static readonly Dictionary<EnemyStatusEffect, Entry> Entries =
            new Dictionary<EnemyStatusEffect, Entry>();
        private static readonly List<EnemyStatusEffect> Scratch = new List<EnemyStatusEffect>();

        /// <summary>
        /// Show (or, on refresh, restart) the countdown marker for one live status. Markers
        /// are live UI entities the viewer repositions every frame, so the guards live here:
        ///  * a global cap ([EnemyStatuses] MarkerCap) - past it a status simply runs
        ///    markerless until a slot frees;
        ///  * lock-gated markers exist only WHILE locked - a hidden marker entity
        ///    would still be walked by the viewer, so the entity is created and
        ///    destroyed on lock change instead of toggled;
        ///  * refreshes restart the countdown IN PLACE on the existing entity
        ///    (skipped entirely while the displayed timer is within half a second of
        ///    truth); the marker is destroyed and recreated only if the in-place seam
        ///    fails, and that fallback path is additionally rate-limited.
        /// </summary>
        internal static void Show(EnemyStatusEffect fx)
        {
            if (!EctypeConfig.StatusMarkers.Value) return;
            if (!fx.Def.ShowMarker || fx.Target == null) return;
            if (!EnsureInstalled()) return;

            OrbitObject target = fx.Target;
            if (fx.Def.MarkerRequiresTargetLock && !IsLockedOn(target))
            {
                Hide(fx);
                return;
            }

            Entry existing;
            int endTs = unchecked(PhotonNetwork.ServerTimestamp + (int)fx.RemainingMs);
            if (Entries.TryGetValue(fx, out existing))
            {
                if (Math.Abs(unchecked(endTs - existing.ShownEndTs)) <= 500) return;
                if (RestartInPlace(existing, fx, endTs)) return;
                // Fallback only (the in-place seam failed): destroy+create, throttled.
                if (Time.unscaledTime - existing.ShownAt < 0.5f) return;
                Hide(fx);
            }
            else if (EnemyStatuses.CapReached(Entries.Count,
                                              EctypeConfig.StatusMarkerCap.Value,
                                              "marker", "a marker", "MarkerCap"))
                return;

            ObjectMarkerData data = new ObjectMarkerData(target.transform);
            data.MarkerType = ObjectMarkerData.EMarkerType.Ability;
            data.Affiliation = ResolveAffiliation(target);
            // Left unset, the marker table's Ability-type default icon is the fallback.
            if (fx.Def.Icon != null) data.Icon.Value = fx.Def.Icon;
            data.Text = FirstNonEmpty(fx.Def.MarkerText, fx.Def.DisplayName, fx.Def.Key);
            data.AbilityChannelTimeMS = fx.RemainingMs;
            data.AbilityDirectionInverted = true;   // drains down, channel-style

            _repo.Add(data);
            Entries[fx] = new Entry
            {
                Data = data,
                ShownEndTs = endTs,
                ShownAt = Time.unscaledTime,
            };
        }

        /// <summary>
        /// Reset the countdown without touching the marker entity. The viewer's
        /// StartAbilityAnimation is public and reads AbilityChannelTimeMS - but it
        /// does NOT stop the schedule it replaces, and the step function accumulates
        /// +-deltaTime/abilityTime into shared progress, so two live schedules drain
        /// the circle at double speed. The old schedule is therefore paused and
        /// cleared through its private field first. If a game update moves that field,
        /// the SetField probe reports it and the caller falls back to recreating the
        /// marker - degraded to the old churn, never to a double-speed circle.
        /// </summary>
        private static bool RestartInPlace(Entry entry, EnemyStatusEffect fx, int endTs)
        {
            try
            {
                entry.Data.AbilityChannelTimeMS = fx.RemainingMs;
                MarkerVE ve = entry.Data.Ve;
                if (ve != null)
                {
                    IVisualElementScheduledItem sched =
                        Refl.GetField<IVisualElementScheduledItem>(ve, "abilitySchedule", null);
                    if (!Refl.SetFieldLogged(ve, "abilitySchedule", null)) return false;
                    if (sched != null) sched.Pause();
                    ve.StartAbilityAnimation(entry.Data);
                }
                // VE not realized yet: its Init reads the updated AbilityChannelTimeMS.
                entry.ShownEndTs = endTs;
                entry.ShownAt = Time.unscaledTime;
                return true;
            }
            catch (Exception e)
            {
                if (EnemyStatuses.WarnOnce("markerrestart"))
                    Plugin.Log.LogWarning("Ectype: in-place countdown restart failed, " +
                                          "falling back to marker recreate: " + e.Message);
                return false;
            }
        }

        internal static void Hide(EnemyStatusEffect fx)
        {
            Entry entry;
            if (!Entries.TryGetValue(fx, out entry)) return;
            Entries.Remove(fx);
            try
            {
                // Remove fires the viewer's removed event and disposes the data.
                if (_repo != null) _repo.Remove(entry.Data);
            }
            catch (Exception e)
            {
                if (EnemyStatuses.WarnOnce("markerhide"))
                    Plugin.Log.LogError("Ectype: status marker removal failed " +
                                        "(suppressing further reports): " + e);
            }
        }

        /// <summary>Driven from EnemyStatuses.Poll: re-install after a scene swap and
        /// keep the target-lock subscription pointed at the current player ship.</summary>
        internal static void Poll(bool anyLive)
        {
            if (!anyLive && Entries.Count == 0) return;
            EnsureInstalled();
            EnsureLockSubscription();
        }

        // ---------------------------------------------------------------- wiring

        /// <summary>
        /// The field is assigned before the rebuild below, and that order is load-bearing:
        /// the rebuild calls Show, which calls back into here, and the ReferenceEquals guard
        /// is what terminates that recursion. Nothing is disposed on the way through - a
        /// different repository instance means the previous one went with the scene that
        /// owned it, so its entries are already gone and there is nothing left to Remove
        /// them from.
        /// </summary>
        private static bool EnsureInstalled()
        {
            ObjectMarkerRepository repo = ObjectMarkerRepository.Instance;
            if (repo == null) { _repo = null; return false; }
            if (ReferenceEquals(repo, _repo)) return true;
            _repo = repo;

            // A fresh repository knows nothing about statuses already running (scene
            // swap mid-run): rebuild their markers.
            Entries.Clear();
            Scratch.Clear();
            EnemyStatuses.CollectLive(Scratch);
            for (int i = 0; i < Scratch.Count; i++) Show(Scratch[i]);
            return true;
        }

        /// <summary>
        /// Presentation only, so the player-ship getter is the right source: the gate follows
        /// the ship this client is viewing. A multi-ship mod changes that reference as the
        /// player walks between ships or spectates, so a different reference drops the old
        /// subscription and resolves again. Resolve at most every 2s per ship: while the
        /// component is missing (hub, ship swap) a per-frame GetComponentInChildren over the
        /// ship would itself be a cost.
        /// </summary>
        private static void EnsureLockSubscription()
        {
            try
            {
                ClientGame game = ClientGame.Current;
                OrbitObject ship = game != null ? game.PlayerShip : null;
                if (!ReferenceEquals(ship, _lockShip))
                {
                    _lockShip = ship;
                    _nextLockResolve = 0f;
                    if (!ReferenceEquals(_lock, null))
                    {
                        try { if (_lock != null) _lock.TargetLockChanged -= OnTargetLockChanged; }
                        catch (Exception) { }
                        _lock = null;
                        OnTargetLockChanged(null);   // ungated until the new ship answers
                    }
                }
                if (_lock != null) return;
                if (Time.unscaledTime < _nextLockResolve) return;
                _nextLockResolve = Time.unscaledTime + 2f;
                if (ship == null) return;
                TargetLockComponent tlc = ship.GetComponentInChildren<TargetLockComponent>(true);
                if (tlc == null) return;
                _lock = tlc;
                tlc.TargetLockChanged += OnTargetLockChanged;
                if (Plugin.Verbose)
                    Plugin.Log.LogInfo("Ectype: status marker lock gate bound to ship " +
                                       Topology.ShipNumberOf(ship as PlayerControlledShip) + ".");
                OnTargetLockChanged(null);   // apply the gate now that it is answerable
            }
            catch (Exception e)
            {
                if (EnemyStatuses.WarnOnce("locksub"))
                    Plugin.Log.LogWarning("Ectype: target-lock gating unavailable: " + e);
            }
        }

        /// <summary>Lock-gated markers are created/destroyed here, never toggled -
        /// see Show. Runs only on the game's lock-changed event, not per frame.</summary>
        private static void OnTargetLockChanged(TargetLock unused)
        {
            try { OnTargetLockChangedInner(); }
            catch (Exception e) { Plugin.Log.LogError("Ectype: status marker lock update failed: " + e); }
        }

        private static void OnTargetLockChangedInner()
        {
            Scratch.Clear();
            EnemyStatuses.CollectLive(Scratch);
            for (int i = 0; i < Scratch.Count; i++)
            {
                EnemyStatusEffect fx = Scratch[i];
                if (!fx.Def.MarkerRequiresTargetLock || fx.Target == null) continue;
                if (IsLockedOn(fx.Target)) Show(fx);
                else Hide(fx);
            }
        }

        private static bool IsLockedOn(OrbitObject target)
        {
            // Unresolvable lock component fails visible rather than hiding everything.
            if (_lock == null) return true;
            try { return _lock.IsTargetLocked(target); }
            catch { return true; }
        }

        /// <summary>The game's own marker resolution: the faction helper every object marker
        /// uses (a multi-ship mod patches it to colour from the viewer's side), then the
        /// marker table's per-asset override.</summary>
        private static ObjectMarkerData.EAffiliation ResolveAffiliation(OrbitObject target)
        {
            ObjectMarkerData.EAffiliation aff = ObjectMarkerData.EAffiliation.Enemy;
            try
            {
                aff = ObjectMarkerHelpers.GetAffiliationBasedOnFaction(target);
                MarkerDataTable table = DataTable<MarkerDataTable>.Instance;
                if (table != null) aff = table.CheckForOverride(target.ContainerGuid, aff);
            }
            catch { }
            return aff;
        }

        private static string FirstNonEmpty(string a, string b, string c)
        {
            if (!string.IsNullOrEmpty(a)) return a;
            return !string.IsNullOrEmpty(b) ? b : c;
        }
    }

    /// <summary>
    /// The optional looping visual on the affected object. Two routes, tried in order:
    /// the template's own factory (full control - it returns a fresh GameObject and this
    /// layer parents, offsets and destroys it), or a stock-donor clone by name - the
    /// clone pipeline in src/shared/kitbash: instantiate into an inactive holder, strip
    /// to visuals (behaviours, colliders and the donor's PhotonView removed before
    /// anything wakes), then parent to the target and activate. Lookup is by substring
    /// over loaded prefab assets and cached per name.
    /// </summary>
    internal static class EnemyStatusVfx
    {
        private static readonly Dictionary<EnemyStatusEffect, GameObject> Attached =
            new Dictionary<EnemyStatusEffect, GameObject>();
        private static readonly Dictionary<string, Transform> DonorCache =
            new Dictionary<string, Transform>();
        private static readonly HashSet<string> DonorMisses = new HashSet<string>();

        internal static void Attach(EnemyStatusEffect fx)
        {
            EnemyStatusDef def = fx.Def;
            if (def.CreateVfx == null && string.IsNullOrEmpty(def.VfxDonorName)) return;
            if (!EctypeConfig.StatusVfx.Value) return;
            if (Attached.ContainsKey(fx) || fx.Target == null) return;
            if (EnemyStatuses.CapReached(Attached.Count, EctypeConfig.StatusVfxCap.Value,
                                         "vfx", "a visual", "VfxCap")) return;
            try
            {
                GameObject go = def.CreateVfx != null
                                    ? def.CreateVfx(fx.Target)
                                    : CloneDonor(def, fx.Target);
                if (go == null) return;
                if (def.CreateVfx != null)
                {
                    go.transform.SetParent(fx.Target.transform, false);
                    go.transform.localPosition = def.VfxLocalOffset;
                    go.transform.localScale = go.transform.localScale * def.VfxScale;
                }
                Attached[fx] = go;
            }
            catch (Exception e)
            {
                if (EnemyStatuses.WarnOnce("vfx:" + def.Key))
                    Plugin.Log.LogError("Ectype: status '" + def.Key + "' vfx attach " +
                                        "failed (suppressing further reports): " + e);
            }
        }

        internal static void Detach(EnemyStatusEffect fx)
        {
            GameObject go;
            if (!Attached.TryGetValue(fx, out go)) return;
            Attached.Remove(fx);
            if (go != null) UnityEngine.Object.Destroy(go);
        }

        private static GameObject CloneDonor(EnemyStatusDef def, OrbitObject target)
        {
            if (KitbashCore.Log == null) KitbashCore.Log = Plugin.Log;
            Transform donor = FindDonor(def.VfxDonorName, def.VfxDonorChild);
            if (donor == null)
            {
                if (EnemyStatuses.WarnOnce("vfxdonor:" + def.Key))
                    Plugin.Log.LogWarning("Ectype: status '" + def.Key + "' vfx donor '" +
                                          def.VfxDonorName + "' not found among loaded " +
                                          "prefabs; no visual attached.");
                return null;
            }
            GameObject holder;
            GameObject clone = KitbashCore.CloneVisualInert(
                donor, "EctypeStatusVfx_" + def.Key, def.Key,
                KitbashStripOptions.Default, out holder);
            if (clone == null) return null;
            KitbashCore.FinishInert(clone, target.transform, def.VfxLocalOffset,
                                    Vector3.zero, donor.localScale * def.VfxScale,
                                    ref holder, true);
            return clone;
        }

        /// <summary>Substring match over loaded prefab ASSETS (never scene instances),
        /// then an optional child-substring descent via the kitbash child finder. The
        /// scan walks every loaded GameObject, so both hits AND misses are cached: a
        /// donor name that resolves nothing must not re-scan on every application.</summary>
        private static Transform FindDonor(string name, string child)
        {
            string cacheKey = name + "|" + child;
            if (DonorMisses.Contains(cacheKey)) return null;
            Transform cached;
            if (DonorCache.TryGetValue(cacheKey, out cached) && cached != null) return cached;

            Transform root = null;
            GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < all.Length; i++)
            {
                GameObject go = all[i];
                if (go == null || go.scene.IsValid()) continue;      // asset, not scene
                if (go.transform.parent != null) continue;           // prefab roots only
                if (KitbashCore.Contains(go.name, name)) { root = go.transform; break; }
            }
            Transform result = root;
            if (result != null && !string.IsNullOrEmpty(child))
                result = KitbashCore.FindChild(result, child, true);
            if (result != null) DonorCache[cacheKey] = result;
            else DonorMisses.Add(cacheKey);
            return result;
        }
    }
}
