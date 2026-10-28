using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Zetta3D.Editor.Pipelines
{
    /// <summary>
    /// One change to a recipe, as the agent sends it (the web's PipelineChange, lib/agent.ts):
    /// <c>{op:"add", kind, after?, output?, settings?}</c>, <c>{op:"set", nodeId, settings}</c>, <c>{op:"remove", nodeId}</c>.
    /// </summary>
    public sealed class PipelineChange
    {
        /// <summary>add | set | remove</summary>
        public string Op;
        public string Kind;
        /// <summary>add: the step it follows (the seed when absent).</summary>
        public string After;
        /// <summary>add after Isolate Parts: character | each | part:&lt;key&gt;.</summary>
        public string Output;
        public string NodeId;
        /// <summary>Agent- or web-shaped settings (see <see cref="AgentSettings.ToWeb"/>).</summary>
        public JObject Settings;

        public static PipelineChange Add(string kind, string after = null, JObject settings = null, string output = null) =>
            new PipelineChange { Op = "add", Kind = kind, After = after, Settings = settings, Output = output };

        public static PipelineChange Set(string nodeId, JObject settings) => new PipelineChange { Op = "set", NodeId = nodeId, Settings = settings };

        public static PipelineChange Remove(string nodeId) => new PipelineChange { Op = "remove", NodeId = nodeId };

        public static PipelineChange FromJson(JObject json) => json == null ? null : new PipelineChange
        {
            Op = json["op"]?.ToString(),
            Kind = json["kind"]?.ToString(),
            After = json["after"]?.Type == JTokenType.Null ? null : json["after"]?.ToString(),
            Output = json["output"]?.Type == JTokenType.String ? json["output"].ToString() : null,
            NodeId = json["nodeId"]?.ToString(),
            Settings = json["settings"] as JObject,
        };
    }

    /// <summary>A step of <see cref="GraphEditing.Build"/> — the agent's build_pipeline steps.</summary>
    public sealed class BuildStep
    {
        public string Kind;
        /// <summary>The index of the earlier step this one follows; null (or not earlier) = the seed.</summary>
        public int? After;
        /// <summary>After Isolate Parts: character (default) | each | part:&lt;key&gt;.</summary>
        public string Output;
        public JObject Settings;

        public static BuildStep FromJson(JObject json) => json == null ? null : new BuildStep
        {
            Kind = json["kind"]?.ToString(),
            After = json["after"]?.Type == JTokenType.Integer ? json["after"].Value<int>() : (int?)null,
            Output = json["output"]?.Type == JTokenType.String ? json["output"].ToString() : null,
            Settings = json["settings"] as JObject,
        };
    }

    /// <summary>The result of <see cref="GraphEditing.ApplyAgentChanges"/>.</summary>
    public sealed class AgentChangesResult
    {
        public PipelineGraph Graph;
        /// <summary>The step the user should see — the last one added or changed.</summary>
        public string Focus;
        /// <summary>The ids of the steps added, in the order of the changes that added them.</summary>
        public List<string> Added = new List<string>();
        /// <summary>Why each skipped change was skipped (index into the changes, reason).</summary>
        public List<(int index, string reason)> Skipped = new List<(int, string)>();
    }

    /// <summary>The result of <see cref="GraphEditing.Build"/>.</summary>
    public sealed class BuildResult
    {
        public string Title;
        public PipelineGraph Graph;
        /// <summary>The node id of each step, in step order (the seed is "seed").</summary>
        public List<string> StepNodeIds = new List<string>();
        public List<(int index, string reason)> Skipped = new List<(int, string)>();
    }

    /// <summary>
    /// The agent's settings vocabulary (the Unity agent protocol, "Pinned details v1.3") mapped onto the web
    /// form shapes a graph stores. Web names are accepted as they are.
    /// </summary>
    public static class AgentSettings
    {
        /// <summary>
        /// Agent-shaped settings → web-shaped, for one kind. Generate: <c>pbr</c> → <c>pbrMaterials</c>, <c>faceCount</c>
        /// (number) → <c>polygonCount</c> (string, marked typed). Rigging settings already are the web's (<c>spec</c> "standard" | "mixamo").
        /// Smart UV <c>{bake, orientation, compress}</c> and segmentation <c>{mode, granularity, splitByConnectivity, hint}</c>
        /// already are the web's. Keys it does not know pass through. Returns a new object (null → empty).
        /// </summary>
        public static JObject ToWeb(string kind, JObject agentSettings)
        {
            var web = new JObject();
            if (agentSettings == null) return web;
            foreach (var pair in agentSettings)
            {
                var value = pair.Value?.DeepClone();
                switch (kind)
                {
                    case NodeKinds.Generate3D:
                    case NodeKinds.GenerateSmart3D:
                    case NodeKinds.Generate3DMultiview:
                    case NodeKinds.GenerateSmart3DMultiview:
                        if (pair.Key == "pbr")
                        {
                            web["pbrMaterials"] = value;
                            continue;
                        }
                        if (pair.Key == "faceCount" || pair.Key == "polygonCount")
                        {
                            if (value == null || value.Type == JTokenType.Null) web["polygonCount"] = "";
                            else if (value.Type == JTokenType.Integer) web["polygonCount"] = value.Value<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
                            else if (value.Type == JTokenType.Float) web["polygonCount"] = value.Value<double>().ToString(System.Globalization.CultureInfo.InvariantCulture);
                            else web["polygonCount"] = value.ToString();
                            if (agentSettings["polygonCountTyped"] == null) web["polygonCountTyped"] = true;
                            continue;
                        }
                        break;
                    case NodeKinds.Retopology:
                        if (pair.Key == "faceCount")
                        {
                            web["faces"] = value;
                            continue;
                        }
                        break;
                    case NodeKinds.Texture:
                    case NodeKinds.TextureMultiview:
                        if (pair.Key == "quality" && value?.Type == JTokenType.String)
                        {
                            web["tier"] = value.ToString() switch { "detailed" => ToolSettings.RetextureHd, "extreme" or "8k" => ToolSettings.Retexture8K, _ => ToolSettings.Retexture };
                            continue;
                        }
                        break;
                }
                web[pair.Key] = value;
            }
            return web;
        }
    }

    /// <summary>
    /// PURE EDITS OF A RECIPE, shared by the canvas UI and the agent tools — each returns a new graph and leaves the one
    /// it was given alone. Ports of pipeline-editor.tsx (add, remove, connect) and agent-changes.ts. Requests are rebuilt
    /// for the nodes an edit touches; call <see cref="RequestBuilder.WithRequests"/> before saving anyway.
    /// </summary>
    public static class GraphEditing
    {
        static readonly System.Random Random = new System.Random();
        const string Base36 = "0123456789abcdefghijklmnopqrstuvwxyz";

        /// <summary>A fresh id: <c>&lt;prefix&gt;-&lt;7 base36&gt;</c> — <c>generate-3d-k3j9x0a</c> for a node, <c>e-…</c> for an edge.</summary>
        public static string NewId(string prefix)
        {
            var chars = new char[7];
            lock (Random)
                for (var i = 0; i < chars.Length; i++)
                    chars[i] = Base36[Random.Next(Base36.Length)];
            return $"{prefix}-{new string(chars)}";
        }

        /// <summary>The id prefix a kind's nodes use: <c>generate_3d</c> → <c>generate-3d</c>.</summary>
        public static string NodeIdPrefix(string kind) => (kind ?? "node").Replace('_', '-');

        /// <summary>
        /// Adds a step. With <paramref name="afterNodeId"/> it goes beside that step (x + 300, one 180px row down per step
        /// already after it) and is wired from the first output that accepts it — for Isolate Parts, the named
        /// <paramref name="output"/> (character by default). Without, it is placed at <paramref name="position"/> (or
        /// stacked by count) unwired. Settings are the Studio's defaults with <paramref name="settings"/> over them.
        /// Throws <see cref="InvalidOperationException"/> for an unknown kind, one not available in Unity, a second seed,
        /// or a missing <paramref name="afterNodeId"/>.
        /// </summary>
        public static PipelineGraph AddStep(PipelineGraph graph, string kind, out string nodeId, string afterNodeId = null, string output = null,
            JObject settings = null, NodePosition? position = null, ThreeDEntitlements entitlements = null)
        {
            var spec = NodeCatalog.Spec(kind) ?? throw new InvalidOperationException($"“{kind}” is not a step Zetta runs.");
            if (spec.NotAvailableInUnity) throw new InvalidOperationException($"{spec.Label} is not available in Unity.");
            if (NodeCatalog.IsSeed(kind) && graph.Nodes.Any(n => NodeCatalog.IsSeed(n.Kind)))
                throw new InvalidOperationException("A pipeline starts from one seed.");
            var after = afterNodeId == null ? null : graph.Node(afterNodeId) ?? throw new InvalidOperationException($"There is no step {afterNodeId}.");

            var next = graph.Clone();
            nodeId = NewId(NodeIdPrefix(kind));
            var siblings = after == null ? 0 : next.Edges.Count(e => e.Source == after.Id);
            var place = position ?? (after != null
                ? new NodePosition(after.Position.X + 300, after.Position.Y + siblings * 180)
                : new NodePosition(next.Nodes.Count * 60, next.Nodes.Count * 40));
            var nodeSettings = NodeSettings.WithDefaults(kind, settings ?? new JObject());
            var node = new PipelineNode
            {
                Id = nodeId,
                Kind = kind,
                Position = place,
                Settings = nodeSettings,
                Request = RequestBuilder.RequestFor(kind, nodeSettings, new NodeContext(null, entitlements)),
            };
            next.Nodes.Add(node);

            if (after != null)
            {
                var handles = after.Kind == NodeKinds.IsolateParts
                    ? new List<string> { output ?? "character" }
                    : NodeCatalog.Spec(after.Kind)?.Outputs.Select(o => o.Handle).ToList() ?? new List<string>();
                var id = nodeId;
                var handle = handles.FirstOrDefault(h => GraphRules.CanConnect(next, after.Id, h, id));
                if (handle != null)
                {
                    next.Edges.Add(new PipelineEdge { Id = NewId("e"), Source = after.Id, SourceHandle = handle, Target = nodeId, TargetHandle = "in" });
                    // Built again now that it knows what arrives — a views input changes a generation's request.
                    node.Request = RequestBuilder.RequestFor(kind, nodeSettings, new NodeContext(GraphRules.IncomingType(next, nodeId), entitlements));
                }
            }
            return next;
        }

        /// <summary>AddStep, discarding the id.</summary>
        public static PipelineGraph AddStep(PipelineGraph graph, string kind, string afterNodeId = null, string output = null, JObject settings = null,
            ThreeDEntitlements entitlements = null) => AddStep(graph, kind, out _, afterNodeId, output, settings, null, entitlements);

        /// <summary>
        /// Replaces a step's settings (the canvas form's whole value — for Generate, run it through
        /// <see cref="GenerateForm.ApplyPatch"/> first) and rebuilds its request. Unknown or out-of-scope steps are refused.
        /// </summary>
        public static PipelineGraph SetSettings(PipelineGraph graph, string nodeId, JObject settings, ThreeDEntitlements entitlements = null)
        {
            var next = graph.Clone();
            var node = next.Node(nodeId) ?? throw new InvalidOperationException($"There is no step {nodeId}.");
            if (NodeCatalog.NotAvailableInUnity(node.Kind)) throw new InvalidOperationException($"{node.DisplayName} cannot be changed in Unity.");
            node.Settings = settings != null ? (JObject)settings.DeepClone() : NodeSettings.Defaults(node.Kind);
            node.Request = RequestBuilder.RequestFor(node, next, entitlements);
            return next;
        }

        /// <summary>
        /// Merges a change over a step's settings, as the agent's "set" does: over what it had, then over the Studio's
        /// defaults (a Generate engine named settles its coupled options). Agent-shaped keys are mapped first.
        /// </summary>
        public static PipelineGraph MergeSettings(PipelineGraph graph, string nodeId, JObject patch, ThreeDEntitlements entitlements = null)
        {
            var node = graph.Node(nodeId) ?? throw new InvalidOperationException($"There is no step {nodeId}.");
            var merged = node.Settings != null ? (JObject)node.Settings.DeepClone() : new JObject();
            foreach (var pair in AgentSettings.ToWeb(node.Kind, patch)) merged[pair.Key] = pair.Value?.DeepClone();
            return SetSettings(graph, nodeId, NodeSettings.WithDefaults(node.Kind, merged), entitlements);
        }

        /// <summary>Renames a step (null or empty clears the name).</summary>
        public static PipelineGraph SetLabel(PipelineGraph graph, string nodeId, string label)
        {
            var next = graph.Clone();
            var node = next.Node(nodeId) ?? throw new InvalidOperationException($"There is no step {nodeId}.");
            node.Label = string.IsNullOrWhiteSpace(label) ? null : (label.Length > 80 ? label.Substring(0, 80) : label);
            return next;
        }

        /// <summary>Moves a step on the canvas.</summary>
        public static PipelineGraph Move(PipelineGraph graph, string nodeId, NodePosition position)
        {
            var next = graph.Clone();
            var node = next.Node(nodeId) ?? throw new InvalidOperationException($"There is no step {nodeId}.");
            node.Position = position;
            return next;
        }

        /// <summary>Removes a step and every connection to or from it; the steps after it are left unconnected.</summary>
        public static PipelineGraph Remove(PipelineGraph graph, string nodeId)
        {
            var next = graph.Clone();
            next.Nodes.RemoveAll(n => n.Id == nodeId);
            next.Edges.RemoveAll(e => e.Source == nodeId || e.Target == nodeId);
            RebuildIncomingDependents(next, null);
            return next;
        }

        /// <summary>
        /// Connects an output to an input. An input takes one connection, so one already there is replaced. Throws
        /// <see cref="InvalidOperationException"/> when <see cref="GraphRules.CanConnect"/> refuses it. The target's request
        /// is rebuilt (views arriving make a generation multi-view).
        /// </summary>
        public static PipelineGraph Connect(PipelineGraph graph, string source, string sourceHandle, string target, string targetHandle = "in",
            ThreeDEntitlements entitlements = null)
        {
            var without = graph.Clone();
            without.Edges.RemoveAll(e => e.Target == target && e.TargetHandle == targetHandle);
            if (!GraphRules.CanConnect(without, source, sourceHandle, target, targetHandle))
                throw new InvalidOperationException(GraphRules.ConnectionProblem(without, source, sourceHandle, target, targetHandle)
                                                    ?? "Those two steps cannot be connected that way.");
            without.Edges.Add(new PipelineEdge { Id = NewId("e"), Source = source, SourceHandle = sourceHandle, Target = target, TargetHandle = targetHandle });
            RebuildRequest(without, target, entitlements);
            return without;
        }

        /// <summary>
        /// Moves a connection: either end dragged to another socket. The moved one replaces whatever was in its new input;
        /// refused — with the reason — where that socket cannot take it, the graph unchanged.
        /// </summary>
        public static PipelineGraph Reconnect(PipelineGraph graph, string edgeId, string source, string sourceHandle, string target, string targetHandle,
            ThreeDEntitlements entitlements = null)
        {
            var old = graph.Edge(edgeId);
            var without = graph.Clone();
            without.Edges.RemoveAll(e => e.Id == edgeId);
            var next = Connect(without, source, sourceHandle, target, targetHandle, entitlements);
            if (old != null) RebuildRequest(next, old.Target, entitlements);
            return next;
        }

        /// <summary>Removes one connection.</summary>
        public static PipelineGraph Disconnect(PipelineGraph graph, string edgeId, ThreeDEntitlements entitlements = null)
        {
            var next = graph.Clone();
            var edge = next.Edge(edgeId);
            if (edge == null) return next;
            next.Edges.Remove(edge);
            RebuildRequest(next, edge.Target, entitlements);
            return next;
        }

        static void RebuildRequest(PipelineGraph graph, string nodeId, ThreeDEntitlements entitlements)
        {
            var node = graph.Node(nodeId);
            if (node == null || NodeCatalog.NotAvailableInUnity(node.Kind)) return;
            node.Request = RequestBuilder.RequestFor(node, graph, entitlements);
        }

        static void RebuildIncomingDependents(PipelineGraph graph, ThreeDEntitlements entitlements)
        {
            foreach (var node in graph.Nodes.Where(n => NodeCatalog.IsGenerate(n.Kind)))
                RebuildRequest(graph, node.Id, entitlements);
        }

        /// <summary>
        /// Isolate Parts learns its parts from a run: each part a run isolated becomes an output of its own on the recipe's
        /// node (settings.knownParts), only ever added to. Not the user's edit — <see cref="UndoStack.Replace"/> it.
        /// Returns the same instance when nothing was learnt.
        /// </summary>
        public static PipelineGraph LearnParts(PipelineGraph graph, PipelineRun run)
        {
            PipelineGraph next = null;
            foreach (var step in run.Steps)
            {
                if (!(step.Output is IsolatedStepOutput isolated)) continue;
                var source = (next ?? graph).Node(step.NodeId);
                if (source == null) continue;
                var known = NodeCatalog.KnownParts(source);
                var fresh = isolated.Parts.Where(p => p.Key != null && known.All(k => k.key != p.Key)).ToList();
                if (fresh.Count == 0) continue;
                next ??= graph.Clone();
                var node = next.Node(step.NodeId);
                node.Settings ??= NodeSettings.Defaults(node.Kind);
                var list = new JArray(known.Select(k => new JObject { ["key"] = k.key, ["label"] = k.label }));
                foreach (var part in fresh) list.Add(new JObject { ["key"] = part.Key, ["label"] = part.Label ?? part.Key });
                node.Settings["knownParts"] = list;
            }
            return next ?? graph;
        }

        /// <summary>
        /// The agent's changes applied the way the editor's own controls apply them (agent-changes.ts): add as the palette
        /// adds (Studio defaults with only what was named, wired from the step it follows — the seed when none is named);
        /// set merges over what the step had; remove takes the step and its connections. A change naming a step that is
        /// not there, a seed, or a kind not offered in Unity is skipped, never half-applied.
        /// </summary>
        public static AgentChangesResult ApplyAgentChanges(PipelineGraph graph, IEnumerable<PipelineChange> changes, ThreeDEntitlements entitlements = null)
        {
            var result = new AgentChangesResult { Graph = graph };
            var index = -1;
            foreach (var change in changes ?? Enumerable.Empty<PipelineChange>())
            {
                index++;
                if (change == null) continue;
                var next = result.Graph;
                switch (change.Op)
                {
                    case "remove":
                    {
                        var node = next.Node(change.NodeId);
                        if (node == null || NodeCatalog.IsSeed(node.Kind))
                        {
                            result.Skipped.Add((index, node == null ? $"There is no step {change.NodeId}." : "The seed stays."));
                            continue;
                        }
                        result.Graph = Remove(next, node.Id);
                        if (result.Focus == node.Id) result.Focus = null;
                        continue;
                    }
                    case "set":
                    {
                        var node = next.Node(change.NodeId);
                        if (node == null || NodeCatalog.IsSeed(node.Kind) || NodeCatalog.NotAvailableInUnity(node.Kind))
                        {
                            result.Skipped.Add((index, node == null ? $"There is no step {change.NodeId}." : $"{node.DisplayName} cannot be changed here."));
                            continue;
                        }
                        result.Graph = MergeSettings(next, node.Id, change.Settings, entitlements);
                        result.Focus = node.Id;
                        continue;
                    }
                    case "add":
                    {
                        var spec = NodeCatalog.Spec(change.Kind);
                        if (spec == null || !spec.OfferedInUnity || NodeCatalog.IsSeed(change.Kind))
                        {
                            result.Skipped.Add((index, $"“{change.Kind}” cannot be added here."));
                            continue;
                        }
                        var after = change.After != null ? next.Node(change.After) : next.Seed;
                        if (change.After != null && after == null)
                        {
                            result.Skipped.Add((index, $"There is no step {change.After}."));
                            continue;
                        }
                        result.Graph = AddStep(next, change.Kind, out var id, after?.Id, change.Output, AgentSettings.ToWeb(change.Kind, change.Settings), null, entitlements);
                        result.Added.Add(id);
                        result.Focus = id;
                        continue;
                    }
                    default:
                        result.Skipped.Add((index, $"Unknown change “{change.Op}”."));
                        continue;
                }
            }
            return result;
        }

        /// <summary>
        /// A draft pipeline from the agent's steps (build_pipeline): a seed (picture or model — it says what a run starts
        /// from, never which), then each step after the one its <c>after</c> index names, or after the seed when it names
        /// none. Laid out left to right by depth. Settings are the Studio's defaults with the agent's over them, and every
        /// request is built — ready to save with <see cref="PipelinesApi.CreateAsync"/>.
        /// </summary>
        public static BuildResult Build(string title, string startsFrom, IList<BuildStep> steps, ThreeDEntitlements entitlements = null)
        {
            var fromModel = startsFrom == "model";
            var seedKind = fromModel ? NodeKinds.SeedModel : NodeKinds.SeedPicture;
            var graph = new PipelineGraph();
            graph.Nodes.Add(new PipelineNode
            {
                Id = "seed", Kind = seedKind, Position = new NodePosition(0, 0), Settings = NodeSettings.Defaults(seedKind), Request = new JObject(),
            });
            var result = new BuildResult
            {
                Title = string.IsNullOrWhiteSpace(title) ? "Pipeline from the Agent" : (title.Trim().Length > 200 ? title.Trim().Substring(0, 200) : title.Trim()),
            };
            var depth = new List<int>();
            var rows = new Dictionary<int, int>();
            var list = (steps ?? new List<BuildStep>()).Take(24).ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var step = list[i];
                var spec = NodeCatalog.Spec(step?.Kind);
                if (spec == null || !spec.OfferedInUnity || NodeCatalog.IsSeed(step.Kind))
                {
                    result.Skipped.Add((i, $"“{step?.Kind}” cannot be a step here."));
                    depth.Add(1);
                    result.StepNodeIds.Add(null);
                    continue;
                }
                var after = step.After.HasValue && step.After.Value >= 0 && step.After.Value < i && result.StepNodeIds[step.After.Value] != null
                    ? step.After.Value
                    : -1;
                var level = after < 0 ? 1 : depth[after] + 1;
                depth.Add(level);
                rows.TryGetValue(level, out var row);
                rows[level] = row + 1;
                var nodeId = $"s{i}";
                var settings = NodeSettings.WithDefaults(step.Kind, AgentSettings.ToWeb(step.Kind, step.Settings));
                graph.Nodes.Add(new PipelineNode { Id = nodeId, Kind = step.Kind, Position = new NodePosition(level * 300, row * 200), Settings = settings });
                var source = after < 0 ? "seed" : result.StepNodeIds[after];
                var sourceKind = graph.Node(source).Kind;
                var handle = sourceKind == NodeKinds.IsolateParts
                    ? (step.Output == "each" || (step.Output ?? "").StartsWith(NodeCatalog.PartHandlePrefix, StringComparison.Ordinal) ? step.Output : "character")
                    : "out";
                graph.Edges.Add(new PipelineEdge { Id = $"e{i}", Source = source, SourceHandle = handle, Target = nodeId, TargetHandle = "in" });
                result.StepNodeIds.Add(nodeId);
            }
            // Draw views into Generate 3D becomes Multi-view - Generate 3D, as the server would rename it.
            result.Graph = RequestBuilder.WithRequests(NodeCatalog.WithMultiViewSteps(graph), entitlements);
            return result;
        }
    }

    /// <summary>
    /// UNDO AND REDO FOR A RECIPE (graph-history.ts). Every edit is one step back, except streams: changes sharing a
    /// coalesce key within <see cref="CoalesceMs"/> of the last fold into the step they started (one drag, one undo).
    /// Changes the editor makes on its own (Isolate Parts learning its parts) are <see cref="Replace"/>d — kept, never undone.
    /// </summary>
    public sealed class UndoStack
    {
        public const int Limit = 100;
        public const int CoalesceMs = 1000;

        readonly List<PipelineGraph> _past = new List<PipelineGraph>();
        readonly List<PipelineGraph> _future = new List<PipelineGraph>();
        (string key, long at)? _last;

        public PipelineGraph Present { get; private set; }
        public bool CanUndo => _past.Count > 0;
        public bool CanRedo => _future.Count > 0;
        public int PastCount => _past.Count;
        public int FutureCount => _future.Count;

        /// <summary>Raised after every change of <see cref="Present"/> (commit, replace, undo, redo, load).</summary>
        public event Action<PipelineGraph> Changed;

        public UndoStack(PipelineGraph graph = null) => Present = graph ?? new PipelineGraph();

        static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>Starts over on a freshly loaded graph.</summary>
        public void Load(PipelineGraph graph)
        {
            _past.Clear();
            _future.Clear();
            _last = null;
            Present = graph;
            Changed?.Invoke(Present);
        }

        /// <summary>A user's change: a new step back, unless it continues the stream the last one started.</summary>
        public void Commit(PipelineGraph next, string coalesce = null, long? nowMs = null)
        {
            if (ReferenceEquals(next, Present) || next == null) return;
            var now = nowMs ?? NowMs();
            var continues = coalesce != null && _last.HasValue && _last.Value.key == coalesce && now - _last.Value.at < CoalesceMs;
            if (!continues)
            {
                _past.Add(Present);
                if (_past.Count > Limit) _past.RemoveRange(0, _past.Count - Limit);
            }
            Present = next;
            _future.Clear();
            _last = coalesce != null ? (coalesce, now) : ((string, long)?)null;
            Changed?.Invoke(Present);
        }

        /// <summary>A change that is not the user's — kept, but never undone.</summary>
        public void Replace(PipelineGraph next)
        {
            if (next == null) return;
            Present = next;
            Changed?.Invoke(Present);
        }

        public bool Undo()
        {
            if (_past.Count == 0) return false;
            var previous = _past[_past.Count - 1];
            _past.RemoveAt(_past.Count - 1);
            _future.Insert(0, Present);
            Present = previous;
            _last = null;
            Changed?.Invoke(Present);
            return true;
        }

        public bool Redo()
        {
            if (_future.Count == 0) return false;
            var next = _future[0];
            _future.RemoveAt(0);
            _past.Add(Present);
            Present = next;
            _last = null;
            Changed?.Invoke(Present);
            return true;
        }
    }
}
