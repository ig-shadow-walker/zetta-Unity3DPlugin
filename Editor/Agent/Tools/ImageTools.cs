using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Zetta3D.Editor.Studio;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// The 2D Studio for the agent (protocol v1.8): drawing in the look of references, editing one picture, composing
    /// several, and browsing the user's 2D library. Run here rather than on the server because a picture may be a file
    /// in the project — read from disk and sent up inline, as the Studio does — or one already in the library, sent by id.
    /// Each result is a new library picture: drawn in the chat, remembered for save_image_asset, and named to the model.
    /// </summary>
    public static class ImageTools
    {
        const int MaxSide = 2048;

        /// <summary>A picture to send: a library id, or the bytes of a project image.</summary>
        sealed class Source
        {
            public long? Id;
            public InlineImage Inline;
            public string Label;
        }

        public static async Task<ToolResult> GenerateWithReferences(JObject input, ToolRunContext ctx)
        {
            ZettaTools.RequireSignIn();
            var prompt = Prompt(input);
            var sources = Sources(input, "references", 1, 3);
            ctx.Status("Reading the references…");
            var references = new List<InlineImage>();
            foreach (var source in sources) references.Add(await Inline(await Resolve(source, ctx), ctx));
            ctx.Status("Drawing…");
            var result = await ZettaApi.GenerateImageAsync(prompt, Frame(input), Size(input), styleReferences: references);
            return await Made(result, input, $"in the look of {Fmt.Plural(references.Count, "reference", "references")}", ctx);
        }

        public static async Task<ToolResult> Edit(JObject input, ToolRunContext ctx)
        {
            ZettaTools.RequireSignIn();
            var prompt = Prompt(input);
            var image = await Resolve(Args.Get(input, "image") ?? throw new ToolException("'image' is required: { imageId } or { path }."), ctx);
            ctx.Status("Editing…");
            var result = await ZettaApi.EditImageAsync(prompt, image.Inline, image.Id, Frame(input), Size(input));
            return await Made(result, input, $"an edit of {image.Label}", ctx);
        }

        public static async Task<ToolResult> Compose(JObject input, ToolRunContext ctx)
        {
            ZettaTools.RequireSignIn();
            var prompt = Prompt(input);
            var tokens = Sources(input, "images", 2, 14);
            ctx.Status("Reading the pictures…");
            var sources = new List<Source>();
            foreach (var token in tokens) sources.Add(await Resolve(token, ctx));

            // The server puts library pictures before uploaded ones, so a mix is all sent inline: the order the prompt names stays true.
            List<InlineImage> inline;
            List<long> ids;
            if (sources.All(s => s.Id != null))
            {
                inline = null;
                ids = sources.Select(s => s.Id.Value).ToList();
            }
            else
            {
                inline = new List<InlineImage>();
                foreach (var source in sources) inline.Add(await Inline(source, ctx));
                ids = null;
            }
            ctx.Status("Composing…");
            var result = await ZettaApi.ComposeImagesAsync(prompt, inline, ids, Frame(input), Size(input));
            return await Made(result, input, $"composed from {string.Join(", ", sources.Select(s => s.Label))}", ctx);
        }

        public static async Task<ToolResult> List(JObject input, ToolRunContext ctx)
        {
            ZettaTools.RequireSignIn();
            var limit = Args.Int(input, "limit", 20, 1, 50);
            var search = Args.Str(input, "search");
            var page = await ZettaApi.ListImagesAsync(1, limit, search);
            var items = page?.Items?.Where(p => string.IsNullOrEmpty(p.ErrorMessage) && p.Images.Count > 0).ToList() ?? new List<ImagePost>();
            if (items.Count == 0)
                return ToolResult.Ok(string.IsNullOrWhiteSpace(search) ? "The user's 2D library is empty." : $"No pictures match \"{search.Trim()}\".");
            var lines = items.Select(p =>
                $"#{p.Id} · {FeatureLabel(p.Feature)} · {p.AspectRatio ?? "?"} {p.Resolution ?? ""} · {p.CreatedAt:yyyy-MM-dd} · \"{Fmt.Short((p.Prompt ?? "").Replace('\n', ' '), 140)}\"");
            var more = page.Total > items.Count ? $"\n…{page.Total - items.Count} more — search to narrow it." : "";
            return ToolResult.Ok($"{page.Total} picture(s) in the user's 2D library, newest first (imageId · how it was made · frame · date · prompt):\n" +
                                 string.Join("\n", lines) + more);
        }

        static string FeatureLabel(string feature) => feature switch
        {
            "editImg" => "edit",
            "composeImg" => "composite",
            _ => "generated",
        };

        // ─── Inputs ──────────────────────────────────────────────────────────────

        static string Prompt(JObject input)
        {
            var prompt = (Args.Str(input, "prompt") ?? "").Trim();
            if (prompt.Length == 0) throw new ToolException("'prompt' is required.");
            if (prompt.Length > 5000) throw new ToolException("The prompt is longer than 5,000 characters.");
            return prompt;
        }

        static string Frame(JObject input) => (Args.Str(input, "aspectRatio") ?? "1:1").Trim();

        static string Size(JObject input)
        {
            var size = (Args.Str(input, "resolution") ?? "1K").Trim().ToUpperInvariant();
            if (size != "1K" && size != "2K") throw new ToolException("'resolution' must be 1K or 2K.");
            return size;
        }

        static List<JToken> Sources(JObject input, string key, int min, int max)
        {
            var list = Args.Get(input, key) as JArray ?? throw new ToolException($"'{key}' is required: a list of {{ imageId }} or {{ path }}.");
            if (list.Count < min || list.Count > max)
                throw new ToolException(min == max ? $"'{key}' takes {min} picture(s)." : $"'{key}' takes {min} to {max} pictures; got {list.Count}.");
            return list.ToList();
        }

        /// <summary>{ imageId } or { path }: a library id as it is, a project image read and made acceptable to the API.</summary>
        static Task<Source> Resolve(JToken token, ToolRunContext ctx)
        {
            if (!(token is JObject source)) throw new ToolException("A picture is { imageId } or { path }.");
            var id = Args.LongN(source, "imageId");
            var path = Args.Str(source, "path");
            if (id != null && !string.IsNullOrWhiteSpace(path)) throw new ToolException("Give a picture's imageId or its path, not both.");
            if (id != null)
            {
                if (id <= 0) throw new ToolException("'imageId' must be positive.");
                return Task.FromResult(new Source { Id = id, Label = $"image #{id}" });
            }
            if (string.IsNullOrWhiteSpace(path)) throw new ToolException("A picture needs an imageId (from the 2D library) or a path (in the project).");
            path = path.Trim();
            if (path.StartsWith(AttachedPictures.Prefix, StringComparison.Ordinal))
            {
                // A picture the user attached to a message: kept outside the project, read from there.
                var file = AttachedPictures.FileOf(path) ?? throw new ToolException($"'{path}' is no longer available — ask the user to attach it again.");
                var bytes = File.ReadAllBytes(file);
                return Task.FromResult(new Source { Inline = Acceptable(bytes, ZettaTools.ImageContentType(bytes), path), Label = path });
            }
            path = FileTools.ForRead(path);
            return Task.FromResult(new Source { Inline = ReadProjectImage(path), Label = path });
        }

        /// <summary>The bytes to send for a source: a project image as read, a library picture downloaded (style references go up inline).</summary>
        static async Task<InlineImage> Inline(Source source, ToolRunContext ctx)
        {
            if (source.Inline != null) return source.Inline;
            var url = await ZettaTools.ImageUrl(source.Id.Value, ctx);
            var bytes = await ZettaHttp.DownloadAsync(url);
            if (bytes == null || bytes.Length == 0) throw new ToolException($"Image #{source.Id} downloaded empty.");
            return Acceptable(bytes, ZettaTools.ImageContentType(bytes), $"image #{source.Id}");
        }

        /// <summary>
        /// A project image as the API takes it: PNG, JPEG or WebP between 128 and 2,048 px a side and under 8 MB. Anything
        /// Unity imports as a texture but the API doesn't read (PSD, TGA, EXR…) goes through the importer to a PNG.
        /// </summary>
        internal static InlineImage ReadProjectImage(string path)
        {
            var physical = FileTools.Physical(path);
            if (!File.Exists(physical)) throw new ToolException($"'{path}' does not exist.");
            var type = StudioAssets.ContentTypeOf(path);
            if (type != null) return Acceptable(File.ReadAllBytes(physical), type, path);

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null) throw new ToolException($"'{path}' is not an image (use a PNG, JPEG, WebP or a texture Unity imports).");
            return Acceptable(Encode(texture, texture.width, texture.height), "image/png", path);
        }

        static InlineImage Acceptable(byte[] bytes, string type, string label)
        {
            if (StudioAssets.CheckImage(bytes, type) == null && StudioAssets.ImageSize(bytes) is { } size && Math.Max(size.Item1, size.Item2) <= MaxSide)
                return InlineImage.From(bytes, type);

            // Too big, too small or too heavy: resampled into range (alpha kept).
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!texture.LoadImage(bytes)) throw new ToolException($"{label} could not be read as an image.");
                var scale = Math.Min((float)MaxSide / Math.Max(texture.width, texture.height), 1f);
                scale = Math.Max(scale, (float)StudioAssets.MinSide / Math.Min(texture.width, texture.height));
                var w = Mathf.Clamp(Mathf.RoundToInt(texture.width * scale), StudioAssets.MinSide, MaxSide);
                var h = Mathf.Clamp(Mathf.RoundToInt(texture.height * scale), StudioAssets.MinSide, MaxSide);
                var png = Encode(texture, w, h);
                if (png.Length > StudioAssets.MaxImageBytes) throw new ToolException($"{label} is too large to send, even scaled to {w}×{h}.");
                return InlineImage.From(png, "image/png");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        /// <summary>PNG of any texture at a size, alpha kept — through a RenderTexture, so an unreadable import works too.</summary>
        static byte[] Encode(Texture texture, int width, int height)
        {
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            Graphics.Blit(texture, rt);
            RenderTexture.active = rt;
            var copy = new Texture2D(width, height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            copy.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            var png = copy.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(copy);
            return png;
        }

        // ─── The result ──────────────────────────────────────────────────────────

        /// <summary>A new library picture: drawn in the chat, its link remembered, its id told to the model.</summary>
        static async Task<ToolResult> Made(ImageResult result, JObject input, string what, ToolRunContext ctx)
        {
            if (result?.Images == null || result.Images.Count == 0)
                return ToolResult.Error(string.IsNullOrWhiteSpace(result?.Text) ? "No picture came back. Try describing it differently." : result.Text.Trim());
            if (result.PostId is not { } id)
                return ToolResult.Error("The picture was made but could not be saved to the library, so it cannot be used. Try again.");

            string url = null;
            try
            {
                url = await ZettaTools.ImageUrl(id, ctx);
            }
            catch (Exception)
            {
                // The picture is in the library either way; only the chat preview is missing.
            }
            var ok = ToolResult.Ok(
                $"Made image #{id} ({Frame(input)}, {Size(input)}), {what}. It is in the user's 2D library and shown to them. " +
                $"Use imageId {id} with save_image_asset to put it in the project (replace: true to overwrite a project file), " +
                "with edit_image or compose_images to change it further, or with generate_3d_model or process_model.");
            ok.ShowImageId = id;
            ok.ShowImageUrl = url;
            return ok;
        }

        // ─── Labels ──────────────────────────────────────────────────────────────

        public static string Describe(string name, JObject i)
        {
            switch (name)
            {
                case "generate_image_with_references":
                    return $"Drawing a picture from {Fmt.Plural((Args.Get(i, "references") as JArray)?.Count ?? 0, "reference", "references")}";
                case "edit_image":
                    return $"Editing {SourceLabel(Args.Get(i, "image"))}";
                case "compose_images":
                    return $"Composing {(Args.Get(i, "images") as JArray)?.Count ?? 0} pictures";
                case "list_images":
                    var search = Args.Str(i, "search");
                    return string.IsNullOrWhiteSpace(search) ? "Browsing your pictures" : $"Searching your pictures for \"{Fmt.Short(search, 50)}\"";
                default:
                    return null;
            }
        }

        static string SourceLabel(JToken token) =>
            token is JObject o
                ? Args.LongN(o, "imageId") is { } id ? $"image #{id}" : Path.GetFileName(Args.Str(o, "path") ?? "") is { Length: > 0 } file ? file : "a picture"
                : "a picture";
    }
}
