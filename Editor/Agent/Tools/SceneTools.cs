using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Zetta3D.Editor.Agent
{
    /// <summary>Hierarchy reads and GameObject / scene edits.</summary>
    static class SceneTools
    {
        public static string PlayModeNote() =>
            EditorApplication.isPlaying ? " (Play mode: scene changes are lost when Play mode exits.)" : "";

        // ─── scene_hierarchy ─────────────────────────────────────────────────────

        public static string ObjectLine(GameObject go, bool components, bool ids = true)
        {
            var sb = new StringBuilder(go.name);
            if (components)
            {
                var names = go.GetComponents<Component>()
                    .Where(c => !(c is Transform))
                    .Select(c => c == null ? "Missing Script" : c.GetType().Name)
                    .ToList();
                if (names.Count > 0) sb.Append(" [").Append(string.Join(", ", names)).Append(']');
            }
            if (ids) sb.Append(" #").Append(Ids.Of(go));
            if (!go.activeSelf) sb.Append(" (inactive)");
            if (PrefabUtility.IsPartOfPrefabInstance(go) && PrefabUtility.IsOutermostPrefabInstanceRoot(go))
            {
                var path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                if (!string.IsNullOrEmpty(path)) sb.Append(" (prefab: ").Append(path).Append(')');
            }
            return sb.ToString();
        }

        /// <summary>Indented outline of one subtree; appends to <paramref name="lines"/>.</summary>
        static void Outline(Transform t, int level, int maxDepth, bool components, List<string> lines, int budget)
        {
            if (lines.Count >= budget) return;
            var line = new string(' ', level * 2) + ObjectLine(t.gameObject, components);
            if (level + 1 >= maxDepth && t.childCount > 0) line += $" (+{t.childCount} children)";
            lines.Add(line);
            if (level + 1 >= maxDepth) return;
            foreach (Transform child in t) Outline(child, level + 1, maxDepth, components, lines, budget);
        }

        public static string SceneHeader(Scene scene) =>
            $"{(string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name)} (scene{(string.IsNullOrEmpty(scene.path) ? ", unsaved" : ": " + scene.path)}" +
            $"{(scene.isDirty ? ", modified" : "")}, {scene.rootCount} roots)";

        /// <summary>The active scene, depth ≤ <paramref name="depth"/>, at most <paramref name="maxChars"/> characters (for the snapshot).</summary>
        public static string SnapshotOutline(int depth, int maxChars)
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return "";
            var lines = new List<string>();
            foreach (var root in scene.GetRootGameObjects()) Outline(root.transform, 1, depth + 1, true, lines, 4000);
            var sb = new StringBuilder(SceneHeader(scene)).Append('\n');
            var total = CountObjects(scene);
            var shown = 0;
            foreach (var l in lines)
            {
                if (sb.Length + l.Length + 1 > maxChars - 120) break;
                sb.Append(l).Append('\n');
                shown++;
            }
            if (shown < lines.Count || total > lines.Count)
                sb.Append($"  … ({total} objects in the scene; showing {shown}. Use scene_hierarchy for more.)");
            return sb.ToString().TrimEnd();
        }

        static int CountObjects(Scene scene) => scene.GetRootGameObjects().Sum(r => r.GetComponentsInChildren<Transform>(true).Length);

        public static ToolResult Hierarchy(JObject input)
        {
            var depth = Args.Int(input, "depth", 3, 1, 8);
            var components = Args.Bool(input, "components", true);
            var offset = Args.Int(input, "offset", 0, 0);
            var limit = Args.Int(input, "limit", 200, 1, 500);
            var budget = offset + limit + 1;

            var lines = new List<string>();
            var header = new StringBuilder();
            if (Args.Present(input, "root"))
            {
                var root = TargetResolver.Resolve(Args.Str(input, "root"), out var note);
                header.AppendLine($"Under {TargetResolver.Label(root)}{(note != null ? " — " + note : "")}:");
                Outline(root.transform, 0, depth + 1, components, lines, budget);
            }
            else
            {
                foreach (var scene in TargetResolver.LoadedScenes())
                {
                    lines.Add(SceneHeader(scene) + (scene == SceneManager.GetActiveScene() ? " [active]" : ""));
                    foreach (var r in scene.GetRootGameObjects()) Outline(r.transform, 1, depth + 1, components, lines, budget + 8);
                    if (lines.Count >= budget) break;
                }
                var stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage != null) header.AppendLine($"(The user is editing the prefab {stage.assetPath} in Prefab Mode; it is not listed here.)");
            }

            var page = lines.Skip(offset).Take(limit).ToList();
            var sb = new StringBuilder(header.ToString());
            foreach (var l in page) sb.AppendLine(l);
            var more = lines.Count > offset + limit;
            sb.Append($"— lines {offset + 1}–{offset + page.Count}");
            sb.Append(more ? $"; more below, call again with offset={offset + limit}." : " (end).");
            if (!Args.Present(input, "root"))
                sb.Append($" Total objects: {TargetResolver.LoadedScenes().Sum(CountObjects)}.");
            return ToolResult.Ok(sb.ToString());
        }

        // ─── find_gameobjects ────────────────────────────────────────────────────

        public static int ParseLayer(string layer)
        {
            if (int.TryParse(layer, out var n) && n >= 0 && n < 32) return n;
            var i = LayerMask.NameToLayer(layer);
            if (i < 0) throw new ToolException($"Layer '{layer}' does not exist. Layers: {string.Join(", ", Layers())}.");
            return i;
        }

        public static IEnumerable<string> Layers()
        {
            for (var i = 0; i < 32; i++)
            {
                var name = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(name)) yield return $"{i} {name}";
            }
        }

        public static ToolResult Find(JObject input)
        {
            var name = Args.Str(input, "name");
            var component = Args.Str(input, "component");
            var tag = Args.Str(input, "tag");
            var layerName = Args.Str(input, "layer");
            var limit = Args.Int(input, "limit", 50, 1, 200);
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(component) && string.IsNullOrWhiteSpace(tag) && string.IsNullOrWhiteSpace(layerName))
                throw new ToolException("Give at least one of name, component, tag or layer (or use scene_hierarchy to list everything).");

            var layer = string.IsNullOrWhiteSpace(layerName) ? -1 : ParseLayer(layerName.Trim());
            Type type = null;
            if (!string.IsNullOrWhiteSpace(component))
            {
                try
                {
                    type = ComponentTools.ResolveType(component);
                }
                catch (ToolException)
                {
                    type = null; // fall back to matching by type name
                }
            }

            var hits = new List<GameObject>();
            foreach (var go in TargetResolver.AllObjects())
            {
                if (!string.IsNullOrWhiteSpace(name) && go.name.IndexOf(name.Trim(), StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!string.IsNullOrWhiteSpace(tag) && !string.Equals(go.tag, tag.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                if (layer >= 0 && go.layer != layer) continue;
                if (!string.IsNullOrWhiteSpace(component))
                {
                    var has = type != null
                        ? go.GetComponent(type) != null
                        : go.GetComponents<Component>().Any(c => c != null && TargetResolver.TypeMatches(c.GetType(), component.Trim()));
                    if (!has) continue;
                }
                hits.Add(go);
            }

            if (hits.Count == 0) return ToolResult.Ok("No matching GameObjects in the loaded scenes.");
            var sb = new StringBuilder($"{hits.Count} match{(hits.Count == 1 ? "" : "es")}{(hits.Count > limit ? $", showing {limit}" : "")}:\n");
            foreach (var go in hits.Take(limit))
                sb.Append(TargetResolver.PathOf(go)).Append("  ").AppendLine(ObjectLine(go, true).Substring(go.name.Length).Trim());
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }

        // ─── get_selection ───────────────────────────────────────────────────────

        public static ToolResult Selection_(JObject input)
        {
            var objects = Selection.objects;
            if (objects == null || objects.Length == 0) return ToolResult.Ok("Nothing is selected.");
            var sb = new StringBuilder();
            foreach (var o in objects.Take(100))
            {
                if (o == null) continue;
                var active = o == Selection.activeObject ? " (active)" : "";
                if (o is GameObject go && !EditorUtility.IsPersistent(go))
                    sb.AppendLine($"scene: {TargetResolver.PathOf(go)}  {ObjectLine(go, true).Substring(go.name.Length).Trim()}{active}");
                else
                    sb.AppendLine($"asset: {AssetDatabase.GetAssetPath(o)} ({o.GetType().Name}){active}");
            }
            if (objects.Length > 100) sb.AppendLine($"… and {objects.Length - 100} more");
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }

        // ─── create_gameobject ───────────────────────────────────────────────────

        static readonly Dictionary<string, PrimitiveType> Primitives = new Dictionary<string, PrimitiveType>(StringComparer.OrdinalIgnoreCase)
        {
            ["cube"] = PrimitiveType.Cube,
            ["sphere"] = PrimitiveType.Sphere,
            ["capsule"] = PrimitiveType.Capsule,
            ["cylinder"] = PrimitiveType.Cylinder,
            ["plane"] = PrimitiveType.Plane,
            ["quad"] = PrimitiveType.Quad,
        };

        public static ToolResult Create(JObject input)
        {
            var name = Args.Required(input, "name");
            var primitive = Args.Str(input, "primitive", "empty").Trim();
            var prefabPath = Args.Str(input, "prefab");
            var notes = new List<string>();

            Transform parent = null;
            if (Args.Present(input, "parent"))
            {
                parent = TargetResolver.Resolve(Args.Str(input, "parent"), out var pnote).transform;
                if (pnote != null) notes.Add(pnote);
            }

            GameObject go;
            if (!string.IsNullOrWhiteSpace(prefabPath))
            {
                prefabPath = FileTools.Normalize(prefabPath);
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (asset == null) throw new ToolException($"No prefab or model at '{prefabPath}'. Use find_assets with \"t:Prefab\" to find one.");
                go = parent != null
                    ? (GameObject)PrefabUtility.InstantiatePrefab(asset, parent.gameObject.scene)
                    : (GameObject)PrefabUtility.InstantiatePrefab(asset);
                if (go == null) throw new ToolException($"Could not instantiate '{prefabPath}'.");
                go.name = name;
            }
            else if (primitive.Equals("empty", StringComparison.OrdinalIgnoreCase) || primitive.Length == 0)
            {
                go = new GameObject(name);
            }
            else if (Primitives.TryGetValue(primitive, out var type))
            {
                go = GameObject.CreatePrimitive(type);
                go.name = name;
            }
            else throw new ToolException($"Unknown primitive '{primitive}'. Use empty, cube, sphere, capsule, cylinder, plane or quad.");

            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            if (parent != null)
            {
                if (go.scene != parent.gameObject.scene) SceneManager.MoveGameObjectToScene(go, parent.gameObject.scene);
                go.transform.SetParent(parent, false);
            }

            var local = Args.Bool(input, "local", true);
            ApplyTransform(go.transform, input, local);

            if (Args.Present(input, "tag")) SetTag(go, Args.Str(input, "tag").Trim(), notes);
            if (Args.Present(input, "layer"))
            {
                try
                {
                    go.layer = ParseLayer(Args.Str(input, "layer").Trim());
                }
                catch (ToolException e)
                {
                    notes.Add(e.Message);
                }
            }

            var added = new List<string>();
            if (Args.Get(input, "components") is JArray comps)
            {
                foreach (var entry in comps)
                {
                    var typeName = entry.Type == JTokenType.String ? (string)entry : entry["type"]?.ToString();
                    try
                    {
                        var (component, outcome) = ComponentTools.Add(go, typeName, entry is JObject eo ? eo["properties"] as JObject : null);
                        var detail = outcome.Applied.Count + outcome.Errors.Count > 0 ? $" ({outcome.Describe().TrimEnd('.')})" : "";
                        added.Add(component.GetType().Name + detail);
                    }
                    catch (ToolException e)
                    {
                        notes.Add($"{typeName}: {e.Message}");
                    }
                }
            }

            EditorSceneManager.MarkSceneDirty(go.scene);
            var t = go.transform;
            var sb = new StringBuilder($"Created {TargetResolver.Label(go)}");
            if (!string.IsNullOrWhiteSpace(prefabPath)) sb.Append($" from {prefabPath}");
            else if (!primitive.Equals("empty", StringComparison.OrdinalIgnoreCase)) sb.Append($" ({primitive.ToLowerInvariant()})");
            sb.Append($" — position {Fmt.V(t.position)}");
            if (t.parent != null) sb.Append($" (local {Fmt.V(t.localPosition)})");
            if (t.localEulerAngles != Vector3.zero) sb.Append($", rotation {Fmt.V(t.localEulerAngles)}");
            if (t.localScale != Vector3.one) sb.Append($", scale {Fmt.V(t.localScale)}");
            sb.Append('.');
            var existing = go.GetComponents<Component>().Where(c => !(c is Transform)).Select(c => c == null ? "Missing Script" : c.GetType().Name).ToList();
            if (existing.Count > 0) sb.Append($" Components: {string.Join(", ", existing)}.");
            if (added.Count > 0) sb.Append($" Added: {string.Join("; ", added)}.");
            if (notes.Count > 0) sb.Append(" Notes: ").Append(string.Join(" ", notes));
            sb.Append(PlayModeNote());
            return ToolResult.Ok(sb.ToString());
        }

        static void ApplyTransform(Transform t, JObject input, bool local)
        {
            var position = Args.Vec3(input, "position");
            var rotation = Args.Vec3(input, "rotation");
            var scale = Args.Vec3(input, "scale");
            if (position.HasValue)
            {
                if (local) t.localPosition = position.Value;
                else t.position = position.Value;
            }
            if (rotation.HasValue)
            {
                if (local) t.localEulerAngles = rotation.Value;
                else t.eulerAngles = rotation.Value;
            }
            if (scale.HasValue) t.localScale = scale.Value;
        }

        /// <summary>Sets a tag, adding it to the Tag Manager first when the project does not have it.</summary>
        static void SetTag(GameObject go, string tag, List<string> notes)
        {
            if (string.IsNullOrEmpty(tag)) return;
            if (!InternalEditorUtility.tags.Contains(tag))
            {
                var manager = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset").FirstOrDefault();
                if (manager == null)
                {
                    notes.Add($"Tag '{tag}' does not exist and the Tag Manager could not be opened.");
                    return;
                }
                var so = new SerializedObject(manager);
                var tags = so.FindProperty("tags");
                tags.arraySize++;
                tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = tag;
                so.ApplyModifiedProperties();
                notes.Add($"Added the tag '{tag}' to the project.");
            }
            go.tag = tag;
        }

        // ─── modify_gameobject ───────────────────────────────────────────────────

        public static ToolResult Modify(JObject input)
        {
            var go = TargetResolver.Resolve(Args.Required(input, "target"), out var note);
            var t = go.transform;
            var changes = new List<string>();
            var notes = new List<string>();
            if (note != null) notes.Add(note);

            Undo.RecordObject(go, "Modify " + go.name);

            if (Args.Present(input, "name"))
            {
                var name = Args.Str(input, "name");
                changes.Add($"name {go.name} → {name}");
                go.name = name;
            }

            if (Args.Present(input, "tag"))
            {
                var tag = Args.Str(input, "tag").Trim();
                SetTag(go, tag, notes);
                changes.Add($"tag {tag}");
            }

            if (Args.Present(input, "layer"))
            {
                go.layer = ParseLayer(Args.Str(input, "layer").Trim());
                changes.Add($"layer {go.layer} {LayerMask.LayerToName(go.layer)}");
            }

            if (Args.BoolN(input, "isStatic") is bool isStatic)
            {
                go.isStatic = isStatic;
                changes.Add($"static {isStatic.ToString().ToLowerInvariant()}");
            }

            if (Args.BoolN(input, "active") is bool active)
            {
                go.SetActive(active);
                changes.Add(active ? "activated" : "deactivated");
            }

            if (Args.Present(input, "parent") || Args.IsNull(input, "parent"))
            {
                Transform parent = null;
                if (Args.Present(input, "parent"))
                {
                    var p = Args.Str(input, "parent").Trim();
                    if (p.Length > 0 && !p.Equals("null", StringComparison.OrdinalIgnoreCase) && p != "/")
                    {
                        parent = TargetResolver.Resolve(p, out var pnote).transform;
                        if (pnote != null) notes.Add(pnote);
                    }
                }
                if (parent != null && (parent == t || parent.IsChildOf(t)))
                    throw new ToolException($"Cannot parent {go.name} under {parent.name}: it is {go.name} itself or one of its children.");
                if (parent != null && parent.gameObject.scene != go.scene)
                    throw new ToolException($"{parent.name} is in another scene ({TargetResolver.SceneLabel(parent.gameObject.scene)}); objects can only be parented within one scene.");
                if (t.parent != parent)
                {
                    Undo.SetTransformParent(t, parent, "Reparent " + go.name);
                    changes.Add(parent == null ? "moved to the scene root" : $"parent → {TargetResolver.PathOf(parent.gameObject)}");
                }
            }

            if (Args.IntN(input, "siblingIndex") is int sibling)
            {
                if (t.parent != null) Undo.RegisterFullObjectHierarchyUndo(t.parent.gameObject, "Reorder");
                else Undo.RecordObject(t, "Reorder");
                t.SetSiblingIndex(sibling);
                changes.Add($"sibling index {t.GetSiblingIndex()}");
            }

            var local = Args.Bool(input, "local", true);
            if (Args.Present(input, "position") || Args.Present(input, "rotation") || Args.Present(input, "scale"))
            {
                Undo.RecordObject(t, "Move " + go.name);
                ApplyTransform(t, input, local);
                if (Args.Present(input, "position")) changes.Add($"{(local ? "local " : "")}position {Fmt.V(local ? t.localPosition : t.position)}");
                if (Args.Present(input, "rotation")) changes.Add($"{(local ? "local " : "")}rotation {Fmt.V(local ? t.localEulerAngles : t.eulerAngles)}");
                if (Args.Present(input, "scale")) changes.Add($"scale {Fmt.V(t.localScale)}");
            }

            if (changes.Count == 0) throw new ToolException("Nothing to change: give name, active, parent, siblingIndex, position, rotation, scale, tag, layer or isStatic.");

            if (PrefabUtility.IsPartOfPrefabInstance(go))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(go);
                PrefabUtility.RecordPrefabInstancePropertyModifications(t);
            }
            EditorSceneManager.MarkSceneDirty(go.scene);
            var text = $"Updated {TargetResolver.Label(go)}: {string.Join("; ", changes)}.";
            if (notes.Count > 0) text += " Notes: " + string.Join(" ", notes);
            return ToolResult.Ok(text + PlayModeNote());
        }

        // ─── delete_gameobject ───────────────────────────────────────────────────

        public static ToolResult Delete(JObject input)
        {
            var go = TargetResolver.Resolve(Args.Required(input, "target"), out var note);
            if (PrefabUtility.IsPartOfPrefabInstance(go) && !PrefabUtility.IsOutermostPrefabInstanceRoot(go) && !PrefabUtility.IsAddedGameObjectOverride(go))
                throw new ToolException($"{go.name} is part of a prefab instance and cannot be deleted on its own; delete the prefab root ({PrefabUtility.GetOutermostPrefabInstanceRoot(go).name}) or edit the prefab asset.");
            var label = TargetResolver.Label(go);
            var children = go.GetComponentsInChildren<Transform>(true).Length - 1;
            var scene = go.scene;
            Undo.DestroyObjectImmediate(go);
            EditorSceneManager.MarkSceneDirty(scene);
            var text = $"Deleted {label}" + (children > 0 ? $" and its {Fmt.Plural(children, "child", "children")}." : ".") + " (undo can restore it)";
            if (note != null) text += " Note: " + note;
            return ToolResult.Ok(text + PlayModeNote());
        }

        // ─── scene_operation ─────────────────────────────────────────────────────

        static string ScenePath(string path)
        {
            path = FileTools.ForWrite(path);
            if (!path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) path += ".unity";
            return path;
        }

        static IEnumerable<Scene> DirtyScenes() => TargetResolver.LoadedScenes().Where(s => s.isDirty);

        public static ToolResult SceneOperation(JObject input)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new ToolException("Exit Play mode first (play_mode exit): scenes cannot be opened or saved while playing.");
            var action = Args.Required(input, "action").ToLowerInvariant();
            var additive = Args.Bool(input, "additive", false);
            var rawPath = Args.Str(input, "path");

            string UnsavedError() =>
                $"The open scene has unsaved changes ({string.Join(", ", DirtyScenes().Select(TargetResolver.SceneLabel))}). " +
                "Save it first with scene_operation save (or ask the user), or pass additive:true.";

            switch (action)
            {
                case "new":
                {
                    if (!additive && DirtyScenes().Any()) throw new ToolException(UnsavedError());
                    var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, additive ? NewSceneMode.Additive : NewSceneMode.Single);
                    var text = $"Created a new {(additive ? "additive " : "")}scene with a camera and a light.";
                    if (!string.IsNullOrWhiteSpace(rawPath))
                    {
                        var path = ScenePath(rawPath);
                        if (File.Exists(path)) throw new ToolException($"{path} already exists; the new scene is open but unsaved. Save it with save_as and another path.");
                        FileTools.EnsureFolder(Path.GetDirectoryName(path)?.Replace('\\', '/'));
                        if (!EditorSceneManager.SaveScene(scene, path)) throw new ToolException($"The new scene could not be saved to {path}.");
                        text += $" Saved as {path}.";
                    }
                    else text += " It is unsaved — use scene_operation save_as with a path to keep it.";
                    return ToolResult.Ok(text);
                }
                case "open":
                {
                    var path = ScenePath(Args.Required(input, "path"));
                    if (!File.Exists(path)) throw new ToolException($"No scene at {path}. Use find_assets \"t:Scene\" to list scenes.");
                    if (!additive && DirtyScenes().Any()) throw new ToolException(UnsavedError());
                    var scene = EditorSceneManager.OpenScene(path, additive ? OpenSceneMode.Additive : OpenSceneMode.Single);
                    return ToolResult.Ok($"Opened {path}{(additive ? " additively" : "")} ({scene.rootCount} root objects).");
                }
                case "save":
                {
                    var scene = SceneManager.GetActiveScene();
                    if (!string.IsNullOrWhiteSpace(rawPath))
                    {
                        var path = ScenePath(rawPath);
                        var loaded = TargetResolver.LoadedScenes().FirstOrDefault(s => s.path == path);
                        if (loaded.IsValid()) scene = loaded;
                        else if (!string.IsNullOrEmpty(scene.path))
                            throw new ToolException($"{path} is not open. To save the active scene under a new path use save_as.");
                        else
                        {
                            FileTools.EnsureFolder(Path.GetDirectoryName(path)?.Replace('\\', '/'));
                            if (!EditorSceneManager.SaveScene(scene, path)) throw new ToolException($"Could not save the scene to {path}.");
                            return ToolResult.Ok($"Saved the scene as {path}.");
                        }
                    }
                    if (string.IsNullOrEmpty(scene.path)) throw new ToolException("The scene has never been saved; use save_as with a path like \"Assets/Scenes/Main.unity\".");
                    if (!EditorSceneManager.SaveScene(scene)) throw new ToolException($"Could not save {scene.path}.");
                    return ToolResult.Ok($"Saved {scene.path}.");
                }
                case "save_as":
                {
                    var path = ScenePath(Args.Required(input, "path"));
                    if (File.Exists(path)) throw new ToolException($"{path} already exists. Pick another path, or use save to save that scene.");
                    var scene = SceneManager.GetActiveScene();
                    FileTools.EnsureFolder(Path.GetDirectoryName(path)?.Replace('\\', '/'));
                    if (!EditorSceneManager.SaveScene(scene, path)) throw new ToolException($"Could not save the scene to {path}.");
                    return ToolResult.Ok($"Saved the active scene as {path}.");
                }
                default:
                    throw new ToolException($"Unknown action '{action}'. Use new, open, save or save_as.");
            }
        }

        // ─── play_mode, execute_menu_item, undo ──────────────────────────────────

        public static ToolResult PlayMode(JObject input)
        {
            var action = Args.Required(input, "action").ToLowerInvariant();
            switch (action)
            {
                case "enter":
                    if (EditorApplication.isPlaying) return ToolResult.Ok("Already in Play mode.");
                    if (EditorUtility.scriptCompilationFailed) throw new ToolException("Scripts have compile errors; fix them before entering Play mode.");
                    EditorApplication.EnterPlaymode();
                    return ToolResult.Ok("Entering Play mode (starts after this step). Use console_logs and capture_view game to watch it; exit with play_mode exit.");
                case "exit":
                    if (!EditorApplication.isPlaying) return ToolResult.Ok("Not in Play mode.");
                    EditorApplication.ExitPlaymode();
                    return ToolResult.Ok("Exiting Play mode (scene changes made while playing are discarded).");
                case "pause":
                    if (!EditorApplication.isPlaying) throw new ToolException("Not in Play mode; nothing to pause.");
                    EditorApplication.isPaused = !EditorApplication.isPaused;
                    return ToolResult.Ok(EditorApplication.isPaused ? "Paused." : "Resumed.");
                default:
                    throw new ToolException($"Unknown action '{action}'. Use enter, exit or pause.");
            }
        }

        static readonly string[] BlockedMenus = { "File/Quit", "File/Exit", "Unity/Quit", "File/Build And Run", "File/Build Profiles", "Help/" };

        public static ToolResult MenuItem(JObject input)
        {
            var path = Args.Required(input, "path");
            if (BlockedMenus.Any(b => path.StartsWith(b, StringComparison.OrdinalIgnoreCase)))
                throw new ToolException($"'{path}' is not available to the agent; ask the user to do it.");
            if (!EditorApplication.ExecuteMenuItem(path))
                throw new ToolException($"Menu item '{path}' was not found or is disabled right now (check the exact path, e.g. \"GameObject/Align With View\").");
            return ToolResult.Ok($"Ran {path}.");
        }

        static readonly System.Reflection.MethodInfo GetRecords = typeof(Undo).GetMethod("GetRecords",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
            null, new[] { typeof(List<string>), typeof(List<string>) }, null);

        /// <summary>Names of the undo stack, newest last (internal API; empty when unavailable).</summary>
        static List<string> UndoNames()
        {
            var undo = new List<string>();
            try
            {
                GetRecords?.Invoke(null, new object[] { undo, new List<string>() });
            }
            catch (Exception)
            {
                undo.Clear();
            }
            return undo;
        }

        public static ToolResult Undo_(JObject input)
        {
            var steps = Args.Int(input, "steps", 1, 1, 20);
            var before = UndoNames();
            if (GetRecords != null && before.Count == 0) return ToolResult.Ok("Nothing to undo.");
            Undo.IncrementCurrentGroup();
            var done = 0;
            for (var i = 0; i < steps; i++)
            {
                Undo.PerformUndo();
                done++;
                if (GetRecords != null && UndoNames().Count == 0) break;
            }
            var after = UndoNames();
            var names = before.Count > after.Count ? before.Skip(after.Count).Reverse().ToList() : new List<string>();
            // The record list holds individual operations; the agent's own steps are the named groups.
            var agent = names.Where(n => n.StartsWith("Zetta Agent: ")).ToList();
            if (agent.Count > 0) names = agent;
            var text = $"Undid {Fmt.Plural(done, "step")}";
            if (names.Count > 0) text += ": " + string.Join("; ", names);
            return ToolResult.Ok(text + ".");
        }
    }
}
