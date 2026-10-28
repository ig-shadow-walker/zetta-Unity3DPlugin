using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Zetta3D.Editor
{
    /// <summary>
    /// Brings a finished generation into the project: downloads its mesh (GLB via glTFast, or FBX
    /// natively), writes a small
    /// zetta.json beside it, imports, and optionally places it in the open scene sitting on the ground.
    /// Segmentation results arrive as several part GLBs, which are grouped under one parent. Gaussian
    /// splats (made on the web) are not supported in Unity and are refused.
    /// </summary>
    public static class ZettaImporter
    {
        public class Result
        {
            public string Folder;
            public List<string> AssetPaths = new List<string>();
            public GameObject Placed;
            public string MainAssetPath => AssetPaths.FirstOrDefault();
            /// <summary>A note for the user or the agent ("this is a collider", …).</summary>
            public string Note;
            /// <summary>A Smart UV job's layout picture, when one came in (also listed in AssetPaths).</summary>
            public string UvLayoutPath;
        }

        /// <summary>Why a splat row (a Gaussian splat or world made on the web) cannot be imported.</summary>
        public const string SplatUnsupported = "Gaussian splats are not supported in Unity — open it on zetta3d.ai.";

        public class Placement
        {
            public Transform Parent;
            public Vector3? Position;
            public bool Select = true;
        }

        public static string Slug(string text)
        {
            var slug = Regex.Replace((text ?? "model").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (slug.Length > 40) slug = slug.Substring(0, 40).Trim('-');
            return string.IsNullOrEmpty(slug) ? "model" : slug;
        }

        /// <summary>
        /// Which files to bring in: every segmentation part; a world mesh export's first GLB (the variant that
        /// was asked for); otherwise one mesh by preference. Pictures and splats are never among them.
        /// </summary>
        public static List<ResultFile> PickFiles(Generation generation)
        {
            var meshes = generation.MeshFiles.ToList();
            var glbs = meshes.Where(f => f.Extension == "glb").ToList();
            if (generation.Feature is "segmentation" or "smartSegmentation" && glbs.Count > 1)
                return glbs.OrderBy(f => PartIndex(f.FileName)).ToList();
            if (generation.Feature == "worldMeshExport" && glbs.Count > 0)
                return new List<ResultFile> { glbs[0] };

            var preferred = ZettaProjectSettings.Instance.PreferredFormat == "fbx" ? new[] { "fbx", "glb" } : new[] { "glb", "fbx" };
            foreach (var ext in preferred.Concat(new[] { "gltf", "obj", "zip" }))
            {
                var file = meshes.FirstOrDefault(f => f.Extension == ext);
                if (file != null) return new List<ResultFile> { file };
            }
            return new List<ResultFile>();
        }

        /// <summary>Whether this import is a physics proxy (a world's "Collider" mesh export).</summary>
        public static bool IsCollider(Generation generation) =>
            PickFiles(generation).Any(f => string.Equals(f.Label?.Trim(), "Collider", StringComparison.OrdinalIgnoreCase));

        static int PartIndex(string fileName)
        {
            var match = Regex.Match(fileName ?? "", @"-(\d+)\.glb$");
            return match.Success ? int.Parse(match.Groups[1].Value) : int.MaxValue;
        }

        /// <param name="progress">0–1 across every file the model has (downloading is ~95% of it), for a progress bar.</param>
        public static async Task<Result> ImportAsync(Generation generation, string folder = null, Placement place = null,
            Action<string> status = null, Action<float> progress = null)
        {
            if (generation.IsSplat) throw new InvalidOperationException(SplatUnsupported);
            if (!generation.IsSuccess) throw new InvalidOperationException("That model is not ready yet.");

            var files = PickFiles(generation);
            if (files.Count == 0) throw new InvalidOperationException("This generation has no mesh file Unity can import.");

            folder = string.IsNullOrEmpty(folder) ? ZettaProjectSettings.Instance.ModelsFolder : folder.TrimEnd('/');
            var slug = Slug(generation.DisplayTitle);
            var target = $"{folder}/{generation.Id}-{slug}";
            EnsureFolder(target);

            var result = new Result { Folder = target };
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                status?.Invoke(files.Count > 1 ? $"Downloading part {i + 1} of {files.Count}…" : "Downloading…");
                // The manifest's URLs expire after ~15 minutes — ask for a fresh one by key.
                var url = await ZettaApi.DownloadUrlAsync(generation.Id, file.Key) ?? file.Url;
                var index = i;
                var bytes = await ZettaHttp.DownloadAsync(url, default,
                    p => progress?.Invoke(0.95f * (index + p) / files.Count));
                // Already clean: the server takes the supplier's naming out of every file as it stores it.
                var name = files.Count > 1 ? $"{slug}-part{i + 1}" : slug;
                if (file.Extension == "zip") result.AssetPaths.AddRange(ExtractArchive(bytes, target, name));
                else
                {
                    var path = $"{target}/{name}.{file.Extension}";
                    Agent.AgentChanges.WillWrite(path);
                    File.WriteAllBytes(path, bytes);
                    result.AssetPaths.Add(path);
                }
            }

            WriteManifest(target, generation);

            status?.Invoke("Importing…");
            progress?.Invoke(0.96f);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            foreach (var path in result.AssetPaths)
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            if (result.AssetPaths.Any(p => p.EndsWith(".glb")) && !GltfImporterPresent())
                Debug.LogWarning("[Zetta 3D] GLB files need the glTFast package (com.unity.cloud.gltfast). Add it in the Package Manager, or set the preferred format to FBX in Zetta 3D settings.");

            if (IsCollider(generation))
                result.Note = "This is a collider — a physics proxy. When placed it gets a MeshCollider on every mesh and its renderers are turned off.";

            if (place != null) result.Placed = Place(generation, result.AssetPaths, place);

            // Smart UV: the layout picture comes in beside the model. A missing picture never fails the import.
            if (generation.Feature == "uvUnwrap" && generation.UvLayout != null)
            {
                try
                {
                    status?.Invoke("Downloading the UV layout…");
                    var layout = await ImportUvLayoutAsync(generation, target);
                    if (layout != null)
                    {
                        result.UvLayoutPath = layout;
                        result.AssetPaths.Add(layout);
                        if (layout.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                            result.Note = $"The UV layout picture is WebP, which Unity does not import as a texture; it is saved as {layout}.";
                    }
                }
                catch (Exception e)
                {
                    result.Note = "The UV layout picture could not be downloaded: " + e.Message;
                }
            }

            status?.Invoke("Imported");
            progress?.Invoke(1f);
            return result;
        }

        /// <summary>
        /// Downloads a Smart UV job's layout picture next to its model as <c>&lt;slug&gt;-uv-layout.&lt;ext&gt;</c> and
        /// returns its path; null when the job has none. PNG / JPEG land as textures. WebP — which Unity
        /// does not import — is kept as the raw .webp (a plain asset) so the picture is still in the project.
        /// </summary>
        public static async Task<string> ImportUvLayoutAsync(Generation generation, string folder = null)
        {
            var file = generation?.UvLayout;
            if (file == null) return null;
            var slug = Slug(generation.DisplayTitle);
            folder = string.IsNullOrEmpty(folder)
                ? $"{ZettaProjectSettings.Instance.ModelsFolder}/{generation.Id}-{slug}"
                : folder.TrimEnd('/');
            EnsureFolder(folder);

            var url = await ZettaApi.DownloadUrlAsync(generation.Id, file.Key) ?? file.Url;
            var bytes = await ZettaHttp.DownloadAsync(url);
            var extension = ImageExtension(bytes) ?? (file.Extension is "png" or "jpg" or "jpeg" or "webp" ? file.Extension : "png");
            var path = $"{folder}/{slug}-uv-layout.{extension}";

            if (extension == "webp" || File.Exists(path))
            {
                /* Re-imports overwrite the copy already there rather than piling up -1, -2 … */
                Agent.AgentChanges.WillWrite(path);
                File.WriteAllBytes(path, bytes);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                return path;
            }
            return SaveImage(bytes, path);
        }

        /// <summary>The image format by its magic bytes: png, jpg, webp, or null.</summary>
        static string ImageExtension(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 12) return null;
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47) return "png";
            if (bytes[0] == 0xff && bytes[1] == 0xd8) return "jpg";
            if (bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P') return "webp";
            return null;
        }

        /// <summary>
        /// Unpacks a model archive (an OBJ bundle: model, MTL, textures) into the folder; returns the model
        /// files in it. Entry names keep only their file name, so nothing lands outside the folder.
        /// </summary>
        static List<string> ExtractArchive(byte[] bytes, string target, string slug)
        {
            var models = new List<string>();
            using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(bytes), System.IO.Compression.ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                var name = Path.GetFileName(entry.FullName);
                if (string.IsNullOrEmpty(name) || name.StartsWith(".", StringComparison.Ordinal) || entry.FullName.Contains("__MACOSX")) continue;
                var path = $"{target}/{name}";
                Agent.AgentChanges.WillWrite(path);
                using (var input = entry.Open())
                using (var output = File.Create(path))
                    input.CopyTo(output);
                var extension = Path.GetExtension(name).ToLowerInvariant();
                if (extension is ".obj" or ".fbx" or ".glb" or ".gltf") models.Add(path);
            }
            if (models.Count == 0) throw new InvalidOperationException($"The archive for {slug} has no model Unity can import.");
            return models;
        }

        /// <summary>The small zetta.json written beside every import.</summary>
        internal static void WriteManifest(string target, Generation generation, JObject extra = null)
        {
            var manifest = JObject.FromObject(new
            {
                generationId = generation.Id,
                title = generation.DisplayTitle,
                feature = generation.Feature,
                model = generation.Model,
                prompt = generation.InputPrompt,
                faceCount = generation.FaceCount,
                importedAt = DateTime.UtcNow,
                site = $"{ZettaConfig.SiteBase}/3d-studio?generation={generation.Id}",
            });
            if (extra != null) manifest.Merge(extra);
            Agent.AgentChanges.WillWrite($"{target}/zetta.json");
            File.WriteAllText($"{target}/zetta.json", manifest.ToString(Formatting.Indented), Encoding.UTF8);
        }

        public static GameObject Place(Generation generation, IList<string> assetPaths, Placement place)
        {
            var models = assetPaths.Select(AssetDatabase.LoadAssetAtPath<GameObject>).Where(g => g != null).ToList();
            if (models.Count == 0) return null;

            var collider = IsCollider(generation);
            var title = collider ? $"{generation.DisplayTitle} (Collider)" : generation.DisplayTitle;
            GameObject root;
            if (models.Count == 1)
            {
                root = (GameObject)PrefabUtility.InstantiatePrefab(models[0]);
                root.name = title;
            }
            else
            {
                root = new GameObject(title);
                foreach (var model in models)
                {
                    var part = (GameObject)PrefabUtility.InstantiatePrefab(model);
                    part.transform.SetParent(root.transform, false);
                }
            }

            Undo.RegisterCreatedObjectUndo(root, $"Place {root.name}");
            if (place.Parent != null) Undo.SetTransformParent(root.transform, place.Parent, "Parent model");

            var position = place.Position ?? DefaultPosition();
            root.transform.position = position;
            Ground(root);
            if (collider) MakeCollider(root);

            EditorSceneManager.MarkSceneDirty(root.scene);
            if (place.Select)
            {
                Selection.activeGameObject = root;
                SceneView.lastActiveSceneView?.FrameSelected();
            }
            return root;
        }

        /// <summary>
        /// Turns a placed model into a physics proxy: a MeshCollider (sharedMesh) on every mesh, renderers off.
        /// </summary>
        public static void MakeCollider(GameObject root)
        {
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null) continue;
                if (!filter.TryGetComponent(out MeshCollider meshCollider)) meshCollider = Undo.AddComponent<MeshCollider>(filter.gameObject);
                meshCollider.sharedMesh = filter.sharedMesh;
            }
            foreach (var skinned in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (skinned.sharedMesh == null) continue;
                if (!skinned.TryGetComponent(out MeshCollider meshCollider)) meshCollider = Undo.AddComponent<MeshCollider>(skinned.gameObject);
                meshCollider.sharedMesh = skinned.sharedMesh;
            }
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = false;
        }

        /// <summary>In front of the Scene view camera, on the ground plane.</summary>
        static Vector3 DefaultPosition()
        {
            var view = SceneView.lastActiveSceneView;
            if (view == null) return Vector3.zero;
            var pivot = view.pivot;
            return new Vector3(pivot.x, 0f, pivot.z);
        }

        /// <summary>Lifts or lowers the object so its lowest renderer bound sits at its pivot's height.</summary>
        public static void Ground(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;
            var bounds = renderers[0].bounds;
            foreach (var r in renderers.Skip(1)) bounds.Encapsulate(r.bounds);
            root.transform.position += new Vector3(0, root.transform.position.y - bounds.min.y, 0);
        }

        public static void EnsureFolder(string path)
        {
            path = path.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(path)) return;
            Agent.AgentChanges.WillCreateFolder(path);
            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            // A folder on disk that Unity has not seen yet: import just it — a full Refresh could start a
            // script compilation in the middle of an agent tool batch.
            if (Directory.Exists(path)) AssetDatabase.ImportAsset(path);
            else AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        /// <summary>Saves image bytes into the project as a texture asset and returns its path.</summary>
        public static string SaveImage(byte[] bytes, string path, string textureType = "default")
        {
            path = path.Replace('\\', '/');
            if (!path.StartsWith("Assets/")) throw new ArgumentException("Images must be saved under Assets/.");
            EnsureFolder(Path.GetDirectoryName(path)?.Replace('\\', '/'));
            path = AssetDatabase.GenerateUniqueAssetPath(path);
            Agent.AgentChanges.WillWrite(path);
            File.WriteAllBytes(path, bytes);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                switch (textureType)
                {
                    case "sprite":
                        importer.textureType = TextureImporterType.Sprite;
                        importer.spriteImportMode = SpriteImportMode.Single;
                        break;
                    case "normal":
                        importer.textureType = TextureImporterType.NormalMap;
                        break;
                    default:
                        importer.textureType = TextureImporterType.Default;
                        break;
                }
                importer.SaveAndReimport();
            }
            return path;
        }

        public static bool GltfImporterPresent() =>
            UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/com.unity.cloud.gltfast") != null;
    }
}
