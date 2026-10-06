using System;
using System.Collections.Generic;
using System.Reflection;

using BepInEx.Logging;
using HarmonyLib;

// Global namespace and `internal`, same reasoning as ModFlagPolicy: compiled into each mod that
// opts in from its csproj, so each assembly gets its own private copy and a mod's
// `Refl.X` call resolves here without a using.

/// <summary>
/// Reflection helpers for game members reached by name, so a rename in a game update degrades
/// one feature instead of throwing into game code.
///
/// Families, which differ on purpose:
///   Field / GetField / SetField / Property / Prop       silent; SetField can still throw on a
///                                                        type-mismatched value
///   SetFieldLogged                                       warns on a miss, never throws
///   Must / MustGet                                       throws on a miss - for a member the mod
///                                                        cannot do without
///   GetMember / GetStaticMember / CallMethod             AccessTools-based, swallow everything
///   Get / Set / GetStatic / Call / CallStatic            cached, never AccessTools (its misses
///                                                        log a warning), Call* warn on a miss
///   CachedField / CachedMethod / GetValue / SetValue /   cached including misses; a miss warns
///   InvokeNoArgs                                         once per session
///
/// No helper takes a struct by reference: a helper taking object boxes a copy and the write is
/// silently lost. Box a struct once at the call site, write through the box, and unbox back.
/// </summary>
internal static class Refl
{
    private const BindingFlags ALL =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public const BindingFlags ALL_INSTANCE =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private const BindingFlags DECLARED =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly string Tag = typeof(Refl).Assembly.GetName().Name;
    private static ManualLogSource _log;

    private static ManualLogSource Log
    {
        get
        {
            if (_log == null) _log = BepInEx.Logging.Logger.CreateLogSource(Tag);
            return _log;
        }
    }

    // ---- Silent base-chain walk ------------------------------------------------------------

    public static FieldInfo Field(Type t, string name)
    {
        while (t != null)
        {
            FieldInfo f = t.GetField(name, ALL);
            if (f != null) return f;
            t = t.BaseType;
        }
        return null;
    }

    public static PropertyInfo Property(Type t, string name)
    {
        while (t != null)
        {
            PropertyInfo p = t.GetProperty(name, ALL);
            if (p != null) return p;
            t = t.BaseType;
        }
        return null;
    }

    public static PropertyInfo Prop(Type t, string name) { return Property(t, name); }

    public static object GetField(object target, string name)
    {
        if (target == null) return null;
        FieldInfo f = Field(target.GetType(), name);
        return f == null ? null : f.GetValue(target);
    }

    public static T GetField<T>(object target, string name, T fallback)
    {
        try
        {
            object v = GetField(target, name);
            return v is T ? (T)v : fallback;
        }
        catch { return fallback; }
    }

    /// <summary>Swallows a throwing getter and returns null.</summary>
    public static object GetProperty(object target, string name)
    {
        if (target == null) return null;
        PropertyInfo p = Property(target.GetType(), name);
        if (p == null || !p.CanRead) return null;
        try { return p.GetValue(target, null); }
        catch { return null; }
    }

    /// <summary>Unlike <see cref="GetProperty"/>, lets a throwing getter's exception through.</summary>
    public static object GetProp(object target, string name)
    {
        if (target == null) return null;
        PropertyInfo p = Property(target.GetType(), name);
        if (p == null || !p.CanRead) return null;
        return p.GetValue(target, null);
    }

    /// <summary>Silent on a miss; a value of the wrong type throws out of SetValue.</summary>
    public static bool SetField(object target, string name, object value)
    {
        if (target == null) return false;
        FieldInfo f = Field(target.GetType(), name);
        if (f == null) return false;
        f.SetValue(target, value);
        return true;
    }

    /// <summary>Warns on a miss or a failed write and never throws.</summary>
    public static bool SetFieldLogged(object target, string name, object value)
    {
        if (target == null) return false;
        try
        {
            FieldInfo f = AccessTools.Field(target.GetType(), name);
            if (f == null)
            {
                Log.LogWarning(Tag + ": field '" + name + "' not found on " +
                               target.GetType().Name + " (game update?)");
                return false;
            }
            f.SetValue(target, value);
            return true;
        }
        catch (Exception e)
        {
            Log.LogWarning(Tag + ": could not set " + target.GetType().Name + "." + name +
                           ": " + e.Message);
            return false;
        }
    }

