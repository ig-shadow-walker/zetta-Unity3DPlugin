using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zetta3D.Editor.Agent
{
    /// <summary>A tool failure with a one-line reason the model can act on.</summary>
    public class ToolException : Exception
    {
        public ToolException(string message) : base(message)
        {
        }
    }

    /// <summary>Reading tool inputs leniently (numbers as strings, strings as numbers) with clear errors.</summary>
    static class Args
    {
        public static bool Present(JObject o, string key) =>
            o != null && o.TryGetValue(key, out var t) && t.Type != JTokenType.Null && t.Type != JTokenType.Undefined;

        /// <summary>The key is there and explicitly null (e.g. <c>parent: null</c> = move to the scene root).</summary>
        public static bool IsNull(JObject o, string key) =>
            o != null && o.TryGetValue(key, out var t) && t.Type == JTokenType.Null;

        public static JToken Get(JObject o, string key) => Present(o, key) ? o[key] : null;

        public static string Str(JObject o, string key, string fallback = null)
        {
            var t = Get(o, key);
            if (t == null) return fallback;
            return t.Type == JTokenType.String ? (string)t : t.ToString(Formatting.None);
        }

        public static string Required(JObject o, string key)
        {
            var s = Str(o, key);
            if (string.IsNullOrWhiteSpace(s)) throw new ToolException($"'{key}' is required.");
            return s.Trim();
        }

        public static int? IntN(JObject o, string key) => ToInt(Get(o, key));

        public static int? ToInt(JToken t)
        {
            if (t == null) return null;
            switch (t.Type)
            {
                case JTokenType.Integer:
                    var l = (long)t;
                    return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, l));
                case JTokenType.Float:
                    return (int)Math.Round((double)t);
                case JTokenType.Boolean:
                    return (bool)t ? 1 : 0;
                case JTokenType.String:
                    return int.TryParse((string)t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : (int?)null;
                default:
                    return null;
            }
        }

        public static int Int(JObject o, string key, int fallback, int min = int.MinValue, int max = int.MaxValue)
        {
            var v = IntN(o, key) ?? fallback;
            return Math.Max(min, Math.Min(max, v));
        }

        public static long? LongN(JObject o, string key)
        {
            var t = Get(o, key);
            if (t == null) return null;
            if (t.Type == JTokenType.Integer) return (long)t;
            if (t.Type == JTokenType.Float) return (long)Math.Round((double)t);
            if (t.Type == JTokenType.String && long.TryParse(((string)t).Trim().TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
            return null;
        }

        public static bool? ToBool(JToken t)
        {
            if (t == null) return null;
            switch (t.Type)
            {
                case JTokenType.Boolean: return (bool)t;
                case JTokenType.Integer: return (long)t != 0;
                case JTokenType.Float: return Math.Abs((double)t) > double.Epsilon;
                case JTokenType.String:
                    var s = ((string)t).Trim().ToLowerInvariant();
                    if (s is "true" or "yes" or "on" or "1") return true;
                    if (s is "false" or "no" or "off" or "0") return false;
                    return null;
                default: return null;
            }
        }

        public static bool Bool(JObject o, string key, bool fallback) => ToBool(Get(o, key)) ?? fallback;

        public static bool? BoolN(JObject o, string key) => ToBool(Get(o, key));

        public static float? ToFloat(JToken t)
        {
            if (t == null) return null;
            switch (t.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return (float)(double)t;
                case JTokenType.Boolean:
                    return (bool)t ? 1f : 0f;
                case JTokenType.String:
                    return float.TryParse((string)t, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : (float?)null;
                default:
                    return null;
            }
        }

        /// <summary>Numbers of a vector: <c>[x,y,z]</c>, <c>{x,y,z}</c>, or <c>"x,y,z"</c>.</summary>
        public static float[] Floats(JToken t)
        {
            if (t == null) return null;
            if (t is JArray a)
            {
                var values = a.Select(ToFloat).ToArray();
                return values.Any(v => v == null) ? null : values.Select(v => v.Value).ToArray();
            }
            if (t is JObject o)
            {
                var keys = new[] { "x", "y", "z", "w" };
                var list = new List<float>();
                foreach (var k in keys)
                {
                    var v = ToFloat(o[k]);
                    if (v == null) break;
                    list.Add(v.Value);
                }
                return list.Count > 0 ? list.ToArray() : null;
            }
            if (t.Type == JTokenType.String)
            {
                var parts = ((string)t).Trim('[', ']', '(', ')', ' ').Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var values = parts.Select(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : (float?)null).ToArray();
                return values.Length == 0 || values.Any(v => v == null) ? null : values.Select(v => v.Value).ToArray();
            }
            var single = ToFloat(t);
            return single != null ? new[] { single.Value } : null;
        }

        public static Vector3? Vec3(JObject o, string key)
        {
            var t = Get(o, key);
            if (t == null) return null;
            var f = Floats(t);
            if (f == null || f.Length != 3)
            {
                if (f != null && f.Length == 1) return Vector3.one * f[0];
                throw new ToolException($"'{key}' must be [x, y, z].");
            }
            return new Vector3(f[0], f[1], f[2]);
        }

        public static List<string> StrList(JObject o, string key)
        {
            var t = Get(o, key);
            if (t == null) return null;
            if (t is JArray a) return a.Where(x => x.Type != JTokenType.Null).Select(x => x.Type == JTokenType.String ? (string)x : x.ToString(Formatting.None)).ToList();
            return new List<string> { t.Type == JTokenType.String ? (string)t : t.ToString(Formatting.None) };
        }
    }

    /// <summary>Compact, culture-invariant formatting for model-facing text.</summary>
    static class Fmt
    {
        public static readonly CultureInfo C = CultureInfo.InvariantCulture;

        public static string F(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return v.ToString(C);
            var r = Math.Round(v, 4);
            if (Math.Abs(r) < 1e-9) r = 0;
            return r.ToString("0.####", C);
        }

        public static string F(double v) => F((float)v);

        public static string V(Vector2 v) => $"[{F(v.x)}, {F(v.y)}]";
        public static string V(Vector3 v) => $"[{F(v.x)}, {F(v.y)}, {F(v.z)}]";
        public static string V(Vector4 v) => $"[{F(v.x)}, {F(v.y)}, {F(v.z)}, {F(v.w)}]";

        public static string Color(Color c) => "#" + ColorUtility.ToHtmlStringRGBA(c).ToLowerInvariant();

        /// <summary>One line, at most <paramref name="max"/> characters.</summary>
        public static string Short(string s, int max = 120)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", "").Replace("\n", " ⏎ ").Replace("\t", " ");
            return s.Length <= max ? s : s.Substring(0, Math.Max(1, max - 1)) + "…";
        }

        public static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var i = s.IndexOfAny(new[] { '\r', '\n' });
            return i < 0 ? s : s.Substring(0, i);
        }

        public static string Plural(int n, string one, string many = null) => n == 1 ? $"1 {one}" : $"{n} {many ?? one + "s"}";

        public static string Quote(string s) => JsonConvert.ToString(s ?? "");
    }

    /// <summary>
    /// Instance ids for the protocol's <c>"#1234"</c> targets, in one place: Unity 6.3+ replaces
    /// instance ids with EntityId (the old calls are compile errors in 6.6).
    /// </summary>
    static class Ids
    {
#if UNITY_6000_3_OR_NEWER
        public static long Of(Object o) => o == null ? 0 : (long)EntityId.ToULong(o.GetEntityId());

        public static Object Find(long id) => id == 0 ? null : EditorUtility.EntityIdToObject(EntityId.FromULong((ulong)id));

        public static bool IsMissingReference(SerializedProperty p) => p.objectReferenceValue == null && p.objectReferenceEntityIdValue.IsValid();
#else
        public static long Of(Object o) => o == null ? 0 : o.GetInstanceID();

        public static Object Find(long id) => id < int.MinValue || id > int.MaxValue ? null : EditorUtility.InstanceIDToObject((int)id);

        public static bool IsMissingReference(SerializedProperty p) => p.objectReferenceValue == null && p.objectReferenceInstanceIDValue != 0;
#endif
    }
}
