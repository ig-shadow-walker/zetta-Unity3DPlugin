using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Zetta3D.Editor.Pipelines
{
    /// <summary>What stops a graph from running, pinned to its node (null = the graph as a whole).</summary>
    public sealed class PipelineProblem
    {
        [JsonProperty("nodeId")] public string NodeId;
        [JsonProperty("message")] public string Message;

        public PipelineProblem()
        {
        }

        public PipelineProblem(string nodeId, string message)
        {
            NodeId = nodeId;
            Message = message;
        }

        public override string ToString() => NodeId == null ? Message : $"[{NodeId}] {Message}";
    }

    /// <summary>A pipeline, as GET /pipelines/:id returns it.</summary>
    public sealed class Pipeline
    {
        [JsonProperty("id")] public long Id;
        [JsonProperty("title")] public string Title;
        [JsonProperty("graph")] public PipelineGraph Graph = new PipelineGraph();
        [JsonProperty("createdAt")] public DateTime CreatedAt;
        [JsonProperty("updatedAt")] public DateTime UpdatedAt;
        /// <summary>The server's own graphProblems — note it also asks for a seed picture, which the editor does not.</summary>
        [JsonProperty("problems")] public List<PipelineProblem> Problems = new List<PipelineProblem>();
    }

    public sealed class PipelineLastRun
    {
        [JsonProperty("id")] public long Id;
        [JsonProperty("status")] public string Status;
        [JsonProperty("createdAt")] public DateTime CreatedAt;
        [JsonIgnore] public RunStatus State => RunStatuses.Parse(Status);
    }

    /// <summary>A pipeline as GET /pipelines lists it.</summary>
    public sealed class PipelineSummary
    {
        [JsonProperty("id")] public long Id;
        [JsonProperty("title")] public string Title;
        [JsonProperty("steps")] public int Steps;
        [JsonProperty("createdAt")] public DateTime CreatedAt;
        [JsonProperty("updatedAt")] public DateTime UpdatedAt;
        [JsonProperty("lastRun")] public PipelineLastRun LastRun;
        /// <summary>How many of its runs are in each state (status → count) — "2 running · 1 needs you".</summary>
        [JsonProperty("runCounts")] public Dictionary<string, int> RunCounts = new Dictionary<string, int>();

        /// <summary>"2 running · 1 needs you" — the live and attention states only, in the list's words.</summary>
        public string RunCountsLabel
        {
            get
            {
                var parts = new List<string>();
                int Count(string status) => RunCounts != null && RunCounts.TryGetValue(status, out var n) ? n : 0;
                if (Count(RunStatuses.Running) > 0) parts.Add($"{Count(RunStatuses.Running)} running");
                var attention = Count(RunStatuses.Awaiting) + Count(RunStatuses.Draft);
                if (attention > 0) parts.Add($"{attention} needs you");
                if (Count(RunStatuses.Paused) > 0) parts.Add($"{Count(RunStatuses.Paused)} paused");
                return string.Join(" · ", parts);
            }
        }
    }

    public sealed class RunProgress
    {
        [JsonProperty("done")] public int Done;
        [JsonProperty("total")] public int Total;
        [JsonProperty("waiting")] public int Waiting;
        [JsonProperty("failed")] public int Failed;
    }

    public sealed class RunSeedSummary
    {
        /// <summary>picture | model</summary>
        [JsonProperty("type")] public string Type;
        [JsonProperty("name")] public string Name;
        [JsonProperty("thumbnailUrl")] public string ThumbnailUrl;
    }

    /// <summary>
    /// A run as GET /pipelines/:id/runs lists it. POST /pipelines/:id/batch returns a lighter one
    /// (id, mode, status, estimatedCredits, createdAt, finishedAt only).
    /// </summary>
    public sealed class RunSummary
    {
        [JsonProperty("id")] public long Id;
        [JsonProperty("mode")] public string Mode;
        [JsonProperty("status")] public string Status;
        [JsonProperty("paused")] public bool Paused;
        [JsonProperty("estimatedCredits")] public int EstimatedCredits;
        [JsonProperty("spentCredits")] public int SpentCredits;
        [JsonProperty("createdAt")] public DateTime CreatedAt;
        [JsonProperty("finishedAt")] public DateTime? FinishedAt;
        [JsonProperty("progress")] public RunProgress Progress;
        /// <summary>A draft's steps still to fill in before it can start.</summary>
        [JsonProperty("inputsNeeded")] public int InputsNeeded;
        [JsonProperty("seed")] public RunSeedSummary Seed;

        [JsonIgnore] public RunStatus State => RunStatuses.Parse(Status);
        [JsonIgnore] public bool IsLive => RunStatuses.IsLive(Status);
    }

    /// <summary>A picture the server signed for display (the url expires).</summary>
    public sealed class SignedPicture
    {
        [JsonProperty("url")] public string Url;
        [JsonProperty("gen2dPostId")] public long? Gen2dPostId;
    }

    public sealed class IdentifiedPart
    {
        [JsonProperty("key")] public string Key;
        [JsonProperty("label")] public string Label;
        /// <summary>held | attached | detail (character on the server side, too)</summary>
        [JsonProperty("kind")] public string Kind;
        /// <summary>low | medium | high — the sensitivity at which it is isolated.</summary>
        [JsonProperty("level")] public string Level;
        /// <summary>x, y, width, height in 0–1 of the source picture; null when unknown.</summary>
        [JsonProperty("box")] public double[] Box;
    }

    public sealed class PartIdentification
    {
        [JsonProperty("subject")] public string Subject;
        [JsonProperty("humanoid")] public bool Humanoid;
        [JsonProperty("parts")] public List<IdentifiedPart> Parts = new List<IdentifiedPart>();
    }

    /// <summary>Isolate Parts waiting for the user (step status <c>review</c>): what it found, what it proposes, the picture it read.</summary>
    public sealed class StepReview
    {
        [JsonProperty("identification")] public PartIdentification Identification;
        [JsonProperty("proposedKeys")] public List<string> ProposedKeys = new List<string>();
        [JsonProperty("source")] public SignedPicture Source;
    }

    /// <summary>
    /// What a step produced — a union on <c>type</c>: picture, views, isolated, mesh, splat or file. An output
    /// type this version does not know reads as <see cref="UnknownStepOutput"/>; <see cref="Raw"/> always holds the JSON.
    /// </summary>
    [JsonConverter(typeof(StepOutputConverter))]
    public abstract class StepOutput
    {
        public string Type;
        public JObject Raw;

        public static StepOutput FromJson(JToken token)
        {
            if (!(token is JObject json)) return null;
            var type = json["type"]?.ToString();
            StepOutput output = type switch
            {
                "picture" => new PictureStepOutput { Picture = json.As<SignedPicture>("picture") },
                "views" => new ViewsStepOutput { Views = json.As<List<ViewPicture>>("views") ?? new List<ViewPicture>() },
                "isolated" => new IsolatedStepOutput
                {
                    Character = json.As<SignedPicture>("character"),
                    Parts = json.As<List<IsolatedPartOutput>>("parts") ?? new List<IsolatedPartOutput>(),
                },
                "mesh" => new MeshStepOutput { Gen3dPostId = json.As<long>("gen3dPostId") },
                "splat" => new SplatStepOutput { Gen3dPostId = json.As<long>("gen3dPostId") },
                "file" => new FileStepOutput { Gen3dPostId = json.As<long>("gen3dPostId"), Format = json["format"]?.ToString() },
                _ => new UnknownStepOutput(),
            };
            output.Type = type;
            output.Raw = (JObject)json.DeepClone();
            return output;
        }

        /// <summary>The 3D post the output names (mesh, splat, file), or null.</summary>
        public virtual long? Gen3dPost => null;
    }

    [JsonConverter(typeof(StepOutputConverter))]
    public sealed class PictureStepOutput : StepOutput
    {
        public SignedPicture Picture;
    }

    public sealed class ViewPicture
    {
        [JsonProperty("viewType")] public string ViewType;
        [JsonProperty("picture")] public SignedPicture Picture;
    }

    [JsonConverter(typeof(StepOutputConverter))]
    public sealed class ViewsStepOutput : StepOutput
    {
        public List<ViewPicture> Views = new List<ViewPicture>();
    }

    public sealed class IsolatedPartOutput
    {
        [JsonProperty("key")] public string Key;
        [JsonProperty("label")] public string Label;
        [JsonProperty("kind")] public string Kind;
        [JsonProperty("box")] public double[] Box;
        [JsonProperty("picture")] public SignedPicture Picture;
    }

    [JsonConverter(typeof(StepOutputConverter))]
    public sealed class IsolatedStepOutput : StepOutput
    {
        public SignedPicture Character;
        public List<IsolatedPartOutput> Parts = new List<IsolatedPartOutput>();
    }

    [JsonConverter(typeof(StepOutputConverter))]
    public sealed class MeshStepOutput : StepOutput
    {
        public long Gen3dPostId;
        public override long? Gen3dPost => Gen3dPostId;
    }

    /// <summary>A Gaussian splat — made by a web-only node; not importable in Unity.</summary>
    [JsonConverter(typeof(StepOutputConverter))]
    public sealed class SplatStepOutput : StepOutput
    {
        public long Gen3dPostId;
        public override long? Gen3dPost => Gen3dPostId;
    }

    /// <summary>An Export node's file (web-only).</summary>
    [JsonConverter(typeof(StepOutputConverter))]
    public sealed class FileStepOutput : StepOutput
    {
        public long Gen3dPostId;
        public string Format;
        public override long? Gen3dPost => Gen3dPostId;
    }

    [JsonConverter(typeof(StepOutputConverter))]
    public sealed class UnknownStepOutput : StepOutput
    {
    }

    /// <summary>Reads the <c>type</c> union; writes an output back exactly as it was read (<see cref="StepOutput.Raw"/>).</summary>
    public sealed class StepOutputConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => typeof(StepOutput).IsAssignableFrom(objectType);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer) =>
            StepOutput.FromJson(JToken.Load(reader));

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (!(value is StepOutput output) || output.Raw == null) writer.WriteNull();
            else output.Raw.WriteTo(writer);
        }
    }

    /// <summary>One step of a run — one per node, or one per part downstream of Isolate Parts' <c>each</c>.</summary>
    public sealed class PipelineRunStep
    {
        [JsonProperty("id")] public long Id;
        [JsonProperty("nodeId")] public string NodeId;
        /// <summary>"" normally; the part's key (nested as parent/child) downstream of <c>each</c>.</summary>
        [JsonProperty("partKey")] public string PartKey = "";
        [JsonProperty("status")] public string Status;
        [JsonProperty("attempt")] public int Attempt;
        [JsonProperty("credits")] public int Credits;
        [JsonProperty("errorMessage")] public string ErrorMessage;
        [JsonProperty("gen3dPostId")] public long? Gen3dPostId;
        [JsonProperty("output")] public StepOutput Output;
        [JsonProperty("review")] public StepReview Review;
        /// <summary>The 3D job this step made or reads (the plugin's own model), when there is one.</summary>
        [JsonProperty("generation")] public Generation Generation;
        [JsonProperty("createdAt")] public DateTime CreatedAt;
        [JsonProperty("updatedAt")] public DateTime UpdatedAt;

        [JsonIgnore] public StepStatus State => StepStatuses.Parse(Status);
    }

    /// <summary>One run of a pipeline: the graph applied to one seed, on its own copy of the graph.</summary>
    public sealed class PipelineRun
    {
        [JsonProperty("id")] public long Id;
        [JsonProperty("pipelineId")] public long PipelineId;
        /// <summary>auto | manual</summary>
        [JsonProperty("mode")] public string Mode;
        [JsonProperty("status")] public string Status;
        [JsonProperty("paused")] public bool Paused;
        /// <summary>The seed's request plus its type (picture | model).</summary>
        [JsonProperty("seed")] public JObject Seed;
        [JsonProperty("estimatedCredits")] public int EstimatedCredits;
        [JsonProperty("spentCredits")] public int SpentCredits;
        /// <summary>The run's own copy of the graph — per-run settings live here, never on the pipeline.</summary>
        [JsonProperty("graph")] public PipelineGraph Graph = new PipelineGraph();
        [JsonProperty("createdAt")] public DateTime CreatedAt;
        [JsonProperty("updatedAt")] public DateTime UpdatedAt;
        [JsonProperty("finishedAt")] public DateTime? FinishedAt;
        [JsonProperty("steps")] public List<PipelineRunStep> Steps = new List<PipelineRunStep>();

        [JsonIgnore] public RunStatus State => RunStatuses.Parse(Status);
        [JsonIgnore] public bool IsLive => RunStatuses.IsLive(Status);

        public IEnumerable<PipelineRunStep> StepsOf(string nodeId) => Steps.Where(s => s.NodeId == nodeId);

        /// <summary>What a node is doing in this run, from all of its steps (node-visuals.ts nodeRunState).</summary>
        public NodeRunState NodeState(string nodeId) => NodeRunState.Of(StepsOf(nodeId));

        /// <summary>
        /// Whether a node's settings may still be changed for this run (runs-view.tsx): not a seed, the run not
        /// cancelled, and every one of its steps still <c>waiting</c> (or none made yet).
        /// </summary>
        public bool IsNodeEditable(string nodeId)
        {
            var node = Graph?.Node(nodeId);
            if (node == null || NodeCatalog.IsSeed(node.Kind) || Status == RunStatuses.Cancelled) return false;
            return StepsOf(nodeId).All(s => s.Status == StepStatuses.Waiting);
        }

        /// <summary>A run that is over — done, stopped or cancelled — or a draft never started, may be deleted.</summary>
        public bool CanDelete => !IsLive;

        /// <summary>What a step's part key is called: "knight/sword" → the label the isolation gave "sword" (partLabel).</summary>
        public string PartLabel(string partKey)
        {
            if (string.IsNullOrEmpty(partKey)) return "";
            var key = partKey.Substring(partKey.LastIndexOf('/') + 1);
            foreach (var step in Steps)
                if (step.Output is IsolatedStepOutput isolated)
                {
                    var part = isolated.Parts.FirstOrDefault(p => p.Key == key);
                    if (part != null) return part.Label;
                }
            return key;
        }

        /// <summary>
        /// The mesh steps of this run that are finished and importable under an import mode — never a seed, a splat or a
        /// step whose generation has not succeeded. FinalSteps: those no mesh-taking step follows in the run's graph.
        /// </summary>
        public List<PipelineRunStep> ImportableSteps(RunImportMode mode)
        {
            if (mode == RunImportMode.None || Graph == null) return new List<PipelineRunStep>();
            return Steps.Where(step =>
            {
                if (step.Status != StepStatuses.Success || !(step.Output is MeshStepOutput)) return false;
                var node = Graph.Node(step.NodeId);
                if (node == null || NodeCatalog.IsSeed(node.Kind)) return false;
                if (step.Generation == null || !step.Generation.IsSuccess || step.Generation.IsSplat) return false;
                if (mode == RunImportMode.AllMeshSteps) return true;
                return !Graph.Edges.Any(e => e.Source == node.Id &&
                                             NodeCatalog.InputPort(Graph.Node(e.Target)?.Kind, e.TargetHandle)?.AcceptsType(PortTypes.Mesh) == true);
            }).ToList();
        }

        /// <summary>How many steps are waiting for the user's Run (Manual mode).</summary>
        public int WaitingCount => Steps.Count(s => s.Status == StepStatuses.Waiting);
    }

    /// <summary>The server's estimate of a graph (POST /pipelines/estimate).</summary>
    public sealed class ServerEstimate
    {
        [JsonProperty("credits")] public int Credits;
        [JsonProperty("lines")] public List<ServerEstimateLine> Lines = new List<ServerEstimateLine>();
        [JsonProperty("perPart")] public bool PerPart;
        [JsonProperty("problems")] public List<PipelineProblem> Problems = new List<PipelineProblem>();

        public string Label => GraphEstimate.LabelFor(Credits, PerPart);
    }

    public sealed class ServerEstimateLine
    {
        [JsonProperty("nodeId")] public string NodeId;
        [JsonProperty("credits")] public int Credits;
        /// <summary>How many times it runs — Max parts downstream of Isolate Parts' <c>each</c>.</summary>
        [JsonProperty("count")] public int Count;
    }

    // ─── Status vocabularies ──────────────────────────────────────────────────────

    public enum RunStatus
    {
        Unknown,
        /// <summary>Made by New run, not started until its per-run inputs are given and Start is pressed.</summary>
        Draft,
        Running,
        Awaiting,
        Paused,
        Failed,
        Done,
        Cancelled,
    }

    /// <summary>The Runs view's grouping of a run state (node-visuals.ts RunTone).</summary>
    public enum RunTone
    {
        Running,
        Attention,
        Done,
        Failed,
        Idle,
    }

    public static class RunStatuses
    {
        public const string Draft = "draft";
        public const string Running = "running";
        public const string Awaiting = "awaiting";
        public const string Paused = "paused";
        public const string Failed = "failed";
        public const string Done = "done";
        public const string Cancelled = "cancelled";

        public const string ModeAuto = "auto";
        public const string ModeManual = "manual";

        public static RunStatus Parse(string status) => status switch
        {
            Draft => RunStatus.Draft,
            Running => RunStatus.Running,
            Awaiting => RunStatus.Awaiting,
            Paused => RunStatus.Paused,
            Failed => RunStatus.Failed,
            Done => RunStatus.Done,
            Cancelled => RunStatus.Cancelled,
            _ => RunStatus.Unknown,
        };

        /// <summary>Still moving — kept polling while it is (isLive).</summary>
        public static bool IsLive(string status) => status is Running or Paused or Awaiting;

        /// <summary>The Runs view's words (RUN_STATE).</summary>
        public static string Label(string status) => status switch
        {
            Draft => "Not started",
            Running => "Running",
            Awaiting => "Needs you",
            Paused => "Paused",
            Failed => "Stopped",
            Done => "Done",
            Cancelled => "Cancelled",
            _ => status ?? "",
        };

        public static RunTone Tone(string status) => status switch
        {
            Draft or Awaiting => RunTone.Attention,
            Running => RunTone.Running,
            Done => RunTone.Done,
            Failed => RunTone.Failed,
            _ => RunTone.Idle,
        };

        public static string ModeLabel(string mode) => mode == ModeAuto ? "Auto" : "Manual";

        public static string ModeHint(string mode) => mode == ModeAuto
            ? "Every step submits itself, to the end."
            : "Waits for you before each step — you run it from Runs.";
    }

    public enum StepStatus
    {
        Unknown,
        /// <summary>Manual mode: due, waiting for the user's Run.</summary>
        Waiting,
        Ready,
        Running,
        /// <summary>A 3D job is in the queue.</summary>
        Submitted,
        /// <summary>Isolate Parts in Manual mode: parts found, waiting for the user to choose.</summary>
        Review,
        Success,
        Failed,
        Cancelled,
    }

    public static class StepStatuses
    {
        public const string Waiting = "waiting";
        public const string Ready = "ready";
        public const string Running = "running";
        public const string Submitted = "submitted";
        public const string Review = "review";
        public const string Success = "success";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";

        public static StepStatus Parse(string status) => status switch
        {
            Waiting => StepStatus.Waiting,
            Ready => StepStatus.Ready,
            Running => StepStatus.Running,
            Submitted => StepStatus.Submitted,
            Review => StepStatus.Review,
            Success => StepStatus.Success,
            Failed => StepStatus.Failed,
            Cancelled => StepStatus.Cancelled,
            _ => StepStatus.Unknown,
        };
    }

    /// <summary>What one node is doing in a run, from all of its steps (node-visuals.ts nodeRunState).</summary>
    public sealed class NodeRunState
    {
        /// <summary>idle (no steps yet), or one of the step statuses (running covers ready/submitted).</summary>
        public string Status = "idle";
        public int Total;
        public int Done;
        public int Credits;

        public string Label => StatusLabel(Status);

        public static NodeRunState Of(IEnumerable<PipelineRunStep> steps)
        {
            var list = steps?.ToList() ?? new List<PipelineRunStep>();
            if (list.Count == 0) return new NodeRunState();
            bool Has(string status) => list.Any(s => s.Status == status);
            var status =
                Has(StepStatuses.Running) || Has(StepStatuses.Submitted) || Has(StepStatuses.Ready) ? StepStatuses.Running :
                Has(StepStatuses.Review) ? StepStatuses.Review :
                Has(StepStatuses.Waiting) ? StepStatuses.Waiting :
                Has(StepStatuses.Failed) ? StepStatuses.Failed :
                Has(StepStatuses.Cancelled) ? StepStatuses.Cancelled :
                StepStatuses.Success;
            return new NodeRunState
            {
                Status = status,
                Total = list.Count,
                Done = list.Count(s => s.Status == StepStatuses.Success),
                Credits = list.Sum(s => s.Credits),
            };
        }

        /// <summary>STATUS_LABEL.</summary>
        public static string StatusLabel(string status) => status switch
        {
            "idle" => "Not run",
            StepStatuses.Waiting => "Waiting for you",
            StepStatuses.Ready => "Starting",
            StepStatuses.Running => "Running",
            StepStatuses.Submitted => "Running",
            StepStatuses.Review => "Choose parts",
            StepStatuses.Success => "Done",
            StepStatuses.Failed => "Failed",
            StepStatuses.Cancelled => "Cancelled",
            _ => status ?? "",
        };
    }
}
