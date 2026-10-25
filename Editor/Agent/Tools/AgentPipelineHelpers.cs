using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Zetta3D.Editor.Pipelines;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// What the pipeline tools need from the Pipelines window, without a compile dependency on it. The Pipelines UI
    /// assigns these when it loads; the tools call them only when set.
    /// </summary>
    public static class PipelineToolHooks
    {
        /// <summary>Opens Window › Zetta 3D › Pipelines on a pipeline (and, when given, one of its runs). Main thread.</summary>
        public static Action<long, long?> OpenPipeline;

        /// <summary>
        /// The agent saved a pipeline (build_pipeline, edit_pipeline): a window showing it should reload it rather than
        /// keep (and autosave) the copy it has.
        /// </summary>
        public static Action<long> PipelineSaved;
    }

    /// <summary>One seed of run_pipeline: exactly one of <see cref="ImageId"/>, <see cref="GenerationId"/>, <see cref="AssetPath"/>.</summary>
    public sealed class PipelineSeedSpec
    {
        public int Index;
        /// <summary>A Gen2D post id (generate_image's imageId) — a picture seed.</summary>
        public long? ImageId;
        /// <summary>A 3D library row — a model seed.</summary>
        public long? GenerationId;
        /// <summary>A project file: .png / .jpg / .jpeg (picture) or .glb (model).</summary>
        public string AssetPath;

        public bool IsModel => GenerationId != null || AgentPipelineHelpers.IsModelPath(AssetPath);

        /// <summary>picture | model — the seed node it fits.</summary>
        public string Type => IsModel ? "model" : "picture";

        public string Label =>
            ImageId != null ? $"picture #{ImageId}" :
            GenerationId != null ? $"model #{GenerationId}" :
            AssetPath;
    }

    /// <summary>One step's per-run input of run_pipeline (inputs["&lt;nodeId&gt;"]).</summary>
    public sealed class PipelineRunInput
    {
        public string NodeId;
        public string Prompt;
        /// <summary>A Gen2D post id — AI texturing's reference picture.</summary>
        public long? ImageId;
    }

    /// <summary>
    /// The pure part of the pipeline tools (validation, input mapping, the text the model reads) — no Editor or
    /// network calls, so a smoke test can assert it. Every validation failure is a <see cref="ToolException"/> with one
    /// line the model can act on.
    /// </summary>
    public static class AgentPipelineHelpers
    {
        public const int MaxSeeds = 20;
        public const int MaxSteps = 24;
        public const int MaxChanges = 24;
        public const int MaxTitle = 200;
        public const int MaxPrompt = 2000;
        public const int MaxPartList = 16;
        public const int MaxPartLength = 80;
        public const int MaxImportNodes = 50;

        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        public static readonly string[] PictureExtensions = { ".png", ".jpg", ".jpeg" };

        static string Extension(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            var name = path.Substring(path.LastIndexOf('/') + 1);
            var dot = name.LastIndexOf('.');
            return dot < 0 ? "" : name.Substring(dot).ToLowerInvariant();
        }

        public static bool IsModelPath(string path) => Extension(path) == ".glb";

        public static bool IsPicturePath(string path) => PictureExtensions.Contains(Extension(path));

        public static string ContentTypeOf(string path) => Extension(path) == ".png" ? "image/png" : "image/jpeg";

        static long? Long(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            if (t.Type == JTokenType.Integer) return t.Value<long>();
            if (t.Type == JTokenType.Float) return (long)Math.Round(t.Value<double>());
            if (t.Type == JTokenType.String && long.TryParse(t.ToString().Trim().TrimStart('#'), NumberStyles.Integer, C, out var l)) return l;
            return null;
        }

        static bool Has(JObject o, string key) => o != null && o.TryGetValue(key, out var t) && t.Type != JTokenType.Null && t.Type != JTokenType.Undefined;

        static string Str(JToken t) => t == null || t.Type == JTokenType.Null ? null : t.Type == JTokenType.String ? t.ToString() : t.ToString(Formatting.None);

        static string Short(string s, int max)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, Math.Max(0, max - 1)) + "…";
        }

        static string Plural(int n, string one, string many = null) => n == 1 ? $"1 {one}" : $"{n} {many ?? one + "s"}";

        // ─── Inputs ──────────────────────────────────────────────────────────────

        /// <summary>A pipeline/run id from the input ("#12" and "12" accepted).</summary>
        public static long RequireId(JObject input, string key)
        {
            var id = Long(input?[key]);
            if (id == null || id <= 0) throw new ToolException($"'{key}' is required (a positive number{(key == "pipelineId" ? " from list_pipelines" : key == "runId" ? " from run_pipeline or pipeline_run_status" : "")}).");
            return id.Value;
        }

        /// <summary>
        /// run_pipeline's seeds: 1–20, each exactly one of imageId / generationId / assetPath ("Assets/…png|jpg|jpeg|glb"),
        /// every one of the type the pipeline starts from (<paramref name="seedKind"/>: seed_picture | seed_model).
        /// </summary>
        public static List<PipelineSeedSpec> ParseSeeds(JToken seeds, string seedKind)
        {
            if (!(seeds is JArray list) || list.Count == 0) throw new ToolException("'seeds' is required: 1–20 of { imageId } | { generationId } | { assetPath }.");
            if (list.Count > MaxSeeds) throw new ToolException($"At most {MaxSeeds} seeds per call ({list.Count} given) — split them over several run_pipeline calls.");
            var wants = seedKind == NodeKinds.SeedModel ? "model" : seedKind == NodeKinds.SeedPicture ? "picture" : null;
            var result = new List<PipelineSeedSpec>();
            for (var i = 0; i < list.Count; i++)
            {
                if (!(list[i] is JObject o)) throw new ToolException($"seeds[{i}] must be an object: {{ imageId }} | {{ generationId }} | {{ assetPath }}.");
                var given = new[] { "imageId", "generationId", "assetPath" }.Where(k => Has(o, k)).ToList();
                if (given.Count != 1)
                    throw new ToolException($"seeds[{i}] must name exactly one of imageId, generationId, assetPath ({(given.Count == 0 ? "none" : string.Join(" and ", given))} given).");
                var seed = new PipelineSeedSpec { Index = i };
                switch (given[0])
                {
                    case "imageId":
                        seed.ImageId = Long(o["imageId"]);
                        if (seed.ImageId is null or <= 0) throw new ToolException($"seeds[{i}].imageId must be a picture id from generate_image.");
                        break;
                    case "generationId":
                        seed.GenerationId = Long(o["generationId"]);
                        if (seed.GenerationId is null or <= 0) throw new ToolException($"seeds[{i}].generationId must be a model id from list_generations.");
                        break;
                    default:
                        var path = (Str(o["assetPath"]) ?? "").Trim().Trim('"').Replace('\\', '/');
                        while (path.StartsWith("./", StringComparison.Ordinal)) path = path.Substring(2);
                        if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                            throw new ToolException($"seeds[{i}].assetPath must be a project path under Assets/ (\"{path}\" given).");
                        if (!IsPicturePath(path) && !IsModelPath(path))
                            throw new ToolException($"seeds[{i}].assetPath must be a .png, .jpg or .jpeg picture or a .glb model (\"{path}\" given).");
                        seed.AssetPath = path;
                        break;
                }
                if (wants != null && seed.Type != wants)
                    throw new ToolException(wants == "picture"
                        ? $"seeds[{i}] ({seed.Label}) is a model, but this pipeline starts from a picture — give an imageId or a .png/.jpg assetPath."
                        : $"seeds[{i}] ({seed.Label}) is a picture, but this pipeline starts from a model — give a generationId or a .glb assetPath.");
                result.Add(seed);
            }
            return result;
        }

        /// <summary>run_pipeline's inputs: { "&lt;nodeId&gt;": { prompt?: ≤2000, imageId?: int } } (null/absent = none).</summary>
        public static List<PipelineRunInput> ParseInputs(JToken inputs)
        {
            var result = new List<PipelineRunInput>();
            if (inputs == null || inputs.Type == JTokenType.Null) return result;
            if (!(inputs is JObject map)) throw new ToolException("'inputs' must be an object keyed by node id: { \"<nodeId>\": { prompt?, imageId? } }.");
            foreach (var pair in map)
            {
                if (!(pair.Value is JObject o)) throw new ToolException($"inputs[\"{pair.Key}\"] must be {{ prompt?, imageId? }}.");
                var input = new PipelineRunInput { NodeId = pair.Key };
                if (Has(o, "prompt"))
                {
                    input.Prompt = Str(o["prompt"]).Trim();
                    if (input.Prompt.Length > MaxPrompt) throw new ToolException($"inputs[\"{pair.Key}\"].prompt is longer than {MaxPrompt:N0} characters.");
                    if (input.Prompt.Length == 0) input.Prompt = null;
                }
                if (Has(o, "imageId"))
                {
                    input.ImageId = Long(o["imageId"]);
                    if (input.ImageId is null or <= 0) throw new ToolException($"inputs[\"{pair.Key}\"].imageId must be a picture id from generate_image.");
                }
                if (input.Prompt == null && input.ImageId == null) throw new ToolException($"inputs[\"{pair.Key}\"] gives neither a prompt nor an imageId.");
                result.Add(input);
            }
            return result;
        }

        /// <summary>
        /// The settings change a per-run input makes to its step (what <see cref="RequestBuilder.RunNodeChange"/> takes):
        /// AI texturing a description <c>{prompt}</c> or a reference picture <c>{reference:{gen2dPostId}}</c>; AI edit an
        /// instruction <c>{prompt}</c>. Throws for a step that takes no per-run input, or the wrong kind of input.
        /// </summary>
        public static JObject InputPatch(PipelineNode node, PipelineRunInput input)
        {
            if (node == null) throw new ToolException($"inputs[\"{input.NodeId}\"]: the pipeline has no step {input.NodeId} — see get_pipeline for node ids.");
            var name = $"{input.NodeId} ({node.DisplayName})";
            if (!NodeSettings.SetOnEachRun(node))
                throw new ToolException(node.Kind == NodeKinds.AiEdit
                    ? $"inputs[\"{input.NodeId}\"]: {name} is not set on each run — change its prompt with edit_pipeline (or set askAtRun: true)."
                    : $"inputs[\"{input.NodeId}\"]: {name} takes no per-run input (only AI texturing and AI edit with askAtRun do).");
            if (node.Kind == NodeKinds.AiEdit)
            {
                if (input.ImageId != null) throw new ToolException($"inputs[\"{input.NodeId}\"]: {name} takes a prompt (the instruction), not an imageId.");
                return new JObject { ["prompt"] = input.Prompt };
            }
            if (input.ImageId != null && input.Prompt != null)
                throw new ToolException($"inputs[\"{input.NodeId}\"]: give {name} a prompt or an imageId, not both — a reference picture replaces the description.");
            return input.ImageId != null
                ? new JObject { ["prompt"] = "", ["reference"] = new JObject { ["gen2dPostId"] = input.ImageId.Value, ["name"] = $"Picture #{input.ImageId.Value}" } }
                : new JObject { ["prompt"] = input.Prompt, ["reference"] = null };
        }

        /// <summary>
        /// A run's copy of the graph with the per-run inputs applied, the way the editor sends them — for checking what
        /// would still be missing. Throws (via <see cref="InputPatch"/>) for an input that does not fit its step.
        /// </summary>
        public static PipelineGraph WithInputs(PipelineGraph runGraph, IEnumerable<PipelineRunInput> inputs, ThreeDEntitlements entitlements = null)
        {
            var graph = runGraph.Clone();
            foreach (var input in inputs ?? Enumerable.Empty<PipelineRunInput>())
            {
                var patch = InputPatch(graph.Node(input.NodeId), input);
                var (settings, request) = RequestBuilder.RunNodeChange(graph, input.NodeId, patch, entitlements);
                var node = graph.Node(input.NodeId);
                node.Settings = settings;
                node.Request = request;
            }
            return graph;
        }

        /// <summary>
        /// What a run of this recipe would still need after the inputs — one line per step, naming the input key to give
        /// (empty = every step has its input). <paramref name="recipe"/> is the pipeline's (normalised) graph.
        /// </summary>
        public static List<string> MissingInputs(PipelineGraph recipe, IList<PipelineRunInput> inputs, ThreeDEntitlements entitlements = null)
        {
            var runGraph = RequestBuilder.WithRequests(RequestBuilder.ForNewRun(recipe), entitlements);
            var applied = WithInputs(runGraph, inputs, entitlements);
            return GraphRules.RunInputProblems(applied).Select(p =>
            {
                var node = applied.Node(p.NodeId);
                var what = node?.Kind == NodeKinds.Texture
                    ? $"inputs[\"{p.NodeId}\"].prompt (a texture description) or inputs[\"{p.NodeId}\"].imageId (a reference picture)"
                    : $"inputs[\"{p.NodeId}\"].prompt (how to change the picture)";
                return $"{p.NodeId} ({node?.DisplayName ?? "step"}): give {what}";
            }).ToList();
        }

        /// <summary>build_pipeline's steps (1–24): kind, after (an earlier index; absent = the seed), output, settings.</summary>
        public static List<BuildStep> ParseBuildSteps(JToken steps)
        {
            if (!(steps is JArray list) || list.Count == 0) throw new ToolException("'steps' is required: 1–24 of { kind, after?, output?, settings? }.");
            if (list.Count > MaxSteps) throw new ToolException($"A pipeline is built from at most {MaxSteps} steps ({list.Count} given).");
            var result = new List<BuildStep>();
            for (var i = 0; i < list.Count; i++)
            {
                if (!(list[i] is JObject o)) throw new ToolException($"steps[{i}] must be an object {{ kind, after?, output?, settings? }}.");
                var kind = Str(o["kind"])?.Trim();
                if (string.IsNullOrEmpty(kind)) throw new ToolException($"steps[{i}].kind is required.");
                CheckKind(kind, $"steps[{i}].kind");
                int? after = null;
                if (Has(o, "after"))
                {
                    var a = Long(o["after"]);
                    if (a == null || a < 0 || a >= i)
                        throw new ToolException($"steps[{i}].after must be the index of an earlier step ({(i == 0 ? "step 0 has none — omit it to follow the seed" : $"0–{i - 1}")}); omit it to follow the seed.");
                    after = (int)a.Value;
                }
                var output = Has(o, "output") ? Str(o["output"]).Trim() : null;
                if (output != null)
                {
                    if (output != "character" && output != "each" && !output.StartsWith(NodeCatalog.PartHandlePrefix, StringComparison.Ordinal))
                        throw new ToolException($"steps[{i}].output must be character, each or part:<key>.");
                    var parentKind = after == null ? null : Str(list[after.Value]?["kind"]);
                    if (parentKind != NodeKinds.IsolateParts)
                        throw new ToolException($"steps[{i}].output is only for a step after isolate_parts.");
                }
                if (Has(o, "settings") && !(o["settings"] is JObject)) throw new ToolException($"steps[{i}].settings must be an object.");
                result.Add(new BuildStep { Kind = kind, After = after, Output = output, Settings = o["settings"] as JObject });
            }
            return result;
        }

        /// <summary>A step kind the agent may use here, or a one-line refusal.</summary>
        public static void CheckKind(string kind, string where)
        {
            if (NodeCatalog.IsSeed(kind)) throw new ToolException($"{where}: “{kind}” is a seed — build_pipeline adds it from startsFrom.");
            var spec = NodeCatalog.Spec(kind);
            if (spec == null)
                throw new ToolException($"{where}: “{kind}” is not a step kind. Use one of: {string.Join(", ", NodeCatalog.OfferedInUnity.Where(s => !NodeCatalog.IsSeed(s.Kind)).Select(s => s.Kind))}.");
            if (!spec.OfferedInUnity)
                throw new ToolException($"{where}: {spec.Label} ({kind}) is not available in Unity — it can be added on zetta3d.ai only.");
        }

        /// <summary>edit_pipeline's changes (1–24). Kinds not offered in Unity, and set on such a step, are refused.</summary>
        public static List<PipelineChange> ParseChanges(JToken changes, PipelineGraph graph = null)
        {
            if (!(changes is JArray list) || list.Count == 0)
                throw new ToolException("'changes' is required: 1–24 of { op: \"add\", kind, after, output?, settings? } | { op: \"set\", nodeId, settings } | { op: \"remove\", nodeId }.");
            if (list.Count > MaxChanges) throw new ToolException($"At most {MaxChanges} changes per call ({list.Count} given).");
            var result = new List<PipelineChange>();
            for (var i = 0; i < list.Count; i++)
            {
                if (!(list[i] is JObject o)) throw new ToolException($"changes[{i}] must be an object.");
                var change = PipelineChange.FromJson(o);
                switch (change.Op)
                {
                    case "add":
                        if (string.IsNullOrWhiteSpace(change.Kind)) throw new ToolException($"changes[{i}] (add) needs a kind.");
                        CheckKind(change.Kind, $"changes[{i}]");
                        if (Has(o, "after") && o["after"].Type != JTokenType.String && o["after"].Type != JTokenType.Integer)
                            throw new ToolException($"changes[{i}].after must be a node id (see get_pipeline).");
                        if (change.Output != null && change.Output != "character" && change.Output != "each" &&
                            !change.Output.StartsWith(NodeCatalog.PartHandlePrefix, StringComparison.Ordinal))
                            throw new ToolException($"changes[{i}].output must be character, each or part:<key>.");
                        break;
                    case "set":
                        if (string.IsNullOrWhiteSpace(change.NodeId)) throw new ToolException($"changes[{i}] (set) needs a nodeId.");
                        if (change.Settings == null) throw new ToolException($"changes[{i}] (set) needs settings (an object of what to change).");
                        var node = graph?.Node(change.NodeId);
                        if (node != null && NodeCatalog.NotAvailableInUnity(node.Kind))
                            throw new ToolException($"changes[{i}]: {change.NodeId} ({node.DisplayName}) is not available in Unity — change it on zetta3d.ai.");
                        break;
                    case "remove":
                        if (string.IsNullOrWhiteSpace(change.NodeId)) throw new ToolException($"changes[{i}] (remove) needs a nodeId.");
                        break;
                    default:
                        throw new ToolException($"changes[{i}].op must be add, set or remove (“{change.Op}” given).");
                }
                if (Has(o, "settings") && !(o["settings"] is JObject)) throw new ToolException($"changes[{i}].settings must be an object.");
                result.Add(change);
            }
            return result;
        }

        /// <summary>choose_parts' keys / extraParts: ≤16 strings of ≤80 characters (absent = empty).</summary>
        public static List<string> ParsePartList(JToken token, string name)
        {
            if (token == null || token.Type == JTokenType.Null) return new List<string>();
            if (!(token is JArray list)) throw new ToolException($"'{name}' must be an array of strings.");
            if (list.Count > MaxPartList) throw new ToolException($"'{name}' holds at most {MaxPartList} entries ({list.Count} given).");
            var result = new List<string>();
            foreach (var item in list)
            {
                var s = (Str(item) ?? "").Trim();
                if (s.Length == 0) continue;
                if (s.Length > MaxPartLength) throw new ToolException($"'{name}' entries are at most {MaxPartLength} characters (“{Short(s, 30)}” is {s.Length}).");
                result.Add(s);
            }
            return result;
        }

        // ─── Text for the model ──────────────────────────────────────────────────

        /// <summary>
        /// A step's settings in a few words, agent-shaped (pbr / faceCount for a generation, spec standard for rigging),
        /// as the model would write them back in a "set". Empty when there is nothing to say.
        /// </summary>
        public static string SettingsBrief(PipelineNode node, int max = 220)
        {
            var s = node?.Settings;
            if (s == null || NodeCatalog.IsSeed(node.Kind)) return "";
            var parts = new List<string>();
            void Add(string key, JToken value)
            {
                if (value == null || value.Type == JTokenType.Null) return;
                if (value.Type == JTokenType.String && value.ToString().Length == 0) return;
                string text = value.Type switch
                {
                    JTokenType.String => value.ToString(),
                    JTokenType.Boolean => value.Value<bool>() ? "true" : "false",
                    JTokenType.Array => string.Join("/", value.Select(v => v.Type == JTokenType.Object ? (Str(v["key"]) ?? v.ToString(Formatting.None)) : Str(v))),
                    _ => value.ToString(Formatting.None),
                };
                if (value.Type == JTokenType.Array && text.Length == 0) return;
                parts.Add($"{key}={(key == "prompt" || key == "hint" ? "“" + Short(text, 60) + "”" : text)}");
            }
            switch (node.Kind)
            {
                case NodeKinds.Generate3D:
                case NodeKinds.GenerateSmart3D:
                    Add("model", s["model"]);
                    Add("quality", s["quality"]);
                    Add("output", s["output"]);
                    Add("pbr", s["pbrMaterials"]);
                    if (long.TryParse(Str(s["polygonCount"]) ?? "", NumberStyles.Integer, C, out var faces)) parts.Add($"faceCount={faces}");
                    Add("polygonType", s["polygonType"]);
                    Add("textureQuality", s["textureQuality"]);
                    if (s["segmentParts"]?.Type == JTokenType.Boolean && s["segmentParts"].Value<bool>()) parts.Add("segmentParts=true");
                    break;
                case NodeKinds.Rigging:
                    foreach (var pair in s)
                        Add(pair.Key, pair.Value);
                    break;
                case NodeKinds.Texture:
                    foreach (var pair in s)
                    {
                        if (pair.Key == "prompt" || pair.Key == "reference") continue; // given per run
                        Add(pair.Key, pair.Value);
                    }
                    break;
                default:
                    foreach (var pair in s)
                    {
                        if (pair.Key == "model" && (pair.Value == null || pair.Value.Type == JTokenType.Null)) continue;
                        Add(pair.Key == "knownParts" ? "parts" : pair.Key, pair.Value);
                    }
                    break;
            }
            var joined = string.Join(", ", parts);
            return joined.Length <= max ? joined : joined.Substring(0, max - 1) + "…";
        }

        /// <summary>
        /// The recipe as an indented tree from the seed (then any steps not connected), one line per step: id, kind,
        /// name, where its input comes from (parent.port), the Studio summary, its settings, price, and whether it runs per
        /// part or takes a per-run input.
        /// </summary>
        public static string StepTree(PipelineGraph graph, PriceList prices = null, ThreeDEntitlements entitlements = null)
        {
            var sb = new StringBuilder();
            if (graph == null || graph.Nodes.Count == 0) return "(no steps)";
            var perPart = GraphRules.FanOutNodes(graph);
            var seen = new HashSet<string>();

            void Line(PipelineNode node, int depth, PipelineEdge from)
            {
                if (!seen.Add(node.Id)) return;
                sb.Append(new string(' ', depth * 2)).Append("- ").Append(node.Id).Append(' ').Append(node.Kind);
                if (!string.IsNullOrEmpty(node.Label)) sb.Append($" “{node.Label}”");
                else if (NodeCatalog.Spec(node.Kind) != null) sb.Append($" ({NodeCatalog.Label(node.Kind)})");
                if (from != null) sb.Append($" ← {from.Source}.{from.SourceHandle}");
                else if (!NodeCatalog.IsSeed(node.Kind)) sb.Append(" ← NOT CONNECTED");
                var summary = NodeSettings.Summary(node);
                if (!string.IsNullOrEmpty(summary)) sb.Append(" · ").Append(summary);
                var brief = SettingsBrief(node);
                if (brief.Length > 0) sb.Append(" · {").Append(brief).Append('}');
                if (!NodeCatalog.IsSeed(node.Kind) && NodeCatalog.IsKnown(node.Kind))
                {
                    var price = RequestBuilder.PriceFor(node, prices, new NodeContext(GraphRules.IncomingType(graph, node.Id), entitlements));
                    sb.Append(" · ").Append(price == null ? "price unknown" : RequestBuilder.PriceLabel(price, perPart.Contains(node.Id)));
                }
                else if (!NodeCatalog.IsKnown(node.Kind)) sb.Append(" · unknown step (kept as it is)");
                if (perPart.Contains(node.Id)) sb.Append(" · runs once per part");
                if (NodeSettings.SetOnEachRun(node)) sb.Append(" · input given on each run");
                if (NodeCatalog.IsKnown(node.Kind) && NodeCatalog.NotAvailableInUnity(node.Kind)) sb.Append(" · web-only (cannot be changed in Unity)");
                sb.Append('\n');
                foreach (var edge in graph.Edges.Where(e => e.Source == node.Id))
                {
                    var child = graph.Node(edge.Target);
                    if (child != null) Line(child, depth + 1, edge);
                }
            }

            var roots = graph.Nodes.Where(n => !graph.Edges.Any(e => e.Target == n.Id && graph.Node(e.Source) != null))
                .OrderBy(n => NodeCatalog.IsSeed(n.Kind) ? 0 : 1).ToList();
            foreach (var root in roots) Line(root, 0, null);
            foreach (var node in graph.Nodes) Line(node, 0, GraphRules.InputEdge(graph, node.Id)); // anything in a loop
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>"Up to 132 credits a run (steps after Isolate parts › each run once per part — counted for up to 6 parts)".</summary>
        public static string EstimateText(PipelineGraph graph, GraphEstimate estimate)
        {
            if (estimate == null || estimate.Credits == null) return "Estimate unknown (prices not loaded).";
            var text = estimate.Credits == 0 ? "Free to run (no paid steps)" : (estimate.PerPart ? "Up to " : "") + $"{estimate.Credits} credits a run";
            if (estimate.PerPart)
            {
                var count = estimate.Lines.Where(l => l.PerPart).Select(l => l.Count).DefaultIfEmpty(1).Max();
                text += $" (steps after Isolate parts › each run once per part — counted for up to {count} parts)";
            }
            return text + ".";
        }

        /// <summary>Problems as lines "[s2 Retopology] …" (empty string when none).</summary>
        public static string ProblemsText(IEnumerable<PipelineProblem> problems, PipelineGraph graph)
        {
            var list = (problems ?? Enumerable.Empty<PipelineProblem>()).ToList();
            if (list.Count == 0) return "";
            return string.Join("\n", list.Select(p =>
            {
                var node = p.NodeId == null ? null : graph?.Node(p.NodeId);
                return p.NodeId == null ? $"- {p.Message}" : $"- [{p.NodeId}{(node != null ? " " + node.DisplayName : "")}] {p.Message}";
            }));
        }

        /// <summary>Warnings (Smart UV over budget, web-only steps) as lines, or "".</summary>
        public static string WarningsText(PipelineGraph graph)
        {
            var all = GraphRules.AllWarnings(graph);
            return string.Join("\n", all.SelectMany(pair => pair.Value.Select(w => $"- [{pair.Key}] {w}")));
        }

        /// <summary>
        /// The run_pipeline approval card's detail: the pipeline, its steps with prices, the seeds, the per-run inputs,
        /// the mode, and the total.
        /// </summary>
        public static string RunApprovalDetail(long pipelineId, string title, PipelineGraph recipe, IList<PipelineSeedSpec> seeds, string mode,
            IList<PipelineRunInput> inputs, GraphEstimate estimate, PriceList prices = null, ThreeDEntitlements entitlements = null)
        {
            var sb = new StringBuilder();
            var seedNode = recipe.Seed;
            sb.AppendLine($"Pipeline #{pipelineId} “{title}” — starts from a {(seedNode?.Kind == NodeKinds.SeedModel ? "model" : "picture")}");
            var order = GraphRules.TopologicalOrder(recipe) ?? recipe.Nodes.Select(n => n.Id).ToList();
            var perPart = GraphRules.FanOutNodes(recipe);
            sb.AppendLine("Steps:");
            foreach (var id in order)
            {
                var node = recipe.Node(id);
                if (node == null || NodeCatalog.IsSeed(node.Kind)) continue;
                var price = RequestBuilder.PriceFor(node, prices, new NodeContext(GraphRules.IncomingType(recipe, id), entitlements));
                var summary = NodeSettings.Summary(node);
                sb.AppendLine($"• {node.DisplayName}{(string.IsNullOrEmpty(summary) ? "" : " — " + summary)} ({RequestBuilder.PriceLabel(price, perPart.Contains(id))})");
            }
            var shown = seeds.Take(6).Select(s => s.Label).ToList();
            sb.AppendLine($"Seeds ({seeds.Count}): {string.Join(", ", shown)}{(seeds.Count > shown.Count ? $", +{seeds.Count - shown.Count} more" : "")}");
            if (inputs is { Count: > 0 })
                foreach (var input in inputs)
                {
                    var node = recipe.Node(input.NodeId);
                    var given = input.ImageId != null ? $"reference picture #{input.ImageId}" : $"“{Short(input.Prompt, 120)}”";
                    sb.AppendLine($"Per-run input — {node?.DisplayName ?? input.NodeId}: {given}");
                }
            sb.AppendLine(mode == RunStatuses.ModeAuto
                ? "Mode: Auto — every step submits itself, to the end."
                : "Mode: Manual — waits for you before each step (credits are spent only when you run a step).");
            var each = estimate?.Credits;
            if (each == null) sb.Append("Cost: unknown (prices not loaded).");
            else
            {
                var up = estimate.PerPart || mode != RunStatuses.ModeAuto ? "up to " : "";
                sb.Append(seeds.Count == 1
                    ? $"Cost: {up}{each} credits."
                    : $"Cost: {up}{each} credits a run × {seeds.Count} = {up}{each.Value * seeds.Count} credits.");
            }
            sb.Append($"\nResults import to the models folder when each run finishes.");
            return sb.ToString();
        }

        /// <summary>The total the card quotes: the estimate per run × the seeds; null while unknown.</summary>
        public static int? RunTotal(GraphEstimate estimate, int seeds) => estimate?.Credits == null ? (int?)null : estimate.Credits.Value * Math.Max(1, seeds);

        /// <summary>"5 min ago", "3 h ago", "2 d ago" — or the date past a week.</summary>
        public static string Ago(DateTime when, DateTime nowUtc)
        {
            if (when == default) return "—";
            var utc = when.Kind == DateTimeKind.Local ? when.ToUniversalTime() : when;
            var span = nowUtc - utc;
            if (span.TotalSeconds < 60) return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
            if (span.TotalDays < 7) return $"{(int)span.TotalDays} d ago";
            return utc.ToString("yyyy-MM-dd", C);
        }

        /// <summary>A run's seed in a few words: "picture “knight.png”", "model #501".</summary>
        public static string SeedText(JObject seed)
        {
            if (seed == null) return "—";
            var type = Str(seed["type"]) ?? (seed["gen3dPostId"] != null ? "model" : "picture");
            var name = Str(seed["name"]);
            var id = Str(seed["gen3dPostId"]) ?? Str(seed["gen2dPostId"]);
            if (!string.IsNullOrEmpty(name)) return $"{type} “{Short(name, 60)}”{(id != null ? $" (#{id})" : "")}";
            return id != null ? $"{type} #{id}" : type;
        }

        /// <summary>One run step's price (the node in the run's own graph), or null while unknown.</summary>
        public static int? StepPrice(PipelineRun run, PipelineRunStep step, PriceList prices, ThreeDEntitlements entitlements = null)
        {
            var node = run?.Graph?.Node(step?.NodeId);
            if (node == null || prices == null) return null;
            return RequestBuilder.PriceFor(node, prices, new NodeContext(GraphRules.IncomingType(run.Graph, node.Id), entitlements));
        }

        /// <summary>The sum of the steps' prices; null when any is unknown.</summary>
        public static int? StepsPrice(PipelineRun run, IEnumerable<PipelineRunStep> steps, PriceList prices, ThreeDEntitlements entitlements = null)
        {
            var total = 0;
            foreach (var step in steps)
            {
                var price = StepPrice(run, step, prices, entitlements);
                if (price == null) return null;
                total += price.Value;
            }
            return total;
        }

        /// <summary>"step 302 · s1 Generate 3D [part sword]".</summary>
        public static string StepName(PipelineRun run, PipelineRunStep step)
        {
            var node = run.Graph?.Node(step.NodeId);
            var part = string.IsNullOrEmpty(step.PartKey) ? "" : $" [part {run.PartLabel(step.PartKey)}]";
            return $"step {step.Id} · {step.NodeId} {node?.DisplayName ?? NodeCatalog.Label(step.NodeId)}{part}";
        }

        /// <summary>
        /// pipeline_run_status: the run's state, mode, spend against its estimate, every step (status, credits, error,
        /// model made, picture outputs), the Isolate Parts reviews waiting for a choice, and the steps waiting for a Run.
        /// </summary>
        public static string RunStatusText(PipelineRun run, PriceList prices, ThreeDEntitlements entitlements, DateTime nowUtc)
        {
            var sb = new StringBuilder();
            sb.Append($"Run #{run.Id} of pipeline #{run.PipelineId} — {RunStatuses.Label(run.Status)} ({run.Status}{(run.Paused && run.Status != RunStatuses.Paused ? ", paused" : "")})");
            sb.Append($" · {RunStatuses.ModeLabel(run.Mode)} mode · spent {run.SpentCredits} of an estimated {run.EstimatedCredits} credits");
            sb.Append($" · seed: {SeedText(run.Seed)} · created {Ago(run.CreatedAt, nowUtc)}");
            if (run.FinishedAt.HasValue) sb.Append($" · finished {Ago(run.FinishedAt.Value, nowUtc)}");
            sb.AppendLine();

            var order = GraphRules.TopologicalOrder(run.Graph) ?? run.Graph.Nodes.Select(n => n.Id).ToList();
            int Rank(string nodeId) => order.IndexOf(nodeId) is var i && i >= 0 ? i : int.MaxValue;
            var steps = run.Steps.OrderBy(s => Rank(s.NodeId)).ThenBy(s => s.PartKey).ThenBy(s => s.Id).ToList();

            if (steps.Count == 0) sb.AppendLine(run.Status == RunStatuses.Draft ? "No steps yet — the run has not started." : "No steps yet.");
            else sb.AppendLine("Steps:");
            var pictures = new List<string>();
            foreach (var step in steps)
            {
                sb.Append("- ").Append(StepName(run, step)).Append($" · {step.Status}");
                if (step.Attempt > 1) sb.Append($" (attempt {step.Attempt})");
                if (step.Credits > 0) sb.Append($" · {step.Credits} cr");
                else if (step.Status == StepStatuses.Waiting)
                {
                    var price = StepPrice(run, step, prices, entitlements);
                    sb.Append(price == null ? " · price unknown" : price == 0 ? " · free" : $" · will cost {price} cr");
                }
                var genId = step.Gen3dPostId ?? step.Output?.Gen3dPost;
                if (genId != null)
                {
                    sb.Append($" · model #{genId}");
                    if (step.Generation != null) sb.Append($" “{step.Generation.DisplayTitle}”{(step.Generation.IsSuccess ? "" : $" ({step.Generation.Status})")}");
                }
                if (step.Output is SplatStepOutput) sb.Append(" · Gaussian splat (not usable in Unity)");
                if (step.Output is FileStepOutput file) sb.Append($" · export file {file.Format} (web only)");
                if (!string.IsNullOrEmpty(step.ErrorMessage)) sb.Append($" · error: {Short(step.ErrorMessage, 300)}");
                sb.Append('\n');

                switch (step.Output)
                {
                    case PictureStepOutput picture when !string.IsNullOrEmpty(picture.Picture?.Url):
                        pictures.Add($"- step {step.Id} ({step.NodeId}): {picture.Picture.Url}");
                        break;
                    case ViewsStepOutput views:
                        foreach (var view in views.Views.Where(v => !string.IsNullOrEmpty(v.Picture?.Url)))
                            pictures.Add($"- step {step.Id} ({step.NodeId}) {view.ViewType}: {view.Picture.Url}");
                        break;
                    case IsolatedStepOutput isolated:
                        if (!string.IsNullOrEmpty(isolated.Character?.Url)) pictures.Add($"- step {step.Id} ({step.NodeId}) character: {isolated.Character.Url}");
                        foreach (var part in isolated.Parts.Where(p => !string.IsNullOrEmpty(p.Picture?.Url)))
                            pictures.Add($"- step {step.Id} ({step.NodeId}) part {part.Key} “{part.Label}”: {part.Picture.Url}");
                        break;
                }
            }

            var reviews = steps.Where(s => s.Status == StepStatuses.Review).ToList();
            foreach (var step in reviews)
            {
                sb.AppendLine($"Isolate parts review — {StepName(run, step)} waits for parts to be chosen:");
                var parts = step.Review?.Identification?.Parts ?? new List<IdentifiedPart>();
                if (!string.IsNullOrEmpty(step.Review?.Identification?.Subject)) sb.AppendLine($"  subject: {step.Review.Identification.Subject}{(step.Review.Identification.Humanoid ? " (humanoid)" : "")}");
                if (parts.Count > 0)
                    sb.AppendLine("  parts found: " + string.Join(", ", parts.Select(p => $"{p.Key} “{p.Label}”{(string.IsNullOrEmpty(p.Kind) ? "" : $" ({p.Kind})")}")));
                sb.AppendLine($"  proposedKeys: [{string.Join(", ", step.Review?.ProposedKeys ?? new List<string>())}]");
                sb.AppendLine($"  → manage_pipeline_run {{ runId: {run.Id}, action: \"choose_parts\", stepId: {step.Id}, keys: [...], extraParts?: [...] }}");
            }

            var waiting = steps.Where(s => s.Status == StepStatuses.Waiting).ToList();
            if (waiting.Count > 0 && run.Status != RunStatuses.Draft)
            {
                var total = StepsPrice(run, waiting, prices, entitlements);
                sb.AppendLine($"Waiting for a Run: {Plural(waiting.Count, "step")} ({(total == null ? "price unknown" : $"{total} credits")}) — " +
                              "manage_pipeline_run run_step (stepId) or run_waiting (all); the user approves the credits.");
            }
            var failed = steps.Where(s => s.Status == StepStatuses.Failed).ToList();
            if (failed.Count > 0) sb.AppendLine($"Failed: {string.Join(", ", failed.Select(s => $"step {s.Id}"))} — retry_step (stepId) tries one again.");
            if (pictures.Count > 0)
            {
                sb.AppendLine("Picture outputs (signed URLs, they expire):");
                foreach (var line in pictures.Take(40)) sb.AppendLine(line);
                if (pictures.Count > 40) sb.AppendLine($"… +{pictures.Count - 40} more");
            }
            var importable = run.ImportableSteps(RunImportMode.FinalSteps);
            if (importable.Count > 0)
                sb.AppendLine($"Importable results: {string.Join(", ", importable.Select(s => $"{s.NodeId} (model #{s.Generation?.Id})").Distinct())} — import_pipeline_results.");
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>One line per pipeline for list_pipelines.</summary>
        public static string PipelineLine(PipelineSummary p, DateTime nowUtc)
        {
            var sb = new StringBuilder($"#{p.Id} “{p.Title}” — {Plural(p.Steps, "step")}");
            if (p.LastRun != null) sb.Append($" · last run #{p.LastRun.Id} {RunStatuses.Label(p.LastRun.Status).ToLowerInvariant()} ({p.LastRun.Status}) {Ago(p.LastRun.CreatedAt, nowUtc)}");
            else sb.Append(" · never run");
            var counts = (p.RunCounts ?? new Dictionary<string, int>()).Where(c => c.Value > 0).Select(c => $"{c.Value} {c.Key}").ToList();
            if (counts.Count > 0) sb.Append($" · runs: {string.Join(", ", counts)}");
            sb.Append($" · edited {Ago(p.UpdatedAt, nowUtc)}");
            return sb.ToString();
        }

        /// <summary>One line per run for get_pipeline's recent runs.</summary>
        public static string RunLine(RunSummary r, DateTime nowUtc)
        {
            var sb = new StringBuilder($"#{r.Id} {r.Status}{(r.Paused && r.Status != RunStatuses.Paused ? " (paused)" : "")} · {r.Mode}");
            if (r.Progress != null && r.Progress.Total > 0)
            {
                sb.Append($" · {r.Progress.Done}/{r.Progress.Total} steps done");
                if (r.Progress.Waiting > 0) sb.Append($", {r.Progress.Waiting} waiting");
                if (r.Progress.Failed > 0) sb.Append($", {r.Progress.Failed} failed");
            }
            if (r.InputsNeeded > 0) sb.Append($" · {Plural(r.InputsNeeded, "input")} needed");
            sb.Append($" · {r.SpentCredits}/{r.EstimatedCredits} cr");
            if (r.Seed != null) sb.Append($" · seed: {r.Seed.Type}{(string.IsNullOrEmpty(r.Seed.Name) ? "" : $" “{Short(r.Seed.Name, 40)}”")}");
            sb.Append($" · {Ago(r.CreatedAt, nowUtc)}");
            return sb.ToString();
        }
    }
}
