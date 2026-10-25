using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

using CG.Client.UserData;
using CG.Profile;
using CG.Ship.Object;
using Client.Player.Interactions;
using Gameplay.Mutators;
using Gameplay.Quests;
using Gameplay.Utilities;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using ResourceAssets;
using UnityEngine;

namespace Ectype
{
    /// <summary>How a session-stat mutator effect composes with the stat.</summary>
    public enum SessionStatModifierKind
    {
        /// <summary>Added to the stat's base (the game's PrimaryAddend).</summary>
        Flat,
        /// <summary>A fraction added to the multiplier: 0.25 is +25% (AdditiveMultiplier). Percent
        /// effects on one stat add to each other before they multiply the base.</summary>
        Percent,
        /// <summary>A multiplier: 1.5 is x1.5 (ExponentialMultiplier with an amount of
        /// factor - 1). Factor effects on one stat multiply each other.</summary>
        Factor,
    }

    /// <summary>One modifier a mutator puts on a session stat.</summary>
    public sealed class SessionStatEffect
    {
        /// <summary>The stat: <c>"owner/Name"</c> for a stat a stats file declares,
        /// <c>"ectype/Name"</c> or a bare name for a framework stat. It must live in the session
        /// collection.</summary>
        public string Stat;

        /// <summary>How <see cref="Amount"/> composes.</summary>
        public SessionStatModifierKind Kind;

        /// <summary>The addend, the fraction or the factor. Finite; a factor is above 0.</summary>
        public float Amount;
    }

    /// <summary>A mutator the framework registers in the game's Mutators menu, whose rule is a
    /// list of session-stat modifiers.</summary>
    public sealed class MutatorSpec
    {
        /// <summary>Namespace of <see cref="Key"/>, conventionally the registering plugin's GUID
        /// (a role's id for a manifest mutator). Part of the derived GUID.</summary>
        public string Owner;

        /// <summary>Stable key within the owner: letters, digits, <c>_</c>, <c>.</c> and
        /// <c>-</c>, starting with a letter or digit, at most 64 characters. Part of the derived
        /// GUID: renaming it derives a new mutator, and a saved run that used the old one loses
        /// it.</summary>
        public string Key;

        /// <summary>Optional explicit GUID, 32 hex digits (dashes allowed). Absent: derived from
        /// <c>"ectype:mutator:" + Owner + "/" + Key</c>.</summary>
        public string Guid;

        /// <summary>The menu card's title.</summary>
        public string DisplayName;

        /// <summary>The menu card's body. Rich text is allowed.</summary>
        public string Description;

        /// <summary>The menu card's icon. Optional.</summary>
        public Sprite Icon;

        /// <summary>A registered mutator's GUID whose icon this one borrows when
        /// <see cref="Icon"/> is null. Optional; the framework's placeholder otherwise.</summary>
        public string IconDonor;

        /// <summary>1 to <see cref="SessionStatMutators.MaxEffects"/> modifiers.</summary>
        public List<SessionStatEffect> Effects = new List<SessionStatEffect>();
    }

    /// <summary>A registration made with <see cref="SessionStatMutators.Register"/>.</summary>
    public sealed class MutatorHandle
    {
        internal MutatorHandle(string owner, string key)
        {
            Owner = owner;
            Key = key;
        }

        /// <summary>The spec's owner.</summary>
        public string Owner { get; private set; }

        /// <summary>The spec's key.</summary>
        public string Key { get; private set; }

        /// <summary>The mutator's GUID; null GUID when the spec was refused before one was
        /// known.</summary>
        public GUIDUnion Guid { get; internal set; }

        /// <summary>Pending, Registered or Failed.</summary>
        public ContentState State { get; internal set; }

        /// <summary>Why it is Failed, or what the last failed attempt hit while Pending.</summary>
        public string Reason { get; internal set; }

        /// <summary>The registered asset, or null before registration.</summary>
        public Mutator Asset { get { return Rec != null ? Rec.Mutator : null; } }

        internal MutatorRecord Rec;
    }

    /// <summary>One modifier as the effect carries it: the stat already resolved to its id.</summary>
    [Serializable]
    public sealed class SessionStatModifier
    {
        /// <summary>The stat's name as the spec gave it, for logs.</summary>
        public string Stat;

        /// <summary>The stat's id in the session collection.</summary>
        public int StatId;

        /// <summary>The game's modifier type.</summary>
        public ModifierType Type;

        /// <summary>The game's modifier amount (a factor f is f - 1).</summary>
        public float Amount;
    }

    /// <summary>
    /// A mutator effect whose rule is a list of session-stat modifiers. It sits in a Mutator
    /// asset's <c>Effects</c> list like any of the game's own effects.
    ///
    /// The game activates a mutator after the host has published the session's stats, so the
    /// effect does not contribute on activation. The framework reads which mutators the session
    /// will activate from the session's own inputs while the host opens the session collection,
    /// and contributes then; see <see cref="SessionStatMutators"/>. Activation only catches a
    /// mutator that arrived some other way.
    /// </summary>
    [Serializable]
    public sealed class SessionStatMutatorEffect : MutatorEffectSession
    {
        /// <summary>The modifiers, applied with the mutator as their source.</summary>
        public List<SessionStatModifier> Modifiers = new List<SessionStatModifier>();

        /// <summary>Host: contributes now when the session's stats were published without this
        /// mutator.</summary>
        public override void OnMutatorActivated()
        {
            SessionStatMutators.Activated(Mutator, this);
        }

        /// <summary>Nothing: the session collection is dropped with the session.</summary>
        public override void OnMutatorDeactivated() { }

        /// <summary>Nothing: the session collection is dropped with the session.</summary>
        public override void OnSessionEnd() { }
    }

    /// <summary>The framework's state for one registered mutator.</summary>
    internal sealed class MutatorRecord
    {
        internal MutatorSpec Spec;
        internal GUIDUnion Guid;
        internal string Name;
        internal Mutator Mutator;
        internal SessionStatMutatorEffect Effect;
        internal MutatorDef Def;
        internal UnlockItemDef UnlockDef;
        internal ContextInfo Context;
        internal readonly List<MutatorHandle> Handles = new List<MutatorHandle>();
        internal string Stage = "queued";
        internal int Attempts;
        internal string LastLogged;
        internal bool Verified;
        internal bool Claimed;

