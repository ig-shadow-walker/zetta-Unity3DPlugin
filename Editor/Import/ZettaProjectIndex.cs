using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Zetta3D.Editor
{
    /// <summary>
    /// Which Zetta generations are already in this project, found by the zetta.json each import writes beside
    /// the model. Built once and kept until a zetta.json is added, moved or deleted (or its folder is), so the
    /// Library can mark every card "In project" without scanning the project per card.
    /// </summary>
    public static class ZettaProjectIndex
    {
        public class Entry
        {
            public long GenerationId;
            /// <summary>The folder the import made, e.g. Assets/Zetta3D/Models/501-helmet.</summary>
            public string Folder;
            public string ManifestPath;

            /// <summary>Every model asset the import made (one, or a segmentation's parts), in name order.</summary>
            public List<string> ModelPaths =>
                AssetDatabase.FindAssets("t:GameObject", new[] { Folder })
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => Path.GetDirectoryName(p)?.Replace('\\', '/') == Folder)
                    .Distinct()
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToList();

            /// <summary>The model Unity imported there (a GLB/FBX/OBJ asset), or null if it was removed.</summary>
            public GameObject Model =>
                ModelPaths.Select(AssetDatabase.LoadAssetAtPath<GameObject>).FirstOrDefault(g => g != null);
        }

        static Dictionary<long, Entry> _entries;

        /// <summary>The project's imports changed. Raised on the main thread, at most once per editor update.</summary>
        public static event Action Changed;

        static Dictionary<long, Entry> Entries => _entries ??= Build();

        public static bool Contains(long generationId) => Find(generationId) != null;

        /// <summary>The newest import of a generation that still has its model, or null.</summary>
        public static Entry Find(long generationId)
        {
            if (!Entries.TryGetValue(generationId, out var entry)) return null;
            if (!AssetDatabase.IsValidFolder(entry.Folder))
            {
                Invalidate();
                return null;
            }
            return entry;
        }

        public static GameObject FindModel(long generationId) => Find(generationId)?.Model;

        public static void Invalidate()
        {
            if (_entries == null) return;
            _entries = null;
            EditorApplication.delayCall -= RaiseChanged;
            EditorApplication.delayCall += RaiseChanged;
        }

        static void RaiseChanged() => Changed?.Invoke();

        static Dictionary<long, Entry> Build()
        {
            var entries = new Dictionary<long, Entry>();
            foreach (var guid in AssetDatabase.FindAssets("zetta t:TextAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith("/zetta.json", StringComparison.Ordinal)) continue;
                long? id = null;
                try
                {
                    var token = JObject.Parse(File.ReadAllText(path))["generationId"];
                    if (token?.Type == JTokenType.Integer) id = (long)token;
                }
                catch (Exception)
                {
                    // A hand-edited or broken manifest is simply not an import we can vouch for.
                }
                if (id == null) continue;
                var folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
                // Two imports of one generation: keep the most recently written one.
                if (entries.TryGetValue(id.Value, out var existing) &&
                    File.GetLastWriteTimeUtc(existing.ManifestPath) >= File.GetLastWriteTimeUtc(path))
                    continue;
                entries[id.Value] = new Entry { GenerationId = id.Value, Folder = folder, ManifestPath = path };
            }
            return entries;
        }

        /// <summary>Keeps the index honest when imports appear, move or disappear.</summary>
        class Watcher : AssetPostprocessor
        {
            static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
            {
                if (_entries == null) return;
                if (imported.Any(IsManifest) || moved.Any(IsManifest) || movedFrom.Any(IsManifest) ||
                    deleted.Any(p => IsManifest(p) || _entries.Values.Any(e => e.Folder == p || e.Folder.StartsWith(p + "/"))) ||
                    movedFrom.Any(p => _entries.Values.Any(e => e.Folder == p || e.Folder.StartsWith(p + "/"))))
                    Invalidate();
            }

            static bool IsManifest(string path) => path.EndsWith("/zetta.json", StringComparison.Ordinal);
        }
    }
}
