using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// Turns the protocol's object targets into GameObjects: <c>"Path/To/Object"</c> (hierarchy path
    /// from a root, active scene first, then the other loaded scenes), <c>"Scene.unity:Path"</c> for a
    /// specific loaded scene, or <c>"#1234"</c> (instance id). Inactive objects are included.
    /// </summary>
    static class TargetResolver
    {
        public const string NotFoundHint = "Use scene_hierarchy or find_gameobjects to get the exact path or #id.";

        /// <summary>Loaded scenes, active first.</summary>
        public static List<Scene> LoadedScenes()
        {
            var list = new List<Scene>();
            var active = SceneManager.GetActiveScene();
            if (active.IsValid() && active.isLoaded) list.Add(active);
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.IsValid() && s.isLoaded && s != active) list.Add(s);
            }
            return list;
        }

        /// <summary>Every GameObject in the loaded scenes (inactive included), depth-first per scene.</summary>
        public static IEnumerable<GameObject> AllObjects(Scene? only = null)
        {
            var scenes = only.HasValue ? new List<Scene> { only.Value } : LoadedScenes();
            foreach (var scene in scenes)
            foreach (var root in scene.GetRootGameObjects())
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                yield return t.gameObject;
        }

        /// <summary>Components of active objects in the loaded scenes (no FindObjectsByType: its overloads differ across versions).</summary>
        public static IEnumerable<T> Components<T>() where T : Component
        {
            foreach (var scene in LoadedScenes())
            foreach (var root in scene.GetRootGameObjects())
            foreach (var c in root.GetComponentsInChildren<T>(false))
                yield return c;
        }

        public static string SceneLabel(Scene scene)
        {
            if (!string.IsNullOrEmpty(scene.path)) return System.IO.Path.GetFileName(scene.path);
            return string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name;
        }

        /// <summary>Hierarchy path; prefixed with "Scene.unity:" when the object is not in the active scene.</summary>
        public static string PathOf(GameObject go, bool withScene = true)
        {
            if (go == null) return "(none)";
            var names = new List<string>();
            for (var t = go.transform; t != null; t = t.parent) names.Add(t.name);
            names.Reverse();
            var path = string.Join("/", names);
            if (!withScene) return path;
            var scene = go.scene;
            if (!scene.IsValid()) return path;
            if (scene != SceneManager.GetActiveScene()) return SceneLabel(scene) + ":" + path;
            return path;
        }

        public static string Label(GameObject go) => go == null ? "(none)" : $"{PathOf(go)} #{Ids.Of(go)}";

        /// <summary>Resolves or throws a <see cref="ToolException"/> with a hint.</summary>
        public static GameObject Resolve(string target) => Resolve(target, out _);

        public static GameObject Resolve(string target, out string note)
        {
            note = null;
            var go = TryResolve(target, out note, out var error);
            if (go == null) throw new ToolException(error);
            return go;
        }

        public static GameObject TryResolve(string target, out string note, out string error)
        {
            note = null;
            error = null;
            target = (target ?? "").Trim();
            if (target.Length == 0)
            {
                error = "No target given. " + NotFoundHint;
                return null;
            }

            if (target.StartsWith("#"))
            {
                if (!long.TryParse(target.Substring(1), out var id))
                {
                    error = $"'{target}' is not a valid #instanceId.";
                    return null;
                }
                var obj = Ids.Find(id);
                var go = obj as GameObject ?? (obj as Component)?.gameObject;
                if (go == null)
                {
                    error = $"No GameObject with id #{id} (it may have been deleted or the id changed after a reload). {NotFoundHint}";
                    return null;
                }
                if (UnityEditor.EditorUtility.IsPersistent(go))
                {
                    error = $"#{id} is an asset ({UnityEditor.AssetDatabase.GetAssetPath(go)}), not a scene object.";
                    return null;
                }
                return go;
            }

            // "Scene.unity:Path" — pick the scene.
            List<Scene> scenes = null;
            var colon = target.IndexOf(':');
            if (colon > 0)
            {
                var sceneName = target.Substring(0, colon).Trim();
                var match = FindScene(sceneName);
                if (match.HasValue)
                {
                    scenes = new List<Scene> { match.Value };
                    target = target.Substring(colon + 1).Trim();
                }
            }
            scenes ??= LoadedScenes();

            var segments = target.Trim('/').Split('/');
            var matches = new List<GameObject>();
            foreach (var scene in scenes)
            {
                foreach (var root in scene.GetRootGameObjects())
                    Match(root.transform, segments, 0, matches);
                if (matches.Count > 0) break;
            }

            if (matches.Count == 0 && !target.Contains("/"))
            {
                // A bare name: accept a unique-ish object anywhere in the hierarchy.
                foreach (var scene in scenes)
                    matches.AddRange(AllObjects(scene).Where(g => g.name == target).Take(10));
                if (matches.Count > 0)
                    note = $"'{target}' is not a root object; matched {PathOf(matches[0])}" +
                           (matches.Count > 1 ? $" (first of {matches.Count} objects with that name)." : ".");
            }
            else if (matches.Count > 1)
            {
                note = $"'{target}' matches {matches.Count} objects; used the first ({Label(matches[0])}). Use #id to pick another.";
            }

            if (matches.Count == 0)
            {
                error = $"No GameObject at '{target}'" + (scenes.Count == 1 ? $" in {SceneLabel(scenes[0])}" : "") + ". " + NotFoundHint;
                return null;
            }
            return matches[0];
        }

        static void Match(Transform t, string[] segments, int index, List<GameObject> matches)
        {
            if (matches.Count >= 10) return;
            // Names may contain '/', so a node can consume several segments.
            var name = t.name;
            var joined = segments[index];
            for (var end = index; end < segments.Length; end++)
            {
                if (end > index) joined += "/" + segments[end];
                if (joined.Length > name.Length) break;
                if (joined != name) continue;
                if (end == segments.Length - 1)
                {
                    matches.Add(t.gameObject);
                    return;
                }
                foreach (Transform child in t) Match(child, segments, end + 1, matches);
            }
        }

        public static Scene? FindScene(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return null;
            var bare = name.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 6) : name;
            foreach (var s in LoadedScenes())
            {
                if (string.Equals(s.path, name, StringComparison.OrdinalIgnoreCase)) return s;
                if (string.Equals(s.name, bare, StringComparison.OrdinalIgnoreCase)) return s;
                if (string.Equals(SceneLabel(s), name, StringComparison.OrdinalIgnoreCase)) return s;
            }
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && string.Equals(System.IO.Path.GetFileNameWithoutExtension(stage.assetPath), bare, StringComparison.OrdinalIgnoreCase))
                return stage.scene;
            return null;
        }

        /// <summary>A component on <paramref name="go"/> by type name (simple or full, case-insensitive).</summary>
        public static Component FindComponent(GameObject go, string typeName, int index, out string error)
        {
            error = null;
            typeName = (typeName ?? "").Trim();
            var all = go.GetComponents<Component>();
            var matches = all.Where(c => c != null && TypeMatches(c.GetType(), typeName)).ToList();
            if (matches.Count == 0)
            {
                var have = string.Join(", ", all.Select(c => c == null ? "Missing Script" : c.GetType().Name));
                error = $"{go.name} has no {typeName} component. It has: {have}.";
                return null;
            }
            if (index < 0 || index >= matches.Count)
            {
                error = $"{go.name} has {matches.Count} {typeName} component(s); index {index} is out of range.";
                return null;
            }
            return matches[index];
        }

        public static bool TypeMatches(Type type, string name)
        {
            for (var t = type; t != null && t != typeof(Object); t = t.BaseType)
                if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(t.FullName, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>A short label for Describe(): the last path segment, or the object's name for #id.</summary>
        public static string ShortName(string target)
        {
            target = (target ?? "").Trim();
            if (target.StartsWith("#") && long.TryParse(target.Substring(1), out var id))
            {
                try
                {
                    var o = Ids.Find(id);
                    if (o != null) return o.name;
                }
                catch (Exception)
                {
                    // Describe must never throw.
                }
                return target;
            }
            var colon = target.IndexOf(':');
            if (colon > 0 && target.Substring(0, colon).EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) target = target.Substring(colon + 1);
            var slash = target.TrimEnd('/').LastIndexOf('/');
            return slash >= 0 ? target.Substring(slash + 1) : target;
        }
    }
}