        // Shared-unlock republish: observed, never latched.
        internal bool SharedSeen;
        internal int SharedAttempts;
        internal bool SharedQuiet;
        internal bool IndexWarned;
    }

    /// <summary>
    /// Mutators whose rule is a set of session-stat modifiers: registered in the game's own
    /// Mutators menu, selectable there, forceable through a quest's <c>ForcedMutators</c>, and
    /// contributing to the session collection so every client reads the result.
    ///
    /// The host contributes while it opens the session collection, before the values are
    /// published. At that point the game has activated no mutator yet: it activates a quest's
    /// forced mutators while it generates the quest, and the menu-selected ones only after it has
    /// sent the session to the room. So the framework reads the set the game is about to
    /// activate from the session's own inputs, the same inputs and the same rules the game's
    /// activation uses: the quest asset's forced list, then the session's starting list, skipping
    /// a GUID already taken, one the quest's whitelist leaves out, and one this client cannot
    /// resolve. After the game has activated the starting list, the host checks the prediction
    /// against the game's own active set and logs any difference.
    /// </summary>
    public static class SessionStatMutators
    {
        /// <summary>The most effects one mutator carries.</summary>
        public const int MaxEffects = 16;

        /// <summary>The seed prefix of a derived mutator GUID.</summary>
        public const string GuidSeedPrefix = "ectype:mutator:";

        internal const int MaxAttempts = 200;
        internal const int SharedUnlockAttemptLimit = 10;
        internal const string SharedMutatorKey = "P_SM";

        private static readonly Regex KeyPattern = new Regex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}\\z");

        private static readonly Dictionary<GUIDUnion, MutatorRecord> _byGuid =
            new Dictionary<GUIDUnion, MutatorRecord>();
        private static readonly Dictionary<string, MutatorHandle> _refused =
            new Dictionary<string, MutatorHandle>(StringComparer.Ordinal);
        private static readonly HashSet<GUIDUnion> _unlocked = new HashSet<GUIDUnion>();
        private static readonly HashSet<RoleRecord> _rolesRead = new HashSet<RoleRecord>();

        // The session the host predicted for, and what it contributed.
        private static GameSession _predictedFor;
        private static readonly HashSet<GUIDUnion> _contributed = new HashSet<GUIDUnion>();
        private static readonly HashSet<SessionStatMutatorEffect> _lateDone = new HashSet<SessionStatMutatorEffect>();

        private static float _nextPoll;
        private static float _nextVerify;

        /// <summary>Counts for the F5 page.</summary>
        public static string Status
        {
            get
            {
                int r = 0, p = 0, f = _refused.Count;
                foreach (MutatorRecord rec in _byGuid.Values)
                {
                    if (rec.Verified) r++;
                    else if (rec.Attempts >= MaxAttempts) f++;
                    else p++;
                }
                return r + " mutator(s) registered, " + p + " pending, " + f + " refused";
            }
        }

        // ------------------------------------------------------------------ registration API

        /// <summary>The GUID a spec without an explicit one gets: 32 hex digits of
        /// MD5(UTF-8 <c>"ectype:mutator:" + owner + "/" + key</c>).</summary>
        public static string DeriveGuid(string owner, string key)
        {
            return Guids.Derive(GuidSeedPrefix + owner + "/" + key);
        }

        /// <summary>
        /// Registers a mutator. Returns a handle that is Pending until the game's containers
        /// exist and the stats files have been read, then Registered; or Failed, with a
        /// <see cref="MutatorHandle.Reason"/>, when the spec is refused. Registering the same
        /// owner and key again returns the first registration's handle; another spec under an
        /// already registered GUID is refused. The asset is registered with the game's Required
        /// effect, so a player without the registering mod cannot join a room that has it.
        /// </summary>
        public static MutatorHandle Register(MutatorSpec spec)
        {
            string owner = spec != null ? spec.Owner : null;
            string key = spec != null ? spec.Key : null;
            MutatorHandle h = new MutatorHandle(owner, key);
            string refusalKey = (owner ?? "") + "/" + (key ?? "");
            try
            {
                MutatorHandle known;
                if (_refused.TryGetValue(refusalKey, out known)) return known;

                string why = Validate(spec);
                if (why != null) return Refuse(h, refusalKey, why);
                if (EctypeConfig.Enabled != null && !EctypeConfig.Enabled.Value)
                    return Refuse(h, refusalKey, "the framework is disabled ([General] Enabled = false)");
                if (EctypeConfig.Enabled != null && !Plugin.PatchesApplied)
                    return Refuse(h, refusalKey, "the framework is inert this session (patching did not complete)");

                GUIDUnion guid = new GUIDUnion(GuidOf(spec));
                h.Guid = guid;
                MutatorRecord rec;
                if (_byGuid.TryGetValue(guid, out rec))
                {
                    if (rec.Spec.Owner == spec.Owner && rec.Spec.Key == spec.Key)
                    {
                        h.Rec = rec;
                        rec.Handles.Add(h);
                        Sync(rec);
                        return h;
                    }
                    return Refuse(h, refusalKey, "GUID " + guid.AsHex() + " is already registered by '" +
                                                 rec.Spec.Owner + "/" + rec.Spec.Key + "'");
                }

                rec = new MutatorRecord { Spec = Copy(spec), Guid = guid, Name = spec.DisplayName };
                rec.Handles.Add(h);
                h.Rec = rec;
                h.State = ContentState.Pending;
                _byGuid[guid] = rec;
                if (ReadyToRegister()) Attempt(rec);
                return h;
            }
            catch (Exception e)
            {
                return Refuse(h, refusalKey, "Register threw: " + e);
            }
        }

        /// <summary>True when the session in progress (not the hub) has the mutator active, on
        /// any client: the game fills its active set identically everywhere from the session
        /// data.</summary>
        public static bool IsActive(GUIDUnion guid)
        {
            GameSession s = GameSessionManager.ActiveSession;
            if (s == null || s.IsHub) return false;
            foreach (Mutator m in s.ActiveMutators)
                if (m != null && m.ContainerGuid.Equals(guid)) return true;
            return false;
        }

