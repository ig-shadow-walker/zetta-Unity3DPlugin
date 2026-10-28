using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Zetta3D.Editor.Pipelines
{
    /// <summary>The most a run of a graph can cost, worked out on this side from the price list (estimateFor).</summary>
    public sealed class GraphEstimate
    {
        /// <summary>The total, or null while a price is unknown.</summary>
        public int? Credits;
        /// <summary>Some steps run once per part (downstream of Isolate Parts' <c>each</c>): the total assumes Max parts.</summary>
        public bool PerPart;
        public List<Line> Lines = new List<Line>();

        public sealed class Line
        {
            public string NodeId;
            /// <summary>One run of the node; null while unknown; 0 for a free step.</summary>
            public int? Credits;
            /// <summary>How many times it runs.</summary>
            public int Count;
            public bool PerPart;
        }

        /// <summary>The editor's line: "—", "48 credits a run", or "Up to 132 credits a run".</summary>
        public string Label => LabelFor(Credits, PerPart);

        public static string LabelFor(int? credits, bool perPart) =>
            credits == null ? "—" : $"{(perPart ? "Up to " : "")}{credits} credits a run";
    }

    /// <summary>
    /// THE GRAPH'S RULES — what may connect, what stops it running, what a run will ask for, what it may cost. Ports
    /// of src/lib/pipelines.ts (canConnect, fanOutNodes, graphProblems, runInputProblems) and node-config.ts
    /// (nodeWarnings, estimateFor), with the server's structural checks (pipeline-graph.ts) added so nothing the
    /// server would refuse is missed. Pure.
    /// </summary>
    public static class GraphRules
    {
        /// <summary>Whether a connection from this output into that input would be allowed (types match, no loop).</summary>
        public static bool CanConnect(PipelineGraph graph, string source, string sourceHandle, string target, string targetHandle = "in")
        {
            if (source == target) return false;
            var from = graph.Node(source);
            var to = graph.Node(target);
            if (from == null || to == null) return false;
            var carries = NodeCatalog.OutputType(from.Kind, sourceHandle);
            var port = NodeCatalog.InputPort(to.Kind, targetHandle);
            if (carries == null || port == null || !port.AcceptsType(carries)) return false;
            // A loop would never finish.
            return !Reaches(graph, target, source);
        }

        /// <summary>
        /// Why a connection the user is making is refused — said on the canvas rather than nothing happening — or null
        /// when it can be made (connectionProblem).
        /// </summary>
        public static string ConnectionProblem(PipelineGraph graph, string source, string sourceHandle, string target, string targetHandle = "in")
        {
            if (CanConnect(graph, source, sourceHandle, target, targetHandle)) return null;
            var from = graph.Node(source);
            var to = graph.Node(target);
            if (from == null || to == null || source == target) return null;
            var carries = NodeCatalog.OutputType(from.Kind, sourceHandle);
            var port = NodeCatalog.InputPort(to.Kind, targetHandle);
            if (carries == null || port == null) return null;
            if (!port.AcceptsType(carries)) return NodeCatalog.MismatchMessage(to.Kind, carries, port.Accepts);
            return "That would loop back on itself.";
        }

        /// <summary>Whether <paramref name="to"/> is downstream of (or is) <paramref name="from"/>.</summary>
        public static bool Reaches(PipelineGraph graph, string from, string to)
        {
            var seen = new HashSet<string>();
            var stack = new Stack<string>();
            stack.Push(from);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (id == to) return true;
                if (!seen.Add(id)) continue;
                foreach (var edge in graph.Edges)
                    if (edge.Source == id)
                        stack.Push(edge.Target);
            }
            return false;
        }

        /// <summary>The edge feeding a node's input, or null.</summary>
        public static PipelineEdge InputEdge(PipelineGraph graph, string nodeId, string handle = "in") =>
            graph.Edges.FirstOrDefault(e => e.Target == nodeId && e.TargetHandle == handle);

        /// <summary>The node feeding this one's input (sourceOf).</summary>
        public static PipelineNode SourceOf(PipelineGraph graph, string nodeId, string handle = "in")
        {
            var edge = InputEdge(graph, nodeId, handle);
            return edge == null ? null : graph.Node(edge.Source);
        }

        /// <summary>What arrives on a node's main input, if it is connected (incomingType).</summary>
        public static string IncomingType(PipelineGraph graph, string nodeId)
        {
            var edge = InputEdge(graph, nodeId);
            var source = edge == null ? null : graph.Node(edge.Source);
            return source == null ? null : NodeCatalog.OutputType(source.Kind, edge.SourceHandle);
        }

        /// <summary>The nodes that run once per part — everything downstream of an Isolate Parts <c>each</c> (fanOutNodes).</summary>
        public static HashSet<string> FanOutNodes(PipelineGraph graph)
        {
            var result = new HashSet<string>();
            void Visit(string nodeId)
            {
                foreach (var edge in graph.Edges)
                {
                    if (edge.Source != nodeId || result.Contains(edge.Target)) continue;
                    result.Add(edge.Target);
                    Visit(edge.Target);
                }
            }
            foreach (var edge in graph.Edges)
            {
                if (edge.SourceHandle != "each" || result.Contains(edge.Target)) continue;
                result.Add(edge.Target);
                Visit(edge.Target);
            }
            return result;
        }

        /// <summary>Node ids in an order where every node comes after the one feeding it; null when there is a loop.</summary>
        public static List<string> TopologicalOrder(PipelineGraph graph)
        {
            var incoming = graph.Nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, _ => 0);
            foreach (var edge in graph.Edges)
                if (incoming.ContainsKey(edge.Target))
                    incoming[edge.Target]++;
            var queue = new Queue<string>(graph.Nodes.Select(n => n.Id).Distinct().Where(id => incoming[id] == 0));
            var order = new List<string>();
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                order.Add(id);
                foreach (var edge in graph.Edges)
                {
                    if (edge.Source != id || !incoming.ContainsKey(edge.Target)) continue;
                    if (--incoming[edge.Target] == 0) queue.Enqueue(edge.Target);
                }
            }
            return order.Count == incoming.Count ? order : null;
        }

        /// <summary>
        /// Everything that stops this graph from running, as the editor shows it: the graph's structural problems
        /// (<see cref="GraphProblems"/>, on the requests the settings build now) plus what each step's Studio form
        /// would refuse (<see cref="RequestBuilder.NodeProblems"/>). An empty list means runnable.
        /// </summary>
        public static List<PipelineProblem> Problems(PipelineGraph graph, ThreeDEntitlements entitlements = null)
        {
            var built = RequestBuilder.WithRequests(graph, entitlements);
            var problems = GraphProblems(built);
            foreach (var node in graph.Nodes)
            {
                if (NodeCatalog.IsSeed(node.Kind) || NodeCatalog.NotAvailableInUnity(node.Kind)) continue;
                var context = new NodeContext(IncomingType(graph, node.Id), entitlements);
                problems.AddRange(RequestBuilder.NodeProblems(node.Kind, NodeSettings.Of(node), context).Select(m => new PipelineProblem(node.Id, m)));
            }
            return problems;
        }

        /// <summary>
        /// The graph's own problems (graphProblems), read from each node's <c>request</c> as it stands — call
        /// <see cref="Problems"/> to check the settings instead. The seed's picture is not asked for: each run chooses its own.
        /// </summary>
        public static List<PipelineProblem> GraphProblems(PipelineGraph graph)
        {
            var problems = new List<PipelineProblem>();
            if (graph.Nodes.Count > 200) problems.Add(new PipelineProblem(null, "A pipeline holds at most 200 steps."));

            var ids = new HashSet<string>();
            foreach (var node in graph.Nodes)
            {
                if (!NodeCatalog.IsKnown(node.Kind)) problems.Add(new PipelineProblem(node.Id, "This step is not one Zetta runs."));
                if (!ids.Add(node.Id)) problems.Add(new PipelineProblem(node.Id, "Two steps share one id."));
            }

            var seeds = graph.Nodes.Where(n => NodeCatalog.IsSeed(n.Kind)).ToList();
            if (seeds.Count == 0) problems.Add(new PipelineProblem(null, "Add a seed — the picture or model the pipeline starts from."));
            foreach (var seed in seeds.Skip(1)) problems.Add(new PipelineProblem(seed.Id, "A pipeline starts from one seed."));

            var filled = new HashSet<string>();
            foreach (var edge in graph.Edges)
            {
                var source = graph.Node(edge.Source);
                var target = graph.Node(edge.Target);
                if (source == null || target == null || !NodeCatalog.IsKnown(source.Kind) || !NodeCatalog.IsKnown(target.Kind))
                {
                    problems.Add(new PipelineProblem(null, "A connection points at a step that is gone."));
                    continue;
                }
                var carries = NodeCatalog.OutputType(source.Kind, edge.SourceHandle);
                var port = NodeCatalog.InputPort(target.Kind, edge.TargetHandle);
                if (carries == null || port == null)
                {
                    problems.Add(new PipelineProblem(target.Id, "A connection uses a socket this step does not have."));
                    continue;
                }
                if (!port.AcceptsType(carries))
                    problems.Add(new PipelineProblem(target.Id, NodeCatalog.MismatchMessage(target.Kind, carries, port.Accepts)));
                var slot = $"{edge.Target}:{edge.TargetHandle}";
                if (!filled.Add(slot)) problems.Add(new PipelineProblem(target.Id, "An input takes one connection."));
            }

            foreach (var node in graph.Nodes)
            {
                var spec = NodeCatalog.Spec(node.Kind);
                if (spec == null) continue;
                foreach (var input in spec.Inputs)
                    if (input.Required && !filled.Contains($"{node.Id}:{input.Handle}"))
                        problems.Add(new PipelineProblem(node.Id, "Connect this step to the one before it."));
            }

            if (TopologicalOrder(graph) == null) problems.Add(new PipelineProblem(null, "The pipeline loops back on itself."));

            foreach (var node in graph.Nodes)
            {
                var spec = NodeCatalog.Spec(node.Kind);
                if (spec == null || NodeCatalog.IsSeed(node.Kind)) continue;
                // AI texturing is always set on each run; AI edit when chosen. Otherwise its input must be here.
                var perRun = NodeSettings.SetOnEachRun(node);
                var single = new PipelineGraph { Nodes = new List<PipelineNode> { node }, Edges = graph.Edges };
                var missing = !perRun && RunInputProblems(single).Count > 0;
                if (missing && node.Kind == NodeKinds.AiEdit)
                    problems.Add(new PipelineProblem(node.Id, "Say how to change the picture — or set it on each run."));
                else if (spec.EngineChoice && node.Request?["model"]?.Type != JTokenType.String)
                    problems.Add(new PipelineProblem(node.Id, "Choose an engine."));
                else if (node.Kind == NodeKinds.Export && node.Request?["format"]?.Type != JTokenType.String)
                    problems.Add(new PipelineProblem(node.Id, "Choose a file format."));
            }
            return problems;
        }

        /// <summary>
        /// What a run needs from the user before it can start (runInputProblems — the server's own rule): AI texturing
        /// a description or a reference picture, AI edit an instruction. A draft is made without them; Start is refused,
        /// naming each step, until they are given.
        /// </summary>
        public static List<PipelineProblem> RunInputProblems(PipelineGraph graph)
        {
            bool Text(JToken value) => value?.Type == JTokenType.String && value.ToString().Trim() != "";
            var problems = new List<PipelineProblem>();
            foreach (var node in graph.Nodes)
            {
                var request = node.Request ?? new JObject();
                if (node.Kind == NodeKinds.AiEdit && !Text(request["prompt"]))
                    problems.Add(new PipelineProblem(node.Id, $"{(string.IsNullOrEmpty(node.Label) ? "AI edit" : node.Label)}: say how to change the picture."));
                // Multi-view - AI texturing: views wired in, or the run's own pictures of each side, the front at least.
                if (node.Kind == NodeKinds.TextureMultiview)
                {
                    var views = request["referenceViews"] as JArray ?? new JArray();
                    if (!NodeSettings.ViewsWired(graph, node.Id) && !views.Any(v => v["viewType"]?.ToString() == "front"))
                        problems.Add(new PipelineProblem(node.Id, $"{(string.IsNullOrEmpty(node.Label) ? "Multi-view - AI texturing" : node.Label)}: add the pictures of each side — a front one at least."));
                    continue;
                }
                if (node.Kind == NodeKinds.Texture)
                {
                    var given = graph.Edges.Any(e => e.Target == node.Id && e.TargetHandle == "reference")
                                || Text(request["prompt"])
                                || request["referenceUploadKey"]?.Type == JTokenType.String
                                || ToolSettings.IsNumber(request["referenceImageId"]);
                    if (!given)
                        problems.Add(new PipelineProblem(node.Id, $"{(string.IsNullOrEmpty(node.Label) ? "AI texturing" : node.Label)}: describe the texture or add a reference picture."));
                }
            }
            return problems;
        }

        /// <summary>
        /// What may go wrong once it runs — said while building (nodeWarnings): Smart UV past 80,000 triangles, read from
        /// the nearest step upstream that sets a polygon budget. Also flags steps the Unity plugin cannot edit.
        /// </summary>
        public static List<string> Warnings(PipelineGraph graph, string nodeId)
        {
            var warnings = new List<string>();
            var node = graph.Node(nodeId);
            if (node == null) return warnings;
            if (!NodeCatalog.IsKnown(node.Kind))
                warnings.Add("This step is newer than this version of the plugin — it is kept as it is. Update the plugin to edit it.");
            else if (NodeCatalog.NotAvailableInUnity(node.Kind))
                warnings.Add("This step is not available in Unity — it is kept as it is; open the pipeline on zetta3d.ai to change it.");
            if (node.Kind != NodeKinds.SmartUv) return warnings;
            var budget = PolygonBudgetBefore(graph, nodeId);
            if (budget == null || budget.Value.triangles <= ToolSettings.SmartUvMaxTriangles) return warnings;
            warnings.Add($"{budget.Value.from} can make about {GenerateForm.Count(budget.Value.triangles)} triangles — over Smart UV’s {GenerateForm.Count(ToolSettings.SmartUvMaxTriangles)}. Add Retopology before it, or lower the polygon count.");
            return warnings;
        }

        /// <summary>Every node's warnings, keyed by node id (nodes without any are left out).</summary>
        public static Dictionary<string, List<string>> AllWarnings(PipelineGraph graph) =>
            graph.Nodes.Select(n => (n.Id, list: Warnings(graph, n.Id))).Where(p => p.list.Count > 0)
                .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().list);

        /// <summary>The triangle budget arriving at a node: the nearest step upstream that sets one (quads count twice).</summary>
        static (string from, long triangles)? PolygonBudgetBefore(PipelineGraph graph, string nodeId)
        {
            var seen = new HashSet<string>();
            var current = nodeId;
            while (seen.Add(current))
            {
                var source = SourceOf(graph, current);
                if (source == null) return null;
                var settings = NodeSettings.Of(source);
                var label = !string.IsNullOrEmpty(source.Label) ? source.Label : Capitalised(source.Kind.Replace('_', ' '));
                if (source.Kind == NodeKinds.Retopology)
                {
                    if (settings["engine"]?.ToString() != "r1" || !ToolSettings.IsNumber(settings["faces"])) return null;
                    var faces = (long)settings["faces"].Value<double>();
                    return (label, faces * (settings["polygonType"]?.ToString() == "quadrilateral" ? 2 : 1));
                }
                if (NodeCatalog.IsGenerate(source.Kind))
                {
                    var raw = settings["polygonCount"]?.ToString() ?? "";
                    if (!double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var count) ||
                        double.IsInfinity(count) || count <= 0) return null;
                    return (label, (long)(count * (settings["polygonType"]?.ToString() == "quadrilateral" ? 2 : 1)));
                }
                if (source.Kind == NodeKinds.SeedModel) return null;
                current = source.Id;
            }
            return null;
        }

        static string Capitalised(string text) => string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

        /// <summary>
        /// The most a run of the whole graph can cost (estimateFor): each node once — Max parts times downstream of
        /// <c>each</c> — picture steps and seeds free. Null credits while any price is unknown (no price list yet).
        /// </summary>
        public static GraphEstimate Estimate(PipelineGraph graph, PriceList prices, ThreeDEntitlements entitlements = null)
        {
            var perPart = FanOutNodes(graph);
            var maxParts = 1;
            foreach (var node in graph.Nodes.Where(n => n.Kind == NodeKinds.IsolateParts))
            {
                var raw = node.Settings?["maxParts"];
                var value = ToolSettings.IsNumber(raw) ? (int)raw.Value<double>() : 0;
                maxParts = Math.Max(maxParts, value != 0 ? value : 6);
            }
            var estimate = new GraphEstimate { PerPart = perPart.Count > 0, Credits = 0 };
            foreach (var node in graph.Nodes)
            {
                var price = RequestBuilder.PriceFor(node, prices, new NodeContext(IncomingType(graph, node.Id), entitlements));
                var count = perPart.Contains(node.Id) ? maxParts : 1;
                estimate.Lines.Add(new GraphEstimate.Line { NodeId = node.Id, Credits = price, Count = count, PerPart = perPart.Contains(node.Id) });
                if (price == null) estimate.Credits = null;
                else if (estimate.Credits != null) estimate.Credits += price.Value * count;
            }
            return estimate;
        }
    }
}
