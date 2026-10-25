using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Zetta3D.Editor.Pipelines
{
    /// <summary>
    /// A refusal that names the steps it is about — PIPELINE_NOT_RUNNABLE (a graph that cannot run) or RUN_NEEDS_INPUT
    /// (a draft started before its per-run inputs were given). Still a <see cref="ZettaApiException"/>, so existing
    /// handlers (and <see cref="ZettaCatalog.Explain"/>) keep working.
    /// </summary>
    public sealed class PipelineApiException : ZettaApiException
    {
        public readonly List<PipelineProblem> Problems;

        public PipelineApiException(ZettaApiException inner, List<PipelineProblem> problems)
            : base(inner.Status, inner.Message, inner.Code, inner.Body)
        {
            Problems = problems ?? new List<PipelineProblem>();
        }

        /// <summary>The error as a <see cref="PipelineApiException"/> when its body names problems; otherwise the error itself.</summary>
        public static ZettaApiException From(ZettaApiException error) =>
            error is PipelineApiException || !(error?.Body?["problems"] is JArray problems)
                ? error
                : new PipelineApiException(error, problems.ToObject<List<PipelineProblem>>());
    }

    /// <summary>
    /// Every 3D Pipelines route (Zetta3D-Backend src/pipelines/pipelines.controller.ts), typed. Calls are made from the
    /// main thread (continuations return there). Graphs are always sent whole — rebuild requests with
    /// <see cref="RequestBuilder.WithRequests"/> first (the Save/Start helpers here do it for you when given entitlements).
    /// </summary>
    public static class PipelinesApi
    {
        static async Task<JToken> Call(Func<Task<JToken>> send)
        {
            try
            {
                return await send();
            }
            catch (ZettaApiException e) when (!(e is PipelineApiException) && e.Body?["problems"] is JArray)
            {
                throw PipelineApiException.From(e);
            }
        }

        static JObject GraphJson(PipelineGraph graph) => graph?.ToJson();

        // ─── Pipelines ────────────────────────────────────────────────────────────

        /// <summary>GET /pipelines — newest first, with each one's last run and run counts.</summary>
        public static async Task<List<PipelineSummary>> ListAsync() =>
            (await Call(() => ZettaHttp.GetAsync("/pipelines"))).ToObject<List<PipelineSummary>>() ?? new List<PipelineSummary>();

        /// <summary>GET /pipelines/:id. Run the graph through <see cref="RequestBuilder.Normalize"/> before showing it.</summary>
        public static async Task<Pipeline> GetAsync(long id) =>
            (await Call(() => ZettaHttp.GetAsync($"/pipelines/{id}"))).ToObject<Pipeline>();

        /// <summary>POST /pipelines — a new pipeline (title defaults to "Untitled pipeline"; graph to empty).</summary>
        public static async Task<Pipeline> CreateAsync(string title = null, PipelineGraph graph = null)
        {
            var body = new JObject();
            if (!string.IsNullOrWhiteSpace(title)) body["title"] = title;
            if (graph != null) body["graph"] = GraphJson(graph);
            return (await Call(() => ZettaHttp.PostAsync("/pipelines", body))).ToObject<Pipeline>();
        }

        /// <summary>PATCH /pipelines/:id — a new title and/or the whole graph (requests rebuilt before sending).</summary>
        public static async Task<Pipeline> UpdateAsync(long id, string title = null, PipelineGraph graph = null)
        {
            var body = new JObject();
            if (title != null) body["title"] = title;
            if (graph != null) body["graph"] = GraphJson(graph);
            return (await Call(() => ZettaHttp.PatchAsync($"/pipelines/{id}", body))).ToObject<Pipeline>();
        }

        /// <summary>Saves the editor's graph: every request rebuilt from its settings, then PATCHed whole (the web's autosave).</summary>
        public static Task<Pipeline> SaveAsync(long id, PipelineGraph graph, string title = null, ThreeDEntitlements entitlements = null) =>
            UpdateAsync(id, title, RequestBuilder.WithRequests(graph, entitlements));

        /// <summary>POST /pipelines/:id/duplicate — "Title (copy)".</summary>
        public static async Task<Pipeline> DuplicateAsync(long id) =>
            (await Call(() => ZettaHttp.PostAsync($"/pipelines/{id}/duplicate"))).ToObject<Pipeline>();

        /// <summary>DELETE /pipelines/:id.</summary>
        public static Task DeleteAsync(long id) => Call(() => ZettaHttp.DeleteAsync($"/pipelines/{id}"));

        /// <summary>POST /pipelines/estimate — the server's price and problems for a graph; nothing is saved.</summary>
        public static async Task<ServerEstimate> EstimateAsync(PipelineGraph graph) =>
            (await Call(() => ZettaHttp.PostAsync("/pipelines/estimate", new JObject { ["graph"] = GraphJson(graph) }))).ToObject<ServerEstimate>();

        // ─── Runs ────────────────────────────────────────────────────────────────

        /// <summary>GET /pipelines/:id/runs — the 50 newest, as the Runs view lists them.</summary>
        public static async Task<List<RunSummary>> RunsAsync(long pipelineId) =>
            (await Call(() => ZettaHttp.GetAsync($"/pipelines/{pipelineId}/runs"))).ToObject<List<RunSummary>>() ?? new List<RunSummary>();

        /// <summary>
        /// POST /pipelines/:id/runs — one run on the graph's own seed (the seed node's request must name it). The web uses
        /// <see cref="BatchAsync"/> instead, since a recipe keeps no seed.
        /// </summary>
        public static async Task<PipelineRun> StartRunAsync(long pipelineId, string mode, PipelineGraph graph = null)
        {
            var body = new JObject { ["mode"] = mode ?? RunStatuses.ModeManual };
            if (graph != null) body["graph"] = GraphJson(graph);
            return (await Call(() => ZettaHttp.PostAsync($"/pipelines/{pipelineId}/runs", body))).ToObject<PipelineRun>();
        }

        /// <summary>
        /// POST /pipelines/:id/batch — one run per seed (1–20; each a <see cref="PipelineSeed.Request"/>). With
        /// <paramref name="draft"/> the runs are made, not started (the web's New run): give each its per-run inputs with
        /// <see cref="PatchRunNodeAsync"/>, then <see cref="StartAsync"/>. The graph sent is saved onto the pipeline first —
        /// pass <c>RequestBuilder.WithRequests(RequestBuilder.ForNewRun(graph))</c>, as the web does.
        /// Returns light summaries (id, mode, status, estimatedCredits, createdAt, finishedAt).
        /// </summary>
        public static async Task<List<RunSummary>> BatchAsync(long pipelineId, string mode, IEnumerable<JObject> seeds, PipelineGraph graph = null, bool draft = true)
        {
            var body = new JObject
            {
                ["mode"] = mode ?? RunStatuses.ModeManual,
                ["seeds"] = new JArray((seeds ?? Enumerable.Empty<JObject>()).Select(s => (JObject)s.DeepClone())),
                ["draft"] = draft,
            };
            if (graph != null) body["graph"] = GraphJson(graph);
            var json = await Call(() => ZettaHttp.PostAsync($"/pipelines/{pipelineId}/batch", body));
            return json["runs"]?.ToObject<List<RunSummary>>() ?? new List<RunSummary>();
        }

        /// <summary>The same, from <see cref="PipelineSeed"/>s.</summary>
        public static Task<List<RunSummary>> BatchAsync(long pipelineId, string mode, IEnumerable<PipelineSeed> seeds, PipelineGraph graph = null, bool draft = true) =>
            BatchAsync(pipelineId, mode, seeds?.Select(s => s.Request), graph, draft);

        /// <summary>DELETE /pipeline-runs/:runId — a run that is over (or never started); its models stay in the library.</summary>
        public static Task DeleteRunAsync(long runId) => Call(() => ZettaHttp.DeleteAsync($"/pipeline-runs/{runId}"));

        /// <summary>POST /pipeline-runs/:runId/start — a draft starts; refused (RUN_NEEDS_INPUT, with Problems) while a step needs its input.</summary>
        public static Task<PipelineRun> StartAsync(long runId) => RunAction($"/pipeline-runs/{runId}/start");

        /// <summary>GET /pipeline-runs/:runId.</summary>
        public static async Task<PipelineRun> GetRunAsync(long runId, CancellationToken cancel = default) =>
            (await Call(() => ZettaHttp.GetAsync($"/pipeline-runs/{runId}", cancel))).ToObject<PipelineRun>();

        public static Task<PipelineRun> PauseAsync(long runId) => RunAction($"/pipeline-runs/{runId}/pause");

        public static Task<PipelineRun> ResumeAsync(long runId) => RunAction($"/pipeline-runs/{runId}/resume");

        /// <summary>Stops a run: steps not started are cancelled, a queued 3D job is cancelled and refunded.</summary>
        public static Task<PipelineRun> CancelAsync(long runId) => RunAction($"/pipeline-runs/{runId}/cancel");

        /// <summary>POST /pipeline-runs/:runId/mode — auto releases every waiting step now and from now on.</summary>
        public static Task<PipelineRun> SetModeAsync(long runId, string mode) =>
            RunAction($"/pipeline-runs/{runId}/mode", new JObject { ["mode"] = mode });

        /// <summary>POST /pipeline-runs/:runId/run-waiting — every step waiting for a press, at once (spends credits).</summary>
        public static Task<PipelineRun> RunWaitingAsync(long runId) => RunAction($"/pipeline-runs/{runId}/run-waiting");

        /// <summary>
        /// PATCH /pipeline-runs/:runId/nodes/:nodeId — a step's settings for this run only, before it has run. Send both
        /// (see <see cref="RequestBuilder.RunNodeChange"/>).
        /// </summary>
        public static async Task<PipelineRun> PatchRunNodeAsync(long runId, string nodeId, JObject request = null, JObject settings = null)
        {
            var body = new JObject();
            if (request != null) body["request"] = request.DeepClone();
            if (settings != null) body["settings"] = settings.DeepClone();
            return (await Call(() => ZettaHttp.PatchAsync($"/pipeline-runs/{runId}/nodes/{Uri.EscapeDataString(nodeId ?? "")}", body))).ToObject<PipelineRun>();
        }

        /// <summary>POST /pipeline-runs/:runId/steps/:stepId/run — Manual mode's Run on one waiting step (spends credits).</summary>
        public static Task<PipelineRun> RunStepAsync(long runId, long stepId) => RunAction($"/pipeline-runs/{runId}/steps/{stepId}/run");

        /// <summary>POST …/steps/:stepId/retry — a failed step again, optionally with changed settings for this run.</summary>
        public static Task<PipelineRun> RetryStepAsync(long runId, long stepId, JObject request = null, JObject settings = null)
        {
            var body = new JObject();
            if (request != null) body["request"] = request.DeepClone();
            if (settings != null) body["settings"] = settings.DeepClone();
            return RunAction($"/pipeline-runs/{runId}/steps/{stepId}/retry", body);
        }

        /// <summary>POST …/steps/:stepId/parts — Isolate Parts in Manual mode: the parts ticked and any typed (≤16 each, ≤80 chars).</summary>
        public static Task<PipelineRun> ChoosePartsAsync(long runId, long stepId, IEnumerable<string> keys, IEnumerable<string> extraParts = null) =>
            RunAction($"/pipeline-runs/{runId}/steps/{stepId}/parts", new JObject
            {
                ["keys"] = new JArray((keys ?? Enumerable.Empty<string>()).ToArray()),
                ["extraParts"] = new JArray((extraParts ?? Enumerable.Empty<string>()).ToArray()),
            });

        static async Task<PipelineRun> RunAction(string path, JObject body = null) =>
            (await Call(() => ZettaHttp.PostAsync(path, body))).ToObject<PipelineRun>();
    }
}
