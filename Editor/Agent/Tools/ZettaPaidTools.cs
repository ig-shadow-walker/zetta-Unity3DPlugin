using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// process_model — the paid post-process tool beside generate_3d_model. It validates first, quotes from
    /// the price list, waits for the user's click (a declined job spends nothing — pictures are drawn only
    /// after it), then starts the job and hands it to <see cref="ZettaJobs"/> to import.
    /// The bodies come from <see cref="AgentRequestBuilders"/>.
    /// </summary>
    static partial class ZettaTools
    {
        static async Task LoadPlanAsync(ToolRunContext ctx)
        {
            ctx.Status("Checking your plan…");
            _gates ??= await ZettaApi.Entitlements3DAsync();
            _prices ??= await ZettaApi.PricingAsync();
        }

        static async Task<(byte[] bytes, string type)> DownloadPicture(string url, string what)
        {
            var bytes = await ZettaHttp.DownloadAsync(url);
            if (bytes == null || bytes.Length == 0) throw new ToolException($"{what} downloaded empty.");
            return (bytes, ImageContentType(bytes));
        }

        static async Task<string> Upload(byte[] bytes, string type, ToolRunContext ctx)
        {
            ctx.Status("Uploading…");
            return (await ZettaApi.UploadAsync(bytes, type)).FileUrl;
        }

        static string Credits(int? credits) => credits.HasValue ? $" ({credits.Value} credits)" : "";

        static string FolderFor(Generation g) => $"{ZettaProjectSettings.Instance.ModelsFolder}/{g.Id}-{ZettaImporter.Slug(g.DisplayTitle)}/";

        // ─── process_model ───────────────────────────────────────────────────────

        /// <summary>The options object, or — when the model flattened them — the input's other keys.</summary>
        static JObject ProcessOptions(JObject input)
        {
            if (Args.Get(input, "options") is JObject options) return options;
            if (Args.Present(input, "options")) throw new ToolException("'options' must be an object.");
            var flat = new JObject();
            foreach (var p in input.Properties())
                if (p.Name != "generationId" && p.Name != "operation" && p.Name != "options" && p.Value.Type != JTokenType.Null)
                    flat[p.Name] = p.Value;
            return flat;
        }

        public static async Task<ToolResult> ProcessModel(JObject input, ToolRunContext ctx)
        {
            RequireSignIn();
            var id = Args.LongN(input, "generationId") ?? throw new ToolException("'generationId' is required.");
            var plan = AgentRequestBuilders.Process(Args.Required(input, "operation"), ProcessOptions(input));
            var label = AgentRequestBuilders.OperationLabel(plan.Operation);

            ctx.Status("Fetching the model…");
            var source = await FetchAsync(id);
            var refusal = AgentRequestBuilders.RefuseSource(plan.Operation, source);
            if (refusal != null) throw new ToolException(refusal);
            if (plan.Operation == "smart_uv" && AgentRequestBuilders.SmartUvProblem(source) is { } tooDense) throw new ToolException(tooDense);

            await LoadPlanAsync(ctx);
            if (_gates?.AllowsPostProcess == false)
                throw new ToolException($"{label} is a finishing tool, which needs a paid plan. Ask the user to upgrade at zetta3d.ai/pricing.");
            if ((string)plan.Options["polygonType"] == "quadrilateral" && _gates?.AllowsQuadMesh == false)
                throw new ToolException("The user's plan does not include quad meshes. Use polygonType triangle.");

            // A texture's reference: the agent's picture (checked now), else the model's own input picture.
            string pictureUrl = null;
            if (plan.NeedsPicture && plan.ImageId != null)
            {
                ctx.Status("Fetching the picture…");
                pictureUrl = await ImageUrl(plan.ImageId.Value, ctx);
            }
            var sourcePicture = source.InputImages?.FirstOrDefault(i => !string.IsNullOrEmpty(i.Url))?.Url;

            var price = _prices?.Price(plan.Code, plan.PriceModel);
            var detail = new StringBuilder();
            detail.AppendLine($"{label} on #{source.Id} \"{source.DisplayTitle}\" ({ZettaCatalog.FeatureLabel(source.Feature)})");
            foreach (var line in plan.Summary) detail.AppendLine(line);
            if (plan.NeedsPicture && plan.ImageId == null && plan.Prompt.Length > 0 && sourcePicture != null)
                detail.AppendLine("The description is applied to the model's own input picture.");
            detail.Append($"Imports to {ZettaProjectSettings.Instance.ModelsFolder}/ when done.");

            var approved = await ctx.Confirm(new ApprovalRequest
            {
                Kind = "generate",
                Title = $"{label}: {source.DisplayTitle}",
                Detail = detail.ToString(),
                Credits = price,
                ConfirmLabel = plan.Operation switch
                {
                    "retopologize" => "Retopologize",
                    "texture" => "Apply texture",
                    "rig" => "Auto Rig",
                    "segment" => "Segment",
                    _ => "Run Smart UV",
                },
            });
            if (!approved) return ToolResult.Error("The user declined.");

            string imageUrl = null;
            if (plan.NeedsPicture)
            {
                byte[] bytes = null;
                string type = null;
                if (pictureUrl != null)
                {
                    ctx.Status("Fetching the picture…");
                    (bytes, type) = await DownloadPicture(pictureUrl, $"Image #{plan.ImageId}");
                }
                if (plan.Prompt.Length > 0)
                {
                    // The Studio's Preview: the description edits a reference (the agent's picture, else the model's
                    // input picture) or, with none, is drawn from scratch — the engine never reads the words themselves.
                    if (bytes == null && sourcePicture != null)
                    {
                        try
                        {
                            (bytes, type) = await DownloadPicture(sourcePicture, "The model's input picture");
                        }
                        catch (Exception)
                        {
                            bytes = null; // an expired input picture: draw from the description alone
                        }
                    }
                    ctx.Status("Drawing the reference picture…");
                    var drawn = bytes != null
                        ? await ZettaApi.EditImageAsync(plan.Prompt, InlineImage.From(bytes, type), for3D: true, pipelineStep: true)
                        : await ZettaApi.GenerateImageAsync(plan.Prompt, for3D: true);
                    var picture = drawn?.Images?.FirstOrDefault();
                    if (picture == null || picture.Data.Length == 0)
                        throw new ToolException("The reference picture could not be drawn" + (string.IsNullOrWhiteSpace(drawn?.Text) ? "; try again." : $": {Fmt.Short(drawn.Text, 200)}") + " Nothing was charged for the texture.");
                    bytes = picture.Data;
                    type = string.IsNullOrEmpty(picture.ContentType) ? "image/png" : picture.ContentType;
                }
                imageUrl = await Upload(bytes, type, ctx);
            }

            ctx.Status("Starting the job…");
            var created = await ZettaApi.CreateGenerationAsync(AgentRequestBuilders.ProcessBody(plan, source.Id, imageUrl));
            var generation = created?.Generation ?? throw new ToolException("Zetta 3D did not return the new job.");
            var credits = created.Quote?.Credits ?? price;

            ZettaJobs.Track(generation, $"{label} · {source.DisplayTitle}", j =>
            {
                j.Source = "agent";
                j.AgentSessionId = ctx.SessionId;
                j.AutoImport = true;
                j.Place = false;
            });

            var extra = plan.Operation switch
            {
                "segment" => " The parts import as separate objects under one folder.",
                "rig" => (string)plan.Options["rigOutFormat"] == "fbx" ? " The rigged FBX imports with its skeleton." : "",
                "smart_uv" => " The UV layout picture comes with it when the job makes one.",
                _ => "",
            };
            return ToolResult.Ok($"Started {label.ToLowerInvariant()} #{generation.Id} on #{source.Id}{Credits(credits)}. " +
                                 $"It will be imported to {FolderFor(generation)} when done (not placed); you'll get a job_finished event.{extra}");
        }

        public static string SummarizeProcess(JObject input)
        {
            var id = Args.Str(input, "generationId");
            try
            {
                var plan = AgentRequestBuilders.Process(Args.Str(input, "operation"), ProcessOptions(input));
                return $"{AgentRequestBuilders.OperationLabel(plan.Operation)} on #{id}\n{string.Join("\n", plan.Summary)}";
            }
            catch (ToolException e)
            {
                return $"{Args.Str(input, "operation")} on #{id}\n{e.Message}";
            }
        }
    }
}
