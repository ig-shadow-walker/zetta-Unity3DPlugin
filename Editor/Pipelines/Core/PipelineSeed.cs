using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Zetta3D.Editor.Pipelines
{
    /// <summary>
    /// What one run starts from — the seed node's request for that run (run-dialog.tsx RunSeed): an uploaded picture
    /// <c>{uploadKey, name}</c>, a library picture <c>{gen2dPostId}</c>, or a library model <c>{gen3dPostId}</c> — plus a
    /// name and a small JPEG preview for the UI.
    /// </summary>
    public sealed class PipelineSeed
    {
        /// <summary>Longest edge a seed picture is uploaded at — the web's downscaleForUpload (lib/downscale.ts).</summary>
        public const int MaxEdge = 1536;
        /// <summary>The preview's longest edge (thumbnail.ts).</summary>
        public const int ThumbnailSize = 160;

        /// <summary>The seed node's request for the run — what <c>PipelinesApi.BatchAsync</c> sends.</summary>
        public JObject Request = new JObject();
        public string Name;
        /// <summary>picture | model</summary>
        public string Type = "picture";
        /// <summary>A ≤160px JPEG of the picture, when one could be made; null otherwise.</summary>
        public byte[] Thumbnail;
        /// <summary>A URL the UI may load instead (a library row's thumbnail), when there is no <see cref="Thumbnail"/>.</summary>
        public string ThumbnailUrl;

        /// <summary>The preview as a data URL (what the web keeps in a graph).</summary>
        public string ThumbnailDataUrl => Thumbnail == null ? null : "data:image/jpeg;base64," + Convert.ToBase64String(Thumbnail);

        /// <summary>
        /// A picture from disk or memory, uploaded as the Studio uploads a reference: scaled down to <see cref="MaxEdge"/>
        /// (re-encoded as JPEG 90 only when it was larger, as the web does), then up through the upload relay
        /// (<see cref="ZettaApi.UploadAsync"/>). Call from the main thread.
        /// </summary>
        public static async Task<PipelineSeed> FromUploadAsync(byte[] bytes, string contentType, string name, CancellationToken cancel = default, Action<float> progress = null)
        {
            if (bytes == null || bytes.Length == 0) throw new ArgumentException("The picture is empty.");
            var ready = DownscaleForUpload(bytes, contentType, out var readyType);
            var ticket = await ZettaApi.UploadAsync(ready, readyType, cancel, progress);
            var fileName = string.IsNullOrWhiteSpace(name) ? "picture" : name;
            return new PipelineSeed
            {
                Request = new JObject { ["uploadKey"] = ticket.Key, ["name"] = fileName },
                Name = fileName,
                Thumbnail = ThumbnailOf(ready),
            };
        }

        /// <summary>A picture from the user's Image Studio library.</summary>
        public static PipelineSeed FromImagePost(long gen2dPostId, string name = null, string thumbnailUrl = null) => new PipelineSeed
        {
            Request = new JObject { ["gen2dPostId"] = gen2dPostId },
            Name = name ?? $"Picture #{gen2dPostId}",
            ThumbnailUrl = thumbnailUrl,
        };

        /// <summary>A model from the user's 3D library (a finished generation).</summary>
        public static PipelineSeed FromModel(long gen3dPostId, string name = null, string thumbnailUrl = null) => new PipelineSeed
        {
            Request = new JObject { ["gen3dPostId"] = gen3dPostId },
            Name = name ?? $"Model #{gen3dPostId}",
            Type = "model",
            ThumbnailUrl = thumbnailUrl,
        };

        /// <summary>A model from the library, by its generation.</summary>
        public static PipelineSeed FromModel(Generation generation) =>
            FromModel(generation.Id, generation.DisplayTitle, generation.ThumbnailUrl);

        /// <summary>Downloads a picture (a signed library URL) and makes its preview; null when it cannot.</summary>
        public static async Task<byte[]> ThumbnailFromUrlAsync(string url, CancellationToken cancel = default)
        {
            if (string.IsNullOrEmpty(url)) return null;
            try
            {
                return ThumbnailOf(await ZettaHttp.DownloadAsync(url, cancel));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// A picture no larger than <see cref="MaxEdge"/> on its longest side. One already small enough is sent as it is;
        /// one Unity cannot decode is left for the server to judge.
        /// </summary>
        public static byte[] DownscaleForUpload(byte[] bytes, string contentType, out string resultType)
        {
            resultType = string.IsNullOrEmpty(contentType) ? "image/png" : contentType;
            var texture = Decode(bytes);
            if (texture == null) return bytes;
            try
            {
                var longest = Math.Max(texture.width, texture.height);
                if (longest <= MaxEdge) return bytes;
                using var scaled = new Scratch(Resize(texture, MaxEdge));
                resultType = "image/jpeg";
                return scaled.Texture.EncodeToJPG(90);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        /// <summary>A ≤160px JPEG (quality 80) of a picture, or null when it cannot be read.</summary>
        public static byte[] ThumbnailOf(byte[] bytes)
        {
            var texture = Decode(bytes);
            if (texture == null) return null;
            try
            {
                using var scaled = new Scratch(Resize(texture, ThumbnailSize));
                return scaled.Texture.EncodeToJPG(80);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        static Texture2D Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (texture.LoadImage(bytes, false)) return texture;
            }
            catch (Exception)
            {
                // Not decodable here: fall through.
            }
            UnityEngine.Object.DestroyImmediate(texture);
            return null;
        }

        /// <summary>
        /// A box-filtered copy no larger than <paramref name="maxEdge"/> — on the CPU, so it works in batch mode and
        /// without a GPU. A picture already small enough is copied as it is.
        /// </summary>
        static Texture2D Resize(Texture2D source, int maxEdge)
        {
            var scale = Math.Min(1.0, maxEdge / (double)Math.Max(source.width, source.height));
            var width = Math.Max(1, (int)Math.Round(source.width * scale));
            var height = Math.Max(1, (int)Math.Round(source.height * scale));
            var src = source.GetPixels32();
            var dst = new Color32[width * height];
            var sx = source.width / (double)width;
            var sy = source.height / (double)height;
            for (var y = 0; y < height; y++)
            {
                var y0 = (int)(y * sy);
                var y1 = Math.Max(y0 + 1, Math.Min(source.height, (int)Math.Ceiling((y + 1) * sy)));
                for (var x = 0; x < width; x++)
                {
                    var x0 = (int)(x * sx);
                    var x1 = Math.Max(x0 + 1, Math.Min(source.width, (int)Math.Ceiling((x + 1) * sx)));
                    long r = 0, g = 0, b = 0, a = 0;
                    var n = 0;
                    for (var yy = y0; yy < y1; yy++)
                    {
                        var row = yy * source.width;
                        for (var xx = x0; xx < x1; xx++)
                        {
                            var p = src[row + xx];
                            r += p.r;
                            g += p.g;
                            b += p.b;
                            a += p.a;
                            n++;
                        }
                    }
                    n = Math.Max(1, n);
                    dst[y * width + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
                }
            }
            var result = new Texture2D(width, height, TextureFormat.RGBA32, false);
            result.SetPixels32(dst);
            result.Apply(false, false);
            return result;
        }

        /// <summary>A temporary texture destroyed when done.</summary>
        sealed class Scratch : IDisposable
        {
            public readonly Texture2D Texture;
            public Scratch(Texture2D texture) => Texture = texture;
            public void Dispose() => UnityEngine.Object.DestroyImmediate(Texture);
        }
    }
}
