using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Zetta3D.Editor.Pipelines
{
    /// <summary>What a node's request depends on beyond its settings (node-config.ts NodeContext).</summary>
    public sealed class NodeContext
    {
        /// <summary>What arrives on the node's main input (a <see cref="PortTypes"/> value), when it is connected.</summary>
        public string Incoming;
        /// <summary>The account's 3D gates — a plan that may not choose a face count sends none. Null = unknown (allowed).</summary>
        public ThreeDEntitlements Entitlements;

        public NodeContext()
        {
        }

        public NodeContext(string incoming, ThreeDEntitlements entitlements)
        {
            Incoming = incoming;
            Entitlements = entitlements;
        }

        public bool MayChooseFaceCount => Entitlements?.AllowsCustomFaceCount != false;
    }

    /// <summary>The editor's load normalisation, and whether the graph should be saved straight back.</summary>
    public sealed class NormalizeResult
    {
        public PipelineGraph Graph;
        /// <summary>A 3D step had no request.spec (an agent-drafted, settings-only graph): save it with <see cref="RequestBuilder.WithRequests"/>.</summary>
        public bool NeedsRequestRebuild;
    }

    /// <summary>
    /// WHAT EACH NODE SENDS (node-config.ts requestFor / withRequests / nodeProblems / priceFor, and the editor's load
    /// normalisation). A node keeps the Studio form's value in <c>settings</c> and the request that form builds in
    /// <c>request</c> — built here with the Studio's own builders (<see cref="ZettaRequests.Build"/> for a generation,
    /// <see cref="ToolSettings"/> for a post-process), so a node sends exactly what the Studio would. The server reads
    /// only <c>request</c>, adding the inputs that arrive along the connections.
    /// </summary>
    public static class RequestBuilder
    {
        /// <summary>A picture address the builder accepts; the server puts the real picture in its place.</summary>
        public const string InputPlaceholder = "https://pipeline.input/picture.png";

        /// <summary>The node's request for its own settings and what arrives on its input. Never throws.</summary>
        public static JObject RequestFor(PipelineNode node, PipelineGraph graph, ThreeDEntitlements entitlements = null) =>
            RequestFor(node.Kind, NodeSettings.Of(node), new NodeContext(GraphRules.IncomingType(graph, node.Id), entitlements));

        /// <summary>
        /// The request a node's settings build (requestFor). Never throws: a form the Studio would refuse yields the
        /// engine alone, and <see cref="NodeProblems"/> says why. Settings missing a field read the Studio's default for it.
        /// </summary>
        public static JObject RequestFor(string kind, JObject settings, NodeContext context)
        {
            context ??= new NodeContext();
            settings ??= NodeSettings.Defaults(kind);
            switch (kind)
            {
                case NodeKinds.SeedPicture:
                case NodeKinds.SeedModel:
                case NodeKinds.SeedViews:
                case NodeKinds.RemoveBackground:
                    // A seed is chosen for each run, never kept in the pipeline.
                    return new JObject();
                case NodeKinds.AiEdit:
                {
                    var request = new JObject
                    {
                        ["prompt"] = settings["prompt"]?.Type == JTokenType.String ? settings["prompt"].ToString() : "",
                        ["for3D"] = !(settings["for3D"]?.Type == JTokenType.Boolean && !settings["for3D"].Value<bool>()),
                    };
                    if (ToolSettings.Truthy(settings["model"])) request["model"] = settings["model"].DeepClone();
                    return request;
                }
                case NodeKinds.DrawViews:
                {
                    var views = settings["views"];
                    return new JObject { ["views"] = views != null && views.Type != JTokenType.Null ? views.DeepClone() : new JArray("back", "left", "right") };
                }
                case NodeKinds.IsolateParts:
                {
                    var v = ToolSettings.IsolateDefaults();
                    foreach (var pair in settings) v[pair.Key] = pair.Value?.DeepClone();
                    var request = new JObject
                    {
                        ["identify"] = v["identify"]?.DeepClone(),
                        ["sensitivity"] = v["sensitivity"]?.DeepClone(),
                        ["maxParts"] = v["maxParts"]?.DeepClone(),
                        ["pose"] = v["pose"]?.DeepClone(),
                        ["extraParts"] = v["extraParts"]?.DeepClone(),
                    };
                    if (ToolSettings.Truthy(v["model"])) request["model"] = v["model"].DeepClone();
                    return request;
                }
                case NodeKinds.Generate3D:
                case NodeKinds.GenerateSmart3D:
                    return GenerateRequest(settings, context).request;
                // Pictures of each side, always — built as the Studio's Multi-View to 3D builds them.
                case NodeKinds.Generate3DMultiview:
                case NodeKinds.GenerateSmart3DMultiview:
                    return GenerateRequest(settings, AsViews(context)).request;
                case NodeKinds.GenerateSplat:
                    return ToolSettings.SplatRequest(settings);
                case NodeKinds.Retopology:
                    return ToolSettings.RetopologyRequest(settings);
                case NodeKinds.Segmentation:
                case NodeKinds.SmartSegmentation:
                    return ToolSettings.SegmentRequest(settings);
                // Pictures of each side in place of a description or one picture; views wired in win.
                case NodeKinds.TextureMultiview:
                {
                    var multi = ToolSettings.TextureRequest(settings, null);
                    multi["referenceViews"] = NodeSettings.ViewsRequest(settings["referenceViews"] as JObject);
                    multi["removeReferenceBackground"] = !(settings["removeBg"]?.Type == JTokenType.Boolean && !settings["removeBg"].Value<bool>());
                    return multi;
                }
                case NodeKinds.Texture:
                {
                    var reference = settings["reference"] as JObject;
                    // A picture replaces the description, as in the Studio — sending both is refused.
                    var prompt = reference == null ? ToolSettings.Text(settings, "prompt") : null;
                    var request = ToolSettings.TextureRequest(settings, prompt);
                    // Read by the pipeline, never sent to the 3D API: the node's own reference, and its cut-out.
                    if (reference != null && ToolSettings.Truthy(reference["uploadKey"])) request["referenceUploadKey"] = reference["uploadKey"].DeepClone();
                    if (reference != null && ToolSettings.IsNumber(reference["gen2dPostId"])) request["referenceImageId"] = reference["gen2dPostId"].DeepClone();
                    request["removeReferenceBackground"] = !(settings["removeBg"]?.Type == JTokenType.Boolean && !settings["removeBg"].Value<bool>());
                    return request;
                }
                case NodeKinds.SmartUv:
                    return ToolSettings.SmartUvRequest(settings);
                case NodeKinds.Rigging:
                    return ToolSettings.AutoRigRequest(settings);
                case NodeKinds.Export:
                    return new JObject { ["format"] = settings["format"] != null && settings["format"].Type != JTokenType.Null ? settings["format"].DeepClone() : "glb" };
                default:
                    return new JObject();
            }
        }

        /// <summary>
        /// A generation's request: the plugin's <see cref="ZettaRequests.Build"/> over the form with a placeholder
        /// picture, then the inputs and the origin taken out (the server adds them); views coming in make it a
        /// multi-view job, priced as one; plus removeBackground. Problems are the web builder's.
        /// </summary>
        static (JObject request, List<string> problems) GenerateRequest(JObject form, NodeContext context)
        {
            var mayChoose = context.MayChooseFaceCount;
            var problems = GenerateForm.ParameterProblems(form, mayChoose);
            var options = GenerateForm.ToOptions(form, mayChoose);
            options.ImageUrl = InputPlaceholder;
            options.Prompt = "";
            options.ImageFromPrompt = false;
            options.Views.Clear();
            var built = ZettaRequests.Build(options, context.Entitlements);
            // The plugin builder bounds a count by its own table; the web's (stricter on N1) is the one ported above.
            problems.AddRange(built.Problems.Where(p => !p.StartsWith("The polygon count") && !problems.Contains(p)));
            if (problems.Count > 0 || built.Body == null)
                return (new JObject { ["model"] = form["model"]?.DeepClone() ?? GenerateForm.ModelOf(form) }, problems);

            var body = (JObject)built.Body.DeepClone();
            body.Remove("imageUrl");
            body.Remove("prompt");
            body.Remove("imageFromPrompt");
            body.Remove("origin");
            // engineFields: the web sends the form's polygonType wherever the engine reads one (triangles beside parts too).
            var polygonType = GenerateForm.SentPolygonType(form, built.Base, built.Modifiers);
            if (polygonType != null) body["polygonType"] = polygonType;
            else body.Remove("polygonType");
            if (context.Incoming == PortTypes.Views)
            {
                options.Views.Add(("front", InputPlaceholder));
                var (baseCode, modifiers) = ZettaRequests.Spec(options, context.Entitlements);
                body["spec"] = new JObject { ["generation"] = new JObject { ["base"] = baseCode, ["modifiers"] = new JArray(modifiers) } };
            }
            body["removeBackground"] = form["removeBackground"]?.Type == JTokenType.Boolean && form["removeBackground"].Value<bool>();
            return (body, problems);
        }

        /// <summary>A multi-view step's context: views always arrive, whatever is wired in yet.</summary>
        static NodeContext AsViews(NodeContext context) => new NodeContext(PortTypes.Views, context.Entitlements);

        /// <summary>What the Studio's form would refuse — shown on the node, and no run starts on it (nodeProblems).</summary>
        public static List<string> NodeProblems(string kind, JObject settings, NodeContext context)
        {
            context ??= new NodeContext();
            settings ??= NodeSettings.Defaults(kind);
            if (kind == NodeKinds.Retopology)
            {
                var problem = ToolSettings.RetopologyRangeProblem(settings);
                return problem == null ? new List<string>() : new List<string> { problem };
            }
            if (!NodeCatalog.IsGenerate(kind)) return new List<string>();
            return GenerateRequest(settings, NodeCatalog.IsMultiViewGenerate(kind) ? AsViews(context) : context).problems;
        }

        /// <summary>One run of this node, in credits — the price the Studio panel quotes. Null while prices are unknown (priceFor).</summary>
        public static int? PriceFor(PipelineNode node, PriceList prices, NodeContext context)
        {
            var settings = NodeSettings.Of(node);
            switch (node.Kind)
            {
                case NodeKinds.Generate3D:
                case NodeKinds.GenerateSmart3D:
                case NodeKinds.Generate3DMultiview:
                case NodeKinds.GenerateSmart3DMultiview:
                {
                    var request = RequestFor(node.Kind, settings, context);
                    if (!(request["spec"]?["generation"] is JObject generation) || prices == null) return null;
                    var codes = new List<string> { generation["base"]?.ToString() };
                    if (generation["modifiers"] is JArray modifiers) codes.AddRange(modifiers.Select(m => m.ToString()));
                    return prices.Total(request["model"]?.Type == JTokenType.String ? request["model"].ToString() : null, codes);
                }
                case NodeKinds.GenerateSplat:
                    return ToolSettings.SplatPrice(prices, settings);
                case NodeKinds.Retopology:
                    return ToolSettings.RetopologyPrice(prices, settings);
                case NodeKinds.Segmentation:
                case NodeKinds.SmartSegmentation:
                    return ToolSettings.SegmentPrice(prices, settings);
                case NodeKinds.Texture:
                case NodeKinds.TextureMultiview:
                    return ToolSettings.TexturePrice(prices, settings);
                case NodeKinds.SmartUv:
                    return ToolSettings.SmartUvPrice(prices);
                case NodeKinds.Rigging:
                    return ToolSettings.AutoRigPrice(prices);
                default:
                    return 0;
            }
        }

        /// <summary>A node's price as the canvas writes it: "—" while unknown, "Free", "48 cr" or "33 cr / part".</summary>
        public static string PriceLabel(int? price, bool perPart) =>
            price == null ? "—" : price == 0 ? "Free" : $"{price} cr{(perPart ? " / part" : "")}";

        /// <summary>
        /// Every node's request rebuilt from its settings — run before saving, pricing or starting a run (withRequests).
        /// Nodes out of the Unity plugin's scope (and unknown kinds) keep the request they came with, untouched.
        /// Not for a run's own graph: a seed's request is emptied, as the recipe keeps none.
        /// </summary>
        public static PipelineGraph WithRequests(PipelineGraph graph, ThreeDEntitlements entitlements = null)
        {
            var next = graph.Clone();
            foreach (var node in next.Nodes)
            {
                if (NodeCatalog.NotAvailableInUnity(node.Kind)) continue;
                node.Request = RequestFor(node.Kind, NodeSettings.Of(node), new NodeContext(GraphRules.IncomingType(next, node.Id), entitlements));
            }
            return next;
        }

        /// <summary>
        /// The editor's load normalisation (pipeline-editor.tsx): each node's settings over the Studio's defaults (an
        /// agent-drafted graph names only what it changed); the seed holds no picture or model; a step set on each run
        /// holds no input; a wire into a socket a step does not have is dropped. Nodes out of scope or unknown are kept
        /// exactly as they came. Flags a 3D step without request.spec so the caller saves it with <see cref="WithRequests"/>.
        /// </summary>
        public static NormalizeResult Normalize(PipelineGraph saved)
        {
            // A generation fed views opens as its multi-view step (the server renames it the same way when it reads it).
            var graph = NodeCatalog.WithMultiViewSteps(saved).Clone();
            foreach (var node in graph.Nodes)
            {
                if (NodeCatalog.NotAvailableInUnity(node.Kind)) continue;
                node.Settings = NodeSettings.WithDefaults(node.Kind, node.Settings);
                if (NodeCatalog.IsSeed(node.Kind))
                {
                    node.Settings = NodeSettings.Defaults(node.Kind);
                    node.Request = new JObject();
                }
                WithoutRunInputInPlace(node);
            }
            graph.Edges = graph.Edges.Where(edge =>
            {
                var target = graph.Node(edge.Target);
                return target == null || !NodeCatalog.IsKnown(target.Kind) || NodeCatalog.InputPort(target.Kind, edge.TargetHandle) != null;
            }).ToList();
            return new NormalizeResult
            {
                Graph = graph,
                NeedsRequestRebuild = saved.Nodes.Any(n =>
                    NodeCatalog.IsThreeD(n.Kind) && !NodeCatalog.NotAvailableInUnity(n.Kind) && !(n.Request?["spec"] is JObject)),
            };
        }

        /// <summary>
        /// The recipe as a new run's copy (forNewRun): a step set on each run starts empty, so the run asks for its own
        /// input — the run is made as a draft and waits for it before Start.
        /// </summary>
        public static PipelineGraph ForNewRun(PipelineGraph graph)
        {
            var next = graph.Clone();
            foreach (var node in next.Nodes) WithoutRunInputInPlace(node);
            return next;
        }

        /// <summary>A step set on each run, with the input a run gives it taken out (withoutRunInput). Returns a copy.</summary>
        public static PipelineNode WithoutRunInput(PipelineNode node)
        {
            var copy = node.Clone();
            WithoutRunInputInPlace(copy);
            return copy;
        }

        static void WithoutRunInputInPlace(PipelineNode node)
        {
            if (!NodeSettings.SetOnEachRun(node)) return;
            node.Settings = node.Settings != null ? (JObject)node.Settings.DeepClone() : new JObject();
            if (node.Kind == NodeKinds.TextureMultiview)
            {
                node.Settings["referenceViews"] = new JObject();
                return;
            }
            node.Settings["prompt"] = "";
            if (node.Kind == NodeKinds.Texture)
            {
                node.Settings["reference"] = null;
                node.Settings["referenceViews"] = new JObject();
            }
        }

        /// <summary>
        /// A run's per-run input for one step, as PATCH /pipeline-runs/:id/nodes/:nodeId wants it: the node's settings
        /// with the change, and the request rebuilt from them (runs-view.tsx send).
        /// </summary>
        public static (JObject settings, JObject request) RunNodeChange(PipelineGraph runGraph, string nodeId, JObject settingsPatch, ThreeDEntitlements entitlements = null)
        {
            var node = runGraph.Node(nodeId) ?? throw new System.ArgumentException($"There is no step {nodeId} in this run.");
            var settings = NodeSettings.WithDefaults(node.Kind, node.Settings);
            if (settingsPatch != null)
            {
                if (NodeCatalog.IsGenerate(node.Kind)) settings = GenerateForm.ApplyPatch(settings, settingsPatch);
                else
                    foreach (var pair in settingsPatch)
                        settings[pair.Key] = pair.Value?.DeepClone();
            }
            var request = RequestFor(node.Kind, settings, new NodeContext(GraphRules.IncomingType(runGraph, nodeId), entitlements));
            return (settings, request);
        }
    }
}
