using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Zetta3D.Editor.Agent
{
    /// <summary>find_assets, create_prefab, create_material, assign_material, move_asset, delete_asset.</summary>
    static class AssetTools
    {
        static string Folder(string path) => Path.GetDirectoryName(path)?.Replace('\\', '/');

        static string TypeName(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return "Folder";
            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            if (type == typeof(GameObject))
                return path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ? "Prefab" : "Model";
            return type?.Name ?? "Unknown";
        }

        // ─── find_assets ─────────────────────────────────────────────────────────

        public static ToolResult Find(JObject input)
        {
            var query = Args.Required(input, "query");
            var limit = Args.Int(input, "limit", 50, 1, 200);
            string[] folders = null;
            if (Args.Present(input, "folder"))
            {
                var folder = FileTools.ForRead(Args.Str(input, "folder"));
                if (!AssetDatabase.IsValidFolder(folder)) throw new ToolException($"'{folder}' is not a folder in the project.");
                folders = new[] { folder };
            }

            var paths = AssetDatabase.FindAssets(query, folders)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .ToList();
            // Assets first: package results are usually noise.
            paths = paths.OrderBy(p => p.StartsWith("Assets/") ? 0 : 1).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Count == 0) return ToolResult.Ok($"No assets match \"{query}\"{(folders != null ? " in " + folders[0] : "")}. Filters: t:Prefab, t:Material, t:Texture2D, t:Scene, t:Script, l:Label, or a name.");

            var sb = new StringBuilder($"{paths.Count} asset(s){(paths.Count > limit ? $", showing {limit}" : "")}:\n");
            foreach (var p in paths.Take(limit)) sb.Append(p).Append("  (").Append(TypeName(p)).AppendLine(")");
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }

        // ─── create_prefab ───────────────────────────────────────────────────────

        public static ToolResult CreatePrefab(JObject input)
        {
            var go = TargetResolver.Resolve(Args.Required(input, "target"), out var note);
            var path = FileTools.ForWrite(Args.Required(input, "path"));
            if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) path += ".prefab";
            var connect = Args.Bool(input, "connect", true);
            if (File.Exists(FileTools.Physical(path)) || AssetDatabase.LoadMainAssetAtPath(path) != null)
                throw new ToolException($"{path} already exists. Choose a new name (e.g. {AssetDatabase.GenerateUniqueAssetPath(path)}), or open the prefab and edit it.");
            FileTools.EnsureFolder(Folder(path));

            bool success;
            GameObject prefab;
            if (connect) prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(go, path, InteractionMode.UserAction, out success);
            else prefab = PrefabUtility.SaveAsPrefabAsset(go, path, out success);
            if (!success || prefab == null) throw new ToolException($"Unity could not save {go.name} as a prefab at {path}.");
            if (connect) EditorSceneManager.MarkSceneDirty(go.scene);

            var text = $"Saved {path} from {TargetResolver.Label(go)}" +
                       (connect ? $"; the scene object is now an instance of it." : " (the scene object is unchanged).");
            if (note != null) text += " Note: " + note;
            return ToolResult.Ok(text);
        }

        // ─── create_material ─────────────────────────────────────────────────────

        public static string DefaultShaderName()
        {
            switch (ProjectInfo.Pipeline())
            {
                case "URP": return "Universal Render Pipeline/Lit";
                case "HDRP": return "HDRP/Lit";
                default: return "Standard";
            }
        }

        static Shader FindShader(string name)
        {
            var shader = Shader.Find(name);
            if (shader != null) return shader;
            var asset = name.StartsWith("Assets/") || name.StartsWith("Packages/") ? AssetDatabase.LoadAssetAtPath<Shader>(name) : null;
            if (asset != null) return asset;
            var all = ShaderUtil.GetAllShaderInfo().Select(s => s.name).ToList();
            var exact = all.FirstOrDefault(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return Shader.Find(exact);
            var close = all.Where(s => s.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 && !s.StartsWith("Hidden/")).Take(8).ToList();
            throw new ToolException($"Shader '{name}' was not found." + (close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : $" The project's default lit shader is \"{DefaultShaderName()}\"."));
        }

        public static ToolResult CreateMaterial(JObject input)
        {
            var path = FileTools.ForWrite(Args.Required(input, "path"));
            if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) path += ".mat";

            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing == null && File.Exists(FileTools.Physical(path))) throw new ToolException($"{path} exists and is not a material.");

            Shader shader;
            if (Args.Present(input, "shader")) shader = FindShader(Args.Str(input, "shader").Trim());
            else if (existing != null) shader = existing.shader;
            else
            {
                shader = Shader.Find(DefaultShaderName());
                if (shader == null) shader = GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.defaultMaterial?.shader : null;
                if (shader == null) shader = Shader.Find("Standard");
                if (shader == null) throw new ToolException("No default lit shader was found; pass 'shader'.");
            }

            Material material;
            if (existing != null)
            {
                material = existing;
                Undo.RecordObject(material, "Edit material");
                if (material.shader != shader) material.shader = shader;
            }
            else material = new Material(shader) { name = Path.GetFileNameWithoutExtension(path) };

            var applied = new List<string>();
            var errors = new List<string>();

            if (Args.Present(input, "color"))
            {
                var color = PropertyWriter.ParseColor(Args.Get(input, "color"));
                if (color == null) errors.Add("color: expects \"#rrggbb\" or \"#rrggbbaa\"");
                else
                {
                    var set = false;
                    foreach (var prop in new[] { "_BaseColor", "_Color" })
                        if (material.HasProperty(prop))
                        {
                            material.SetColor(prop, color.Value);
                            set = true;
                        }
                    if (set) applied.Add($"color {Fmt.Color(color.Value)}");
                    else errors.Add($"color: shader {shader.name} has no _BaseColor/_Color");
                }
            }

            if (Args.Get(input, "properties") is JObject props)
                foreach (var pair in props)
                {
                    var e = SetShaderProperty(material, pair.Key, pair.Value, out var shown);
                    if (e != null) errors.Add($"{pair.Key}: {e}");
                    else applied.Add($"{pair.Key}={shown}");
                }

            if (Args.Get(input, "textures") is JObject textures)
                foreach (var pair in textures)
                {
                    var e = SetShaderProperty(material, pair.Key, pair.Value is JObject ? pair.Value : new JObject { ["asset"] = pair.Value }, out var shown);
                    if (e != null) errors.Add($"{pair.Key}: {e}");
                    else applied.Add($"{pair.Key}={shown}");
                }

            if (existing == null)
            {
                FileTools.EnsureFolder(Folder(path));
                AssetDatabase.CreateAsset(material, path);
            }
            else EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);

            if (errors.Any(e => e.EndsWith(NoSuchProperty))) errors.Add($"{shader.name} has: {ShaderProperties(shader)}");
            var sb = new StringBuilder(existing != null ? $"Updated the existing material {path}" : $"Created {path}");
            sb.Append($" (shader {shader.name}).");
            if (applied.Count > 0) sb.Append(" Set: ").Append(string.Join("; ", applied)).Append('.');
            if (errors.Count > 0) sb.Append(" Not set: ").Append(string.Join("; ", errors)).Append('.');
            return ToolResult.Ok(sb.ToString());
        }

        const string NoSuchProperty = "no such shader property";

        static readonly Dictionary<string, string> Counterparts = new Dictionary<string, string>
        {
            ["_BaseColor"] = "_Color", ["_Color"] = "_BaseColor",
            ["_BaseMap"] = "_MainTex", ["_MainTex"] = "_BaseMap",
            ["_Smoothness"] = "_Glossiness", ["_Glossiness"] = "_Smoothness",
            ["_BaseColorMap"] = "_BaseMap", ["_NormalMap"] = "_BumpMap", ["_BumpMap"] = "_NormalMap",
        };

        static string ShaderProperties(Shader shader) =>
            string.Join(", ", Enumerable.Range(0, shader.GetPropertyCount()).Select(shader.GetPropertyName).Where(n => !n.StartsWith("unity_")).Take(40));

        static string SetShaderProperty(Material m, string name, JToken value, out string shown)
        {
            shown = null;
            var shader = m.shader;
            var index = shader.FindPropertyIndex(name);
            if (index < 0 && Counterparts.TryGetValue(name, out var other) && shader.FindPropertyIndex(other) >= 0)
            {
                name = other; // the same idea under the other pipeline's name (_BaseColor ↔ _Color, …)
                index = shader.FindPropertyIndex(name);
            }
            if (index < 0) return NoSuchProperty;

            switch (shader.GetPropertyType(index))
            {
                case ShaderPropertyType.Color:
                {
                    var c = PropertyWriter.ParseColor(value);
                    if (c == null) return "expects a colour";
                    m.SetColor(name, c.Value);
                    shown = Fmt.Color(c.Value);
                    if (name == "_EmissionColor" && c.Value.maxColorComponent > 0.001f)
                    {
                        m.EnableKeyword("_EMISSION");
                        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                    }
                    return null;
                }
                case ShaderPropertyType.Vector:
                {
                    var f = Args.Floats(value);
                    if (f == null || f.Length < 2) return "expects [x, y, z, w]";
                    var v = new Vector4(f[0], f[1], f.Length > 2 ? f[2] : 0, f.Length > 3 ? f[3] : 0);
                    m.SetVector(name, v);
                    shown = Fmt.V(v);
                    return null;
                }
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range:
                {
                    var f = Args.ToFloat(value);
                    if (f == null) return "expects a number";
                    m.SetFloat(name, f.Value);
                    shown = Fmt.F(f.Value);
                    return null;
                }
                case ShaderPropertyType.Int:
                {
                    var i = Args.ToInt(value);
                    if (i == null) return "expects an integer";
                    m.SetInteger(name, i.Value);
                    shown = i.Value.ToString();
                    return null;
                }
                case ShaderPropertyType.Texture:
                {
                    if (value == null || value.Type == JTokenType.Null)
                    {
                        m.SetTexture(name, null);
                        shown = "None";
                        return null;
                    }
                    var path = value is JObject o ? o["asset"]?.ToString() : value.ToString();
                    if (string.IsNullOrWhiteSpace(path)) return "expects a texture asset path";
                    Texture texture;
                    try
                    {
                        texture = PropertyWriter.LoadAsset(FileTools.Normalize(path), "Texture", null) as Texture;
                    }
                    catch (ToolException e)
                    {
                        return e.Message;
                    }
                    if (texture == null) return $"'{path}' is not a texture";
                    m.SetTexture(name, texture);
                    shown = AssetDatabase.GetAssetPath(texture);
                    if (name == "_BumpMap" || name == "_NormalMap")
                    {
                        m.EnableKeyword("_NORMALMAP");
                        if (AssetImporter.GetAtPath(shown) is TextureImporter ti && ti.textureType != TextureImporterType.NormalMap)
                            shown += " (note: the texture is not imported as a Normal map)";
                    }
                    if (name == "_EmissionMap") m.EnableKeyword("_EMISSION");
                    if (name == "_MetallicGlossMap") m.EnableKeyword(ProjectInfo.Pipeline() == "Built-in" ? "_METALLICGLOSSMAP" : "_METALLICSPECGLOSSMAP");
                    return null;
                }
                default:
                    return "unsupported property type";
            }
        }

        // ─── assign_material ─────────────────────────────────────────────────────

        public static ToolResult AssignMaterial(JObject input)
        {
            var go = TargetResolver.Resolve(Args.Required(input, "target"), out var note);
            var path = FileTools.Normalize(Args.Required(input, "material"));
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) throw new ToolException($"No material at '{path}'. Create one with create_material or find one with find_assets \"t:Material\".");
            var slot = Args.Int(input, "slot", 0, 0);

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
            {
                var childRenderers = go.GetComponentsInChildren<Renderer>(true).Take(8).Select(r => TargetResolver.PathOf(r.gameObject)).ToList();
                throw new ToolException($"{go.name} has no Renderer." + (childRenderers.Count > 0 ? $" Its children do: {string.Join(", ", childRenderers)}." : ""));
            }

            Undo.RecordObject(renderer, "Assign material");
            var mats = renderer.sharedMaterials.ToList();
            if (slot > mats.Count) throw new ToolException($"{go.name}'s renderer has {mats.Count} material slot(s); slot {slot} would leave a gap (use 0–{mats.Count}).");
            var previous = slot < mats.Count && mats[slot] != null ? PropertyWriter.RefLabel(mats[slot]) : "None";
            if (slot == mats.Count) mats.Add(material);
            else mats[slot] = material;
            renderer.sharedMaterials = mats.ToArray();
            if (PrefabUtility.IsPartOfPrefabInstance(renderer)) PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var text = $"Assigned {path} to {TargetResolver.Label(go)} slot {slot} (was {previous}).";
            if (note != null) text += " Note: " + note;
            return ToolResult.Ok(text + SceneTools.PlayModeNote());
        }

        // ─── move_asset, delete_asset ────────────────────────────────────────────

        public static ToolResult Move(JObject input)
        {
            var from = FileTools.ForWrite(Args.Required(input, "from"));
            var to = FileTools.ForWrite(Args.Required(input, "to"));
            if (AssetDatabase.LoadMainAssetAtPath(from) == null && !AssetDatabase.IsValidFolder(from))
                throw new ToolException($"No asset at '{from}'.");
            if (AssetDatabase.IsValidFolder(to) && !AssetDatabase.IsValidFolder(from)) to = to + "/" + Path.GetFileName(from);
            if (Path.GetExtension(to).Length == 0 && Path.GetExtension(from).Length > 0) to += Path.GetExtension(from);
            if (AssetDatabase.LoadMainAssetAtPath(to) != null || File.Exists(FileTools.Physical(to)))
                throw new ToolException($"{to} already exists.");
            FileTools.EnsureFolder(Folder(to));
            var error = AssetDatabase.MoveAsset(from, to);
            if (!string.IsNullOrEmpty(error)) throw new ToolException($"Could not move {from}: {error}");
            var note = FileTools.IsScript(from) && Path.GetFileNameWithoutExtension(from) != Path.GetFileNameWithoutExtension(to)
                ? " The class name should match the new file name." : "";
            return ToolResult.Ok($"Moved {from} → {to} (references are kept).{note}");
        }

        public static ToolResult Delete(JObject input)
        {
            var path = FileTools.ForWrite(Args.Required(input, "path"));
            var isFolder = AssetDatabase.IsValidFolder(path);
            if (!isFolder && AssetDatabase.LoadMainAssetAtPath(path) == null) throw new ToolException($"No asset at '{path}'.");
            var contents = isFolder ? AssetDatabase.FindAssets("", new[] { path }).Length : 0;
            var script = FileTools.IsScript(path) || isFolder && AssetDatabase.FindAssets("t:Script", new[] { path }).Length > 0;
            if (!AssetDatabase.MoveAssetToTrash(path)) throw new ToolException($"Could not move {path} to the trash.");
            var result = ToolResult.Ok($"Moved {path}{(isFolder ? $" (folder, {contents} assets)" : "")} to the OS trash (restore it from there if needed).");
            result.WroteScripts = script;
            return result;
        }

        public static string SummarizeDelete(JObject input)
        {
            var raw = Args.Str(input, "path") ?? "?";
            try
            {
                var path = FileTools.ForWrite(raw);
                if (AssetDatabase.IsValidFolder(path))
                {
                    var assets = AssetDatabase.FindAssets("", new[] { path }).Select(AssetDatabase.GUIDToAssetPath).Distinct().ToList();
                    return $"Moves the folder {path} and everything in it ({assets.Count} assets) to the trash:\n" +
                           string.Join("\n", assets.Take(15)) + (assets.Count > 15 ? $"\n… and {assets.Count - 15} more" : "");
                }
                if (AssetDatabase.LoadMainAssetAtPath(path) == null) return $"{path} (no asset there — the call will fail)";
                return $"Moves {path} ({TypeName(path)}) to the trash.";
            }
            catch (ToolException e)
            {
                return $"{raw}: {e.Message}";
            }
        }
    }
}