    // ---- Must-exist ------------------------------------------------------------------------

    /// <summary>
    /// Write a field the mod cannot do without; throws on a miss so the caller's registration
    /// try reports failure instead of shipping a half-built object. A field typed as a private
    /// or nested-private enum accepts its underlying integer.
    /// </summary>
    public static void Must(object target, string name, object value)
    {
        if (target == null)
            throw new InvalidOperationException("cannot set '" + name + "' on a null target");
        FieldInfo f = Field(target.GetType(), name);
        if (f == null)
            throw new MissingFieldException(target.GetType().Name + "." + name +
                " no longer exists - the authored classifier chain cannot be built against " +
                "this build of the game");
        if (value != null && f.FieldType.IsEnum && !f.FieldType.IsInstanceOfType(value))
            value = Enum.ToObject(f.FieldType, value);
        f.SetValue(target, value);
    }

    /// <summary>Read a field the mod cannot do without. Same contract as <see cref="Must"/>.</summary>
    public static object MustGet(object target, string name)
    {
        if (target == null)
            throw new InvalidOperationException("cannot read '" + name + "' from a null target");
        FieldInfo f = Field(target.GetType(), name);
        if (f == null)
            throw new MissingFieldException(target.GetType().Name + "." + name +
                " no longer exists");
        return f.GetValue(target);
    }

    // ---- AccessTools-based, swallow everything ---------------------------------------------

    /// <summary>Property first, then field, walking the base chain. Null on any failure.</summary>
    public static object GetMember(object target, string name)
    {
        if (target == null) return null;
        try
        {
            Type t = target.GetType();
            PropertyInfo p = AccessTools.Property(t, name);
            if (p != null && p.CanRead) return p.GetValue(target, null);
            FieldInfo f = AccessTools.Field(t, name);
            if (f != null) return f.GetValue(target);
        }
        catch { }
        return null;
    }

    /// <summary>Static property or field on a type, e.g. a container's singleton Instance.</summary>
    public static object GetStaticMember(Type t, string name)
    {
        if (t == null) return null;
        try
        {
            PropertyInfo p = t.GetProperty(name,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
            if (p != null && p.CanRead) return p.GetValue(null, null);
            FieldInfo f = t.GetField(name,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
            if (f != null) return f.GetValue(null);

            // A static declared on a generic base is not always reachable through
            // FlattenHierarchy on the closed subclass.
            Type b = t.BaseType;
            while (b != null)
            {
                p = b.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.CanRead) return p.GetValue(null, null);
                f = b.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) return f.GetValue(null);
                b = b.BaseType;
            }
        }
        catch { }
        return null;
    }

    public static object CallMethod(object target, string name, params object[] args)
    {
        if (target == null) return null;
        try
        {
            MethodInfo m = AccessTools.Method(target.GetType(), name);
            if (m == null) return null;
            return m.Invoke(target, args);
        }
        catch { }
        return null;
    }