        // ------------------------------------------------------------------ validation

        /// <summary>The checks a spec must pass when it is registered, in this order: owner,
        /// key, GUID, name, effects. Null when they pass, else the reason. Whether each stat is a
        /// session stat is checked once the stats files have been read.</summary>
        internal static string Validate(MutatorSpec spec)
        {
            if (spec == null) return "no spec";
            if (string.IsNullOrEmpty(spec.Owner) || spec.Owner.Trim() != spec.Owner || spec.Owner.IndexOf('/') >= 0 ||
                spec.Owner.Length > 128)
                return "the owner must be 1 to 128 characters with no '/' and no surrounding whitespace";
            if (spec.Key == null || !KeyPattern.IsMatch(spec.Key))
                return "the key '" + spec.Key + "' must be letters, digits, '_', '.' and '-', starting with a " +
                       "letter or digit, at most 64 characters";
            if (spec.Guid != null && NormalizeGuid(spec.Guid) == null)
                return "the guid '" + spec.Guid + "' is not 32 hex digits (dashes allowed)";
            if (string.IsNullOrEmpty(spec.DisplayName) || spec.DisplayName.Trim().Length == 0)
                return "no display name";
            if (spec.IconDonor != null && NormalizeGuid(spec.IconDonor) == null)
                return "the iconDonor '" + spec.IconDonor + "' is not 32 hex digits (dashes allowed)";
            if (spec.Effects == null || spec.Effects.Count == 0) return "no effects";
            if (spec.Effects.Count > MaxEffects)
                return spec.Effects.Count + " effects, more than the " + MaxEffects + " a mutator carries";
            for (int i = 0; i < spec.Effects.Count; i++)
            {
                string p = EffectProblem(spec.Effects[i]);
                if (p != null) return "effect #" + i + ": " + p;
            }
            return null;
        }

        private static string EffectProblem(SessionStatEffect e)
        {
            if (e == null) return "null";
            if (string.IsNullOrEmpty(e.Stat)) return "no stat";
            if (e.Kind != SessionStatModifierKind.Flat && e.Kind != SessionStatModifierKind.Percent &&
                e.Kind != SessionStatModifierKind.Factor)
                return "unknown kind " + (int)e.Kind;
            if (float.IsNaN(e.Amount) || float.IsInfinity(e.Amount)) return "the amount is not a finite number";
            if (e.Kind == SessionStatModifierKind.Factor && !(e.Amount > 0f))
                return "a factor is above 0 (1 changes nothing; " +
                       e.Amount.ToString("R", CultureInfo.InvariantCulture) + " was given)";
            return null;
        }

        /// <summary>The session stat id <paramref name="name"/> names, or the reason it does not
        /// name one. Needs the stats files read.</summary>
        internal static string ResolveSessionStat(string name, out int id)
        {
            id = 0;
            bool found;
            int slash = name.IndexOf('/');
            if (slash < 0) found = FrameworkStats.TryGet(name, out id);
            else found = ModStats.TryGetDeclaredId(name, out id);
            if (!found || id == 0)
                return "stat '" + name + "' is not declared (name a stats file's stat as \"owner/Name\")";
            if (ModStats.IsSessionStat(id)) return null;
            ModStatDecl decl;
            string where = ModStats.TryGetDecl(id, out decl) ? ModStats.KindName(decl.Collection) : "perk";
            return "stat '" + name + "' is a " + where + " stat; a mutator contributes to session stats only";
        }

        /// <summary>The game's modifier for one effect.</summary>
        internal static SessionStatModifier ToModifier(SessionStatEffect e, int statId)
        {
            SessionStatModifier m = new SessionStatModifier { Stat = e.Stat, StatId = statId };
            switch (e.Kind)
            {
                case SessionStatModifierKind.Flat:
                    m.Type = ModifierType.PrimaryAddend;
                    m.Amount = e.Amount;
                    break;
                case SessionStatModifierKind.Percent:
                    m.Type = ModifierType.AdditiveMultiplier;
                    m.Amount = e.Amount;
                    break;
                default:
                    m.Type = ModifierType.ExponentialMultiplier;
                    m.Amount = e.Amount - 1f;
                    break;
            }
            return m;
        }

        /// <summary>32 lower-case hex digits, or null.</summary>
        internal static string NormalizeGuid(string s)
        {
            if (s == null) return null;
            string n = s.Trim().Replace("-", "").ToLowerInvariant();
            if (n.Length != 32) return null;
            foreach (char c in n)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return null;
            return n;
        }

        internal static string GuidOf(MutatorSpec spec)
        {
            return spec.Guid != null ? NormalizeGuid(spec.Guid) : DeriveGuid(spec.Owner, spec.Key);
        }

        private static MutatorSpec Copy(MutatorSpec s)
        {
            MutatorSpec c = new MutatorSpec
            {
                Owner = s.Owner, Key = s.Key, Guid = s.Guid, DisplayName = s.DisplayName,
                Description = s.Description, Icon = s.Icon, IconDonor = s.IconDonor,
            };
            foreach (SessionStatEffect e in s.Effects)
                c.Effects.Add(new SessionStatEffect { Stat = e.Stat, Kind = e.Kind, Amount = e.Amount });
            return c;
        }

        private static MutatorHandle Refuse(MutatorHandle h, string refusalKey, string why)
        {
            h.State = ContentState.Failed;
            h.Reason = why;
            _refused[refusalKey] = h;
            Plugin.Log.LogError("Ectype: mutators: '" + refusalKey + "' refused: " + why + ".");
            return h;
        }

        // ------------------------------------------------------------------ registration walk

