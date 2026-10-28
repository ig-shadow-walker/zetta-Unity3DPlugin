using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Pipelines.UI
{
    /// <summary>What one step's card shows (the web's StepNodeData).</summary>
    public sealed class NodeCardData
    {
        public PipelineNode Node;
        public string Summary = "";
        /// <summary>One run of it, in credits; null while prices load.</summary>
        public int? Price;
        /// <summary>Runs once per part — the price is per part.</summary>
        public bool PerPart;
        public List<string> Problems = new List<string>();
        public List<string> Warnings = new List<string>();
        /// <summary>In a run: what it is doing. Null in the recipe.</summary>
        public NodeRunState Run;
        /// <summary>In a run: what it made, as small pictures.</summary>
        public List<(string url, string label)> Outputs = new List<(string, string)>();
        /// <summary>In a run: the seed this run started from.</summary>
        public (string url, string name)? Seed;
        /// <summary>In the recipe: its input is given on each run.</summary>
        public bool PerRun;
        /// <summary>In a draft run: what this step still needs before Start; Flash after a refused Start.</summary>
        public string NeedsInput;
        public bool Flash;
    }

    /// <summary>
    /// THE GRAPH, DRAWN (pipeline-canvas.tsx) — a GraphView over the pipeline's own graph, which stays the one source of
    /// truth: every move, connection and removal here is raised as a request; the owner applies it to the graph (through
    /// <see cref="GraphEditing"/>) and calls <see cref="Show"/> again. The canvas only ever draws what the graph says.
    /// Re-syncs are incremental: a node keeps its view (and its place, and the user's selection) while it exists.
    /// </summary>
    public sealed class PipelineCanvas : GraphView
    {
        public const string DragKindKey = "Zetta3D.PipelineNodeKind";

        readonly bool _readOnly;
        readonly Dictionary<string, StepNodeView> _nodes = new Dictionary<string, StepNodeView>();
        readonly Dictionary<string, StepEdge> _edges = new Dictionary<string, StepEdge>();
        readonly Label _emptyHint;
        PipelineGraph _graph = new PipelineGraph();
        Func<string, NodeCardData> _dataFor = _ => null;
        bool _showEmptyHint = true;
        bool _framed;
        bool _syncing;
        IVisualElementScheduledItem _selectionTick;
        string _lastSelection;

        /// <summary>The selected step (one), or null — raised once per change.</summary>
        public event Action<string> SelectionChanged;
        /// <summary>(source, sourceHandle, target, targetHandle) — a wire dropped on a compatible input.</summary>
        public event Action<string, string, string, string> ConnectRequested;
        /// <summary>(node ids, edge ids) the user deleted.</summary>
        public event Action<List<string>, List<string>> RemoveRequested;
        /// <summary>(edge id, source, sourceHandle, target, targetHandle) — a connection's end dragged to another socket.</summary>
        public event Action<string, string, string, string, string> ReconnectRequested;
        /// <summary>Why a wire dropped on a socket was refused — said, rather than nothing happening.</summary>
        public event Action<string> Refused;
        /// <summary>Nodes dragged to new places.</summary>
        public event Action<Dictionary<string, NodePosition>> MoveRequested;
        /// <summary>A palette kind dropped on the canvas at a place in graph coordinates.</summary>
        public event Action<string, NodePosition> DropRequested;

        public PipelineGraph Graph => _graph;
        public bool ReadOnly => _readOnly;

        public PipelineCanvas(bool readOnly = false)
        {
            _readOnly = readOnly;
            AddToClassList("z-pl-canvas");
            focusable = true;
            style.flexGrow = 1;

            SetupZoom(0.25f, 2f);
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());
            var grid = new GridBackground();
            grid.AddToClassList("z-pl-grid");
            Insert(0, grid);
            grid.StretchToParentSize();

            _emptyHint = new Label("Drag a seed in from the left to start.").Classes("z-pl-canvas-hint");
            _emptyHint.pickingMode = PickingMode.Ignore;
            Add(_emptyHint);
            _emptyHint.Visible(false);

            graphViewChanged = OnGraphViewChanged;
            // Nothing is copied or pasted: a step is added from the palette, so its settings are always the Studio's.
            serializeGraphElements = _ => null;
            canPasteSerializedData = _ => false;
            if (readOnly) deleteSelection = (_, __) => { };

            RegisterCallback<DragUpdatedEvent>(OnDragUpdated);
            RegisterCallback<DragPerformEvent>(OnDragPerform);
            RegisterCallback<GeometryChangedEvent>(_ => FrameOnce());
        }

        // ─── Drawing the graph ─────────────────────────────────────────────────────

        /// <summary>
        /// Draws <paramref name="graph"/>: adds, updates and removes node and edge views to match it. Positions are set
        /// from the graph (a node being dragged already sits where it will be committed). Cheap enough for every edit.
        /// </summary>
        public void Show(PipelineGraph graph, Func<string, NodeCardData> dataFor, bool showEmptyHint = true)
        {
            _graph = graph ?? new PipelineGraph();
            _dataFor = dataFor ?? (_ => null);
            _showEmptyHint = showEmptyHint;
            _syncing = true;
            try
            {
                var ids = new HashSet<string>(_graph.Nodes.Select(n => n.Id));
                foreach (var gone in _nodes.Keys.Where(id => !ids.Contains(id)).ToList())
                {
                    var view = _nodes[gone];
                    foreach (var edge in _edges.Where(p => p.Value.input?.node == view || p.Value.output?.node == view).Select(p => p.Key).ToList())
                        RemoveEdgeView(edge);
                    RemoveElement(view);
                    _nodes.Remove(gone);
                }

                foreach (var node in _graph.Nodes)
                {
                    var data = dataFor(node.Id) ?? new NodeCardData { Node = node };
                    data.Node = node;
                    if (!_nodes.TryGetValue(node.Id, out var view))
                    {
                        view = new StepNodeView(this, node.Id);
                        _nodes[node.Id] = view;
                        AddElement(view);
                    }
                    var portsChanged = view.Bind(data, _graph);
                    var rect = view.GetPosition();
                    if (Math.Abs(rect.x - (float)node.Position.X) > 0.01f || Math.Abs(rect.y - (float)node.Position.Y) > 0.01f)
                        view.SetPosition(new Rect((float)node.Position.X, (float)node.Position.Y, rect.width, rect.height));
                    if (portsChanged)
                        foreach (var edge in _edges.Where(p => p.Value.input?.node == view || p.Value.output?.node == view).Select(p => p.Key).ToList())
                            RemoveEdgeView(edge);
                }

                var edgeIds = new HashSet<string>(_graph.Edges.Select(e => e.Id));
                foreach (var gone in _edges.Keys.Where(id => !edgeIds.Contains(id)).ToList()) RemoveEdgeView(gone);
                foreach (var edge in _graph.Edges)
                {
                    if (!_nodes.TryGetValue(edge.Source, out var from) || !_nodes.TryGetValue(edge.Target, out var to)) continue;
                    var output = from.OutputPort(edge.SourceHandle);
                    var input = to.InputPort(edge.TargetHandle);
                    if (output == null || input == null)
                    {
                        RemoveEdgeView(edge.Id);
                        continue;
                    }
                    if (_edges.TryGetValue(edge.Id, out var existing))
                    {
                        if (existing.output == output && existing.input == input)
                        {
                            existing.SetColour(PortColour(NodeCatalog.OutputType(_graph.Node(edge.Source)?.Kind, edge.SourceHandle)));
                            existing.SetRunning(_running.Contains(edge.Target));
                            continue;
                        }
                        RemoveEdgeView(edge.Id);
                    }
                    var view = new StepEdge { EdgeId = edge.Id, Canvas = _readOnly ? null : this, output = output, input = input };
                    view.SetColour(PortColour(NodeCatalog.OutputType(_graph.Node(edge.Source)?.Kind, edge.SourceHandle)));
                    view.SetRunning(_running.Contains(edge.Target));
                    if (_readOnly) view.capabilities &= ~(Capabilities.Deletable | Capabilities.Selectable);
                    output.Connect(view);
                    input.Connect(view);
                    AddElement(view);
                    _edges[edge.Id] = view;
                }
            }
            finally
            {
                _syncing = false;
            }
            _emptyHint.Visible(showEmptyHint && _graph.Nodes.Count == 0 && !_readOnly);
            FrameOnce();
        }

        readonly HashSet<string> _running = new HashSet<string>();

        /// <summary>
        /// Draws the last graph again — after GraphView moved a wire on its own (a wire dragged off its socket, a drop
        /// the graph refused), so what is on screen is always what the graph says.
        /// </summary>
        public void Resync() => schedule.Execute(() => Show(_graph, _dataFor, _showEmptyHint));

        /// <summary>The nodes with a step running, whose incoming wire is drawn lit (call before <see cref="Show"/>).</summary>
        public void SetRunning(IEnumerable<string> nodeIds)
        {
            _running.Clear();
            if (nodeIds != null) _running.UnionWith(nodeIds);
        }

        void RemoveEdgeView(string id)
        {
            if (!_edges.TryGetValue(id, out var view)) return;
            view.input?.Disconnect(view);
            view.output?.Disconnect(view);
            RemoveElement(view);
            _edges.Remove(id);
        }

        public static Color PortColour(string type) => type == null ? new Color(0.33f, 0.3f, 0.26f) : PortTypes.Colour(type);

        void FrameOnce()
        {
            if (_framed || _graph.Nodes.Count == 0 || float.IsNaN(layout.width) || layout.width < 10) return;
            _framed = true;
            // After the nodes have their size.
            schedule.Execute(FrameGraph).StartingIn(30);
        }

        /// <summary>
        /// Fits every step in view, never zoomed in past 100% (FrameAll would blow a lone seed up) — worked out here
        /// rather than read back from the view transform, whose accessors differ between 2022.3 and Unity 6.
        /// </summary>
        public void FrameGraph()
        {
            if (_nodes.Count == 0 || float.IsNaN(layout.width) || layout.width < 10) return;
            Rect? bounds = null;
            foreach (var view in _nodes.Values)
            {
                var r = view.GetPosition();
                var w = float.IsNaN(r.width) || r.width < 1 ? 232 : r.width;
                var h = float.IsNaN(r.height) || r.height < 1 ? 120 : r.height;
                var rect = new Rect(r.x, r.y, w, h);
                bounds = bounds == null ? rect : Rect.MinMaxRect(Mathf.Min(bounds.Value.xMin, rect.xMin), Mathf.Min(bounds.Value.yMin, rect.yMin),
                    Mathf.Max(bounds.Value.xMax, rect.xMax), Mathf.Max(bounds.Value.yMax, rect.yMax));
            }
            var b = bounds.Value;
            const float pad = 48;
            var scale = Mathf.Clamp(Mathf.Min((layout.width - 2 * pad) / b.width, (layout.height - 2 * pad) / b.height), 0.25f, 1f);
            var position = new Vector3(layout.width / 2 - b.center.x * scale, layout.height / 2 - b.center.y * scale, 0);
            UpdateViewTransform(position, new Vector3(scale, scale, 1));
        }

        /// <summary>Frames the graph again the next time it has a size (a newly opened pipeline).</summary>
        public void Reframe()
        {
            _framed = false;
            FrameOnce();
        }

        // ─── Selection ─────────────────────────────────────────────────────────────

        /// <summary>Selects one step (or none) without raising <see cref="SelectionChanged"/> for it.</summary>
        public void Select(string nodeId)
        {
            var current = SelectedNodeId;
            if (current == nodeId) return;
            _syncing = true;
            try
            {
                ClearSelection();
                if (nodeId != null && _nodes.TryGetValue(nodeId, out var view)) AddToSelection(view);
            }
            finally
            {
                _syncing = false;
            }
            _lastSelection = nodeId;
        }

        public string SelectedNodeId => selection.OfType<StepNodeView>().Select(v => v.NodeId).FirstOrDefault();

        public override void AddToSelection(ISelectable selectable)
        {
            base.AddToSelection(selectable);
            SelectionTick();
        }

        public override void RemoveFromSelection(ISelectable selectable)
        {
            base.RemoveFromSelection(selectable);
            SelectionTick();
        }

        public override void ClearSelection()
        {
            base.ClearSelection();
            SelectionTick();
        }

        void SelectionTick()
        {
            if (_syncing) return;
            _selectionTick?.Pause();
            // GraphView clears then adds on a click: report where it settles, once.
            _selectionTick = schedule.Execute(() =>
            {
                var now = SelectedNodeId;
                if (now == _lastSelection) return;
                _lastSelection = now;
                SelectionChanged?.Invoke(now);
            });
        }

        public StepNodeView ViewOf(string nodeId) => nodeId != null && _nodes.TryGetValue(nodeId, out var view) ? view : null;

        // ─── Edits the user makes on the canvas, raised as requests ─────────────────

        GraphViewChange OnGraphViewChanged(GraphViewChange change)
        {
            if (_syncing) return change;
            if (_readOnly)
            {
                change.elementsToRemove?.Clear();
                change.edgesToCreate?.Clear();
                return change;
            }

            if (change.elementsToRemove is { Count: > 0 })
            {
                var nodes = change.elementsToRemove.OfType<StepNodeView>().Select(v => v.NodeId).ToList();
                var edges = change.elementsToRemove.OfType<StepEdge>().Select(e => e.EdgeId).Where(id => id != null).ToList();
                change.elementsToRemove.Clear();
                if (nodes.Count > 0 || edges.Count > 0) RemoveRequested?.Invoke(nodes, edges);
            }

            if (change.edgesToCreate is { Count: > 0 })
            {
                foreach (var edge in change.edgesToCreate) RequestConnect(edge);
                change.edgesToCreate.Clear();
            }

            if (change.movedElements is { Count: > 0 })
            {
                var moved = new Dictionary<string, NodePosition>();
                foreach (var view in change.movedElements.OfType<StepNodeView>())
                {
                    var rect = view.GetPosition();
                    moved[view.NodeId] = new NodePosition(Math.Round(rect.x), Math.Round(rect.y));
                }
                if (moved.Count > 0) MoveRequested?.Invoke(moved);
            }
            return change;
        }

        internal void RequestConnect(Edge edge)
        {
            if (_readOnly || !(edge?.output is StepPort output) || !(edge.input is StepPort input)) return;
            // An existing connection with an end moved: one change, the old wire replaced by the new.
            if (edge is StepEdge { EdgeId: { } moved } && _graph.Edge(moved) is { } old)
            {
                if (old.Source == output.NodeId && old.SourceHandle == output.Handle && old.Target == input.NodeId && old.TargetHandle == input.Handle) return;
                ReconnectRequested?.Invoke(moved, output.NodeId, output.Handle, input.NodeId, input.Handle);
                return;
            }
            if (!CanvasModel.MayWire(_graph, output.NodeId, output.Handle, input.NodeId, input.Handle))
            {
                Refuse(output.NodeId, output.Handle, input.NodeId, input.Handle, null);
                return;
            }
            ConnectRequested?.Invoke(output.NodeId, output.Handle, input.NodeId, input.Handle);
        }

        /// <summary>
        /// A wire let go away from a socket it could join. Over a socket that cannot take it, says why; on empty canvas,
        /// a connection being moved is taken away (a new wire just disappears).
        /// </summary>
        internal void DroppedOutside(Edge edge, Vector2 position)
        {
            if (_readOnly || edge == null) return;
            var existing = (edge as StepEdge)?.EdgeId;
            var hit = PortAt(position);
            if (hit != null)
            {
                var from = edge.output as StepPort;
                var to = edge.input as StepPort;
                if (hit.direction == Direction.Input) to = hit;
                else from = hit;
                if (from != null && to != null && from != to) Refuse(from.NodeId, from.Handle, to.NodeId, to.Handle, existing);
                return;
            }
            if (existing != null && _graph.Edge(existing) != null) RemoveRequested?.Invoke(new List<string>(), new List<string> { existing });
        }

        void Refuse(string source, string sourceHandle, string target, string targetHandle, string movingEdgeId)
        {
            var graph = movingEdgeId == null ? _graph : new PipelineGraph { Nodes = _graph.Nodes, Edges = _graph.Edges.Where(e => e.Id != movingEdgeId).ToList() };
            var without = new PipelineGraph { Nodes = graph.Nodes, Edges = graph.Edges.Where(e => !(e.Target == target && e.TargetHandle == targetHandle)).ToList() };
            var problem = GraphRules.ConnectionProblem(without, source, sourceHandle, target, targetHandle);
            if (problem != null) Refused?.Invoke(problem);
        }

        /// <summary>The socket under a point in world space, if any (a little slack around each, as a dot is small).</summary>
        StepPort PortAt(Vector2 world)
        {
            foreach (var view in _nodes.Values)
            foreach (var port in view.Ports)
            {
                var rect = port.worldBound;
                rect.xMin -= 4;
                rect.xMax += 4;
                rect.yMin -= 4;
                rect.yMax += 4;
                if (rect.Contains(world)) return port;
            }
            return null;
        }

        /// <summary>The ✕ on a selected connection.</summary>
        internal void RemoveEdge(string edgeId)
        {
            if (_readOnly || edgeId == null) return;
            RemoveRequested?.Invoke(new List<string>(), new List<string> { edgeId });
        }

        /// <summary>The inputs a dragged wire may land on: what <see cref="GraphRules.CanConnect"/> allows — types match, no loop.</summary>
        public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter)
        {
            var result = new List<Port>();
            if (_readOnly || !(startPort is StepPort start)) return result;
            foreach (var view in _nodes.Values)
            {
                foreach (var port in view.Ports)
                {
                    if (port.direction == start.direction || port.NodeId == start.NodeId) continue;
                    var ok = start.direction == Direction.Output
                        ? CanConnectIgnoringOccupied(start.NodeId, start.Handle, port.NodeId, port.Handle)
                        : CanConnectIgnoringOccupied(port.NodeId, port.Handle, start.NodeId, start.Handle);
                    if (ok) result.Add(port);
                }
            }
            return result;
        }

        /// <summary>An occupied input is fine — connecting into it replaces the wire already there.</summary>
        bool CanConnectIgnoringOccupied(string source, string sourceHandle, string target, string targetHandle) =>
            CanvasModel.MayWire(_graph, source, sourceHandle, target, targetHandle);

        // ─── Dropping a palette step ───────────────────────────────────────────────

        static string DraggedKind => DragAndDrop.GetGenericData(DragKindKey) as string;

        void OnDragUpdated(DragUpdatedEvent evt)
        {
            if (_readOnly || DraggedKind == null) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            evt.StopPropagation();
        }

        void OnDragPerform(DragPerformEvent evt)
        {
            var kind = DraggedKind;
            if (_readOnly || kind == null) return;
            DragAndDrop.AcceptDrag();
            var local = contentViewContainer.WorldToLocal(evt.mousePosition);
            // The card's middle-top under the pointer, as the web drops it.
            DropRequested?.Invoke(kind, new NodePosition(Math.Round(local.x - 110), Math.Round(local.y - 20)));
            evt.StopPropagation();
        }
    }

    /// <summary>One socket as the canvas draws it.</summary>
    public readonly struct Socket
    {
        public readonly string Handle;
        public readonly string Label;
        /// <summary>What it carries (an input: the first type it accepts); null for a kind this version does not know.</summary>
        public readonly string Type;

        public Socket(string handle, string label, string type)
        {
            Handle = handle;
            Label = label;
            Type = type;
        }
    }

    /// <summary>
    /// What the canvas draws for a graph, worked out without any UI — the sockets of each step and the wires that can be
    /// drawn between them — so the sync can be checked headless.
    /// </summary>
    public static class CanvasModel
    {
        /// <summary>
        /// A step's sockets: the catalogue's (Isolate Parts' learnt <c>part:&lt;key&gt;</c> outputs included); for a kind
        /// this version does not know, the ones its wires use, uncoloured.
        /// </summary>
        public static (List<Socket> inputs, List<Socket> outputs) SocketsOf(PipelineNode node, PipelineGraph graph)
        {
            var spec = NodeCatalog.Spec(node.Kind);
            if (spec != null)
                return (spec.Inputs.Select(i => new Socket(i.Handle, i.Label + (i.Required ? "" : " (optional)"), i.Accepts.FirstOrDefault())).ToList(),
                    NodeCatalog.OutputsOf(node).Select(o => new Socket(o.Handle, o.Label, o.Type)).ToList());
            return (graph.Edges.Where(e => e.Target == node.Id).Select(e => e.TargetHandle).Distinct().Select(h => new Socket(h, h, null)).ToList(),
                graph.Edges.Where(e => e.Source == node.Id).Select(e => e.SourceHandle).Distinct().Select(h => new Socket(h, h, null)).ToList());
        }

        public static string Signature(List<Socket> inputs, List<Socket> outputs) =>
            string.Join("|", inputs.Select(i => "i:" + i.Handle + ":" + i.Label + ":" + i.Type)) + "#" +
            string.Join("|", outputs.Select(o => "o:" + o.Handle + ":" + o.Label + ":" + o.Type));

        /// <summary>The wires the canvas draws: those whose two ends are sockets it shows.</summary>
        public static List<PipelineEdge> DrawnEdges(PipelineGraph graph) => graph.Edges.Where(edge =>
        {
            var from = graph.Node(edge.Source);
            var to = graph.Node(edge.Target);
            if (from == null || to == null) return false;
            return SocketsOf(from, graph).outputs.Any(s => s.Handle == edge.SourceHandle) && SocketsOf(to, graph).inputs.Any(s => s.Handle == edge.TargetHandle);
        }).ToList();

        /// <summary>Whether a dragged wire may land there — an occupied input is fine: the new wire replaces the old.</summary>
        public static bool MayWire(PipelineGraph graph, string source, string sourceHandle, string target, string targetHandle)
        {
            var without = new PipelineGraph { Nodes = graph.Nodes, Edges = graph.Edges.Where(e => !(e.Target == target && e.TargetHandle == targetHandle)).ToList() };
            return GraphRules.CanConnect(without, source, sourceHandle, target, targetHandle);
        }
    }

    /// <summary>One socket, carrying its node and handle so a wire can be turned back into the graph's words.</summary>
    public sealed class StepPort : Port
    {
        public string NodeId;
        public string Handle;
        public string Carries;

        public StepPort(PipelineCanvas canvas, Direction direction, string nodeId, string handle, string type, string label, bool interactive)
            : base(Orientation.Horizontal, direction, direction == Direction.Input ? Capacity.Single : Capacity.Multi, typeof(object))
        {
            NodeId = nodeId;
            Handle = handle;
            Carries = type;
            portName = label;
            portColor = PipelineCanvas.PortColour(type);
            AddToClassList("z-pl-port");
            if (interactive)
            {
                m_EdgeConnector = new EdgeConnector<StepEdge>(new Listener(canvas));
                this.AddManipulator(m_EdgeConnector);
            }
            else pickingMode = PickingMode.Ignore;
        }

        /// <summary>A dropped wire becomes a request, never an edge of its own: the graph draws the edge.</summary>
        sealed class Listener : IEdgeConnectorListener
        {
            readonly PipelineCanvas _canvas;
            public Listener(PipelineCanvas canvas) => _canvas = canvas;
            public void OnDropOutsidePort(Edge edge, Vector2 position)
            {
                _canvas.DroppedOutside(edge, position);
                _canvas.Resync();
            }

            public void OnDrop(GraphView graphView, Edge edge)
            {
                _canvas.RequestConnect(edge);
                _canvas.Resync();
            }
        }
    }

    /// <summary>
    /// A wire, in the colour of what it carries (the web strokes it with the output's type). Selected, it is drawn
    /// thicker with a ✕ at its middle; Delete removes it too, and either end can be dragged to another socket — or off
    /// onto empty canvas, to remove it.
    /// </summary>
    public sealed class StepEdge : Edge
    {
        public string EdgeId;
        /// <summary>The canvas that drew it, when it may be edited; null for a wire being dragged or a read-only run.</summary>
        public PipelineCanvas Canvas;
        Color _colour = Color.gray;
        bool _running;
        Button _remove;

        public void SetColour(Color colour)
        {
            _colour = colour;
            UpdateEdgeControl();
        }

        public void SetRunning(bool running)
        {
            if (_running == running) return;
            _running = running;
            EnableInClassList("z-pl-edge--running", running);
            UpdateEdgeControl();
        }

        public override bool UpdateEdgeControl()
        {
            var result = base.UpdateEdgeControl();
            if (edgeControl == null) return result;
            if (!selected)
            {
                var colour = _running ? Color.Lerp(_colour, Color.white, 0.35f) : _colour;
                edgeControl.inputColor = colour;
                edgeControl.outputColor = colour;
                edgeControl.edgeWidth = _running ? 3 : 2;
            }
            else edgeControl.edgeWidth = 4;
            PlaceRemove();
            return result;
        }

        public override void OnSelected()
        {
            base.OnSelected();
            PlaceRemove();
        }

        public override void OnUnselected()
        {
            base.OnUnselected();
            PlaceRemove();
        }

        /// <summary>The ✕ sits at the wire's middle while it is selected.</summary>
        void PlaceRemove()
        {
            if (Canvas == null || EdgeId == null || edgeControl == null) return;
            if (_remove == null)
            {
                _remove = new Button(() => Canvas?.RemoveEdge(EdgeId)) { text = "✕", tooltip = "Remove this connection (Delete)" };
                _remove.AddToClassList("z-pl-edge-remove");
                Add(_remove);
            }
            _remove.style.display = selected ? DisplayStyle.Flex : DisplayStyle.None;
            if (!selected) return;
            var middle = (edgeControl.from + edgeControl.to) / 2f;
            _remove.style.left = middle.x - 10;
            _remove.style.top = middle.y - 10;
        }
    }

    /// <summary>
    /// ONE STEP ON THE CANVAS (pipeline-node.tsx): the tool's mark in its area's hue, its name and what it is set to,
    /// what it costs, and — during a run — what it is doing. Inputs on the left, outputs on the right (Isolate Parts'
    /// Character, Each part and one per part it learnt), each coloured by what it carries.
    /// </summary>
    public sealed class StepNodeView : Node
    {
        public readonly string NodeId;
        internal readonly PipelineCanvas Owner;
        readonly List<StepPort> _inputs = new List<StepPort>();
        readonly List<StepPort> _outputs = new List<StepPort>();
        string _portSignature;

        readonly VisualElement _icon;
        readonly Label _name;
        readonly Label _mark;
        readonly Label _summary;
        readonly Label _note;
        readonly VisualElement _seed;
        readonly VisualElement _seedThumb;
        readonly Label _seedName;
        readonly VisualElement _thumbs;
        readonly Label _status;
        readonly Label _price;
        string _kind;

        public IEnumerable<StepPort> Ports => _inputs.Concat(_outputs);

        public StepNodeView(PipelineCanvas owner, string nodeId)
        {
            Owner = owner;
            NodeId = nodeId;
            AddToClassList("z-pl-node");
            titleButtonContainer.Clear();
            titleButtonContainer.Visible(false);
            if (owner.ReadOnly) capabilities &= ~(Capabilities.Movable | Capabilities.Deletable | Capabilities.Copiable | Capabilities.Renamable);
            else capabilities &= ~(Capabilities.Copiable | Capabilities.Renamable | Capabilities.Collapsible);

            // The title row: mark, name over summary, status mark.
            var titleLabel = titleContainer.Q<Label>("title-label");
            titleLabel?.Visible(false);
            var head = new VisualElement().Classes("z-pl-node-head");
            _icon = new VisualElement();
            head.Add(_icon);
            var text = new VisualElement().Classes("z-pl-node-text");
            _name = new Label().Classes("z-pl-node-name");
            _summary = new Label().Classes("z-pl-node-summary");
            text.Add(_name);
            text.Add(_summary);
            head.Add(text);
            _mark = new Label().Classes("z-pl-node-mark");
            head.Add(_mark);
            titleContainer.Insert(0, head);

            var body = new VisualElement().Classes("z-pl-node-body");
            _note = new Label().Classes("z-pl-node-note");
            body.Add(_note);
            _seed = new VisualElement().Classes("z-pl-node-seed");
            _seedThumb = new VisualElement().Classes("z-pl-thumb", "z-pl-thumb--seed");
            _seedName = new Label().Classes("z-pl-node-seed-name");
            _seed.Add(_seedThumb);
            _seed.Add(_seedName);
            body.Add(_seed);
            mainContainer.Insert(1, body);

            _thumbs = new VisualElement().Classes("z-pl-node-thumbs");
            extensionContainer.Add(_thumbs);
            var footer = new VisualElement().Classes("z-pl-node-footer");
            _status = new Label().Classes("z-pl-node-status");
            _price = new Label().Classes("z-pl-node-price");
            footer.Add(_status);
            footer.Add(_price);
            extensionContainer.Add(footer);
            expanded = true;
        }

        public StepPort InputPort(string handle) => _inputs.FirstOrDefault(p => p.Handle == handle);
        public StepPort OutputPort(string handle) => _outputs.FirstOrDefault(p => p.Handle == handle);

        /// <summary>Shows a step's data. Returns true when its sockets were rebuilt (the caller re-wires its edges).</summary>
        public bool Bind(NodeCardData data, PipelineGraph graph)
        {
            var node = data.Node;
            var spec = NodeCatalog.Spec(node.Kind);
            var notHere = NodeCatalog.NotAvailableInUnity(node.Kind);

            if (_kind != node.Kind)
            {
                _kind = node.Kind;
                _icon.Clear();
                _icon.Add(PipelineUi.HueIcon(node.Kind));
            }
            _name.text = node.DisplayName;
            tooltip = spec?.Blurb ?? "";
            _summary.text = notHere ? "Not available in Unity" : data.Summary;
            EnableInClassList("z-pl-node--na", notHere);

            // The status mark beside the name (StatusMark).
            var state = data.Run?.Status ?? "idle";
            var issues = data.Problems.Count + data.Warnings.Count;
            string mark = null, markTone = null, markTip = null;
            if (data.NeedsInput != null) (mark, markTone, markTip) = ("!", "amber", "Needs input");
            else if (state == StepStatuses.Running) (mark, markTone, markTip) = ("…", "running", "Running");
            else if (state == StepStatuses.Success) (mark, markTone, markTip) = ("✓", "done", "Done");
            else if (state == StepStatuses.Failed || state == StepStatuses.Cancelled) (mark, markTone, markTip) = ("✕", "failed", NodeRunState.StatusLabel(state));
            else if (state == StepStatuses.Waiting || state == StepStatuses.Review) (mark, markTone, markTip) = ("||", "amber", NodeRunState.StatusLabel(state));
            else if (issues > 0) (mark, markTone, markTip) = ("!", "amber", $"{issues} to fix:\n" + string.Join("\n", data.Problems.Concat(data.Warnings)));
            _mark.text = mark ?? "";
            _mark.tooltip = markTip ?? "";
            foreach (var tone in new[] { "amber", "running", "done", "failed" }) _mark.EnableInClassList("z-pl-node-mark--" + tone, markTone == tone);
            _mark.Visible(mark != null);

            // A step at work is lit in its hue; one waiting for the user (or needing input) wears amber.
            EnableInClassList("z-pl-node--running", state == StepStatuses.Running);
            EnableInClassList("z-pl-node--waiting", data.NeedsInput != null || state == StepStatuses.Waiting || state == StepStatuses.Review);
            EnableInClassList("z-pl-node--flash", data.NeedsInput != null && data.Flash);
            var hue = PipelineUi.HueOf(node.Kind);
            var edgeColour = state == StepStatuses.Running ? hue.Edge : (data.NeedsInput != null || state == StepStatuses.Waiting || state == StepStatuses.Review) ? PipelineUi.Amber.Edge : (Color?)null;
            var border = this.Q("node-border") ?? mainContainer;
            if (edgeColour is { } c) border.style.borderTopColor = border.style.borderBottomColor = border.style.borderLeftColor = border.style.borderRightColor = c;
            else border.style.borderTopColor = border.style.borderBottomColor = border.style.borderLeftColor = border.style.borderRightColor = StyleKeyword.Null;

            // Needs input / set on each run.
            if (data.NeedsInput != null)
            {
                var colon = data.NeedsInput.IndexOf(": ", StringComparison.Ordinal);
                _note.text = "Needs input — " + (colon >= 0 ? data.NeedsInput.Substring(colon + 2) : data.NeedsInput);
                _note.EnableInClassList("z-pl-node-note--amber", true);
                _note.Visible(true);
            }
            else if (data.PerRun)
            {
                _note.text = "↻ Set on each run";
                _note.EnableInClassList("z-pl-node-note--amber", false);
                _note.Visible(true);
            }
            else _note.Visible(false);

            // The seed this run started from.
            if (data.Seed is { } seed && (seed.url != null || seed.name != null))
            {
                _seed.Visible(true);
                _seedThumb.style.backgroundImage = StyleKeyword.Null;
                if (!string.IsNullOrEmpty(seed.url)) ZettaUI.LoadInto(_seedThumb, seed.url);
                _seedName.text = seed.name ?? "";
            }
            else _seed.Visible(false);

            // What it made, as up to 4 pictures and a count.
            _thumbs.Clear();
            foreach (var output in data.Outputs.Take(4))
            {
                var thumb = PipelineUi.Thumb(output.url, "z-pl-thumb--mini");
                thumb.tooltip = output.label;
                _thumbs.Add(thumb);
            }
            if (data.Outputs.Count > 4) _thumbs.Add(new Label($"+{data.Outputs.Count - 4}").Classes("z-pl-thumb", "z-pl-thumb--mini", "z-pl-thumb--more"));
            _thumbs.Visible(data.Outputs.Count > 0);

            // Footer: what it does or is doing, and what it costs.
            if (data.NeedsInput != null) _status.text = "Needs input";
            else if (data.Run != null && data.Run.Status != "idle")
                _status.text = data.Run.Total > 1 ? $"{data.Run.Label} · {data.Run.Done}/{data.Run.Total}" : data.Run.Label;
            else if (notHere) _status.text = "Not available in Unity";
            else _status.text = spec?.ThreeD == true ? "Queued as a 3D job" : "Instant";
            _price.text = data.Run != null && data.Run.Credits > 0 ? $"{data.Run.Credits} cr" : RequestBuilder.PriceLabel(data.Price, data.PerPart);

            var rebuilt = BindPorts(node, graph);
            RefreshExpandedState();
            return rebuilt;
        }

        /// <summary>Sockets from the catalogue (and learnt parts); an unknown kind gets the ones its wires use, uncoloured.</summary>
        bool BindPorts(PipelineNode node, PipelineGraph graph)
        {
            var (inputs, outputs) = CanvasModel.SocketsOf(node, graph);
            var signature = CanvasModel.Signature(inputs, outputs);
            if (signature == _portSignature) return false;
            _portSignature = signature;

            inputContainer.Clear();
            outputContainer.Clear();
            _inputs.Clear();
            _outputs.Clear();
            var interactive = !Owner.ReadOnly;
            foreach (var socket in inputs)
            {
                var port = new StepPort(Owner, Direction.Input, node.Id, socket.Handle, socket.Type, socket.Label, interactive);
                _inputs.Add(port);
                inputContainer.Add(port);
            }
            foreach (var socket in outputs)
            {
                var port = new StepPort(Owner, Direction.Output, node.Id, socket.Handle, socket.Type, socket.Label, interactive);
                if (socket.Handle.StartsWith(NodeCatalog.PartHandlePrefix, StringComparison.Ordinal)) port.AddToClassList("z-pl-port--part");
                _outputs.Add(port);
                outputContainer.Add(port);
            }
            RefreshPorts();
            return true;
        }
    }
}
