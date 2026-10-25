using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// list_generations, import_generation, save_image_asset, generate_3d_model — the tools that talk to Zetta 3D.
    /// The paid post-process tool (process_model) is in ZettaPaidTools.cs.
    /// </summary>
    static partial class ZettaTools
    {
        static void RequireSignIn()
        {
            if (!ZettaAuth.IsSignedIn)
                throw new ToolException("Not signed in to Zetta 3D. Ask the user to sign in from the Zetta 3D window, then try again.");
        }

        // ─── list_generations ────────────────────────────────────────────────────

        public static async Task<ToolResult> List(JObject input, ToolRunContext ctx)
        {
            RequireSignIn();
            var search = Args.Str(input, "search");
            var limit = Args.Int(input, "limit", 20, 1, 50);
            var page = await ZettaApi.ListGenerationsAsync(1, limit, search);
            var items = page?.Items ?? new List<Generation>();
            if (items.Count == 0)
                return ToolResult.Ok(string.IsNullOrWhiteSpace(search) ? "The Zetta 3D library is empty." : $"No models match \"{search}\".");
            var sb = new StringBuilder($"{items.Count} of {page.Total} model(s), newest first:\n");
            foreach (var g in items)
            {
                var model = string.IsNullOrEmpty(g.Model) ? null : ZettaCatalog.ModelLabel(g.Model);
                sb.AppendLine($"#{g.Id} {g.DisplayTitle} — kind: {Kind(g)}, {ZettaCatalog.FeatureLabel(g.Feature)}{(model != null ? $", {model}" : "")}, " +
                              $"{g.StatusLabel}{(g.IsSuccess ? "" : $" ({g.Status})")}, {g.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
            }
            sb.Append("import_generation brings a Ready mesh into the project (free); meshes can be processed with process_model. " +
                      "Splat rows (Gaussian splats and worlds made on the web) cannot be used in Unity — the user can open them on zetta3d.ai.");
            return ToolResult.Ok(sb.ToString());
        }

        /// <summary>mesh | splat (a Gaussian splat or world made on the web — not usable in Unity).</summary>
        public static string Kind(Generation g) => g.IsSplat ? "splat" : "mesh";

        /// <summary>Fetches one of the user's generations, with a message the model can act on when it is missing.</summary>
        static async Task<Generation> FetchAsync(long id)
        {
            Generation generation;
            try
            {
                (generation, _) = await ZettaApi.GetGenerationAsync(id);
            }
            catch (ZettaApiException e) when (e.Status == 404)
            {
                throw new ToolException($"Generation #{id} is not in the user's library. Use list_generations to find the id.");
            }
            return generation ?? throw new ToolException($"Generation #{id} was not found.");
        }

        // ─── import_generation ───────────────────────────────────────────────────

        public static async Task<ToolResult> Import(JObject input, ToolRunContext ctx)
        {
            RequireSignIn();
            var id = Args.LongN(input, "generationId") ?? throw new ToolException("'generationId' is required.");
            var folder = Args.Present(input, "folder") ? FileTools.ForWrite(Args.Str(input, "folder")) : ZettaProjectSettings.Instance.ModelsFolder;

            ZettaImporter.Placement place = null;
            if (Args.Get(input, "place") is JObject p)
            {
                place = new ZettaImporter.Placement { Select = false, Position = Args.Vec3(p, "position") };
                if (Args.Present(p, "parent")) place.Parent = TargetResolver.Resolve(Args.Str(p, "parent")).transform;
            }

            ctx.Status("Fetching the model…");
            var generation = await FetchAsync(id);
            if (generation.IsSplat) throw new ToolException(ZettaImporter.SplatUnsupported);
            if (!generation.IsSuccess)
                throw new ToolException($"Generation #{id} is {generation.StatusLabel.ToLowerInvariant()} ({generation.Status}); only finished models can be imported." +
                                        (generation.IsRunning ? " Wait for it to finish." : ""));

            FileTools.EnsureFolder(folder);
            var result = await ZettaImporter.ImportAsync(generation, folder, place, s => ctx.Status(s));

            var sb = new StringBuilder($"Imported #{id} \"{generation.DisplayTitle}\" into {result.Folder}/: ");
            sb.Append(string.Join(", ", result.AssetPaths.Select(Path.GetFileName))).Append('.');
            if (generation.Feature == "uvUnwrap")
            {
                // The importer brings the layout picture in beside the model; fetch it here only if it did not.
                var layout = result.AssetPaths.FirstOrDefault(IsPicturePath);
                if (layout == null && generation.UvLayout != null && string.IsNullOrEmpty(result.Note))
                {
                    try
                    {
                        layout = await ZettaImporter.ImportUvLayoutAsync(generation, result.Folder);
                    }
                    catch (Exception e)
                    {
                        sb.Append($" (The UV layout picture could not be saved: {Fmt.FirstLine(e.Message)})");
                    }
                }
                if (!string.IsNullOrEmpty(layout)) sb.Append($" UV layout picture: {layout}.");
            }
            if (!string.IsNullOrEmpty(result.Note)) sb.Append(' ').Append(result.Note.Trim());
            if (result.Placed != null)
            {
                var t = result.Placed.transform;
                sb.Append($" Placed {TargetResolver.Label(result.Placed)} at {Fmt.V(t.position)}, size {Fmt.V(ZettaCapture.BoundsOf(result.Placed).size)}.");
            }
            else if (place != null) sb.Append(" It could not be placed in the scene (the imported file is not a model Unity can instantiate — is the glTF importer installed?).");
            else sb.Append($" Not placed; use create_gameobject with prefab \"{result.MainAssetPath}\" to put it in the scene.");
            return ToolResult.Ok(sb.ToString());
        }

        static bool IsPicturePath(string path)
        {
            var ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".webp";
        }

        // ─── images ──────────────────────────────────────────────────────────────

        static async Task<string> ImageUrl(long imageId, ToolRunContext ctx)
        {
            var url = ctx.ImageUrl?.Invoke(imageId);
            if (!string.IsNullOrEmpty(url)) return url;
            try
            {
                var json = await ZettaHttp.GetAsync($"/unityAgent/images/{imageId}");
                url = json?["url"]?.ToString();
            }
            catch (ZettaApiException e) when (e.Status == 404)
            {
                throw new ToolException($"Image #{imageId} was not found. Use the imageId that generate_image returned.");
            }
            if (string.IsNullOrEmpty(url)) throw new ToolException($"Image #{imageId} has no downloadable file.");
            return url;
        }

        static string ImageContentType(byte[] bytes)
        {
            if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8) return "image/jpeg";
            if (bytes.Length > 12 && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[8] == (byte)'W' && bytes[9] == (byte)'E') return "image/webp";
            return "image/png";
        }

        public static async Task<ToolResult> SaveImage(JObject input, ToolRunContext ctx)
        {
            RequireSignIn();
            var imageId = Args.LongN(input, "imageId") ?? throw new ToolException("'imageId' is required.");
            var path = FileTools.ForWrite(Args.Required(input, "path"));
            var textureType = (Args.Str(input, "textureType", "default") ?? "default").Trim().ToLowerInvariant();
            if (textureType != "default" && textureType != "sprite" && textureType != "normal")
                throw new ToolException("'textureType' must be default, sprite or normal.");

            ctx.Status("Downloading the picture…");
            var url = await ImageUrl(imageId, ctx);
            var bytes = await ZettaHttp.DownloadAsync(url);
            if (bytes == null || bytes.Length == 0) throw new ToolException($"Image #{imageId} downloaded empty.");

            var type = ImageContentType(bytes);
            if (type == "image/webp") throw new ToolException("The picture is a WebP, which Unity cannot import as a texture.");
            var ext = type == "image/jpeg" ? ".jpg" : ".png";
            var note = "";
            var current = Path.GetExtension(path).ToLowerInvariant();
            if (current != ext && !(ext == ".jpg" && current == ".jpeg"))
            {
                if (current.Length > 0) note = $" (saved as {ext} to match the file's format)";
                path = (current.Length > 0 ? path.Substring(0, path.Length - current.Length) : path) + ext;
            }

            FileTools.EnsureFolder(Path.GetDirectoryName(path)?.Replace('\\', '/'));
            var saved = ZettaImporter.SaveImage(bytes, path, textureType);
            return ToolResult.Ok($"Saved image #{imageId} as {saved}{note} ({textureType} texture, {bytes.Length / 1024} KB).");
        }

        // ─── generate_3d_model ───────────────────────────────────────────────────

        static ThreeDEntitlements _gates;
        static PriceList _prices;

        static readonly string[] Models = { "n1", "n25", "np1", "np15" };
        static readonly string[] QualityOptions = { "low-poly", "high-res", "ultra" };

        static string Pick(JObject input, string key, string[] allowed, string fallback)
        {
            var v = Args.Str(input, key);
            if (string.IsNullOrWhiteSpace(v)) return fallback;
            v = v.Trim().ToLowerInvariant();
            if (!allowed.Contains(v)) throw new ToolException($"'{key}' must be one of {string.Join(", ", allowed)}.");
            return v;
        }

        public static async Task<ToolResult> Generate(JObject input, ToolRunContext ctx)
        {
            RequireSignIn();
            var prompt = (Args.Str(input, "prompt") ?? "").Trim();
            var imageId = Args.LongN(input, "imageId");
            if (prompt.Length == 0 && imageId == null) throw new ToolException("Give a prompt, or an imageId from generate_image.");
            if (prompt.Length > 1024) throw new ToolException("The prompt is longer than 1,024 characters.");

            var options = new GenerateOptions
            {
                Prompt = prompt,
                Model = Pick(input, "model", Models, "n1"),
                Quality = Pick(input, "quality", QualityOptions, "high-res"),
                Output = Pick(input, "output", new[] { "textured", "geometry" }, "textured"),
                PolygonType = Pick(input, "polygonType", new[] { "triangle", "quadrilateral" }, "triangle"),
                Pbr = Args.Bool(input, "pbr", true),
                FaceCount = Args.IntN(input, "faceCount"),
            };
            var title = (Args.Str(input, "title") ?? "").Trim();
            if (title.Length == 0) title = prompt.Length > 0 ? Fmt.Short(prompt, 60) : $"Model from image #{imageId}";
            if (title.Length > 120) title = title.Substring(0, 120);
            options.Title = title;
            var model = ZettaCatalog.Model(options.Model);

            // Placement, resolved now so the job can find the parent later.
            JObject place = Args.Get(input, "place") as JObject;
            string parentPath = null;
            float[] position = null;
            if (place != null)
            {
                if (Args.Present(place, "parent")) parentPath = TargetResolver.PathOf(TargetResolver.Resolve(Args.Str(place, "parent")), false);
                var p = Args.Vec3(place, "position");
                if (p.HasValue) position = new[] { p.Value.x, p.Value.y, p.Value.z };
            }

            // Plan gates and prices (cached for the session).
            ctx.Status("Checking your plan…");
            _gates ??= await ZettaApi.Entitlements3DAsync();
            _prices ??= await ZettaApi.PricingAsync();
            if (options.Model == "n25" && _gates?.AllowsZettaN25 == false) throw new ToolException($"The user's plan does not include {model.Label}. Use model n1, or ask them to upgrade.");
            if (options.Model is "np1" or "np15" && _gates?.AllowsZettaNp1 == false) throw new ToolException($"The user's plan does not include {model.Label}. Use model n1, or ask them to upgrade.");
            if (options.Quality == "low-poly" && _gates?.AllowsLowPoly == false) throw new ToolException("The user's plan does not include Low-Poly. Use high-res.");
            if (options.PolygonType == "quadrilateral" && _gates?.AllowsQuadMesh == false) throw new ToolException("The user's plan does not include quad meshes. Use triangle.");

            // The picture the engine reads: the agent's own image, or one drawn from the prompt (after approval).
            var drawFirst = imageId == null && !model.BarePrompt;
            if (imageId != null)
            {
                ctx.Status("Fetching the picture…");
                var url = await ImageUrl(imageId.Value, ctx);
                var bytes = await ZettaHttp.DownloadAsync(url);
                ctx.Status("Uploading…");
                options.ImageUrl = (await ZettaApi.UploadAsync(bytes, ImageContentType(bytes))).FileUrl;
                options.ImageFromPrompt = prompt.Length > 0;
            }
            else if (drawFirst)
            {
                // Validate with a stand-in URL; the real picture is drawn only once the user agrees.
                options.ImageUrl = "https://placeholder.invalid/reference.png";
                options.ImageFromPrompt = true;
            }

            var built = ZettaRequests.Build(options, _gates);
            if (!built.Ok) throw new ToolException(string.Join(" ", built.Problems));
            var price = ZettaRequests.Price(_prices, built, options.Model);

            var detail = new StringBuilder();
            detail.Append($"{model.Label} · {ZettaCatalog.QualityLabels[Array.IndexOf(ZettaCatalog.Qualities, options.Quality)]} · {options.Output}");
            if (options.Output == "textured") detail.Append(options.Pbr ? " · PBR" : " · no PBR");
            if (options.FaceCount.HasValue) detail.Append($" · {options.FaceCount.Value:N0} faces");
            if (options.PolygonType == "quadrilateral") detail.Append(" · quads");
            detail.AppendLine();
            if (prompt.Length > 0) detail.AppendLine($"Prompt: {Fmt.Short(prompt, 300)}");
            if (imageId != null) detail.AppendLine($"From picture #{imageId}");
            if (drawFirst) detail.AppendLine("A reference picture is drawn from the prompt first.");
            detail.Append($"Imports to {ZettaProjectSettings.Instance.ModelsFolder}/ when done");
            if (place != null) detail.Append(parentPath != null ? $", placed under {parentPath}" : ", placed in the scene");
            detail.Append('.');

            var approved = await ctx.Confirm(new ApprovalRequest
            {
                Kind = "generate",
                Title = $"Generate 3D: {title}",
                Detail = detail.ToString(),
                Credits = price,
                ConfirmLabel = "Generate",
            });
            if (!approved) return ToolResult.Error("The user declined.");

            if (drawFirst)
            {
                ctx.Status("Drawing the reference picture…");
                var image = await ZettaApi.GenerateImageAsync(prompt, for3D: true);
                var picture = image?.Images?.FirstOrDefault();
                if (picture == null || picture.Data.Length == 0) throw new ToolException("The reference picture could not be drawn; try again or use model n1.");
                ctx.Status("Uploading…");
                options.ImageUrl = (await ZettaApi.UploadAsync(picture.Data, string.IsNullOrEmpty(picture.ContentType) ? "image/png" : picture.ContentType)).FileUrl;
                built = ZettaRequests.Build(options, _gates);
                if (!built.Ok) throw new ToolException(string.Join(" ", built.Problems));
            }

            ctx.Status("Starting the job…");
            var created = await ZettaApi.CreateGenerationAsync(built.Body);
            var generation = created?.Generation ?? throw new ToolException("Zetta 3D did not return the new job.");
            var credits = created.Quote?.Credits ?? price;

            ZettaJobs.Track(generation, title, j =>
            {
                j.Source = "agent";
                j.AgentSessionId = ctx.SessionId;
                j.AutoImport = true;
                j.Place = place != null;
                j.ParentPath = parentPath;
                j.Position = position;
            });

            var folder = $"{ZettaProjectSettings.Instance.ModelsFolder}/{generation.Id}-{ZettaImporter.Slug(generation.DisplayTitle)}/";
            var cost = credits.HasValue ? $" ({credits.Value} credits)" : "";
            return ToolResult.Ok($"Started generation #{generation.Id}{cost}. It will be imported to {folder} when done; you'll get a job_finished event.");
        }

        public static string SummarizeGenerate(JObject input)
        {
            var parts = new List<string>();
            var model = Args.Str(input, "model") ?? "n1";
            parts.Add(ZettaCatalog.Model(model).Label);
            parts.Add(Args.Str(input, "quality") ?? "high-res");
            parts.Add(Args.Str(input, "output") ?? "textured");
            var sb = new StringBuilder(string.Join(" · ", parts));
            var prompt = Args.Str(input, "prompt");
            if (!string.IsNullOrWhiteSpace(prompt)) sb.Append("\nPrompt: ").Append(Fmt.Short(prompt, 300));
            if (Args.Present(input, "imageId")) sb.Append($"\nFrom picture #{Args.Str(input, "imageId")}");
            return sb.ToString();
        }
    }
}