        private static bool ReadyToRegister()
        {
            try
            {
                return Plugin.PatchesApplied && PluginLoad.Finished && ModStats.Discovered &&
                       ResourceAssetContainerRegister.Instance != null &&
                       RuntimeAssetsRegister.Instance != null &&
                       MutatorContainer.Instance != null &&
                       UnlockContainer.Instance != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Registers what is pending. Called from the registration pass after the stats files
        /// and the role manifests have been read. Pending records go in GUID order, so one set
        /// of mods registers in one order on every client.
        /// </summary>
        internal static void Pump()
        {
            ReadManifests();
            if (!ReadyToRegister()) return;
            List<MutatorRecord> pending = new List<MutatorRecord>();
            foreach (MutatorRecord rec in _byGuid.Values)
                if (!rec.Verified && rec.Attempts < MaxAttempts) pending.Add(rec);
            pending.Sort((a, b) => string.CompareOrdinal(a.Guid.AsHex(), b.Guid.AsHex()));
            foreach (MutatorRecord rec in pending) Attempt(rec);
        }

        private sealed class Refusal : Exception
        {
            internal Refusal(string message) : base(message) { }
        }

        private static void Attempt(MutatorRecord rec)
        {
            rec.Attempts++;
            try
            {
                Walk(rec);
                string problem = Verify(rec);
                if (problem != null) throw new Exception("verification failed: " + problem);
                rec.Verified = true;
                rec.LastLogged = null;
                _unlocked.Add(rec.Guid);
                Sync(rec);
                Plugin.Log.LogInfo("Ectype: mutators: '" + rec.Name + "' registered (" + rec.Guid.AsHex() +
                                   ", effect " + ModFlagPolicy.RequiredEffect + "): " + Describe(rec.Effect) + ".");
            }
            catch (Refusal r)
            {
                Fail(rec, r.Message, true);
            }
            catch (Exception e)
            {
                bool final = rec.Attempts >= MaxAttempts;
                Fail(rec, "attempt " + rec.Attempts + " failed at [" + rec.Stage + "]: " + e.Message, final);
            }
        }

        private static void Fail(MutatorRecord rec, string why, bool final)
        {
            rec.Verified = false;
            _unlocked.Remove(rec.Guid);
            foreach (MutatorHandle h in rec.Handles)
            {
                h.State = final ? ContentState.Failed : ContentState.Pending;
                h.Reason = why;
            }
            if (final)
            {
                rec.Attempts = MaxAttempts;
                Plugin.Log.LogError("Ectype: mutators: '" + rec.Spec.Owner + "/" + rec.Spec.Key + "' refused: " + why + ".");
                return;
            }
            if (rec.LastLogged == why) return;
            rec.LastLogged = why;
            Plugin.Log.LogWarning("Ectype: mutators: '" + rec.Name + "' registration " + why +
                                  " (retrying; repeats of this message are not logged).");
        }

        private static void Sync(MutatorRecord rec)
        {
            foreach (MutatorHandle h in rec.Handles)
            {
                if (rec.Verified)
                {
                    h.State = ContentState.Registered;
                    h.Reason = null;
                }
            }
        }

        /// <summary>
        /// The walk, every step guarded on the container already holding the GUID: the Mutator
        /// asset with its effect, its card, its def, the runtime-asset entry (Required effect),
        /// the container entry, the def in the container's authored list in GUID order, the
        /// unlock entry, and the authored order mirrored into the container's runtime list.
        /// </summary>
        private static void Walk(MutatorRecord rec)
        {
            MutatorSpec spec = rec.Spec;

            rec.Stage = "effects";
            if (rec.Effect == null)
            {
                SessionStatMutatorEffect effect = new SessionStatMutatorEffect();
                foreach (SessionStatEffect e in spec.Effects)
                {
                    int id;
                    string problem = ResolveSessionStat(e.Stat, out id);
                    if (problem != null) throw new Refusal(problem);
                    effect.Modifiers.Add(ToModifier(e, id));
                }
                rec.Effect = effect;
            }

            rec.Stage = "mutator asset";
            if (rec.Mutator == null)
            {
                Mutator m = ScriptableObject.CreateInstance<Mutator>();
                m.name = "Ectype_Mutator_" + spec.Owner + "_" + spec.Key;
                EctypeAssets.Keep(m);
                rec.Mutator = m;
            }
            rec.Mutator.ContainerGuid = rec.Guid;
            if (rec.Mutator.Effects == null) rec.Mutator.Effects = new List<MutatorEffect>();
            if (!rec.Mutator.Effects.Contains(rec.Effect)) rec.Mutator.Effects.Add(rec.Effect);

            rec.Stage = "card";
            if (rec.Context == null)
            {
                rec.Context = ContextInfo.Create(spec.Icon ?? IconFrom(spec.IconDonor, rec.Name) ?? IconStore.Placeholder(false),
                                                 spec.DisplayName, spec.Description ?? "");
                EctypeAssets.Keep(rec.Context);
            }

            rec.Stage = "def";
            if (rec.Def == null)
            {
                MutatorDef def = new MutatorDef();
                def.Ref = new ResourceAssetRef(rec.Guid, "") { IsRuntime = true };
                def.ContextInfo = rec.Context;
                rec.Def = def;
            }

            rec.Stage = "runtime asset";
            RuntimeAssetsRegister reg = RuntimeAssetsRegister.Instance;
            if (!reg.HasAsset(rec.Guid))
                reg.RegisterAsset(rec.Guid, rec.Mutator, ModFlagPolicy.RequiredEffect,
                                  new RuntimeAssetInfo { Name = rec.Mutator.name, DisplayName = spec.DisplayName });

            rec.Stage = "mutator container";
            MutatorContainer container = MutatorContainer.Instance;
            MutatorDef held;
            if (!container.TryGetByGuid(rec.Guid, out held)) container.RegisterRuntimeAsset(rec.Guid, rec.Def);
            else if (!ReferenceEquals(held, rec.Def))
            {
                // Before this registration first held the GUID, another entry is someone else's;
                // after, it is a stale copy a container rebuild left, and is replaced.
                if (!rec.Claimed)
                    throw new Refusal("GUID " + rec.Guid.AsHex() + " is already in the mutator container, put " +
                                      "there by something other than this registration");
                container.UpdateRuntimeAsset(rec.Guid, rec.Def);
            }
            rec.Claimed = true;
            List<MutatorDef> authored = Refl.GetField<List<MutatorDef>>(container, "AssetDescriptions", null);
            if (authored == null) throw new Exception("the mutator container has no authored list");
            ListByGuid(authored, rec.Def, d => d.Ref);
            AlignRuntimeList(container, authored);

            rec.Stage = "unlock";
            UnlockContainer unlocks = UnlockContainer.Instance;
            if (rec.UnlockDef == null)
            {
                UnlockItemDef u = new UnlockItemDef();
                u.Ref = new ResourceAssetRef(rec.Guid, "") { IsRuntime = true };
                u.UnlockOptions = new UnlockOptions { UnlockCriteria = UnlockCriteriaType.Manual };
                u.rarity = RarityType.Epic;
                rec.UnlockDef = u;
            }
            UnlockItemDef heldUnlock;
            if (!unlocks.TryGetByGuid(rec.Guid, out heldUnlock)) unlocks.RegisterRuntimeAsset(rec.Guid, rec.UnlockDef);
            List<UnlockItemDef> unlockList = Refl.GetField<List<UnlockItemDef>>(unlocks, "AssetDescriptions", null);
            if (unlockList != null) ListByGuid(unlockList, rec.UnlockDef, d => d.Ref);
            rec.Stage = "verify";
        }

        /// <summary>
        /// Lists a def in the authored list in GUID order, or replaces a stale def with the same
        /// GUID in place, so no other entry's index moves and the list never holds a GUID twice.
        /// </summary>
        private static void ListByGuid<T>(List<T> list, T def, Func<T, ResourceAssetRef> refOf)
            where T : class, IResourceAsset
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == null || !list[i].AssetGuid.Equals(def.AssetGuid)) continue;
                if (!ReferenceEquals(list[i], def)) list[i] = def;
                return;
            }
            AssetDescriptionOrder.InsertDef(list, def, refOf);
        }

        /// <summary>
        /// Puts the container's runtime list in the authored list's order, followed by any entry
        /// registered without being listed. The shared mutator unlocks name an entry by its index
        /// in the authored list and read it back by index in the runtime list, so the two must
        /// agree; an insert into the middle of the authored list would otherwise shift one
        /// against the other.
        /// </summary>
        internal static void AlignRuntimeList(MutatorContainer container, List<MutatorDef> authored)
        {
            List<MutatorDef> runtime = container.RuntimeDescriptions;
            if (runtime == null) return;
            bool aligned = runtime.Count >= authored.Count;
            for (int i = 0; aligned && i < authored.Count; i++)
                if (!ReferenceEquals(runtime[i], authored[i])) aligned = false;
            if (aligned) return;
            HashSet<MutatorDef> listed = new HashSet<MutatorDef>(authored);
            List<MutatorDef> extra = new List<MutatorDef>();
            foreach (MutatorDef d in runtime)
                if (d != null && !listed.Contains(d)) extra.Add(d);
            HashSet<MutatorDef> present = new HashSet<MutatorDef>(runtime);
            runtime.Clear();
            foreach (MutatorDef d in authored)
                if (d != null && present.Contains(d)) runtime.Add(d);
            runtime.AddRange(extra);
        }

        private static Sprite IconFrom(string donor, string name)
        {
            string hex = NormalizeGuid(donor);
            if (hex == null) return null;
            try
            {
                MutatorDef d;
                if (MutatorContainer.Instance.TryGetByGuid(new GUIDUnion(hex), out d) && d != null)
                {
                    ContextInfo ctx = d.ContextInfo as ContextInfo;
                    if (ctx != null && ctx.Icon != null) return ctx.Icon;
                }
                Plugin.Log.LogWarning("Ectype: mutators: '" + name + "' names iconDonor " + hex + ", which " +
                                      "resolves to no mutator icon; the placeholder is used.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: mutators: '" + name + "' iconDonor lookup failed (" + e.Message + ").");
            }
            return null;
        }

        /// <summary>Walks back what the game will dereference. Null when it holds, else the
        /// first problem.</summary>
        private static string Verify(MutatorRecord rec)
        {
            MutatorContainer container = MutatorContainer.Instance;
            MutatorDef def;
            if (!container.TryGetByGuid(rec.Guid, out def) || def == null) return "the mutator container does not resolve the GUID";
            Mutator asset = def.Asset;
            if (asset == null) return "the def's asset does not resolve (runtime-asset entry or the runtime flag)";
            if (asset.Effects == null) return "the mutator's Effects is null, which the game's activation iterates";
            if (!asset.Effects.Contains(rec.Effect)) return "the mutator does not carry its session-stat effect";
            if (!asset.ContainerGuid.Equals(rec.Guid)) return "the asset carries another ContainerGuid";
            if (def.ContextInfo == null || string.IsNullOrEmpty(def.ContextInfo.HeaderText)) return "the card has no title";
            if (((IResourceAssetContainer)container).GetAssetIndex(rec.Guid) < 0)
                return "the def is not in the container's authored list, which the Mutators menu lists";
            if (!UnlockContainer.Instance.HasItem(rec.Guid))
                return "no unlock entry, which the Mutators menu reads for every row";
            return null;
        }

        /// <summary>Re-verifies every registered mutator and re-registers one a container rebuild
        /// dropped. Runs on a slow cadence and as the first thing a session start does.</summary>
        internal static void Reverify()
        {
            if (_byGuid.Count == 0 || !ReadyToRegister()) return;
            foreach (MutatorRecord rec in _byGuid.Values)
            {
                if (!rec.Verified) continue;
                string problem;
                try { problem = Verify(rec); }
                catch (Exception e) { problem = "verification threw: " + e.Message; }
                if (problem == null) continue;
                rec.Verified = false;
                rec.Attempts = 0;
                _unlocked.Remove(rec.Guid);
                Plugin.Log.LogWarning("Ectype: mutators: '" + rec.Name + "' no longer verifies (" + problem +
                                      "); re-registering.");
                Attempt(rec);
            }
        }

        private static string Describe(SessionStatMutatorEffect effect)
        {
            List<string> parts = new List<string>();
            foreach (SessionStatModifier m in effect.Modifiers) parts.Add(Describe(m));
            return string.Join(", ", parts.ToArray());
        }

        internal static string Describe(SessionStatModifier m)
        {
            string amount;
            switch (m.Type)
            {
                case ModifierType.ExponentialMultiplier:
                    amount = "x" + (1f + m.Amount).ToString("0.###", CultureInfo.InvariantCulture);
                    break;
                case ModifierType.AdditiveMultiplier:
                    amount = (m.Amount >= 0 ? "+" : "") + (m.Amount * 100f).ToString("0.###", CultureInfo.InvariantCulture) + "%";
                    break;
                default:
                    amount = (m.Amount >= 0 ? "+" : "") + m.Amount.ToString("0.###", CultureInfo.InvariantCulture);
                    break;
            }
            return m.Stat + " " + amount;
        }

        // ------------------------------------------------------------------ manifests

        /// <summary>Registers each role manifest's mutators once, with the role's id as the
        /// owner.</summary>
        private static void ReadManifests()
        {
            if (!RoleRegistry.Discovered) return;
            foreach (RoleRecord role in RoleRegistry.Roles)
            {
                if (!_rolesRead.Add(role)) continue;
                List<MutatorManifestSpec> list = role.Manifest.mutators;
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    string why;
                    MutatorSpec spec = FromManifest(role.Manifest.roleId, list[i], role.SourceAssembly, out why);
                    if (spec == null)
                    {
                        string key = list[i] != null && list[i].key != null ? list[i].key : "#" + i;
                        Refuse(new MutatorHandle(role.Manifest.roleId, key), role.Manifest.roleId + "/" + key, why);
                        continue;
                    }
                    Register(spec);
                }
            }
        }

        /// <summary>A manifest entry as a spec, or null and the reason. Checks only what the
        /// manifest form adds: the kind strings and the amount's presence.</summary>
        internal static MutatorSpec FromManifest(string roleId, MutatorManifestSpec m, Assembly asm, out string why)
        {
            why = null;
            if (m == null) { why = "null mutator entry"; return null; }
            MutatorSpec spec = new MutatorSpec
            {
                Owner = roleId, Key = m.key, Guid = m.guid, DisplayName = m.name, Description = m.description,
                Icon = asm != null && !string.IsNullOrEmpty(m.icon) ? IconStore.Get(asm, m.icon) : null,
                IconDonor = m.iconDonor,
            };
            if (m.effects == null) { why = "no effects"; return null; }
            for (int i = 0; i < m.effects.Count; i++)
            {
                MutatorEffectManifestSpec e = m.effects[i];
                if (e == null) { why = "effect #" + i + ": null"; return null; }
                SessionStatModifierKind kind;
                if (!TryParseKind(e.kind, out kind))
                {
                    why = "effect #" + i + ": unknown kind '" + e.kind + "' (flat, percent or factor)";
                    return null;
                }
                if (!e.amount.HasValue) { why = "effect #" + i + ": no amount"; return null; }
                spec.Effects.Add(new SessionStatEffect { Stat = e.stat, Kind = kind, Amount = e.amount.Value });
            }
            return spec;
        }

        internal static bool TryParseKind(string s, out SessionStatModifierKind kind)
        {
            switch (s)
            {
                case "flat": kind = SessionStatModifierKind.Flat; return true;
                case "percent": kind = SessionStatModifierKind.Percent; return true;
                case "factor": kind = SessionStatModifierKind.Factor; return true;
                default: kind = SessionStatModifierKind.Flat; return false;
            }
        }

        // ------------------------------------------------------------------ session

        /// <summary>The inputs the game's activation reads, taken from one session.</summary>
        internal sealed class ActivationInputs
        {
            internal bool IsHub;
            internal List<GUIDUnion> Forced = new List<GUIDUnion>();
            internal List<GUIDUnion> Starting = new List<GUIDUnion>();
            internal bool UseWhitelist;
            internal List<GUIDUnion> Whitelist = new List<GUIDUnion>();
        }

