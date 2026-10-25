using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Pipelines.UI
{
    /// <summary>
    /// THE SELECTED STEP'S SETTINGS (node-settings.tsx): its mark, its name (≤80, the kind's label as placeholder), the
    /// blurb, Remove, what stops it running and what may go wrong, then the Studio form for its tool. With no step
    /// selected, the Recipe overview. Built once per selected step, refreshed in place on every change.
    /// </summary>
    public sealed class NodeInspector : VisualElement
    {
        /// <summary>(nodeId, patch) — a settings change.</summary>
        public event Action<string, JObject> PatchRequested;
        /// <summary>(nodeId, label) — the step renamed.</summary>
        public event Action<string, string> LabelChanged;
        public event Action<string> RemoveRequested;
        public event Action ShowRunsRequested;

        readonly Label _heading;
        readonly ScrollView _scroll;
        readonly VisualElement _overlayHost;
        string _nodeId;
        string _kind;
        FormContext _form;
        TextField _name;
        VisualElement _notes;
        Label _overviewLead;
        Button _runsButton;

        public NodeInspector(VisualElement overlayHost)
        {
            _overlayHost = overlayHost;
            AddToClassList("z-pl-inspector");
            _heading = new Label("Recipe").Classes("z-pl-panel-title");
            Add(_heading);
            _scroll = new ScrollView(ScrollViewMode.Vertical).Classes("z-pl-inspector-scroll");
            Add(_scroll);
            // Subscribed while on screen: the recipe is taken off screen while Runs is shown, and put back.
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                FormContext.Studio.Changed -= OnStudioChanged;
                FormContext.Studio.Changed += OnStudioChanged;
            });
            RegisterCallback<DetachFromPanelEvent>(_ => FormContext.Studio.Changed -= OnStudioChanged);
        }

        void OnStudioChanged() => _form?.Refresh();

        public string NodeId => _nodeId;

        /// <summary>Shows the step (or the overview when null) as the graph now has it.</summary>
        public void Show(PipelineGraph graph, string nodeId, List<PipelineProblem> problems, int runCount)
        {
            var node = graph.Node(nodeId);
            if (node == null)
            {
                if (_nodeId != null || _overviewLead == null) BuildOverview();
                _nodeId = null;
                _kind = null;
                _overviewLead.text = graph.Nodes.Count == 0 ? "Start with a seed." : "Select a step to set it up.";
                _runsButton.text = $"See its {runCount} run{(runCount == 1 ? "" : "s")}";
                _runsButton.Visible(runCount > 0);
                return;
            }

            if (_nodeId != node.Id || _kind != node.Kind) Build(node, graph);
            _nodeId = node.Id;
            _kind = node.Kind;

            var label = node.Label ?? "";
            if (_name.value != label) _name.SetValueWithoutNotify(label);

            _notes.Clear();
            var own = problems.Where(p => p.NodeId == node.Id).Select(p => p.Message).Distinct().ToList();
            if (own.Count > 0) _notes.Add(PipelineUi.Note(string.Join("\n", own), "error"));
            foreach (var warning in GraphRules.Warnings(graph, node.Id)) _notes.Add(PipelineUi.Note(warning));

            _form?.Refresh(node, graph);
        }

        void BuildOverview()
        {
            _heading.text = "Recipe";
            _scroll.Clear();
            _form = null;
            _name = null;
            var box = new VisualElement().Classes("z-pl-overview");
            _overviewLead = ZettaUI.Text("");
            box.Add(_overviewLead);
            box.Add(ZettaUI.Text("This is the recipe. The seed says what it starts from — a picture or a model; you choose which one each time you press New run, and can start several at once.", "z-muted"));
            box.Add(ZettaUI.Text("Auto submits every step for you to the end, Manual waits for you before each one. Runs keep going on the server — close Unity whenever you like.", "z-muted"));
            box.Add(ZettaUI.Text("Every step makes a real post in your library, charged as the Studio charges it.", "z-muted"));
            _runsButton = ZettaUI.Button("See its runs", () => ShowRunsRequested?.Invoke(), "z-button--small", "z-button--ghost");
            box.Add(_runsButton);
            _scroll.Add(box);
        }

        void Build(PipelineNode node, PipelineGraph graph)
        {
            _heading.text = "Step";
            _scroll.Clear();
            _overviewLead = null;
            var spec = NodeCatalog.Spec(node.Kind);
            var notHere = NodeCatalog.NotAvailableInUnity(node.Kind);
            var id = node.Id;

            var head = new VisualElement().Classes("z-pl-inspector-head");
            head.Add(PipelineUi.HueIcon(node.Kind, "md"));
            var text = new VisualElement().Classes("z-grow");
            _name = ZettaUI.TextInput(spec?.Label ?? node.Kind, node.Label ?? "", 80);
            _name.AddToClassList("z-pl-name");
            ZettaUI.SetPlaceholder(_name, spec?.Label ?? node.Kind);
            _name.SetEnabled(!notHere);
            _name.RegisterValueChangedCallback(e => LabelChanged?.Invoke(id, e.newValue));
            text.Add(_name);
            text.Add(ZettaUI.Hint(spec?.Blurb ?? "This step is newer than this version of the plugin."));
            head.Add(text);
            var remove = ZettaUI.Button("Remove", () => RemoveRequested?.Invoke(id), "z-button--small", "z-button--ghost");
            remove.tooltip = $"Remove {node.DisplayName}";
            head.Add(remove);
            _scroll.Add(head);

            _notes = new VisualElement().Classes("z-pl-notes");
            _scroll.Add(_notes);

            _form = null;
            if (notHere) return;
            var formHost = new VisualElement().Classes("z-pl-form");
            _form = new FormContext
            {
                Node = node,
                Graph = graph,
                Settings = FormContext.SettingsOf(node),
                Incoming = GraphRules.IncomingType(graph, node.Id),
                Patch = patch => PatchRequested?.Invoke(id, patch),
                OverlayHost = _overlayHost,
            };
            NodeForms.Build(formHost, _form);
            _scroll.Add(formHost);
        }
    }
}
