using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// Reads and writes serialized fields by property path, the way the Inspector does, so every
    /// change goes through Undo and prefab overrides. Accepts C#-style names ("mass" → "m_Mass").
    /// </summary>
    static class PropertyWriter
    {
        public class Outcome
        {
            public readonly List<string> Applied = new List<string>();
            public readonly List<string> Errors = new List<string>();

            public string Describe()
            {
                var sb = new StringBuilder();
                if (Applied.Count > 0) sb.Append("Set: ").Append(string.Join("; ", Applied)).Append('.');
                if (Errors.Count > 0)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append("Not set: ").Append(string.Join("; ", Errors)).Append('.');
                }
                return sb.ToString();
            }
        }

        static readonly Dictionary<string, string> TransformAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["position"] = "m_LocalPosition",
            ["localPosition"] = "m_LocalPosition",
            ["rotation"] = "m_LocalRotation",
            ["localRotation"] = "m_LocalRotation",
            ["eulerAngles"] = "m_LocalRotation",
            ["localEulerAngles"] = "m_LocalRotation",
            ["scale"] = "m_LocalScale",
            ["localScale"] = "m_LocalScale",
        };

        /// <summary>C# property names whose serialized names differ (across Unity versions too).</summary>
        static readonly Dictionary<string, string[]> Aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["drag"] = new[] { "m_LinearDamping", "m_Drag" },
            ["linearDamping"] = new[] { "m_LinearDamping", "m_Drag" },
            ["angularDrag"] = new[] { "m_AngularDamping", "m_AngularDrag" },
            ["angularDamping"] = new[] { "m_AngularDamping", "m_AngularDrag" },
            ["interpolation"] = new[] { "m_Interpolate" },
            ["collisionDetectionMode"] = new[] { "m_CollisionDetection" },
            ["shadows"] = new[] { "m_Shadows.m_Type" },
            ["shadowStrength"] = new[] { "m_Shadows.m_Strength" },
            ["sharedMaterials"] = new[] { "m_Materials" },
            ["materials"] = new[] { "m_Materials" },
            ["sharedMaterial"] = new[] { "m_Materials.Array.data[0]", "m_Material" },
            ["material"] = new[] { "m_Materials.Array.data[0]", "m_Material" },
            ["sharedMesh"] = new[] { "m_Mesh" },
            ["mesh"] = new[] { "m_Mesh" },
        };

        /// <summary>Applies every property it can; collects a per-property error for the rest.</summary>
        public static Outcome Apply(Object target, JObject properties)
        {
            var outcome = new Outcome();
            if (properties == null || properties.Count == 0) return outcome;

            var so = new SerializedObject(target);
            var set = new List<(string key, string path)>();
            var listProperties = false;
            foreach (var pair in properties)
            {
                try
                {
                    var prop = Resolve(so, target, pair.Key);
                    if (prop == null)
                    {
                        var close = Close(so, pair.Key);
                        if (close.Count == 0) listProperties = true;
                        outcome.Errors.Add($"{pair.Key}: no such serialized property" + (close.Count > 0 ? $" (did you mean {string.Join(" or ", close)}?)" : ""));
                        continue;
                    }
                    var error = Set(prop, pair.Value);
                    if (error != null) outcome.Errors.Add($"{pair.Key}: {error}");
                    else set.Add((pair.Key, prop.propertyPath));
                }
                catch (ToolException e)
                {
                    outcome.Errors.Add($"{pair.Key}: {e.Message}");
                }
                catch (Exception e)
                {
                    outcome.Errors.Add($"{pair.Key}: {e.GetType().Name}: {Fmt.FirstLine(e.Message)}");
                }
            }
            if (listProperties) outcome.Errors.Add($"{target.GetType().Name} has: {string.Join(", ", VisibleNames(so).Take(40))}");

            so.ApplyModifiedProperties();
            if (PrefabUtility.IsPartOfPrefabInstance(target)) PrefabUtility.RecordPrefabInstancePropertyModifications(target);

            // Report what actually landed (Unity silently rejects some values, e.g. wrong reference types).
            so.Update();
            foreach (var (key, path) in set)
            {
                var prop = so.FindProperty(path);
                var shown = prop == null ? "?" : Format(prop, 1, 200);
                outcome.Applied.Add(path == key ? $"{path} = {shown}" : $"{path} ({key}) = {shown}");
            }
            return outcome;
        }

        static SerializedProperty Resolve(SerializedObject so, Object target, string key)
        {
            if (target is Transform && TransformAliases.TryGetValue(key, out var t)) return so.FindProperty(t);
            var exact = so.FindProperty(key);
            if (exact != null) return exact;
            if (Aliases.TryGetValue(key, out var paths))
                foreach (var path in paths)
                {
                    var alias = Find(so, path);
                    if (alias != null) return alias;
                }
            return Find(so, key);
        }

        // ─── Finding properties ──────────────────────────────────────────────────

        static readonly Regex Indexer = new Regex(@"^(.*)\[(\d+)\]$");

        static string Pascal(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        static IEnumerable<string> Candidates(string name)
        {
            yield return name;
            if (!name.StartsWith("m_")) yield return "m_" + Pascal(name);
            if (name.StartsWith("m_") && name.Length > 2) yield return char.ToLowerInvariant(name[2]) + name.Substring(3);
            yield return Pascal(name);
            yield return "_" + name;
        }

        static string Normalize(string name) => Regex.Replace(name ?? "", "^m_|[_ ]", "").ToLowerInvariant();

        /// <summary>Finds a property by exact path, or segment by segment with C#-style names and [i] indexers.</summary>
        public static SerializedProperty Find(SerializedObject so, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var exact = so.FindProperty(path);
            if (exact != null) return exact;

            SerializedProperty current = null;
            var segments = path.Replace(".Array.data[", "[").Split('.');
            foreach (var raw in segments)
            {
                var seg = raw;
                var indices = new List<int>();
                Match m;
                while ((m = Indexer.Match(seg)).Success)
                {
                    indices.Insert(0, int.Parse(m.Groups[2].Value));
                    seg = m.Groups[1].Value;
                }

                current = Child(so, current, seg);
                if (current == null) return null;
                foreach (var i in indices)
                {
                    if (!current.isArray) return null;
                    if (i >= current.arraySize) current.arraySize = i + 1;
                    current = current.GetArrayElementAtIndex(i);
                }
            }
            return current;
        }

        static SerializedProperty Child(SerializedObject so, SerializedProperty parent, string name)
        {
            foreach (var c in Candidates(name))
            {
                var p = parent == null ? so.FindProperty(c) : parent.FindPropertyRelative(c);
                if (p != null) return p;
            }
            // Case- and prefix-insensitive scan of the direct children (also matches display names).
            var wanted = Normalize(name);
            var children = Children(so, parent).Select(p => p.Copy()).ToList();
            foreach (var p in children)
                if (Normalize(p.name) == wanted || Normalize(p.displayName) == wanted)
                    return p;
            // Near misses such as "interpolation" → m_Interpolate: a long shared prefix, and only one such field.
            var near = children.Where(p =>
            {
                var have = Normalize(p.name);
                var common = CommonPrefix(have, wanted);
                return common >= 6 && common >= 0.8 * Math.Min(have.Length, wanted.Length);
            }).ToList();
            return near.Count == 1 ? near[0] : null;
        }

        static IEnumerable<SerializedProperty> Children(SerializedObject so, SerializedProperty parent)
        {
            if (parent == null)
            {
                var it = so.GetIterator();
                if (!it.Next(true)) yield break;
                do yield return it; while (it.Next(false));
                yield break;
            }
            if (!parent.hasChildren) yield break;
            var child = parent.Copy();
            var end = parent.GetEndProperty();
            if (!child.Next(true)) yield break;
            while (!SerializedProperty.EqualContents(child, end))
            {
                yield return child;
                if (!child.Next(false)) yield break;
            }
        }

        static List<string> VisibleNames(SerializedObject so)
        {
            var names = new List<string>();
            var it = so.GetIterator();
            if (it.NextVisible(true))
                do
                {
                    if (it.name != "m_Script") names.Add(it.name);
                } while (it.NextVisible(false));
            return names;
        }

        static List<string> Close(SerializedObject so, string name)
        {
            var wanted = Normalize(name);
            if (wanted.Length < 3) return new List<string>();
            return VisibleNames(so).Where(n =>
            {
                var have = Normalize(n);
                return have.Contains(wanted) || wanted.Contains(have) && have.Length >= 3;
            }).Take(4).ToList();
        }

        static int CommonPrefix(string a, string b)
        {
            var n = 0;
            while (n < a.Length && n < b.Length && a[n] == b[n]) n++;
            return n;
        }

        // ─── Writing values ──────────────────────────────────────────────────────

        /// <summary>Sets one property from JSON; returns an error message or null.</summary>
        public static string Set(SerializedProperty p, JToken v)
        {
            if (v == null) return "no value";

            if (p.isArray && p.propertyType != SerializedPropertyType.String)
            {
                if (v is JArray arr)
                {
                    p.arraySize = arr.Count;
                    for (var i = 0; i < arr.Count; i++)
                    {
                        var e = Set(p.GetArrayElementAtIndex(i), arr[i]);
                        if (e != null) return $"element {i}: {e}";
                    }
                    return null;
                }
                var n = Args.ToInt(v);
                if (n != null && v.Type != JTokenType.String)
                {
                    p.arraySize = Math.Max(0, n.Value);
                    return null;
                }
                return $"expects an array (JSON list) of {ElementType(p)}";
            }

            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                {
                    if (v.Type == JTokenType.Integer)
                    {
                        p.longValue = (long)v;
                        return null;
                    }
                    var i = Args.ToInt(v);
                    if (i == null) return "expects an integer";
                    p.intValue = i.Value;
                    return null;
                }
                case SerializedPropertyType.Boolean:
                {
                    var b = Args.ToBool(v);
                    if (b == null) return "expects true or false";
                    p.boolValue = b.Value;
                    return null;
                }
                case SerializedPropertyType.Float:
                {
                    var f = Args.ToFloat(v);
                    if (f == null) return "expects a number";
                    if (p.type == "double") p.doubleValue = f.Value;
                    else p.floatValue = f.Value;
                    return null;
                }
                case SerializedPropertyType.String:
                    p.stringValue = v.Type == JTokenType.String ? (string)v : v.ToString(Formatting.None);
                    return null;
                case SerializedPropertyType.Character:
                {
                    var s = v.Type == JTokenType.String ? (string)v : null;
                    if (!string.IsNullOrEmpty(s)) p.intValue = s[0];
                    else if (Args.ToInt(v) is int code) p.intValue = code;
                    else return "expects a single character";
                    return null;
                }
                case SerializedPropertyType.Color:
                {
                    var c = ParseColor(v);
                    if (c == null) return "expects a colour like \"#ff8800\" or [r,g,b,a]";
                    p.colorValue = c.Value;
                    return null;
                }
                case SerializedPropertyType.Enum:
                    return SetEnum(p, v);
                case SerializedPropertyType.LayerMask:
                {
                    if (Args.ToInt(v) is int mask && v.Type != JTokenType.String)
                    {
                        p.intValue = mask;
                        return null;
                    }
                    var names = v is JArray la ? la.Select(x => x.ToString()).ToArray() : v.ToString().Split(',').Select(s => s.Trim()).ToArray();
                    if (names.Length == 1 && names[0].Equals("everything", StringComparison.OrdinalIgnoreCase)) p.intValue = ~0;
                    else if (names.Length == 1 && names[0].Equals("nothing", StringComparison.OrdinalIgnoreCase)) p.intValue = 0;
                    else
                    {
                        var unknown = names.Where(n => LayerMask.NameToLayer(n) < 0).ToList();
                        if (unknown.Count > 0) return $"unknown layer(s): {string.Join(", ", unknown)}";
                        p.intValue = LayerMask.GetMask(names);
                    }
                    return null;
                }
                case SerializedPropertyType.Vector2:
                {
                    var f = Need(v, 2);
                    p.vector2Value = new Vector2(f[0], f[1]);
                    return null;
                }
                case SerializedPropertyType.Vector3:
                {
                    var f = Need(v, 3);
                    p.vector3Value = new Vector3(f[0], f[1], f[2]);
                    return null;
                }
                case SerializedPropertyType.Vector4:
                {
                    var f = Need(v, 4);
                    p.vector4Value = new Vector4(f[0], f[1], f[2], f[3]);
                    return null;
                }
                case SerializedPropertyType.Vector2Int:
                {
                    var f = Need(v, 2);
                    p.vector2IntValue = new Vector2Int(Mathf.RoundToInt(f[0]), Mathf.RoundToInt(f[1]));
                    return null;
                }
                case SerializedPropertyType.Vector3Int:
                {
                    var f = Need(v, 3);
                    p.vector3IntValue = new Vector3Int(Mathf.RoundToInt(f[0]), Mathf.RoundToInt(f[1]), Mathf.RoundToInt(f[2]));
                    return null;
                }
                case SerializedPropertyType.Quaternion:
                {
                    var f = Args.Floats(v);
                    if (f is { Length: 3 }) p.quaternionValue = Quaternion.Euler(f[0], f[1], f[2]);
                    else if (f is { Length: 4 }) p.quaternionValue = new Quaternion(f[0], f[1], f[2], f[3]).normalized;
                    else return "expects euler angles [x,y,z] or a quaternion [x,y,z,w]";
                    return null;
                }
                case SerializedPropertyType.Rect:
                {
                    var f = RectValues(v);
                    if (f == null) return "expects [x, y, width, height]";
                    p.rectValue = new Rect(f[0], f[1], f[2], f[3]);
                    return null;
                }
                case SerializedPropertyType.RectInt:
                {
                    var f = RectValues(v);
                    if (f == null) return "expects [x, y, width, height]";
                    p.rectIntValue = new RectInt(Mathf.RoundToInt(f[0]), Mathf.RoundToInt(f[1]), Mathf.RoundToInt(f[2]), Mathf.RoundToInt(f[3]));
                    return null;
                }
                case SerializedPropertyType.Bounds:
                {
                    if (v is JObject bo && bo["center"] != null)
                    {
                        var c = Need(bo["center"], 3);
                        var s = Need(bo["size"] ?? new JArray(0, 0, 0), 3);
                        p.boundsValue = new Bounds(new Vector3(c[0], c[1], c[2]), new Vector3(s[0], s[1], s[2]));
                        return null;
                    }
                    var f = Args.Floats(v);
                    if (f is not { Length: 6 }) return "expects {center:[x,y,z], size:[x,y,z]} or [cx,cy,cz,sx,sy,sz]";
                    p.boundsValue = new Bounds(new Vector3(f[0], f[1], f[2]), new Vector3(f[3], f[4], f[5]));
                    return null;
                }
                case SerializedPropertyType.ObjectReference:
                    return SetReference(p, v);
                case SerializedPropertyType.ArraySize:
                {
                    var n = Args.ToInt(v);
                    if (n == null || n < 0) return "expects a size ≥ 0";
                    p.intValue = n.Value;
                    return null;
                }
                case SerializedPropertyType.Hash128:
                    p.hash128Value = Hash128.Parse(v.ToString());
                    return null;
                case SerializedPropertyType.Generic:
                {
                    if (v is JObject obj && obj["asset"] == null && obj["object"] == null)
                    {
                        foreach (var pair in obj)
                        {
                            SerializedProperty child = null;
                            foreach (var c in Candidates(pair.Key))
                            {
                                child = p.FindPropertyRelative(c);
                                if (child != null) break;
                            }
                            if (child == null) return $"no field '{pair.Key}' in {p.type}";
                            var e = Set(child, pair.Value);
                            if (e != null) return $"{pair.Key}: {e}";
                        }
                        return null;
                    }
                    var fields = new List<string>();
                    var f = p.Copy();
                    var end = p.GetEndProperty();
                    if (f.NextVisible(true))
                        while (!SerializedProperty.EqualContents(f, end) && fields.Count < 12)
                        {
                            fields.Add(f.name);
                            if (!f.NextVisible(false)) break;
                        }
                    return $"{p.type} is a struct: pass an object of its fields ({string.Join(", ", fields)}), or set \"{p.propertyPath}.<field>\"";
                }
                case SerializedPropertyType.ManagedReference:
                    return "managed [SerializeReference] fields cannot be set from here";
                case SerializedPropertyType.AnimationCurve:
                    return "animation curves cannot be set from here";
                case SerializedPropertyType.Gradient:
                    return "gradients cannot be set from here";
                default:
                    return $"{p.propertyType} properties are not supported";
            }
        }

        static float[] Need(JToken v, int count)
        {
            var f = Args.Floats(v);
            if (f == null || f.Length < count)
            {
                if (f is { Length: 1 }) return Enumerable.Repeat(f[0], count).ToArray();
                throw new ToolException($"expects {count} numbers [{string.Join(", ", new[] { "x", "y", "z", "w" }.Take(count))}]");
            }
            return f;
        }

        static float[] RectValues(JToken v)
        {
            if (v is JObject o && o["width"] != null)
            {
                var x = Args.ToFloat(o["x"]) ?? 0;
                var y = Args.ToFloat(o["y"]) ?? 0;
                var w = Args.ToFloat(o["width"]) ?? 0;
                var h = Args.ToFloat(o["height"]) ?? 0;
                return new[] { x, y, w, h };
            }
            var f = Args.Floats(v);
            return f is { Length: 4 } ? f : null;
        }

        public static Color? ParseColor(JToken v)
        {
            if (v == null) return null;
            if (v.Type == JTokenType.String)
            {
                var s = ((string)v).Trim();
                if (!s.StartsWith("#") && Regex.IsMatch(s, "^[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$")) s = "#" + s;
                return ColorUtility.TryParseHtmlString(s, out var c) ? c : (Color?)null;
            }
            float[] f;
            if (v is JObject o && o["r"] != null)
                f = new[] { Args.ToFloat(o["r"]) ?? 0, Args.ToFloat(o["g"]) ?? 0, Args.ToFloat(o["b"]) ?? 0, Args.ToFloat(o["a"]) ?? (Args.ToFloat(o["r"]) > 1 ? 255 : 1) };
            else f = Args.Floats(v);
            if (f == null || f.Length < 3) return null;
            var scale = f.Take(3).Any(x => x > 1f) ? 1f / 255f : 1f;
            var a = f.Length > 3 ? f[3] * (f[3] > 1f ? 1f / 255f : 1f) : 1f;
            return new Color(f[0] * scale, f[1] * scale, f[2] * scale, a);
        }

        static string SetEnum(SerializedProperty p, JToken v)
        {
            var names = p.enumNames;
            var display = p.enumDisplayNames;
            if (v.Type == JTokenType.Integer || v.Type == JTokenType.Float)
            {
                var i = Args.ToInt(v) ?? 0;
                if (i >= 0 && i < names.Length) p.enumValueIndex = i;
                else p.intValue = i;
                return null;
            }
            var text = v.ToString().Trim();
            string Key(string s) => Regex.Replace(s ?? "", @"[\s_]", "").ToLowerInvariant();
            var wanted = Key(text);
            for (var i = 0; i < names.Length; i++)
                if (Key(names[i]) == wanted || (i < display.Length && Key(display[i]) == wanted))
                {
                    p.enumValueIndex = i;
                    return null;
                }
            // "Soft" → "Soft Shadows": one option that contains the word.
            var partial = Enumerable.Range(0, names.Length)
                .Where(i => wanted.Length >= 3 && (Key(names[i]).Contains(wanted) || i < display.Length && Key(display[i]).Contains(wanted)))
                .ToList();
            if (partial.Count == 1)
            {
                p.enumValueIndex = partial[0];
                return null;
            }
            if (int.TryParse(text, out var n))
            {
                if (n >= 0 && n < names.Length) p.enumValueIndex = n;
                else p.intValue = n;
                return null;
            }
            return $"unknown value '{text}' (one of: {string.Join(", ", display)})";
        }

        /// <summary>The type name a reference field accepts, from "PPtr&lt;$Material&gt;".</summary>
        public static string ReferenceType(SerializedProperty p)
        {
            var m = Regex.Match(p.type ?? "", @"PPtr<\$?(.+?)>");
            return m.Success ? m.Groups[1].Value : "Object";
        }

        static string ElementType(SerializedProperty p)
        {
            var t = p.arrayElementType ?? "";
            var m = Regex.Match(t, @"PPtr<\$?(.+?)>");
            return m.Success ? m.Groups[1].Value + " references" : t;
        }

        static bool IsA(Type type, string name)
        {
            if (name == "Object") return true;
            for (var t = type; t != null; t = t.BaseType)
                if (t.Name == name) return true;
            return false;
        }

        static string SetReference(SerializedProperty p, JToken v)
        {
            var wanted = ReferenceType(p);
            if (v.Type == JTokenType.Null || (v.Type == JTokenType.String && ((string)v == "" || ((string)v).Equals("none", StringComparison.OrdinalIgnoreCase))))
            {
                p.objectReferenceValue = null;
                return null;
            }

            Object value;
            if (v is JObject o && o["asset"] != null) value = LoadAsset(o["asset"].ToString(), wanted, o["name"]?.ToString());
            else if (v is JObject so && so["object"] != null) value = SceneReference(so["object"].ToString(), so["component"]?.ToString(), wanted);
            else if (v.Type == JTokenType.String && ((string)v).StartsWith("Assets/") || v.Type == JTokenType.String && ((string)v).StartsWith("Packages/"))
                value = LoadAsset((string)v, wanted, null);
            else if (v.Type == JTokenType.String) value = SceneReference((string)v, null, wanted);
            else return "expects {\"asset\": \"Assets/…\"} or {\"object\": \"Path/To/Object\", \"component\"?: \"Type\"}";

            p.objectReferenceValue = value;
            if (p.objectReferenceValue == null)
                return $"a {value.GetType().Name} does not fit this field (it takes {wanted})";
            return null;
        }

        public static Object LoadAsset(string path, string wantedType, string subName)
        {
            path = path.Trim();
            var main = AssetDatabase.LoadMainAssetAtPath(path);
            if (main == null)
            {
                if (AssetDatabase.IsValidFolder(path)) throw new ToolException($"'{path}' is a folder, not an asset.");
                throw new ToolException($"no asset at '{path}' (use find_assets to look it up)");
            }
            var all = AssetDatabase.LoadAllAssetsAtPath(path);
            if (all == null || all.Length == 0) all = new[] { main };
            var candidates = new[] { main }.Concat(all.Where(a => a != null && a != main)).ToList();
            if (!string.IsNullOrEmpty(subName)) candidates = candidates.Where(a => a.name == subName).ToList();
            var hit = candidates.FirstOrDefault(a => IsA(a.GetType(), wantedType));
            if (hit != null) return hit;

            // A GameObject asset (prefab / model) for a component-typed field: take the component off its root.
            if (main is GameObject prefab)
            {
                var component = prefab.GetComponents<Component>().FirstOrDefault(c => c != null && IsA(c.GetType(), wantedType));
                if (component != null) return component;
            }
            var types = string.Join(", ", candidates.Select(a => a.GetType().Name).Distinct().Take(8));
            throw new ToolException($"'{path}' has no {wantedType} (it contains: {types})");
        }

        static Object SceneReference(string target, string componentType, string wanted)
        {
            var go = TargetResolver.Resolve(target);
            if (!string.IsNullOrEmpty(componentType))
            {
                var c = TargetResolver.FindComponent(go, componentType, 0, out var error);
                if (c == null) throw new ToolException(error);
                return c;
            }
            if (wanted is "GameObject" or "Object") return go;
            var match = go.GetComponents<Component>().FirstOrDefault(c => c != null && IsA(c.GetType(), wanted));
            if (match == null) throw new ToolException($"{go.name} has no {wanted} component for this field");
            return match;
        }

        // ─── Reading values ──────────────────────────────────────────────────────

        public static string RefLabel(Object o)
        {
            if (o == null) return "None";
            if (EditorUtility.IsPersistent(o))
            {
                var path = AssetDatabase.GetAssetPath(o);
                if (string.IsNullOrEmpty(path)) return $"{o.name} ({o.GetType().Name}, built-in)";
                if (AssetDatabase.IsMainAsset(o)) return path;
                return $"{path}:{o.name} ({o.GetType().Name})";
            }
            var go = o as GameObject ?? (o as Component)?.gameObject;
            if (go == null) return $"{o.name} ({o.GetType().Name})";
            var label = TargetResolver.PathOf(go) + $" #{Ids.Of(go)}";
            return o is Component ? $"{label} ({o.GetType().Name})" : label;
        }

        /// <summary>A one-line value; structs and arrays are inlined <paramref name="depth"/> levels deep.</summary>
        public static string Format(SerializedProperty p, int depth, int max = 300)
        {
            var s = FormatInner(p, depth);
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        static string FormatInner(SerializedProperty p, int depth)
        {
            if (p.isArray && p.propertyType != SerializedPropertyType.String)
            {
                var n = p.arraySize;
                if (n == 0) return "[]";
                if (depth <= 0) return $"[{n} items]";
                var shown = Math.Min(n, 8);
                var items = new List<string>();
                for (var i = 0; i < shown; i++) items.Add(FormatInner(p.GetArrayElementAtIndex(i), depth - 1));
                return $"[{string.Join(", ", items)}{(n > shown ? $", … +{n - shown} more" : "")}] ({n})";
            }

            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return p.type is "ulong" or "long" ? p.longValue.ToString() : p.intValue.ToString();
                case SerializedPropertyType.Boolean: return p.boolValue ? "true" : "false";
                case SerializedPropertyType.Float: return p.type == "double" ? Fmt.F(p.doubleValue) : Fmt.F(p.floatValue);
                case SerializedPropertyType.String: return Fmt.Quote(Fmt.Short(p.stringValue, 160));
                case SerializedPropertyType.Character: return Fmt.Quote(((char)p.intValue).ToString());
                case SerializedPropertyType.Color: return Fmt.Color(p.colorValue);
                case SerializedPropertyType.ObjectReference:
                    if (Ids.IsMissingReference(p)) return "Missing";
                    return RefLabel(p.objectReferenceValue);
                case SerializedPropertyType.LayerMask: return LayerMaskLabel(p.intValue);
                case SerializedPropertyType.Enum:
                    var names = p.enumDisplayNames;
                    var index = p.enumValueIndex;
                    return index >= 0 && index < names.Length ? names[index] : p.intValue.ToString();
                case SerializedPropertyType.Vector2: return Fmt.V(p.vector2Value);
                case SerializedPropertyType.Vector3: return Fmt.V(p.vector3Value);
                case SerializedPropertyType.Vector4: return Fmt.V(p.vector4Value);
                case SerializedPropertyType.Vector2Int: return $"[{p.vector2IntValue.x}, {p.vector2IntValue.y}]";
                case SerializedPropertyType.Vector3Int: return $"[{p.vector3IntValue.x}, {p.vector3IntValue.y}, {p.vector3IntValue.z}]";
                case SerializedPropertyType.Quaternion: return "euler " + Fmt.V(p.quaternionValue.eulerAngles);
                case SerializedPropertyType.Rect:
                    var r = p.rectValue;
                    return $"[{Fmt.F(r.x)}, {Fmt.F(r.y)}, {Fmt.F(r.width)}, {Fmt.F(r.height)}]";
                case SerializedPropertyType.RectInt:
                    var ri = p.rectIntValue;
                    return $"[{ri.x}, {ri.y}, {ri.width}, {ri.height}]";
                case SerializedPropertyType.Bounds:
                    return $"{{center {Fmt.V(p.boundsValue.center)}, size {Fmt.V(p.boundsValue.size)}}}";
                case SerializedPropertyType.ArraySize: return p.intValue.ToString();
                case SerializedPropertyType.AnimationCurve: return $"curve ({p.animationCurveValue?.length ?? 0} keys)";
                case SerializedPropertyType.Gradient: return "gradient";
                case SerializedPropertyType.Hash128: return p.hash128Value.ToString();
                case SerializedPropertyType.ManagedReference: return string.IsNullOrEmpty(p.managedReferenceFullTypename) ? "null" : $"{{{p.managedReferenceFullTypename.Split(' ').Last()}}}";
                case SerializedPropertyType.Generic:
                    if (depth <= 0) return $"{{{p.type}}}";
                    var parts = new List<string>();
                    var child = p.Copy();
                    var end = p.GetEndProperty();
                    if (child.NextVisible(true))
                        while (!SerializedProperty.EqualContents(child, end) && parts.Count < 16)
                        {
                            parts.Add($"{child.name}={FormatInner(child, depth - 1)}");
                            if (!child.NextVisible(false)) break;
                        }
                    return "{" + string.Join(", ", parts) + "}";
                default:
                    return p.propertyType.ToString();
            }
        }

        static string LayerMaskLabel(int mask)
        {
            if (mask == 0) return "Nothing";
            if (mask == ~0) return "Everything";
            var names = new List<string>();
            for (var i = 0; i < 32; i++)
                if ((mask & (1 << i)) != 0)
                    names.Add(string.IsNullOrEmpty(LayerMask.LayerToName(i)) ? i.ToString() : LayerMask.LayerToName(i));
            return string.Join("|", names);
        }

        /// <summary>The visible serialized properties of an object as "path = value" lines.</summary>
        public static List<string> Lines(Object target, int depth, int maxLines = 60, Regex hide = null)
        {
            var lines = new List<string>();
            var so = new SerializedObject(target);
            var it = so.GetIterator();
            if (!it.NextVisible(true)) return lines;
            var count = 0;
            var hidden = 0;
            do
            {
                if (it.name == "m_Script") continue;
                if (hide != null && hide.IsMatch(it.name))
                {
                    hidden++;
                    continue;
                }
                count++;
                if (lines.Count >= maxLines) continue;
                lines.Add($"{it.propertyPath} = {Format(it, depth, 400)}");
            } while (it.NextVisible(false));
            if (count > lines.Count) lines.Add($"… {count - lines.Count} more properties (inspect with components:[\"{target.GetType().Name}\"] to see them)");
            else if (hidden > 0) lines.Add($"(+{hidden} advanced properties; inspect with components:[\"{target.GetType().Name}\"] to see all)");
            return lines;
        }
    }
}
