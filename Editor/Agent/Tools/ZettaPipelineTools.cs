using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Zetta3D.Editor.Pipelines;
using Zetta3D.Editor.Studio;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// The 3D Pipelines client tools (protocol v1.3): list_pipelines, get_pipeline, build_pipeline, edit_pipeline,
    /// run_pipeline, pipeline_run_status, manage_pipeline_run, import_pipeline_results. Built on Editor/Pipelines/Core —
    /// the same builders the Pipelines window uses, so what the agent saves is priced and runnable as it stands. Every
    /// credit spend goes through a card (<see cref="ToolRunContext.Confirm"/>); the pure parts are in
    /// <see cref="AgentPipelineHelpers"/>.
    /// </summary>
    static partial class ZettaTools
    {
        static string ModelsFolder => ZettaProjectSettings.Instance.ModelsFolder;

        /// <summary>A pipeline API call with errors the model can act on: 404 named, refusals with their problems listed.</summary>
        static async Task<T> PipelineCall<T>(Func<Task<T>> call, string notFound = null)
        {
            try
            {
                return await call();
            }
            catch (PipelineApiException e)
            {
                var problems = e.Problems.Count == 0 ? "" : " " + string.Join(" ", e.Problems.Select(p => p.ToString()));
                throw new ToolException(ZettaCatalog.Explain(e) + problems);
            }
            catch (ZettaApiException e) when (e.Status == 404 && notFound != null)
            {
                throw new ToolException(notFound);
            }
        }

        static Task<Pipeline> GetPipeline(long id) =>
            PipelineCall(() => PipelinesApi.GetAsync(id), $"Pipeline #{id} was not found. Use list_pipelines for the ids.");

        static Task<PipelineRun> GetRun(long runId) =>
            PipelineCall(() => PipelinesApi.GetRunAsync(runId), $"Run #{runId} was not found. pipeline_run_status takes the run ids run_pipeline returned.");

        /// <summary>The pipeline's graph as the editor loads it (Studio defaults filled in, no per-run inputs).</summary>
        static PipelineGraph Recipe(Pipeline pipeline) => RequestBuilder.Normalize(pipeline.Graph ?? new PipelineGraph()).Graph;

        static string CheckTitle(string title)
        {
            title = (title ?? "").Trim();
            if (title.Length > AgentPipelineHelpers.MaxTitle) throw new ToolException($"'title' is longer than {AgentPipelineHelpers.MaxTitle} characters.");
            return title;
        }

        /// <summary>Problems, warnings, estimate and the step tree — the tail of build/edit/get results.</summary>
        static void AppendGraphReport(StringBuilder sb, PipelineGraph graph)
        {
            var problems = GraphRules.Problems(graph, _gates);
            sb.AppendLine(problems.Count == 0 ? "Problems: none — runnable." : $"Problems (it cannot run until fixed):\n{AgentPipelineHelpers.ProblemsText(problems, graph)}");
            var warnings = AgentPipelineHelpers.WarningsText(graph);
            if (warnings.Length > 0) sb.AppendLine("Warnings:\n" + warnings);
            sb.AppendLine("Estimate: " + AgentPipelineHelpers.EstimateText(graph, GraphRules.Estimate(graph, _prices, _gates)));
            sb.Append("Steps:\n").Append(AgentPipelineHelpers.StepTree(graph, _prices, _gates));
        }

        static void Saved(long pipelineId)
        {
            try
            {
                PipelineToolHooks.PipelineSaved?.Invoke(pipelineId);
            }
            catch (Exception)
            {
                // The window's own trouble; the save stands.
            }
        }

        // ─── list_pipelines ──────────────────────────────────────────────────────

        public static async Task<ToolResult> ListPipelines(JObject input, ToolRunContext ctx)
        {
            RequireSignIn();
            var list = await PipelineCall(PipelinesApi.ListAsync);
            if (list.Count == 0) return ToolResult.Ok("The user has no 3D pipelines yet. build_pipeline makes one.");
            var now = DateTime.UtcNow;
            var sb = new StringBuilder($"{Fmt.Plural(list.Count, "pipeline")}, newest first:\n");
            foreach (var p in list) sb.AppendLine(AgentPipelineHelpers.PipelineLine(p, now));
            sb.Append("get_pipeline shows a pipeline's steps; run_pipeline runs it on seeds.");
            return ToolResult.Ok(sb.ToString());
        }

        // ─── get_pipeline ────────────────────────────────────────────────────────

        public static async Task<ToolResult> GetPipelineTool(JObject input, ToolRunContext ctx)
        {
            RequireSignIn();
            var id = AgentPipelineHelpers.RequireId(input, "pipelineId");
            var pipeline = await GetPipeline(id);
            await LoadPlanAsync(ctx);
            var graph = Recipe(pipeline);
            List<RunSummary> runs;
            try
            {
                runs = await PipelinesApi.RunsAsync(id);
            }
            catch (ZettaApiException)
            {
                runs = null;
            }

            var seed = graph.Seed;
            var sb = new StringBuilder($"Pipeline #{pipeline.Id} “{pipeline.Title}” — starts from {(seed?.Kind == NodeKinds.SeedModel ? "a model" : seed?.Kind == NodeKinds.SeedViews ? "pictures of each side" : seed != null ? "a picture" : "(no seed)")}" +
                                       $", {Fmt.Plural(graph.Nodes.Count(n => !NodeCatalog.IsSeed(n.Kind)), "step")}, edited {AgentPipelineHelpers.Ago(pipeline.UpdatedAt, DateTime.UtcNow)}.\n");
            AppendGraphReport(sb, graph);
            sb.AppendLine();
            var perRun = graph.Nodes.Where(NodeSettings.SetOnEachRun).Select(n => n.Id).ToList();
            if (perRun.Count > 0) sb.AppendLine($"Per-run inputs (run_pipeline inputs): {string.Join(", ", perRun)}.");
            if (runs == null) sb.Append("Recent runs: could not be read.");
            else if (runs.Count == 0) sb.Append("Recent runs: none.");
            else
            {
                var now = DateTime.UtcNow;
                sb.AppendLine($"Recent runs ({Math.Min(5, runs.Count)} of {runs.Count}):");
                foreach (var run in runs.Take(5)) sb.AppendLine("- " + AgentPipelineHelpers.RunLine(run, now));
            }
            return ToolResult.Ok(sb.ToString().TrimEnd('\n'));
        }

        // ─── build_pipeline ──────────────────────────────────────────────────────

        public static async Task<ToolResult> BuildPipeline(JObject input, ToolRunContext ctx)
        {
            RequireSignIn();
            var title = CheckTitle(Args.Str(input, "title"));
            if (title.Length == 0) throw new ToolException("'title' is required (≤200 characters).");
            var startsFrom = (Args.Str(input, "startsFrom") ?? "").Trim().ToLowerInvariant();
            if (startsFrom != "picture" && startsFrom != "model") throw new ToolException("'startsFrom' must be picture or model.");
            var steps = AgentPipelineHelpers.ParseBuildSteps(Args.Get(input, "steps"));
            var open = Args.Bool(input, "open", true);

            await LoadPlanAsync(ctx);
            var built = GraphEditing.Build(title, startsFrom, steps, _gates);
            if (built.StepNodeIds.All(n => n == null))
                throw new ToolException("None of the steps could be added: " + string.Join("; ", built.Skipped.Select(s => $"steps[{s.index}]: {s.reason}")));

            ctx.Status("Saving the pipeline…");
            var pipeline = await PipelineCall(() => PipelinesApi.CreateAsync(built.Title, built.Graph));
            var graph = pipeline?.Graph != null && pipeline.Graph.Nodes.Count > 0 ? Recipe(pipeline) : built.Graph;
            Saved(pipeline.Id);

            var opened = false;
            if (open && PipelineToolHooks.OpenPipeline != null)
            {
                try
                {
                    PipelineToolHooks.OpenPipeline(pipeline.Id, null);
                    opened = true;
                }
                catch (Exception)
                {
                    opened = false;
                }
            }

            var sb = new StringBuilder($"Created pipeline #{pipeline.Id} “{pipeline.Title}” (starts from a {startsFrom}).");
            sb.Append(opened ? " Opened in Window › Zetta 3D › Pipelines.\n" : open ? " (The Pipelines window could not be opened.)\n" : "\n");
            sb.Append("Node ids by step: ");
            sb.AppendLine(string.Join(", ", built.StepNodeIds.Select((nodeId, i) => $"[{i}] {steps[i].Kind} → {nodeId ?? "skipped"}")));
            foreach (var (index, reason) in built.Skipped) sb.AppendLine($"Skipped steps[{index}]: {reason}");
            for (var i = 0; i < steps.Count; i++)
                if (steps[i].After is int a && built.StepNodeIds[a] == null && built.StepNodeIds[i] != null)
                    sb.AppendLine($"steps[{i}] follows the seed because steps[{a}] was skipped.");
            AppendGraphReport(sb, graph);
            return ToolResult.Ok(sb.ToString());
        }

        // ─── edit_pipeline ───────────────────────────────────────────────────────

        public static async Task<ToolResult> EditPipeline(JObject input, ToolRunContext ctx)
        {
            RequireSignIn();
            var id = AgentPipelineHelpers.RequireId(input, "pipelineId");
            var title = Args.Present(input, "title") ? CheckTitle(Args.Str(input, "title")) : null;
            if (title != null && title.Length == 0) throw new ToolException("'title' may not be empty.");
            var pipeline = await GetPipeline(id);
            var graph = Recipe(pipeline);
            var changesToken = Args.Get(input, "changes");
            var renameOnly = title != null && (changesToken == null || changesToken is JArray { Count: 0 });
            var changes = renameOnly ? new List<PipelineChange>() : AgentPipelineHelpers.ParseChanges(changesToken, graph);

            await LoadPlanAsync(ctx);
            var applied = GraphEditing.ApplyAgentChanges(graph, changes, _gates);
            var changed = changes.Count > applied.Skipped.Count;
            if (!changed && title == null)
                throw new ToolException("Nothing was changed: " + string.Join("; ", applied.Skipped.Select(s => $"changes[{s.index}]: {s.reason}")));

            ctx.Status("Saving the pipeline…");
            var saved = await PipelineCall(() => PipelinesApi.SaveAsync(id, applied.Graph, title, _gates), $"Pipeline #{id} was not found.");
            Saved(id);
            var result = saved?.Graph != null && saved.Graph.Nodes.Count > 0 ? Recipe(saved) : applied.Graph;

            var sb = new StringBuilder($"Saved pipeline #{id} “{saved?.Title ?? title ?? pipeline.Title}”.");
            sb.AppendLine(title != null ? " Renamed." : "");
            var added = applied.Added.ToList();
            if (added.Count > 0)
            {
                var addIndexes = changes.Select((c, i) => (c, i)).Where(p => p.c.Op == "add" && applied.Skipped.All(s => s.index != p.i)).Select(p => p.i).ToList();
                sb.AppendLine("Added: " + string.Join(", ", added.Select((nodeId, n) => n < addIndexes.Count ? $"changes[{addIndexes[n]}] {changes[addIndexes[n]].Kind} → {nodeId}" : nodeId)));
            }
            foreach (var (index, reason) in applied.Skipped) sb.AppendLine($"Skipped changes[{index}]: {reason}");
            AppendGraphReport(sb, result);
            return ToolResult.Ok(sb.ToString());
        }

        // ─── run_pipeline ────────────────────────────────────────────────────────

        public static async Task<ToolResult> RunPipeline(JObject input, ToolRunContext ctx)
        {
            RequireSignIn();
            var id = AgentPipelineHelpers.RequireId(input, "pipelineId");
            var mode = (Args.Str(input, "mode") ?? RunStatuses.ModeManual).Trim().ToLowerInvariant();
            if (mode != RunStatuses.ModeManual && mode != RunStatuses.ModeAuto) throw new ToolException("'mode' must be manual or auto.");
            var inputs = AgentPipelineHelpers.ParseInputs(Args.Get(input, "inputs"));

            ctx.Status("Reading the pipeline…");
            var pipeline = await GetPipeline(id);
            var recipe = Recipe(pipeline);
            var seedNode = recipe.Seed ?? throw new ToolException($"Pipeline #{id} has no seed step — rebuild it with build_pipeline.");
            var seeds = AgentPipelineHelpers.ParseSeeds(Args.Get(input, "seeds"), seedNode.Kind);

            await LoadPlanAsync(ctx);
            var problems = GraphRules.Problems(recipe, _gates);
            if (problems.Count > 0)
                throw new ToolException($"Pipeline #{id} cannot run yet — fix it with edit_pipeline: " + string.Join(" ", problems.Select(p => p.ToString())));
            var missing = AgentPipelineHelpers.MissingInputs(recipe, inputs, _gates);
            if (missing.Count > 0) throw new ToolException("Every run needs these per-run inputs: " + string.Join("; ", missing) + ".");

            // Check every seed before the card; nothing is uploaded until the user agrees.
            var models = new Dictionary<int, Generation>();
            foreach (var seed in seeds)
            {
                if (seed.GenerationId != null)
                {
                    var generation = await FetchAsync(seed.GenerationId.Value);
                    if (generation.IsSplat) throw new ToolException($"seeds[{seed.Index}]: #{generation.Id} is a Gaussian splat — pipelines in Unity start from a mesh.");
                    if (!generation.IsSuccess) throw new ToolException($"seeds[{seed.Index}]: #{generation.Id} is {generation.StatusLabel.ToLowerInvariant()} — only finished models can seed a run.");
                    models[seed.Index] = generation;
                }
                else if (seed.AssetPath != null)
                {
                    var path = FileTools.ForRead(seed.AssetPath);
                    if (!File.Exists(FileTools.Physical(path))) throw new ToolException($"seeds[{seed.Index}]: {path} does not exist. Use find_assets to find it.");
                }
            }

            var estimate = GraphRules.Estimate(recipe, _prices, _gates);
            var total = AgentPipelineHelpers.RunTotal(estimate, seeds.Count);
            var approved = await ctx.Confirm(new ApprovalRequest
            {
                Kind = "generate",
                Title = $"Run pipeline: {pipeline.Title} on {Fmt.Plural(seeds.Count, "seed")}",
                Detail = AgentPipelineHelpers.RunApprovalDetail(id, pipeline.Title, recipe, seeds, mode, inputs, estimate, _prices, _gates),
                Credits = total,
                ConfirmLabel = seeds.Count == 1 ? "Run" : $"Run {seeds.Count}",
            });
            if (!approved) return ToolResult.Error("The user declined.");

            // The seeds, uploaded now.
            var ready = new List<PipelineSeed>();
            foreach (var seed in seeds)
            {
                ctx.Status($"Preparing seed {seed.Index + 1} of {seeds.Count}…");
                if (seed.ImageId != null) ready.Add(PipelineSeed.FromImagePost(seed.ImageId.Value));
                else if (seed.GenerationId != null) ready.Add(PipelineSeed.FromModel(models[seed.Index]));
                else if (AgentPipelineHelpers.IsModelPath(seed.AssetPath))
                {
                    Generation row;
                    try
                    {
                        row = await StudioAssets.SourceFromAssetAsync(seed.AssetPath, s => ctx.Status(s));
                    }
                    catch (StudioProblem e)
                    {
                        throw new ToolException($"seeds[{seed.Index}] ({seed.AssetPath}): {e.Message} Nothing was run.");
                    }
                    ready.Add(PipelineSeed.FromModel(row));
                }
                else
                {
                    var bytes = File.ReadAllBytes(FileTools.Physical(seed.AssetPath));
                    ready.Add(await PipelineSeed.FromUploadAsync(bytes, AgentPipelineHelpers.ContentTypeOf(seed.AssetPath), Path.GetFileName(seed.AssetPath)));
                }
            }

            // Drafts first, so each gets its inputs before it starts (the web's New run).
            ctx.Status("Creating the runs…");
            var runGraph = RequestBuilder.WithRequests(RequestBuilder.ForNewRun(recipe), _gates);
            var created = await PipelineCall(() => PipelinesApi.BatchAsync(id, mode, ready, runGraph, draft: true), $"Pipeline #{id} was not found.");
            var patches = inputs.Select(i => (input: i, change: RequestBuilder.RunNodeChange(runGraph, i.NodeId, AgentPipelineHelpers.InputPatch(runGraph.Node(i.NodeId), i), _gates))).ToList();

            var started = new List<long>();
            var notStarted = new List<string>();
            foreach (var run in created)
            {
                ctx.Status($"Starting run #{run.Id}…");
                try
                {
                    foreach (var (runInput, change) in patches)
                        await PipelineCall(() => PipelinesApi.PatchRunNodeAsync(run.Id, runInput.NodeId, change.request, change.settings));
                    var live = await PipelineCall(() => PipelinesApi.StartAsync(run.Id));
                    started.Add(run.Id);
                    RunFollower.Follow(run.Id, id, ctx.SessionId, RunImportMode.FinalSteps, ModelsFolder);
                    if (live != null) await RunFollower.Report(live);
                }
                catch (ToolException e)
                {
                    notStarted.Add($"#{run.Id}: {e.Message}");
                }
                catch (ZettaApiException e)
                {
                    notStarted.Add($"#{run.Id}: {ZettaCatalog.Explain(e)}");
                }
            }

            if (started.Count == 0 && notStarted.Count > 0)
                throw new ToolException("The runs were created as drafts but none started: " + string.Join("; ", notStarted));

            var sb = new StringBuilder();
            sb.Append($"Started {Fmt.Plural(started.Count, "run")} of pipeline #{id} “{pipeline.Title}”: {string.Join(", ", started.Select(r => "#" + r))}");
            sb.Append($" — {RunStatuses.ModeLabel(mode)} mode, {AgentPipelineHelpers.EstimateText(recipe, estimate).TrimEnd('.').ToLowerInvariant()} each");
            if (total != null) sb.Append($" (up to {total} credits in all)");
            sb.AppendLine(".");
            if (notStarted.Count > 0) sb.AppendLine("Not started (left as drafts): " + string.Join("; ", notStarted));
            sb.Append(mode == RunStatuses.ModeManual
                ? "Manual: each paid step waits for the user — pipeline_run_status lists them; manage_pipeline_run run_step / run_waiting runs them (with a card). "
                : "Auto: every step submits itself. ");
            sb.Append($"Finished models import to {ModelsFolder}/. A pipeline_run_finished event arrives when a run finishes or needs the user.");
            return ToolResult.Ok(sb.ToString());
        }

        // ─── pipeline_run_status ─────────────────────────────────────────────────

        public static async Task<ToolResult> PipelineRunStatus(JObject input, ToolRunContext ctx)
        {
            RequireSignIn();
            var runId = AgentPipelineHelpers.RequireId(input, "runId");
            var run = await GetRun(runId);
            await LoadPlanAsync(ctx);
            return ToolResult.Ok(AgentPipelineHelpers.RunStatusText(run, _prices, _gates, DateTime.UtcNow));
        }

        // ─── manage_pipeline_run ─────────────────────────────────────────────────

        static readonly string[] ManageActions = { "pause", "resume", "cancel", "set_mode", "run_waiting", "run_step", "retry_step", "choose_parts" };

        /// <summary>Keeps a live run followed for this session (its import choice kept when it was already followed).</summary>
        static async Task EnsureFollowed(PipelineRun run, ToolRunContext ctx)
        {
            if (run == null) return;
            if (run.IsLive)
            {
                var existing = RunFollower.Runs.FirstOrDefault(r => r.RunId == run.Id);
                RunFollower.Follow(run.Id, run.PipelineId, ctx.SessionId, existing?.Import ?? RunImportMode.FinalSteps, existing != null ? existing.Folder : ModelsFolder);
            }
            await RunFollower.Report(run);
        }

        public static async Task<ToolResult> ManagePipelineRun(JObject input, ToolRunContext ctx)
        {
            RequireSignIn();
            var runId = AgentPipelineHelpers.RequireId(input, "runId");
            var action = (Args.Str(input, "action") ?? "").Trim().ToLowerInvariant();
            if (!ManageActions.Contains(action)) throw new ToolException($"'action' must be one of {string.Join(", ", ManageActions)}.");
            var stepId = Args.LongN(input, "stepId");
            if (stepId == null && action is "run_step" or "retry_step" or "choose_parts")
                throw new ToolException($"'{action}' needs a stepId (the numeric step id pipeline_run_status lists).");
            string mode = null;
            if (action == "set_mode")
            {
                mode = (Args.Str(input, "mode") ?? "").Trim().ToLowerInvariant();
                if (mode != RunStatuses.ModeManual && mode != RunStatuses.ModeAuto) throw new ToolException("set_mode needs 'mode': manual or auto.");
            }
            List<string> keys = null, extraParts = null;
            if (action == "choose_parts")
            {
                keys = AgentPipelineHelpers.ParsePartList(Args.Get(input, "keys"), "keys");
                extraParts = AgentPipelineHelpers.ParsePartList(Args.Get(input, "extraParts"), "extraParts");
                if (keys.Count == 0 && extraParts.Count == 0) throw new ToolException("choose_parts needs keys (from the review's parts) and/or extraParts.");
            }

            var run = await GetRun(runId);
            await LoadPlanAsync(ctx);
            var step = stepId == null ? null : run.Steps.FirstOrDefault(s => s.Id == stepId.Value)
                ?? throw new ToolException($"Run #{runId} has no step {stepId}. pipeline_run_status lists its steps.");

            string Name(PipelineRunStep s) => AgentPipelineHelpers.StepName(run, s);
            Task<bool> Spend(string title, string detail, int? credits, string label) => ctx.Confirm(new ApprovalRequest
            {
                Kind = "generate", Title = title, Detail = detail, Credits = credits, ConfirmLabel = label,
            });
            string Lines(IEnumerable<PipelineRunStep> steps) => string.Join("\n", steps.Select(s =>
            {
                var price = AgentPipelineHelpers.StepPrice(run, s, _prices, _gates);
                return $"• {Name(s)} — {(price == null ? "price unknown" : price == 0 ? "free" : $"{price} credits")}";
            }));

            PipelineRun after;
            switch (action)
            {
                case "pause":
                    after = await PipelineCall(() => PipelinesApi.PauseAsync(runId));
                    break;
                case "resume":
                    after = await PipelineCall(() => PipelinesApi.ResumeAsync(runId));
                    break;
                case "cancel":
                {
                    if (!run.IsLive && run.Status != RunStatuses.Draft) throw new ToolException($"Run #{runId} is already over ({run.Status}).");
                    var ok = await ctx.Confirm(new ApprovalRequest
                    {
                        Kind = "destructive",
                        Title = $"Cancel run #{runId}",
                        Detail = $"Stops run #{runId} of pipeline #{run.PipelineId} ({RunStatuses.Label(run.Status)}, {run.SpentCredits} credits spent). " +
                                 "Steps not started are cancelled; a queued 3D job is cancelled and refunded. Finished models stay in the library.",
                        ConfirmLabel = "Cancel run",
                    });
                    if (!ok) return ToolResult.Error("The user declined.");
                    after = await PipelineCall(() => PipelinesApi.CancelAsync(runId));
                    break;
                }
                case "set_mode":
                {
                    // Auto releases every waiting step now and from now on — that spends credits, so it is a card too.
                    if (mode == RunStatuses.ModeAuto && run.Mode != RunStatuses.ModeAuto && (run.IsLive || run.Status == RunStatuses.Draft))
                    {
                        var remaining = Math.Max(0, run.EstimatedCredits - run.SpentCredits);
                        var waiting = run.Steps.Where(s => s.Status == StepStatuses.Waiting).ToList();
                        var ok = await Spend($"Switch run #{runId} to Auto",
                            "Auto submits every step by itself, to the end — " + (waiting.Count > 0 ? $"starting now with:\n{Lines(waiting)}\n" : "") +
                            $"Up to {remaining} more credits (the run's estimate less what it has spent).", remaining, "Switch to Auto");
                        if (!ok) return ToolResult.Error("The user declined.");
                    }
                    after = await PipelineCall(() => PipelinesApi.SetModeAsync(runId, mode));
                    break;
                }
                case "run_waiting":
                {
                    var waiting = run.Steps.Where(s => s.Status == StepStatuses.Waiting).ToList();
                    if (waiting.Count == 0) throw new ToolException($"Run #{runId} has no steps waiting for a Run ({RunStatuses.Label(run.Status)}).");
                    var credits = AgentPipelineHelpers.StepsPrice(run, waiting, _prices, _gates);
                    if (!await Spend($"Run {Fmt.Plural(waiting.Count, "waiting step")} of run #{runId}", Lines(waiting), credits, "Run"))
                        return ToolResult.Error("The user declined.");
                    after = await PipelineCall(() => PipelinesApi.RunWaitingAsync(runId));
                    break;
                }
                case "run_step":
                {
                    if (step.Status != StepStatuses.Waiting)
                        throw new ToolException($"{Name(step)} is {step.Status}, not waiting — only a waiting step can be run{(step.Status == StepStatuses.Failed ? " (use retry_step)" : step.Status == StepStatuses.Review ? " (use choose_parts)" : "")}.");
                    var credits = AgentPipelineHelpers.StepPrice(run, step, _prices, _gates);
                    if (!await Spend($"Run step: {run.Graph.Node(step.NodeId)?.DisplayName ?? step.NodeId} (run #{runId})", Lines(new[] { step }), credits, "Run"))
                        return ToolResult.Error("The user declined.");
                    after = await PipelineCall(() => PipelinesApi.RunStepAsync(runId, step.Id));
                    break;
                }
                case "retry_step":
                {
                    if (step.Status != StepStatuses.Failed)
                        throw new ToolException($"{Name(step)} is {step.Status} — only a failed step can be retried.");
                    var credits = AgentPipelineHelpers.StepPrice(run, step, _prices, _gates);
                    var detail = Lines(new[] { step }) + (string.IsNullOrEmpty(step.ErrorMessage) ? "" : $"\nLast error: {Fmt.Short(step.ErrorMessage, 200)}");
                    if (!await Spend($"Retry step: {run.Graph.Node(step.NodeId)?.DisplayName ?? step.NodeId} (run #{runId})", detail, credits, "Retry"))
                        return ToolResult.Error("The user declined.");
                    after = await PipelineCall(() => PipelinesApi.RetryStepAsync(runId, step.Id));
                    break;
                }
                default: // choose_parts
                {
                    if (step.Status != StepStatuses.Review)
                        throw new ToolException($"{Name(step)} is {step.Status} — choose_parts is for an Isolate parts step waiting for review.");
                    var known = step.Review?.Identification?.Parts?.Select(p => p.Key).Where(k => k != null).ToList() ?? new List<string>();
                    var unknown = keys.Where(k => known.Count > 0 && !known.Contains(k)).ToList();
                    if (unknown.Count > 0)
                        throw new ToolException($"Unknown part key(s) {string.Join(", ", unknown)} — the review found: {string.Join(", ", known)}. Name new parts in extraParts.");
                    after = await PipelineCall(() => PipelinesApi.ChoosePartsAsync(runId, step.Id, keys, extraParts));
                    break;
                }
            }

            after ??= await GetRun(runId);
            await EnsureFollowed(after, ctx);
            var text = AgentPipelineHelpers.RunStatusText(after, _prices, _gates, DateTime.UtcNow);
            var verb = action switch
            {
                "pause" => "Paused", "resume" => "Resumed", "cancel" => "Cancelled", "set_mode" => $"Switched to {RunStatuses.ModeLabel(mode)} mode:",
                "run_waiting" => "Released the waiting steps of", "run_step" => $"Started step {stepId} of", "retry_step" => $"Retrying step {stepId} of",
                _ => $"Chose the parts for step {stepId} of",
            };
            var follow = after.IsLive ? " Followed — a pipeline_run_finished event arrives when it finishes or needs the user." : "";
            return ToolResult.Ok($"{verb} run #{runId}.{follow}\n{text}");
        }

        // ─── import_pipeline_results ─────────────────────────────────────────────

        public static async Task<ToolResult> ImportPipelineResults(JObject input, ToolRunContext ctx)
        {
            RequireSignIn();
            var runId = AgentPipelineHelpers.RequireId(input, "runId");
            var nodeIds = Args.StrList(input, "nodeIds")?.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct().ToList();
            if (nodeIds is { Count: 0 }) nodeIds = null;
            if (nodeIds != null && nodeIds.Count > AgentPipelineHelpers.MaxImportNodes)
                throw new ToolException($"At most {AgentPipelineHelpers.MaxImportNodes} nodeIds per call.");

            ZettaImporter.Placement place = null;
            if (Args.Get(input, "place") is JObject p)
            {
                place = new ZettaImporter.Placement { Select = false, Position = Args.Vec3(p, "position") };
                if (Args.Present(p, "parent")) place.Parent = TargetResolver.Resolve(Args.Str(p, "parent")).transform;
            }

            ctx.Status("Reading the run…");
            var run = await GetRun(runId);
            var unknownNodes = nodeIds?.Where(n => run.Graph.Node(n) == null).ToList();
            if (unknownNodes is { Count: > 0 })
                throw new ToolException($"Run #{runId} has no node(s) {string.Join(", ", unknownNodes)}. Its nodes: {string.Join(", ", run.Graph.Nodes.Select(n => n.Id))}.");

            var importable = run.ImportableSteps(nodeIds == null ? RunImportMode.FinalSteps : RunImportMode.AllMeshSteps)
                .Where(s => nodeIds == null || nodeIds.Contains(s.NodeId)).ToList();
            var notes = new List<string>();
            foreach (var s in run.Steps.Where(s => nodeIds?.Contains(s.NodeId) ?? true).Where(s => !importable.Contains(s)))
            {
                var name = AgentPipelineHelpers.StepName(run, s);
                switch (s.Output)
                {
                    case SplatStepOutput _:
                        notes.Add($"{name}: a Gaussian splat — not supported in Unity (open it on zetta3d.ai)");
                        break;
                    case FileStepOutput _:
                        notes.Add($"{name}: an export file — open it on zetta3d.ai");
                        break;
                    case MeshStepOutput _ when nodeIds == null:
                        break; // an intermediate model; the final ones are imported
                    case MeshStepOutput _:
                        notes.Add($"{name}: its model is not ready ({s.Generation?.Status ?? s.Status})");
                        break;
                    case null when nodeIds != null && NodeCatalog.IsThreeD(run.Graph.Node(s.NodeId)?.Kind):
                        notes.Add($"{name}: {s.Status} — not finished yet");
                        break;
                    default:
                        if (nodeIds != null && !NodeCatalog.IsSeed(run.Graph.Node(s.NodeId)?.Kind)) notes.Add($"{name}: a picture step — nothing to import (pipeline_run_status lists its picture)");
                        break;
                }
            }
            if (importable.Count == 0)
                throw new ToolException($"Nothing to import from run #{runId} ({RunStatuses.Label(run.Status)})" +
                                        (notes.Count > 0 ? ": " + string.Join("; ", notes) : run.IsLive ? " — no model step has finished yet." : " — it made no models."));

            FileTools.EnsureFolder(ModelsFolder);
            var paths = new List<string>();
            var failures = new List<string>();
            foreach (var nodeId in importable.Select(s => s.NodeId).Distinct())
            {
                ctx.Status($"Importing {nodeId}…");
                try
                {
                    paths.AddRange(await RunFollower.ImportAsync(run, new[] { nodeId }, ModelsFolder, place));
                }
                catch (Exception e)
                {
                    failures.Add($"{nodeId}: {Fmt.FirstLine(e.Message)}");
                }
            }
            if (paths.Count == 0 && failures.Count > 0) throw new ToolException("The import failed: " + string.Join("; ", failures));

            var sb = new StringBuilder($"Imported from run #{runId}: ");
            sb.Append(string.Join(", ", paths)).Append('.');
            if (place != null) sb.Append(" Placed in the scene.");
            else sb.Append(" Not placed; create_gameobject with prefab = one of these paths puts it in the scene.");
            if (failures.Count > 0) sb.Append("\nFailed: ").Append(string.Join("; ", failures));
            if (notes.Count > 0) sb.Append("\nSkipped: ").Append(string.Join("; ", notes));
            return ToolResult.Ok(sb.ToString());
        }

        // ─── Labels and cards ────────────────────────────────────────────────────

        /// <summary>The chat label of a pipeline tool call, or null for another tool.</summary>
        public static string DescribePipelineTool(string name, JObject i)
        {
            string S(string key) => Args.Str(i, key) ?? "";
            switch (name)
            {
                case "list_pipelines": return "Listing your 3D pipelines";
                case "get_pipeline": return $"Reading pipeline #{S("pipelineId")}";
                case "build_pipeline": return $"Building pipeline “{Fmt.Short(S("title"), 60)}”";
                case "edit_pipeline": return $"Editing pipeline #{S("pipelineId")}";
                case "run_pipeline":
                {
                    var n = (Args.Get(i, "seeds") as JArray)?.Count ?? 0;
                    return $"Running pipeline #{S("pipelineId")} on {Fmt.Plural(n, "seed")}";
                }
                case "pipeline_run_status": return $"Checking pipeline run #{S("runId")}";
                case "manage_pipeline_run":
                {
                    var run = S("runId");
                    switch (S("action"))
                    {
                        case "pause": return $"Pausing run #{run}";
                        case "resume": return $"Resuming run #{run}";
                        case "cancel": return $"Cancelling run #{run}";
                        case "set_mode": return $"Switching run #{run} to {S("mode")}";
                        case "run_waiting": return $"Running the waiting steps of run #{run}";
                        case "run_step": return $"Running step {S("stepId")} of run #{run}";
                        case "retry_step": return $"Retrying step {S("stepId")} of run #{run}";
                        case "choose_parts": return $"Choosing parts for run #{run}";
                        default: return $"Managing run #{run}";
                    }
                }
                case "import_pipeline_results": return $"Importing the results of run #{S("runId")}";
                default: return null;
            }
        }

        /// <summary>The approval-card detail of a pipeline write (shown when the user confirms every write), or null.</summary>
        public static string SummarizePipelineTool(string name, JObject i)
        {
            string S(string key) => Args.Str(i, key) ?? "";
            switch (name)
            {
                case "build_pipeline":
                {
                    var sb = new StringBuilder($"New pipeline “{S("title")}”, starts from a {S("startsFrom")}.");
                    if (Args.Get(i, "steps") is JArray steps)
                        for (var n = 0; n < steps.Count; n++)
                        {
                            var after = steps[n]?["after"];
                            sb.Append($"\n{n}. {steps[n]?["kind"]}{(after != null && after.Type != JTokenType.Null ? $" after {after}" : "")}");
                            if (steps[n]?["output"] != null) sb.Append($" ({steps[n]["output"]})");
                        }
                    return sb.ToString();
                }
                case "edit_pipeline":
                {
                    var sb = new StringBuilder($"Pipeline #{S("pipelineId")}");
                    if (Args.Present(i, "title")) sb.Append($" → “{S("title")}”");
                    if (Args.Get(i, "changes") is JArray changes)
                        foreach (var c in changes.OfType<JObject>())
                        {
                            var op = c["op"]?.ToString();
                            sb.Append('\n').Append(op switch
                            {
                                "add" => $"+ {c["kind"]}{(c["after"] != null ? $" after {c["after"]}" : "")}",
                                "set" => $"~ {c["nodeId"]}: {Fmt.Short(c["settings"]?.ToString(Newtonsoft.Json.Formatting.None), 160)}",
                                "remove" => $"− {c["nodeId"]}",
                                _ => op,
                            });
                        }
                    return sb.ToString();
                }
                case "manage_pipeline_run":
                    return $"Run #{S("runId")}: {S("action")}{(Args.Present(i, "mode") ? " " + S("mode") : "")}{(Args.Present(i, "stepId") ? $" (step {S("stepId")})" : "")}";
                case "import_pipeline_results":
                {
                    var nodes = Args.StrList(i, "nodeIds");
                    return $"Imports the models of run #{S("runId")} ({(nodes is { Count: > 0 } ? string.Join(", ", nodes) : "its final steps")}) into {ModelsFolder}/" +
                           (Args.Present(i, "place") ? ", placed in the scene." : ".");
                }
                default:
                    return null;
            }
        }
    }
}
