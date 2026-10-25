using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// The project side of the Studio: checking images before they go up, uploading pictures and
    /// models, and finding a generation's imported copy (its zetta.json) in the project.
    /// </summary>
    public static class StudioAssets
    {
        public const int MinSide = 128;
        public const int MaxSide = 5000;
        public const long MaxImageBytes = 8L * 1024 * 1024;
        public const long MaxModelBytes = 100L * 1024 * 1024;

        // ─── Images ─────────────────────────────────────────────────────────────────

        /// <summary>Null when the picture is fine for 3D; otherwise a plain reason.</summary>
        public static string CheckImage(byte[] bytes, string contentType)
        {
            if (bytes == null || bytes.Length == 0) return "That image is empty.";
            if (contentType is not ("image/png" or "image/jpeg" or "image/webp")) return "Use a PNG, JPEG or WebP image.";
            if (bytes.Length > MaxImageBytes) return $"That image is {bytes.Length / 1024f / 1024f:0.#} MB — the limit is 8 MB.";
            var size = ImageSize(bytes);
            if (size == null) return "That image could not be read.";
            var (w, h) = size.Value;
            if (w < MinSide || h < MinSide) return $"That image is {w}×{h} px — each side needs at least {MinSide} px.";
            if (w > MaxSide || h > MaxSide) return $"That image is {w}×{h} px — each side can be at most {MaxSide:N0} px.";
            return null;
        }

        /// <summary>Width and height from a PNG, JPEG or WebP header, without decoding the picture.</summary>
        public static (int w, int h)? ImageSize(byte[] b)
        {
            try
            {
                if (b.Length > 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
                    return (BigEndian(b, 16), BigEndian(b, 20));

                if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
                {
                    var i = 2;
                    while (i + 9 < b.Length)
                    {
                        if (b[i] != 0xFF)
                        {
                            i++;
                            continue;
                        }
                        var marker = b[i + 1];
                        if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                        {
                            i += 2;
                            continue;
                        }
                        var length = (b[i + 2] << 8) | b[i + 3];
                        var isFrame = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                        if (isFrame) return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                        i += 2 + length;
                    }
                    return null;
                }

                if (b.Length > 30 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
                {
                    var chunk = System.Text.Encoding.ASCII.GetString(b, 12, 4);
                    switch (chunk)
                    {
                        case "VP8 ":
                            return (((b[27] << 8) | b[26]) & 0x3FFF, ((b[29] << 8) | b[28]) & 0x3FFF);
                        case "VP8L":
                            var bits = b[21] | (b[22] << 8) | (b[23] << 16) | (b[24] << 24);
                            return ((bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
                        case "VP8X":
                            return (1 + (b[24] | (b[25] << 8) | (b[26] << 16)), 1 + (b[27] | (b[28] << 8) | (b[29] << 16)));
                    }
                }
            }
            catch (IndexOutOfRangeException)
            {
            }
            return null;
        }

        static int BigEndian(byte[] b, int at) => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

        public static string ContentTypeOf(string path) => Path.GetExtension(path ?? "").ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => null,
        };

        public static string ExtensionFor(string contentType) => contentType switch
        {
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            _ => ".png",
        };

        /// <summary>"Uploading… 45%" — what every Studio upload says while it goes up.</summary>
        public static string UploadingText(float progress, string what = "Uploading") =>
            $"{what}… {Mathf.RoundToInt(Mathf.Clamp01(progress) * 100f)}%";

        /// <summary>
        /// Uploads a picture for the 3D engine and returns its URL. Optionally removes the background
        /// first (the result is a PNG) — the same order the web Studio uses. <paramref name="progress"/>
        /// reports the upload itself (0–1; large files go up in 8 MB pieces). Upload refusals (UPLOAD_TOO_LARGE,
        /// UPLOAD_WRONG_TYPE, UPLOAD_TICKET_EXPIRED, UPLOAD_FAILED) arrive as <see cref="ZettaApiException"/>s
        /// carrying the server's own message.
        /// </summary>
        public static async Task<string> UploadImageAsync(byte[] bytes, string contentType, bool removeBackground, Action<float> progress = null)
        {
            var problem = CheckImage(bytes, contentType);
            if (problem != null) throw new StudioProblem(problem);
            if (removeBackground)
            {
                bytes = await ZettaApi.RemoveBackgroundAsync(bytes);
                if (bytes == null || bytes.Length == 0) throw new StudioProblem("Could not remove the background. Try again, or turn Remove background off.");
                contentType = "image/png";
            }
            progress?.Invoke(0f);
            var ticket = await ZettaApi.UploadAsync(bytes, contentType, progress: progress);
            return ticket.FileUrl;
        }

        // ─── Models ─────────────────────────────────────────────────────────────────

        /// <summary>The generation a project model came from, if it was imported by Zetta 3D (zetta.json beside it).</summary>
        public static long? GenerationIdOf(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            var folder = Path.GetDirectoryName(assetPath);
            if (folder == null) return null;
            var manifest = Path.Combine(folder, "zetta.json");
            if (!File.Exists(manifest)) return null;
            try
            {
                var id = JObject.Parse(File.ReadAllText(manifest))["generationId"];
                return id?.Type == JTokenType.Integer ? (long?)id : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The main imported model asset for a generation, if it is in the project.</summary>
        public static GameObject FindImported(long generationId) => ZettaProjectIndex.FindModel(generationId);

        public static bool IsGlb(string assetPath) => Path.GetExtension(assetPath ?? "").Equals(".glb", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Puts a project .glb into the Zetta library (free) and returns the new row, so it can be
        /// retopologized, textured, rigged or converted.
        /// </summary>
        public static async Task<Generation> UploadModelAsync(string assetPath, Action<float> progress = null)
        {
            if (!IsGlb(assetPath)) throw new StudioProblem("Only .glb models can be uploaded. Export the model as GLB first.");
            var full = Path.GetFullPath(assetPath);
            if (!File.Exists(full)) throw new StudioProblem("That file is not on disk.");
            var bytes = File.ReadAllBytes(full);
            if (bytes.Length < 12 || bytes[0] != 'g' || bytes[1] != 'l' || bytes[2] != 'T' || bytes[3] != 'F')
                throw new StudioProblem("That file is not a valid binary glTF (.glb).");
            if (bytes.LongLength > MaxModelBytes) throw new StudioProblem("That model is larger than 100 MB, the most the library accepts. Retopologize or compress it first.");

            progress?.Invoke(0f);
            var ticket = await ZettaApi.UploadAsync(bytes, "model/gltf-binary", progress: progress);
            return await ZettaApi.CreateUploadPostAsync(ticket.Key, bytes.LongLength, Path.GetFileNameWithoutExtension(assetPath));
        }

        /// <summary>
        /// The library row for a project model: its own generation when Zetta imported it, otherwise
        /// an upload of the .glb.
        /// </summary>
        public static async Task<Generation> SourceFromAssetAsync(string assetPath, Action<string> status = null)
        {
            var id = GenerationIdOf(assetPath);
            if (id != null)
            {
                try
                {
                    status?.Invoke("Finding it in your library…");
                    var (generation, _) = await ZettaApi.GetGenerationAsync(id.Value);
                    if (generation != null && generation.IsSuccess) return generation;
                }
                catch (ZettaApiException e) when (e.Status == 404)
                {
                    // Imported from another account, or deleted: fall back to uploading.
                }
            }
            status?.Invoke(UploadingText(0f, "Uploading the model"));
            return await UploadModelAsync(assetPath, p => status?.Invoke(UploadingText(p, "Uploading the model")));
        }

        public static string SelectedModelPath()
        {
            var obj = Selection.activeObject;
            if (obj == null) return null;
            var path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path) && obj is GameObject go)
            {
                var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(go);
                if (source != null) path = AssetDatabase.GetAssetPath(source);
            }
            return string.IsNullOrEmpty(path) ? null : path;
        }

        /// <summary>Saves a picture into the project's images folder.</summary>
        public static string SaveImage(byte[] bytes, string contentType, string name, string textureType)
        {
            var folder = ZettaProjectSettings.Instance.ImagesFolder.TrimEnd('/');
            var path = $"{folder}/{ZettaImporter.Slug(name)}{ExtensionFor(contentType)}";
            return ZettaImporter.SaveImage(bytes, path, textureType);
        }
    }
}
