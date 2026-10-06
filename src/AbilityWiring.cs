using System;
using System.Collections.Generic;
using System.Reflection;

using Gameplay.Perks;
using Gameplay.Tags;
using HarmonyLib;
using Opsive.UltimateCharacterController.Character;
using Opsive.UltimateCharacterController.Character.Abilities;
using UI.Token;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ectype
{
    /// <summary>
    /// Installs each registered role's ability (built by the factory the role mod passed
    /// to RoleRegistry.RegisterAbilityFactory) into a character's Opsive locomotion, and
    /// teaches the single-slot HUD widget about abilities it cannot type-match.
    ///
    /// Runs for every character this client sees, not only the local player's, because
    /// vanilla replication indexes into the abilities array and index parity across clients
    /// must therefore hold; RebuildArray is what preserves it, and vanilla ClassAbility's
    /// own IsMine self-gating is what makes Start() on a remote replica safe.
    ///
    /// The perk-identity fields are pointed at the role's registered perks by grid
    /// position: hero (row 0) = AbilityPerk, the row-5 banner = CapstonePerk, the row-7
    /// banner = SuperiorAbilityPerk - the vanilla tree shape. Roles without a boost or
    /// superior banner fall back to the hero perk, which keeps the game's unconditional
    /// dereferences (IsSuperiorVersion walks SuperiorAbilityPerk.ContainerGuid) from
    /// NREing; a fallback field means "that tier reads as the hero", so ability code
    /// should only consult the helpers for tiers its manifest actually has.
    /// </summary>
    internal static class AbilityWiring
    {
        private static readonly FieldInfo LocomotionF =
            AccessTools.Field(typeof(Ability), "m_CharacterLocomotion");

        private static bool _loggedNoLocomotion;

        // ------------------------------------------------------------------ installation

        internal static void TryInstallAll(ClassAbility trigger)
        {
            try
            {
                if (!Plugin.PatchesApplied || !EctypeConfig.Enabled.Value) return;
                if (trigger is EctypeRoleAbility) return;   // our own Start re-entering
                if (trigger.LocalPlayer == null) return;     // liveness check, not ownership
                if (RoleRegistry.Roles.Count == 0) return;

                UltimateCharacterLocomotion loco =
                    LocomotionF != null
                        ? LocomotionF.GetValue(trigger) as UltimateCharacterLocomotion
                        : null;
                if (loco == null)
                {
                    if (!_loggedNoLocomotion)
                    {
                        _loggedNoLocomotion = true;
                        Plugin.Log.LogError("Ectype: could not read m_CharacterLocomotion " +
                                            "off a ClassAbility - game/Opsive update? " +
                                            "Modded abilities will not install.");
                    }
                    return;
                }

                foreach (RoleRecord rec in RoleRegistry.Roles)
                    InstallOne(rec, loco);

                // Adoption resolves selection against the LOCAL player's perk profile, so
                // unlike the install sweep it is meaningless for a character we don't own.
                if (!trigger.LocalPlayer.IsMine) return;

                // A replacement role inherits the vanilla ability of the tree it suppressed;
                // after the install sweep, so a role with its own factory is already known.
                Adoption.AdoptAbilities(loco);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: ability install sweep failed: " + e);
            }
        }

        private static void InstallOne(RoleRecord rec, UltimateCharacterLocomotion loco)
        {
            if (rec.AbilityFactory == null || rec.AbilityInstallFailed || !rec.Registered)
                return;
            // Same character already served (Start fires once per prefab ability, so this
            // sweep runs up to four times per spawn).
            if (rec.AbilityInstances.ContainsKey(loco)) return;

            PerkBuff hero = RoleRegistry.BannerPerk(rec, 0);
            if (hero == null) return;   // registration not finished; a later Start retries

            ClassAbility ability;
            try
            {
                ability = rec.AbilityFactory() as ClassAbility;
            }
            catch (Exception e)
            {
                rec.AbilityInstallFailed = true;
                Plugin.Log.LogError("Ectype: ability factory for '" +
                                    rec.Manifest.roleId + "' threw - the role stays " +
                                    "data-only this session: " + e);
                return;
            }
            if (ability == null)
            {
                rec.AbilityInstallFailed = true;
                Plugin.Log.LogError("Ectype: ability factory for '" + rec.Manifest.roleId +
                                    "' returned null or a non-ClassAbility - the role " +
                                    "stays data-only this session.");
                return;
            }

            PerkBuff capstone = RoleRegistry.BannerPerk(rec, 5) ?? hero;
            PerkBuff superior = RoleRegistry.BannerPerk(rec, 7) ?? hero;
            bool ok = Refl.SetFieldLogged(ability, "AbilityPerk", hero)
                    & Refl.SetFieldLogged(ability, "CapstonePerk", capstone)
                    & Refl.SetFieldLogged(ability, "SuperiorAbilityPerk", superior)
                    & Refl.SetFieldLogged(ability, "abilityUsable", true)
                    & Refl.SetFieldLogged(ability, "tags", new CsTag[0]);
            Refl.SetFieldLogged(ability, "LocalPerkLoader",
                          loco.GetComponent<PlayerPerkLoader>());
            if (!ok)
            {
                // A perk-identity field that did not take means selection can never work
                // (or Start NREs) - fail this role, leave the character vanilla.
                rec.AbilityInstallFailed = true;
                Plugin.Log.LogError("Ectype: ability field wiring failed for '" +
                                    rec.Manifest.roleId + "' (member rename? see " +
                                    "warnings above) - ability not installed.");
                return;
            }

            // Record BEFORE Start(): Start re-enters our ClassAbility.Start postfix.
            rec.AbilityInstances[loco] = ability;
            // Opt into key-based replication so the ability's array position on other
            // clients is irrelevant to its start/stop sync (see AbilitySync).
            RuntimeAbilities.Register(ability, "ectype:" + rec.Manifest.roleId);

            try
            {
                // Rebuild, never append: appending would park a role at whatever index its
                // arrival order gave it, which differs per client and breaks index parity.
                RebuildArray(loco);
                ability.Start();   // manual - only prefab abilities get it from Unity
            }
            catch (Exception e)
            {
                rec.AbilityInstances.Remove(loco);
                rec.AbilityInstallFailed = true;
                Plugin.Log.LogError("Ectype: installing '" + rec.Manifest.roleId +
                                    "' ability into the locomotion failed: " + e);
                return;
            }

            if (Plugin.Verbose) Plugin.Log.LogInfo("Ectype: ability " + ability.GetType().Name +
                               " installed for '" + rec.Manifest.roleId + "' (slot " +
                               Array.IndexOf(loco.Abilities, ability) + ").");
        }

        /// <summary>Rebuild loco.Abilities as [non-Ectype abilities] + [every role with an
        /// instance for this loco, in RoleRegistry.Roles' fixed roleId-sorted order], so
        /// array position never depends on which pass installed which role.</summary>
        private static void RebuildArray(UltimateCharacterLocomotion loco)
        {
            List<Ability> ordered = new List<Ability>();
            foreach (Ability a in loco.Abilities ?? new Ability[0])
                if (!(a is EctypeRoleAbility)) ordered.Add(a);

            foreach (RoleRecord r in RoleRegistry.Roles)
            {
                ClassAbility a;
                if (r.AbilityInstances.TryGetValue(loco, out a) && a != null)
                    ordered.Add(a);
            }

            loco.Abilities = ordered.ToArray();   // setter: resize active buffer, Initialize, Awake
        }


        internal static void OnAbilityDestroyed(ClassAbility ability)
        {
            foreach (RoleRecord rec in RoleRegistry.Roles)
            {
                object deadLoco = null;
                foreach (KeyValuePair<object, ClassAbility> kv in rec.AbilityInstances)
                {
                    if (ReferenceEquals(kv.Value, ability)) { deadLoco = kv.Key; break; }
                }
                if (deadLoco != null) rec.AbilityInstances.Remove(deadLoco);
            }
        }

        internal static RoleRecord FindByAbility(ClassAbility ability)
        {
            if (ability == null) return null;
            foreach (RoleRecord rec in RoleRegistry.Roles)
                foreach (ClassAbility a in rec.AbilityInstances.Values)
                    if (ReferenceEquals(a, ability)) return rec;
            return null;
        }
    }

    [HarmonyPatch(typeof(ClassAbility), "Start")]
    internal static class Patch_ClassAbility_Start_Install
    {
        [HarmonyPostfix]
        private static void Postfix(ClassAbility __instance)
        {
            AbilityWiring.TryInstallAll(__instance);
        }
    }

    [HarmonyPatch(typeof(ClassAbility), "OnDestroy")]
    internal static class Patch_ClassAbility_OnDestroy_Forget
    {
        [HarmonyPostfix]
        private static void Postfix(ClassAbility __instance)
        {
            try
            {
                AbilityWiring.OnAbilityDestroyed(__instance);
                Adoption.ForgetAbility(__instance);
            }
            catch (Exception e)
            { Plugin.Log.LogError("Ectype: ability-destroy cleanup failed: " + e); }
        }
    }

    /// <summary>
    /// ClassAbilityDisplay resolves its per-role element by hard type-check on the four
    /// vanilla ability classes, so a framework ability lands in its no-match branch and
    /// selects with no HUD slot; this postfix builds the element, points the display's
    /// private state at it, and re-subscribes the status handlers that branch dropped.
    ///
    /// The resubscribe also runs for vanilla abilities: the vanilla body skips its own when
    /// the previous element was still visible (the framework->vanilla switch), leaving the
    /// usable-changed handler dead. Unsubscribe-then-subscribe collapses both states to one
    /// registration.
    /// </summary>
    [HarmonyPatch(typeof(ClassAbilityDisplay), "OnLocalPlayerEnabledClassAbility")]
    internal static class Patch_ClassAbilityDisplay_ModdedSlot
    {
        private static readonly FieldInfo StartedF =
            AccessTools.Field(typeof(ClassAbilityDisplay), "started");
        private static readonly FieldInfo ContainerF =
            AccessTools.Field(typeof(HUDElement), "container");
        private static readonly FieldInfo CurrentF =
            AccessTools.Field(typeof(ClassAbilityDisplay), "currentClassAbility");
        private static readonly FieldInfo VeF =
            AccessTools.Field(typeof(ClassAbilityDisplay), "classAbilityVE");
        private static readonly FieldInfo PerkVeF =
            AccessTools.Field(typeof(ClassAbilityDisplay), "perkVe");
        private static readonly FieldInfo InteractingF =
            AccessTools.Field(typeof(ClassAbilityDisplay), "interactingWithTerminal");
        private static readonly MethodInfo UnsubscribeM =
            AccessTools.Method(typeof(ClassAbilityDisplay),
                               "UnsubscribeFromCurrentAbilityStatusChanges");

        private static bool _loggedFailure;

        [HarmonyPostfix]
        private static void Postfix(ClassAbilityDisplay __instance,
                                    ClassAbility __0, bool __1)
        {
            try
            {
                if (!Plugin.PatchesApplied) return;
                if (StartedF == null || !(bool)StartedF.GetValue(__instance)) return;
                if (!__1) return;   // deselects: vanilla handles ours via the fields we set

                if (__0 is EctypeRoleAbility)
                {
                    RoleRecord rec = AbilityWiring.FindByAbility(__0);
                    if (rec == null) return;

                    VisualElement container = ContainerF.GetValue(__instance) as VisualElement;
                    if (container == null) return;

                    VisualElement ve = EnsureElement(__instance, container, rec);
                    if (ve == null) return;

                    CurrentF.SetValue(__instance, __0);
                    VeF.SetValue(__instance, ve);
                    PerkVeF.SetValue(__instance, ve.Q<PerkBuffItemVE>());

                    bool interacting = InteractingF != null &&
                                       (bool)InteractingF.GetValue(__instance);
                    container.style.display =
                        interacting ? DisplayStyle.None : DisplayStyle.Flex;

                    // The display is single-slot: the vanilla body hides the element it is
                    // replacing, but only when it recognises the incoming ability, so a
                    // switch into a framework ability leaves the previous role's element
                    // visible and stacked over ours. Hide every sibling instead of trusting
                    // which one was last shown.
                    for (int i = 0; i < container.childCount; i++)
                    {
                        VisualElement sibling = container[i];
                        if (!ReferenceEquals(sibling, ve))
                            sibling.style.display = DisplayStyle.None;
                    }
                    ve.style.display = DisplayStyle.Flex;
                    if (Plugin.Verbose) LogSlot(container, ve, rec);
                }

                // Both branches: exactly one registration of the status handlers.
                VisualElement current = VeF.GetValue(__instance) as VisualElement;
                if (current != null && UnsubscribeM != null)
                {
                    UnsubscribeM.Invoke(__instance, null);
                    SubscribeStatusHandlers(__instance);
                }
            }
            catch (Exception e)
            {
                if (!_loggedFailure)
                {
                    _loggedFailure = true;
                    Plugin.Log.LogError("Ectype: HUD ability-slot wiring failed " +
                                        "(reported once; modded roles run without a " +
                                        "HUD slot): " + e);
                }
            }
        }

        // ------------------------------------------------------------------ element build

        private static VisualElement EnsureElement(ClassAbilityDisplay display,
                                                   VisualElement container, RoleRecord rec)
        {
            string name = "Ectype_" + rec.Ordinal;
            VisualElement existing = container.Q(name);
            if (existing != null) return existing;

            UIDocument doc = display.GetComponent<UIDocument>();
            if (doc == null || doc.visualTreeAsset == null) return null;

            // Pristine template, never the live tree: a live donor carries runtime state.
            TemplateContainer fresh = doc.visualTreeAsset.Instantiate();
            VisualElement donorContainer = fresh.Q("ClassAbilityContainer");
            VisualElement donor = donorContainer != null
                ? donorContainer.Q("Scavenger") : null;
            if (donor == null)
            {
                Plugin.Log.LogWarning("Ectype: HUD document has no " +
                                      "ClassAbilityContainer/Scavenger element - HUD " +
                                      "slot skipped (game update?).");
                return null;
            }

            donor.RemoveFromHierarchy();
            donor.name = name;
            donor.style.display = DisplayStyle.None;

            // Add BEFORE painting it. PerkBuffItemVE builds its own child tree (Icon,
            // FillMask/Fill, IconFilled, GlowContainer) when it attaches to a panel, so a
            // detached clone has none of those names yet and every Q on them comes back
            // null - the write then lands nowhere and the donor's authored glyph survives.
            container.Add(donor);
            UssPalette.EnsureInstalled(container);

            // Attachment can still be a frame away, so paint now and once more on attach
            // if the children are not there yet.
            if (!Paint(donor, rec))
            {
                EventCallback<AttachToPanelEvent> once = null;
                once = evt =>
                {
                    donor.UnregisterCallback(once);
                    Paint(donor, rec);
                };
                donor.RegisterCallback(once);
            }
            return donor;
        }

        /// <summary>
        /// The role's glyph and accent onto a slot element. False when the element's
        /// children do not exist yet, which is the caller's cue to try again on attach.
        ///
        /// Accent goes on the glow here and on the fill only while the slot is ready
        /// (HudAbilityBar syncs that against the game's perk-ready class): a ready button
        /// shows the fill over the whole plate, and the stock fill is white while
        /// recovering. Tinting Icon/IconFilled/Frame would recolor the glyph or border.
        /// </summary>
        private static bool Paint(VisualElement donor, RoleRecord rec)
        {
            if (donor.Q("Icon") == null) return false;

            Sprite icon = IconStore.Get(rec.SourceAssembly,
                rec.Manifest.hero != null ? rec.Manifest.hero.icon : null);
            if (icon != null)
            {
                SetBackground(donor, "Icon", icon);
                SetBackground(donor, "IconFilled", icon);
            }
            Color accent;
            System.Runtime.CompilerServices.StrongBox<Color> already;
            if (!string.IsNullOrEmpty(rec.Manifest.color) &&
                ColorUtility.TryParseHtmlString(rec.Manifest.color, out accent) &&
                !HudAbilityBar.SlotAccents.TryGetValue(donor, out already))
            {
                Tint(donor, "GlowContainer", accent);
                HudAbilityBar.SlotAccents.Add(donor, new System.Runtime.CompilerServices.StrongBox<Color>(accent));
            }
            return true;
        }

        private static void SetBackground(VisualElement root, string name, Sprite sprite)
        {
            VisualElement e = root.Q(name);
            if (e == null)
            {
                Plugin.Log.LogWarning("Ectype: HUD ability element has no '" + name +
                                      "' child - that layer keeps the donor's glyph " +
                                      "(game update renamed it?).");
                return;
            }
            e.style.backgroundImage = new StyleBackground(sprite);
        }

        /// <summary>One line naming what the slot ended up holding: which element is
        /// visible, and whether the role's own glyph reached the icon layers. A HUD icon
        /// cannot be read back from here, so this reports what was written.</summary>
        private static void LogSlot(VisualElement container, VisualElement ve, RoleRecord rec)
        {
            if (rec.SlotLogged) return;
            rec.SlotLogged = true;
            string heroIcon = rec.Manifest.hero != null ? rec.Manifest.hero.icon : null;
            bool haveGlyph = IconStore.Get(rec.SourceAssembly, heroIcon) != null;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("Ectype: HUD slot '").Append(ve.name).Append("' for '")
              .Append(rec.Manifest.roleId).Append("': glyph ")
              .Append(haveGlyph ? "'" + heroIcon + "'" : "MISSING (donor or placeholder shows)")
              .Append(", siblings [");
            for (int i = 0; i < container.childCount; i++)
            {
                if (i > 0) sb.Append(' ');
                VisualElement c = container[i];
                sb.Append(c.name).Append('=')
                  .Append(c.resolvedStyle.display == DisplayStyle.Flex ? "shown" : "hidden");
            }
            sb.Append(']');
            Plugin.Log.LogInfo(sb.ToString());
        }

        private static void Tint(VisualElement root, string name, Color c)
        {
            VisualElement e = root.Q(name);
            if (e != null) e.style.unityBackgroundImageTintColor = c;
        }

        // ------------------------------------------------------- status resubscription

        private static void SubscribeStatusHandlers(ClassAbilityDisplay display)
        {
            object bus = CG.Game.ClientGame.Current != null
                ? CG.Game.ClientGame.Current.ModelEventBus : null;
            if (bus == null) return;
            Subscribe(bus, "OnLocalPlayerClassAbilityUsableChanged", display,
                      "OnLocalPlayerClassAbilityUsableChanged");
            Subscribe(bus, "OnLocalPlayerUsedClassAbility", display,
                      "OnLocalPlayerUsedClassAbility");
            Subscribe(bus, "OnLocalPlayerClassAbilityEnteredCooldown", display,
                      "OnLocalPlayerClassAbilityEnteredCooldown");
        }

        /// <summary>Subscribe a private handler method to a model-bus event object whose
        /// concrete delegate type we discover from its Subscribe parameter.</summary>
        private static void Subscribe(object bus, string eventName,
                                      object target, string methodName)
        {
            object ev = Traverse.Create(bus).Field(eventName).GetValue()
                     ?? Traverse.Create(bus).Property(eventName).GetValue();
            if (ev == null) return;
            MethodInfo sub = ev.GetType().GetMethod("Subscribe");
            MethodInfo handler = AccessTools.Method(target.GetType(), methodName);
            if (sub == null || handler == null) return;
            Type delegateType = sub.GetParameters()[0].ParameterType;
            Delegate d = Delegate.CreateDelegate(delegateType, target, handler, false);
            // Second parameter is a null-checked AutoUnsubscribeCollection the game's own
            // subscribers here pass null for; reflection has no optional arguments.
            if (d != null) sub.Invoke(ev, new object[] { d, null });
        }
    }
}
