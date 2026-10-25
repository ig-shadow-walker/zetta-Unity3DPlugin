using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Zetta3D.Editor
{
    /// <summary>
    /// Typed calls for every Zetta 3D route the plugin uses. Bodies are built as JSON objects
    /// with only the fields that are set — the API rejects unknown fields and some known ones in
    /// the wrong combination, so nothing is sent "just in case".
    /// </summary>
    public static class ZettaApi
    {
        // ─── Account ────────────────────────────────────────────────────────────────

        public static async Task<ZettaUser> MeAsync(CancellationToken cancel = default) =>
            (await ZettaHttp.GetAsync("/auth/me", cancel)).As<ZettaUser>("user");

        // ─── 3D: catalogue, gates, prices ──────────────────────────────────────────

        public static async Task<ThreeDEntitlements> Entitlements3DAsync() =>
            (await ZettaHttp.GetAsync("/3d/entitlements")).As<ThreeDEntitlements>("entitlements");

        public static async Task<PriceList> PricingAsync() =>
            new PriceList((await ZettaHttp.GetAsync("/3d/pricing")).As<List<PriceItem>>("items"));

        public static async Task<Quote> QuoteAsync(string baseCode, IEnumerable<string> modifiers, string model)
        {
            var body = new JObject
            {
                ["generation"] = new JObject { ["base"] = baseCode, ["modifiers"] = new JArray(modifiers.Distinct()) },
            };
            if (!string.IsNullOrEmpty(model)) body["model"] = model;
            return (await ZettaHttp.PostAsync("/3d/quote", body)).As<Quote>();
        }

        // ─── 3D: uploads ────────────────────────────────────────────────────────────

        public static async Task<UploadTicket> CreateUploadAsync(string contentType) =>
            (await ZettaHttp.PostAsync("/3d/uploads", new JObject { ["contentType"] = contentType })).As<UploadTicket>();

        /// <summary>
        /// Uploads through the API's relay; returns the ticket whose <c>Key</c> names the stored file and whose
        /// <c>FileUrl</c> the engine reads for 24h. An expired ticket is renewed once.
        /// </summary>
        public static async Task<UploadTicket> UploadAsync(byte[] bytes, string contentType, CancellationToken cancel = default, Action<float> progress = null)
        {
            var ticket = await CreateUploadAsync(contentType);
            try
            {
                await ZettaHttp.PutBytesAsync(ticket.UploadUrl, bytes, ticket.Headers, cancel, progress);
            }
            catch (ZettaApiException e) when (e.Code == "UPLOAD_TICKET_EXPIRED")
            {
                ticket = await CreateUploadAsync(contentType);
                await ZettaHttp.PutBytesAsync(ticket.UploadUrl, bytes, ticket.Headers, cancel, progress);
            }
            return ticket;
        }

        /// <summary>Turns an uploaded GLB into a finished library row (free).</summary>
        public static async Task<Generation> CreateUploadPostAsync(string key, long bytes, string title, string feature = "upload")
        {
            var body = new JObject { ["key"] = key, ["bytes"] = bytes, ["feature"] = feature };
            if (!string.IsNullOrWhiteSpace(title)) body["title"] = title.Length > 120 ? title.Substring(0, 120) : title;
            return (await ZettaHttp.PostAsync("/3d/uploads/post", body)).As<Generation>("generation");
        }

        // ─── 3D: jobs ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a generation or post-process job. <paramref name="body"/> is a CreateGenerationDto;
        /// the origin is stamped here so every job the plugin starts is attributed to Unity.
        /// </summary>
        public static async Task<CreateGenerationResult> CreateGenerationAsync(JObject body, string idempotencyKey = null)
        {
            body["origin"] = ZettaConfig.ClientName;
            var headers = new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey ?? Guid.NewGuid().ToString() };
            return (await ZettaHttp.PostAsync("/3d/generations", body, headers)).As<CreateGenerationResult>();
        }

        public static async Task<(Generation generation, int? queuePosition)> GetGenerationAsync(long id, CancellationToken cancel = default)
        {
            var json = await ZettaHttp.GetAsync($"/3d/generations/{id}", cancel);
            return (json.As<Generation>("generation"), json["queuePosition"]?.Type == JTokenType.Integer ? (int?)json["queuePosition"] : null);
        }

        public static async Task<GenerationPage> ListGenerationsAsync(
            int page = 1, int pageSize = 24, string search = null, string feature = null, IEnumerable<string> statuses = null,
            bool groupRuns = false)
        {
            var query = new List<string> { $"page={page}", $"pageSize={pageSize}", "sortDir=DESC" };
            // Each pipeline run as one entry carrying its models (Generation.RunGroup), so a run is never split across pages.
            if (groupRuns) query.Add("groupRuns=true");
            if (!string.IsNullOrWhiteSpace(search)) query.Add("search=" + Uri.EscapeDataString(search.Trim()));
            if (!string.IsNullOrEmpty(feature)) query.Add("feature=" + Uri.EscapeDataString(feature));
            // Arrays repeat the key: the API rejects the status[]= form.
            if (statuses != null) query.AddRange(statuses.Select(s => "status=" + Uri.EscapeDataString(s)));
            return (await ZettaHttp.GetAsync("/3d/generations?" + string.Join("&", query))).As<GenerationPage>();
        }

        /// <summary>A fresh signed URL (15 min) for one of a job's files; no key = the main mesh.</summary>
        public static async Task<string> DownloadUrlAsync(long id, string key = null)
        {
            var path = $"/3d/generations/{id}/download" + (string.IsNullOrEmpty(key) ? "" : "?key=" + Uri.EscapeDataString(key));
            return (await ZettaHttp.GetAsync(path))["url"]?.ToString();
        }

        public static async Task<Generation> UpdateGenerationAsync(long id, string title = null, IEnumerable<string> tags = null, string availability = null)
        {
            var body = new JObject();
            if (title != null) body["title"] = title;
            if (tags != null) body["tags"] = new JArray(tags);
            if (availability != null) body["availability"] = availability;
            return (await ZettaHttp.PatchAsync($"/3d/generations/{id}", body)).As<Generation>("generation");
        }

        public static async Task<Generation> CancelGenerationAsync(long id) =>
            (await ZettaHttp.PostAsync($"/3d/generations/{id}/cancel")).As<Generation>("generation");

        public static Task DeleteGenerationAsync(long id) => ZettaHttp.DeleteAsync($"/3d/generations/{id}");

        /// <summary>Polls a job every 2.5s until it is terminal, reporting each state.</summary>
        public static async Task<Generation> WaitForAsync(long id, Action<Generation, int?> progress, CancellationToken cancel)
        {
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                var (generation, position) = await GetGenerationAsync(id, cancel);
                progress?.Invoke(generation, position);
                if (generation.IsTerminal) return generation;
                await Task.Delay(2500, cancel);
            }
        }

        // ─── Tools & AI helpers ────────────────────────────────────────────────────

        /// <summary>Background removal; returns PNG bytes.</summary>
        public static async Task<byte[]> RemoveBackgroundAsync(byte[] image)
        {
            var json = await ZettaHttp.PostAsync("/tools/rembg", new JObject { ["image"] = Convert.ToBase64String(image) }, timeoutSeconds: 120);
            return Convert.FromBase64String(json["image"]?.ToString() ?? "");
        }

        static JArray Frames(IEnumerable<byte[]> jpegs) =>
            new JArray(jpegs.Take(4).Select(b => new JObject { ["data"] = Convert.ToBase64String(b), ["mediaType"] = "image/jpeg" }));

        public static async Task<(string category, List<string> tags)> SuggestTagsAsync(IEnumerable<byte[]> jpegFrames)
        {
            var json = await ZettaHttp.PostAsync("/ai/tags", new JObject { ["frames"] = Frames(jpegFrames) }, timeoutSeconds: 90);
            return (json["category"]?.Type == JTokenType.String ? json["category"].ToString() : null, json.As<List<string>>("tags") ?? new List<string>());
        }

        public static async Task<(string bodyType, string confidence, string reason)> RiggingPrecheckAsync(IEnumerable<byte[]> jpegFrames)
        {
            var json = await ZettaHttp.PostAsync("/ai/rigging-precheck", new JObject { ["frames"] = Frames(jpegFrames) }, timeoutSeconds: 90);
            return (json["bodyType"]?.ToString(), json["confidence"]?.ToString(), json["reason"]?.ToString());
        }

        // ─── Image Studio (2D) ──────────────────────────────────────────────────────

        public static async Task<ImageEntitlements> Entitlements2DAsync() =>
            (await ZettaHttp.GetAsync("/2d/entitlements")).As<ImageEntitlements>("entitlements");

        public static async Task<ImageModels> ImageModelsAsync() =>
            (await ZettaHttp.GetAsync("/2d/models")).As<ImageModels>();

        /// <summary>for3D pictures are forced square 1K (an object on the converter's square), so no frame is sent.</summary>
        static JObject ImageOptions(JObject body, string aspectRatio, string resolution, string model, bool for3D)
        {
            if (for3D) body["for3D"] = true;
            else
            {
                if (!string.IsNullOrEmpty(aspectRatio)) body["aspectRatio"] = aspectRatio;
                if (!string.IsNullOrEmpty(resolution)) body["resolution"] = resolution;
            }
            if (!string.IsNullOrEmpty(model)) body["model"] = model;
            return body;
        }

        static JObject Image(InlineImage image) => new JObject { ["data"] = image.Data, ["contentType"] = image.ContentType };

        static Dictionary<string, string> ClientHeader() =>
            new Dictionary<string, string> { ["Idempotency-Key"] = Guid.NewGuid().ToString() };

        public static async Task<ImageResult> GenerateImageAsync(
            string prompt, string aspectRatio = "1:1", string resolution = "1K", string model = null,
            IEnumerable<InlineImage> styleReferences = null, bool for3D = false)
        {
            var body = ImageOptions(new JObject { ["prompt"] = prompt }, aspectRatio, resolution, model, for3D);
            var refs = styleReferences?.Take(3).ToList();
            if (refs is { Count: > 0 }) body["styleReferences"] = new JArray(refs.Select(Image));
            return (await ZettaHttp.PostAsync("/2d/generate", body, ClientHeader(), 180)).As<ImageResult>();
        }

        public static async Task<ImageResult> EditImageAsync(
            string prompt, InlineImage image = null, long? sourcePostId = null,
            string aspectRatio = "1:1", string resolution = "1K", string model = null, bool pipelineStep = false, bool for3D = false)
        {
            var body = ImageOptions(new JObject { ["prompt"] = prompt }, aspectRatio, resolution, model, for3D);
            if (image != null) body["image"] = Image(image);
            else if (sourcePostId != null) body["sourcePostId"] = sourcePostId.Value;
            if (pipelineStep) body["pipelineStep"] = true;
            return (await ZettaHttp.PostAsync("/2d/img-edit", body, ClientHeader(), 180)).As<ImageResult>();
        }

        public static async Task<ImageResult> ComposeImagesAsync(
            string prompt, IEnumerable<InlineImage> images, IEnumerable<long> sourcePostIds = null,
            string aspectRatio = "1:1", string resolution = "1K", string model = null)
        {
            var body = ImageOptions(new JObject { ["prompt"] = prompt }, aspectRatio, resolution, model, false);
            var list = images?.Take(14).ToList();
            if (list is { Count: > 0 }) body["images"] = new JArray(list.Select(Image));
            var ids = sourcePostIds?.ToList();
            if (ids is { Count: > 0 }) body["sourcePostIds"] = new JArray(ids);
            return (await ZettaHttp.PostAsync("/2d/img-compose", body, ClientHeader(), 240)).As<ImageResult>();
        }

        public static async Task<ImagePage> ListImagesAsync(int page = 1, int pageSize = 24, string search = null, string feature = null)
        {
            var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
            if (!string.IsNullOrWhiteSpace(search)) query.Add("search=" + Uri.EscapeDataString(search.Trim()));
            if (!string.IsNullOrEmpty(feature)) query.Add("feature=" + feature);
            return (await ZettaHttp.GetAsync("/2d?" + string.Join("&", query))).As<ImagePage>();
        }

        public static Task DeleteImageAsync(long id) => ZettaHttp.DeleteAsync($"/2d/{id}");
    }
}