#if ECTYPE_DEV
        /// <summary>Dev self-test: the inputs <see cref="ContributeAtOpening"/> reads in place of
        /// the active session's, and the resolver it uses in place of the container.</summary>
        internal static ActivationInputs InputsForTest = null;
        internal static Func<GUIDUnion, Mutator> ResolveForTest = null;
#endif

        /// <summary>
        /// Host, inside the session collection's opening and before the session's own
        /// <see cref="SessionStats.Opening"/> subscribers: contributes the effects of every
        /// mutator the session is about to activate.
        /// </summary>
        internal static void ContributeAtOpening()
        {
            GameSession session = null;
            ActivationInputs inputs = null;
            Func<GUIDUnion, Mutator> resolve = Resolve;
#if ECTYPE_DEV
            if (InputsForTest != null)
            {
                inputs = InputsForTest;
                if (ResolveForTest != null) resolve = ResolveForTest;
            }
#endif
            if (inputs == null)
            {
                session = GameSessionManager.ActiveSession;
                if (session == null) return;
                inputs = InputsOf(session);
            }
            _predictedFor = session;
            _contributed.Clear();
            _lateDone.Clear();

            List<Mutator> active = PredictActive(inputs, resolve);
            List<string> names = new List<string>();
            int modifiers = 0;
            foreach (Mutator m in active)
            {
                int n = Contribute(m, null);
                if (n < 0) continue;
                _contributed.Add(m.ContainerGuid);
                modifiers += n;
                names.Add(NameOf(m));
            }
            if (names.Count > 0)
                Plugin.Log.LogInfo("Ectype: mutators: " + string.Join(", ", names.ToArray()) + " contributed " +
                                   modifiers + " modifier(s) to the session stats.");
        }

        /// <summary>The inputs of <paramref name="session"/>. The quest asset is the one the
        /// game's quest generation reads: the session's own, which a resumed run sets from the
        /// save.</summary>
        internal static ActivationInputs InputsOf(GameSession session)
        {
            ActivationInputs inputs = new ActivationInputs { IsHub = session.IsHub };
            if (session.StartingMutators != null) inputs.Starting.AddRange(session.StartingMutators);
            if (session.IsHub) return inputs;
            QuestAsset quest = null;
            try
            {
                if (session.SessionQuestAsset != null) quest = session.SessionQuestAsset.Asset;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: mutators: the session's quest asset did not resolve (" + e.Message +
                                      "); a quest's forced mutators are not read this session.");
            }
            if (quest == null) return inputs;
            if (quest.ForcedMutators != null)
                foreach (MutatorRef r in quest.ForcedMutators)
                    if (r != null) inputs.Forced.Add(r.AssetGuid);
            inputs.UseWhitelist = quest.UseMutatorWhitelist;
            if (quest.MutatorWhitelist != null)
                foreach (MutatorRef r in quest.MutatorWhitelist)
                    if (r != null) inputs.Whitelist.Add(r.AssetGuid);
            return inputs;
        }

        /// <summary>
        /// The mutators the game will activate, in its order: the quest's forced list, then the
        /// starting list, skipping a GUID already taken, one the quest's whitelist leaves out when
        /// the quest uses one, and one that does not resolve. The hub has no quest: only its
        /// starting list counts.
        /// </summary>
        internal static List<Mutator> PredictActive(ActivationInputs inputs, Func<GUIDUnion, Mutator> resolve)
        {
            List<Mutator> result = new List<Mutator>();
            HashSet<GUIDUnion> taken = new HashSet<GUIDUnion>();
            if (!inputs.IsHub)
                foreach (GUIDUnion g in inputs.Forced)
                {
                    if (!taken.Add(g)) continue;
                    Mutator m = resolve(g);
                    if (m != null) result.Add(m);
                }
            foreach (GUIDUnion g in inputs.Starting)
            {
                if (!inputs.IsHub && inputs.UseWhitelist && !inputs.Whitelist.Contains(g)) continue;
                if (taken.Contains(g)) continue;
                Mutator m = resolve(g);
                if (m == null) continue;
                taken.Add(g);
                result.Add(m);
            }
            return result;
        }

        private static Mutator Resolve(GUIDUnion guid)
        {
            try
            {
                MutatorContainer c = MutatorContainer.Instance;
                MutatorDef def;
                if (c == null || !c.TryGetByGuid(guid, out def) || def == null) return null;
                return def.Asset;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Contributes the session-stat effects <paramref name="m"/> carries (only
        /// <paramref name="only"/> when given). The number of modifiers applied, or -1 when it
        /// carries none.</summary>
        private static int Contribute(Mutator m, SessionStatMutatorEffect only)
        {
            if (m == null || m.Effects == null) return -1;
            int applied = 0;
            bool any = false;
            foreach (MutatorEffect e in m.Effects)
            {
                SessionStatMutatorEffect s = e as SessionStatMutatorEffect;
                if (s == null || (only != null && !ReferenceEquals(s, only))) continue;
                any = true;
                foreach (SessionStatModifier mod in s.Modifiers)
                {
                    if (mod == null) continue;
                    if (SessionStats.Contribute(mod.StatId, new FloatModifier(mod.Amount, mod.Type), m)) applied++;
                }
            }
            return any ? applied : -1;
        }

        /// <summary>Host: the game activated a mutator carrying <paramref name="effect"/>. One the
        /// session's stats were published with needs nothing; any other contributes now, to the
        /// host's collection only.</summary>
        internal static void Activated(Mutator mutator, SessionStatMutatorEffect effect)
        {
            try
            {
                if (mutator == null || effect == null) return;
                if (PhotonNetwork.InRoom && !PhotonNetwork.IsMasterClient) return;
                GameSession session = GameSessionManager.ActiveSession;
                if (session != null && ReferenceEquals(session, _predictedFor) && _contributed.Contains(mutator.ContainerGuid))
                    return;
                if (!_lateDone.Add(effect)) return;
                Plugin.Log.LogWarning("Ectype: mutators: '" + NameOf(mutator) + "' was activated after the session " +
                                      "stats were published; its effects reach the host only, until the next session.");
                Contribute(mutator, effect);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Ectype: mutators: contributing on activation threw: " + e);
            }
        }

        /// <summary>Host, after the game activated the starting list: logs a mutator whose
        /// effects were published and that the game did not activate.</summary>
        private static void AfterInitStartingMutators(GameSession __instance)
        {
            try
            {
                if (__instance == null || !ReferenceEquals(__instance, _predictedFor) || _contributed.Count == 0) return;
                HashSet<GUIDUnion> active = new HashSet<GUIDUnion>();
                foreach (Mutator m in __instance.ActiveMutators)
                    if (m != null) active.Add(m.ContainerGuid);
                foreach (GUIDUnion g in _contributed)
                {
                    if (active.Contains(g)) continue;
                    Mutator m = Resolve(g);
                    Plugin.Log.LogError("Ectype: mutators: '" + (m != null ? NameOf(m) : g.AsHex()) + "' contributed to " +
                                        "the published session stats, but the game did not activate it; every " +
                                        "client reads its effects this session anyway.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Ectype: mutators: checking the activated mutators threw: " + e.Message);
            }
        }

        private static string NameOf(Mutator m)
        {
            MutatorRecord rec;
            if (_byGuid.TryGetValue(m.ContainerGuid, out rec)) return rec.Name;
            try
            {
                MutatorDef def;
                if (MutatorContainer.Instance != null && MutatorContainer.Instance.TryGetByGuid(m.ContainerGuid, out def) &&
                    def != null && def.ContextInfo != null && !string.IsNullOrEmpty(def.ContextInfo.HeaderText))
                    return def.ContextInfo.HeaderText;
            }
            catch { }
            return m.name;
        }

        // ------------------------------------------------------------------ patches and driver

        /// <summary>Patched one by one, so a renamed game member costs only its own hook.</summary>
        internal static void ApplyPatches(Harmony harmony)
        {
            const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo prepare = typeof(GameSessionManager).GetMethod("PrepareNextSession", I, null,
                                                                      new[] { typeof(bool) }, null);
            if (prepare != null)
                harmony.Patch(prepare, prefix: Hook("PrepareNextSessionPrefix"));
            else
                Plugin.Log.LogWarning("Ectype: mutators: GameSessionManager.PrepareNextSession not found (game " +
                                      "update?); a dropped mutator registration is repaired by the 10-second check only.");

            MethodInfo init = typeof(GameSession).GetMethod("InitStartingMutators", I, null, Type.EmptyTypes, null);
            if (init != null) harmony.Patch(init, postfix: Hook("AfterInitStartingMutators"));
            else
                Plugin.Log.LogWarning("Ectype: mutators: GameSession.InitStartingMutators not found (game update?); " +
                                      "the activated mutators are not checked against the published session stats.");

            MethodInfo unlocked = typeof(UnlockContainer).GetMethod("IsUnlocked", I, null, new[] { typeof(GUIDUnion) }, null);
            if (unlocked != null) harmony.Patch(unlocked, postfix: Hook("IsUnlockedPostfix"));
            else
                Plugin.Log.LogWarning("Ectype: mutators: UnlockContainer.IsUnlocked(GUIDUnion) not found (game " +
                                      "update?); framework mutators read as locked in the Mutators menu.");
        }

        private static HarmonyMethod Hook(string name)
        {
            return new HarmonyMethod(typeof(SessionStatMutators).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        }

        /// <summary>The common ancestor of hosting and joining: the host snapshots the required
        /// assets here and a joiner compares them right after, so a dropped registration is
        /// repaired before either.</summary>
        private static void PrepareNextSessionPrefix()
        {
            try { Reverify(); }
            catch (Exception e) { Plugin.Log.LogWarning("Ectype: mutators: re-verifying at session start threw: " + e.Message); }
        }

        private static void IsUnlockedPostfix(GUIDUnion __0, ref bool __result)
        {
            if (!__result && _unlocked.Contains(__0)) __result = true;
        }

        /// <summary>Driver: re-verifies on a slow cadence and keeps every registered mutator in
        /// this client's shared mutator unlocks.</summary>
        internal static void Poll()
        {
            if (_byGuid.Count == 0) return;
            float now = Time.unscaledTime;
            if (now >= _nextVerify)
            {
                _nextVerify = now + 10f;
                Reverify();
            }
            if (now < _nextPoll) return;
            _nextPoll = now + 2f;
            foreach (MutatorRecord rec in _byGuid.Values)
                if (rec.Verified) PublishSharedUnlock(rec);
        }

        /// <summary>
        /// The Mutators menu decides whether a row is selectable from the union, across the room,
        /// of each player's shared mutator unlocks: a player property holding container indices,
        /// written in full from the unlock check when the profile initialises. That snapshot can
        /// precede this registration and is retaken on every profile rebuild, so this reads the
        /// property back and adds the index whenever it is missing. The game's own write is
        /// additive and touches no persistent unlock.
        /// </summary>
        private static void PublishSharedUnlock(MutatorRecord rec)
        {
            try
            {
                Player local = PhotonNetwork.LocalPlayer;
                if (local == null || local.CustomProperties == null) return;
                // Absent until the profile has published its own snapshot; the game's write
                // publishes nothing before then.
                if (!local.CustomProperties.ContainsKey(SharedMutatorKey)) return;

                int index = ((IResourceAssetContainer)MutatorContainer.Instance).GetAssetIndex(rec.Guid);
                if (index < 0) return;
                if (index > byte.MaxValue)
                {
                    if (!rec.IndexWarned)
                    {
                        rec.IndexWarned = true;
                        Plugin.Log.LogWarning("Ectype: mutators: '" + rec.Name + "' sits at index " + index + " of the " +
                                              "mutator list, past the 255 the game's shared unlocks can name; it shows " +
                                              "locked in the Mutators menu.");
                    }
                    return;
                }
                List<byte> indices = PhotonPlayerUnlockSync.GetSharedUnlockIndices(local, SharedMutatorKey);
                if (indices.Contains((byte)index))
                {
                    rec.SharedSeen = true;
                    rec.SharedAttempts = 0;
                    rec.SharedQuiet = false;
                    return;
                }
                rec.SharedSeen = false;
                if (rec.SharedAttempts >= SharedUnlockAttemptLimit) return;
                PhotonPlayerUnlockSync sync = FindUnlockSync();
                if (sync == null)
                {
                    SharedTrouble(rec, "the profile's shared-unlock link is not reachable");
                    return;
                }
                sync.SetSharedUnlock(MutatorContainer.Instance, SharedMutatorKey, rec.Guid);
                SharedTrouble(rec, null);
            }
            catch (Exception e)
            {
                SharedTrouble(rec, "the write threw: " + e.Message);
            }
        }

        private static void SharedTrouble(MutatorRecord rec, string why)
        {
            rec.SharedAttempts++;
            if (rec.SharedQuiet || rec.SharedAttempts < SharedUnlockAttemptLimit) return;
            rec.SharedQuiet = true;
            Plugin.Log.LogWarning("Ectype: mutators: '" + rec.Name + "' is still missing from this client's shared " +
                                  "mutator unlocks after " + rec.SharedAttempts + " attempts" +
                                  (why != null ? " (" + why + ")" : "") + "; it may show locked in the Mutators menu.");
        }

        /// <summary>The link of the profile's unlock chain that owns the shared-unlock property.
        /// Bounded walk.</summary>
        private static PhotonPlayerUnlockSync FindUnlockSync()
        {
            object node = PlayerProfile.Instance != null ? PlayerProfile.Instance.UnlockedItems : null;
            for (int depth = 0; depth < 8 && node != null; depth++)
            {
                PhotonPlayerUnlockSync sync = node as PhotonPlayerUnlockSync;
                if (sync != null) return sync;
                if (!(node is ChainOfResponsibility<ICategorisedCollection>)) return null;
                node = Refl.GetProperty(node, "source");
            }
            return null;
        }
    }

    /// <summary>A role manifest's <c>mutators</c> entry.</summary>
    public class MutatorManifestSpec
    {
        /// <summary>Stable key within the role. Part of the derived GUID.</summary>
        public string key;

        /// <summary>Optional explicit GUID (32 hex digits, dashes allowed).</summary>
        public string guid;

        /// <summary>The menu card's title.</summary>
        public string name;

        /// <summary>The menu card's body.</summary>
        public string description;

        /// <summary>Icon key: an embedded PNG resource in the role's assembly.</summary>
        public string icon;

        /// <summary>A registered mutator's GUID whose icon this one borrows.</summary>
        public string iconDonor;

        /// <summary>The modifiers.</summary>
        public List<MutatorEffectManifestSpec> effects;
    }

    /// <summary>One modifier of a manifest mutator.</summary>
    public class MutatorEffectManifestSpec
    {
        /// <summary>A session stat: <c>"owner/Name"</c>, or a framework stat's name.</summary>
        public string stat;

        /// <summary><c>"flat"</c>, <c>"percent"</c> or <c>"factor"</c>.</summary>
        public string kind;

        /// <summary>The addend, the fraction or the factor.</summary>
        public float? amount;
    }
}
