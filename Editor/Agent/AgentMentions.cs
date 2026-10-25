using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// Something the user tagged in a message with @ — a scene object or a project asset — and how it is told to
    /// the agent (the Unity agent protocol, "Mentions and attached files").
    /// </summary>
    public class AgentMention
    {
        /// <summary>scene | asset</summary>
        public string Kind;
        /// <summary>Hierarchy path ("Player/Camera") or asset path ("Assets/Scripts/Player.cs").</summary>
        public string Path;
        public string TypeName;
        public long Id;
        public string ScenePath;
        /// <summary>Why it is here, when not typed: "copied from your computer".</summary>
        public string Note;

        public bool IsScene => Kind == "scene";
        public string Token => "@" + Path;
        public string Name => Path.Contains('/') ? Path.Substring(Path.LastIndexOf('/') + 1) : Path;

        public string Describe()
        {
            if (IsScene)
                return $"scene object \"{Path}\" ({TypeName} #{Id}{(string.IsNullOrEmpty(ScenePath) ? "" : ", scene " + ScenePath)}) — target it as \"{Path}\" or \"#{Id}\"";
            return $"asset \"{Path}\" ({TypeName})" + (string.IsNullOrEmpty(Note) ? "" : " — " + Note);
        }

        public static AgentMention FromObject(Object obj)
        {
            if (obj == null) return null;
            var assetPath = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(assetPath))
            {
                var main = AssetDatabase.LoadMainAssetAtPath(assetPath);
                var type = AssetDatabase.IsValidFolder(assetPath) ? "Folder" : (main != null ? main.GetType().Name : obj.GetType().Name);
                return new AgentMention { Kind = "asset", Path = assetPath, TypeName = type };
            }
            var go = obj as GameObject ?? (obj as Component)?.gameObject;
            if (go == null || !go.scene.IsValid()) return null;
            return Scene(go);
        }

        public static AgentMention Scene(GameObject go) => new AgentMention
        {
            Kind = "scene",
            Path = TargetResolver.PathOf(go, false),
            TypeName = "GameObject",
            Id = Ids.Of(go),
            ScenePath = go.scene.path,
        };

        public static AgentMention Asset(string path) =>
            FromObject(AssetDatabase.LoadMainAssetAtPath(path)) ?? new AgentMention { Kind = "asset", Path = path, TypeName = "File" };

        /// <summary>The block that closes a message, listing what each tag refers to.</summary>
        public static string Block(IEnumerable<AgentMention> mentions)
        {
            var list = mentions.ToList();
            if (list.Count == 0) return "";
            var sb = new StringBuilder("\n\n<mentions>");
            foreach (var m in list) sb.Append("\n- ").Append(m.Describe());
            return sb.Append("\n</mentions>").ToString();
        }

        // ─── Search ──────────────────────────────────────────────────────────────────

        /// <summary>Objects in every loaded scene (inactive included) whose name or path matches, best first.</summary>
        public static List<AgentMention> SearchScene(string query, int limit = 60)
        {
            query = (query ?? "").Trim();
            var found = new List<(GameObject go, int rank)>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    var go = t.gameObject;
                    if ((go.hideFlags & HideFlags.HideInHierarchy) != 0) continue;
                    var rank = Rank(go.name, TargetResolver.PathOf(go, false), query);
                    if (rank >= 0) found.Add((go, rank));
                }
            }
            return found.OrderBy(f => f.rank).ThenBy(f => f.go.transform.GetSiblingIndex()).Take(limit).Select(f => Scene(f.go)).ToList();
        }

        /// <summary>Assets under Assets/ matching the query (Unity's search syntax works too: "t:Prefab enemy").</summary>
        public static List<AgentMention> SearchProject(string query, int limit = 60)
        {
            query = (query ?? "").Trim();
            IEnumerable<string> paths;
            if (query.Length == 0)
                // Nothing typed yet: what was touched most recently is the likeliest pick.
                paths = AssetDatabase.FindAssets("", new[] { "Assets" }).Take(3000).Select(AssetDatabase.GUIDToAssetPath)
                    .Distinct().Where(p => !AssetDatabase.IsValidFolder(p)).OrderByDescending(p => System.IO.File.GetLastWriteTimeUtc(p));
            else if (query.Contains(':'))
                paths = AssetDatabase.FindAssets(query, new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath).Distinct();
            else
                paths = AssetDatabase.FindAssets(query, new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath).Distinct()
                    .Select(p => (p, rank: Rank(System.IO.Path.GetFileName(p), p, query)))
                    .Where(x => x.rank >= 0).OrderBy(x => x.rank).ThenBy(x => x.p.Length).Select(x => x.p);
            return paths.Take(limit).Select(p => new AgentMention
            {
                Kind = "asset",
                Path = p,
                TypeName = AssetDatabase.IsValidFolder(p) ? "Folder" : (AssetDatabase.GetMainAssetTypeAtPath(p)?.Name ?? "File"),
            }).ToList();
        }

        /// <summary>0 best (name starts with it) … 2 (path contains it), −1 no match. An empty query matches all.</summary>
        static int Rank(string name, string path, string query)
        {
            if (query.Length == 0) return 1;
            if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 0;
            if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            return path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ? 2 : -1;
        }

        public static Texture Icon(AgentMention m)
        {
            if (m.IsScene) return EditorGUIUtility.IconContent("GameObject Icon").image;
            return AssetDatabase.GetCachedIcon(m.Path);
        }
    }
}