    /// <summary>Instance fields of a type and all its bases, deduped, in declaration order.</summary>
    public static List<FieldInfo> InstanceFields(Type t)
    {
        List<FieldInfo> result = new List<FieldInfo>();
        HashSet<string> seen = new HashSet<string>();
        Type cur = t;
        while (cur != null && cur != typeof(object))
        {
            FieldInfo[] fields;
            try { fields = cur.GetFields(ALL_INSTANCE | BindingFlags.DeclaredOnly); }
            catch { break; }
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = fields[i];
                if (f.IsStatic) continue;
                string key = cur.Name + "." + f.Name;
                if (seen.Add(key)) result.Add(f);
            }
            cur = cur.BaseType;
        }
        return result;
    }

    /// <summary>Reference identity, so a visited set never calls a user Equals.</summary>
    public sealed class RefComparer : IEqualityComparer<object>
    {
        public static readonly RefComparer Instance = new RefComparer();
        bool IEqualityComparer<object>.Equals(object a, object b) { return ReferenceEquals(a, b); }
        public int GetHashCode(object o)
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
    }

    // ---- Cached, silent, property-before-field ----------------------------------------------

    private static readonly Dictionary<string, MemberInfo> _members = new Dictionary<string, MemberInfo>();
    private static readonly Dictionary<string, MethodInfo> _methods = new Dictionary<string, MethodInfo>();

    /// <summary>Property first (so auto-property backing fields resolve), then field, walking
    /// the base chain. Cached, including misses.</summary>
    private static MemberInfo Member(Type t, string name)
    {
        if (t == null || string.IsNullOrEmpty(name)) return null;
        string key = t.FullName + "|" + name;
        MemberInfo cached;
        if (_members.TryGetValue(key, out cached)) return cached;

        MemberInfo found = null;
        Type cur = t;
        int guard = 0;
        while (cur != null && cur != typeof(object) && guard++ < 32)
        {
            try
            {
                PropertyInfo p = cur.GetProperty(name, DECLARED);
                if (p != null) { found = p; break; }
                FieldInfo f = cur.GetField(name, DECLARED);
                if (f != null) { found = f; break; }
            }
            catch { }
            cur = cur.BaseType;
        }

        _members[key] = found;
        return found;
    }

    public static object Get(object target, string name)
    {
        if (target == null) return null;
        try
        {
            MemberInfo m = Member(target.GetType(), name);
            PropertyInfo p = m as PropertyInfo;
            if (p != null && p.CanRead) return p.GetValue(target, null);
            FieldInfo f = m as FieldInfo;
            if (f != null) return f.GetValue(target);
        }
        catch { }
        return null;
    }

    public static bool Set(object target, string name, object value)
    {
        if (target == null) return false;
        try
        {
            MemberInfo m = Member(target.GetType(), name);
            FieldInfo f = m as FieldInfo;
            if (f != null) { f.SetValue(target, value); return true; }
            PropertyInfo p = m as PropertyInfo;
            if (p != null && p.CanWrite) { p.SetValue(target, value, null); return true; }
        }
        catch { }
        return false;
    }

    public static object GetStatic(Type t, string name)
    {
        if (t == null) return null;
        try
        {
            BindingFlags flags = BindingFlags.Static | BindingFlags.Public |
                                 BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            Type cur = t;
            int guard = 0;
            while (cur != null && guard++ < 32)
            {
                PropertyInfo p = cur.GetProperty(name, flags);
                if (p != null && p.CanRead) return p.GetValue(null, null);
                FieldInfo f = cur.GetField(name, flags);
                if (f != null) return f.GetValue(null);
                cur = cur.BaseType;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Explicit parameter types, because the members worth calling are often overloaded and
    /// picking by name alone is a coin flip. Null <paramref name="paramTypes"/> matches by name.
    /// </summary>
    public static object Call(object target, string name, Type[] paramTypes, params object[] args)
    {
        if (target == null) return null;
        try
        {
            MethodInfo m = Method(target.GetType(), name, paramTypes);
            if (m == null)
            {
                Log.LogWarning("Refl: no method " + target.GetType().Name + "." + name +
                               " with " + (paramTypes == null ? 0 : paramTypes.Length) +
                               " args (game update?).");
                return null;
            }
            return m.Invoke(target, args);
        }
        catch (TargetInvocationException tie)
        {
            Log.LogWarning("Refl.Call " + name + " threw: " + tie.InnerException);
        }
        catch (Exception e)
        {
            Log.LogWarning("Refl.Call " + name + " failed: " + e.Message);
        }
        return null;
    }

    /// <summary>Static equivalent of Call.</summary>
    public static object CallStatic(Type type, string name, Type[] paramTypes, params object[] args)
    {
        if (type == null) return null;
        try
        {
            BindingFlags flags = BindingFlags.Static | BindingFlags.Public |
                                 BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            MethodInfo m = null;
            Type cur = type;
            int guard = 0;
            while (cur != null && guard++ < 32)
            {
                m = paramTypes == null
                    ? cur.GetMethod(name, flags)
                    : cur.GetMethod(name, flags, null, paramTypes, null);
                if (m != null) break;
                cur = cur.BaseType;
            }
            if (m == null)
            {
                Log.LogWarning("Refl: no static method " + type.Name + "." + name +
                               " with the expected signature.");
                return null;
            }
            return m.Invoke(null, args);
        }
        catch (TargetInvocationException tie)
        {
            Log.LogWarning("Refl.CallStatic " + name + " threw: " + tie.InnerException);
        }
        catch (Exception e)
        {
            Log.LogWarning("Refl.CallStatic " + name + " failed: " + e.Message);
        }
        return null;
    }

    private static MethodInfo Method(Type t, string name, Type[] paramTypes)
    {
        string key = t.FullName + "|" + name + "|" + Sig(paramTypes);
        MethodInfo cached;
        if (_methods.TryGetValue(key, out cached)) return cached;

        MethodInfo found = null;
        Type cur = t;
        int guard = 0;
        while (cur != null && cur != typeof(object) && guard++ < 32)
        {
            try
            {
                found = paramTypes == null
                    ? cur.GetMethod(name, DECLARED)
                    : cur.GetMethod(name, DECLARED, null, paramTypes, null);
                if (found != null) break;
            }
            catch { }
            cur = cur.BaseType;
        }

        _methods[key] = found;
        return found;
    }

    private static string Sig(Type[] types)
    {
        if (types == null) return "*";
        string s = "";
        for (int i = 0; i < types.Length; i++) s += (i > 0 ? "," : "") + types[i].Name;
        return s;
    }

    // ---- Cached FieldInfo/MethodInfo handles, warn once on a miss ---------------------------

    private static readonly Dictionary<string, FieldInfo> _cachedFields = new Dictionary<string, FieldInfo>();
    private static readonly Dictionary<string, MethodInfo> _cachedMethods = new Dictionary<string, MethodInfo>();

    /// <summary>Field on the type or a base, instance or static. A miss is cached and warned
    /// about once, so a member gone in a game update costs one lookup per session.</summary>
    public static FieldInfo CachedField(Type type, string name)
    {
        if (type == null || string.IsNullOrEmpty(name)) return null;

        string key = type.FullName + "::" + name;
        FieldInfo cached;
        if (_cachedFields.TryGetValue(key, out cached)) return cached;

        FieldInfo found = null;
        for (Type t = type; t != null && found == null; t = t.BaseType)
            found = t.GetField(name, ALL);

        _cachedFields[key] = found;
        if (found == null)
            Log.LogWarning(Tag + ": field " + key + " not found; the feature " +
                           "using it is off this session.");
        return found;
    }

    /// <summary>No-argument method on the type or a base. Same caching as <see cref="CachedField"/>.</summary>
    public static MethodInfo CachedMethod(Type type, string name)
    {
        if (type == null || string.IsNullOrEmpty(name)) return null;

        string key = type.FullName + "::" + name + "()";
        MethodInfo cached;
        if (_cachedMethods.TryGetValue(key, out cached)) return cached;

        MethodInfo found = null;
        for (Type t = type; t != null && found == null; t = t.BaseType)
            found = t.GetMethod(name, ALL, null, Type.EmptyTypes, null);

        _cachedMethods[key] = found;
        if (found == null)
            Log.LogWarning(Tag + ": method " + key + " not found; the feature " +
                           "using it is off this session.");
        return found;
    }

    /// <summary>Read a field, returning <paramref name="fallback"/> on any failure.</summary>
    public static T GetValue<T>(FieldInfo field, object target, T fallback)
    {
        try
        {
            if (field == null || target == null) return fallback;
            object v = field.GetValue(target);
            return v is T ? (T)v : fallback;
        }
        catch { return fallback; }
    }

    /// <summary>Write a field. Returns false on any failure rather than throwing.</summary>
    public static bool SetValue(FieldInfo field, object target, object value)
    {
        try
        {
            if (field == null || target == null) return false;
            field.SetValue(target, value);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Invoke a no-argument method. Returns false on any failure.</summary>
    public static bool InvokeNoArgs(MethodInfo method, object target)
    {
        try
        {
            if (method == null || target == null) return false;
            method.Invoke(target, null);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Short one-line description of an exception for log lines that must stay compact.</summary>
    public static string Brief(Exception e)
    {
        return e == null ? "<null>" : e.GetType().Name + ": " + e.Message;
    }
}
