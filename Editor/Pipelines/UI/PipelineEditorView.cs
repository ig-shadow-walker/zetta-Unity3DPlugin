using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Zetta3D.Editor.Studio;

namespace Zetta3D.Editor.Pipelines.UI
{
    /// <summary>
    /// ONE PIPELINE, OPEN (pipeline-editor.tsx) — its RECIPE (palette | node canvas | step settings) and its RUNS.
    ///
    /// THE GRAPH MODEL IS THE SOURCE OF TRUTH: every edit is a <see cref="GraphEditing"/> call committed to the
    /// <see cref="UndoStack"/>; the canvas, palette and inspector re-sync from it. SAVED AS YOU GO: 800 ms after the last
    /// change the whole graph is PATCHed with every request rebuilt (<see cref="PipelinesApi.SaveAsync"/>).
    /// </summary>
    public sealed class PipelineEditorView : VisualElement
    {
        public const string RecipeView = "recipe";
        public const string RunsViewName = "runs";
        const string WebWarningKey = "Zetta3D.Pipelines.WebWarningShown";

        public readonly long PipelineId;
        public readonly EditorWindow Window;
        public readonly VisualElement OverlayHost;
        readonly Action _back;
        readonly Action<string, long?> _viewChanged;

        readonly UndoStack _stack = new UndoStack();
        readonly Debouncer _save;
        bool _dirty;
        bool _saving;
        bool _saveFailed;
        bool _loaded;
        string _title = "";
        string _view = RecipeView;
        string _selected;
        List<PipelineProblem> _problems = new List<PipelineProblem>();
        GraphEstimate _estimate = new GraphEstimate();
        HashSet<string> _fanOut = new HashSet<string>();
        List<RunSummary> _runs;
        string _pendingView;
        long? _pendingRun;

        // Bar
        TextField _titleField;
        Button _recipeTab;
        Button _runsTab;
        Label _runsCount;
        VisualElement _runsDots;
        VisualElement _recipeTools;
        Button _undo;
        Button _redo;
        Label _saveStatus;
        Label _estimateLabel;
        Button _newRun;
        Label _problemLine;
        VisualElement _notes;

        // Body
        VisualElement _body;
        VisualElement _recipe;
        NodePaletteView _palette;
        PipelineCanvas _canvas;
        NodeInspector _inspector;
        RunsView _runsView;
        Label _loading;

        public PipelineGraph Graph => _stack.Present;
        public ThreeDEntitlements Gates => StudioContext.Shared.Gates3D;
        public PriceList Prices => StudioContext.Shared.Prices;
        public string View => _view;
        public long? OpenRunId => _runsView?.OpenRunId;
        public bool HasUnsavedChanges => _dirty || _save.Pending || _saving;

        public PipelineEditorView(EditorWindow window, VisualElement overlayHost, long pipelineId, Action back, Action<string, long?> viewChanged)
        {
            Window = window;
            OverlayHost = overlayHost;
            PipelineId = pipelineId;
            _back = back;
            _viewChanged = viewChanged;
            _save = new Debouncer(800, () => _ = SaveAsync());
            AddToClassList("z-pl-editor");

            BuildBar();
            _body = new VisualElement().Classes("z-pl-body");
            Add(_body);
            _loading = ZettaUI.Hint("Loading the pipeline…").Classes("z-pl-loading");
            _body.Add(_loading);

            _stack.Changed += _ => Render();
            StudioContext.Shared.Changed += OnStudioChanged;
            RunFollower.RunsUpdated += OnRunsUpdated;
            // Back on screen (its tab shown again, or the Pipelines page brought back): listening again.
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                StudioContext.Shared.Changed -= OnStudioChanged;
                StudioContext.Shared.Changed += OnStudioChanged;
                RunFollower.RunsUpdated -= OnRunsUpdated;
                RunFollower.RunsUpdated += OnRunsUpdated;
                OverlayHost?.UnregisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
                (OverlayHost ?? this).RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                StudioContext.Shared.Changed -= OnStudioChanged;
                RunFollower.RunsUpdated -= OnRunsUpdated;
                OverlayHost?.UnregisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
                // Leaving the editor never loses a change: what is pending is saved now.
                _save.Flush();
            });
            // On the window's root, so the keys work wherever focus is in the window (or when nothing has it).
            (OverlayHost ?? this).RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            StudioContext.Shared.EnsureLoaded();
        }

        // ─── The bar ───────────────────────────────────────────────────────────────

        void BuildBar()
        {
            var bar = new VisualElement().Classes("z-pl-bar");
            bar.Add(ZettaUI.Button("‹ Pipelines", () => _back?.Invoke(), "z-button--small", "z-button--ghost"));
            _titleField = ZettaUI.TextInput("Pipeline name", "", 200).Classes("z-pl-title-field");
            _titleField.RegisterValueChangedCallback(e =>
            {
                _title = e.newValue ?? "";
                MarkDirty();
            });
            bar.Add(_titleField);

            var tabs = new VisualElement().Classes("z-pl-tabs");
            _recipeTab = new Button(() => Go(RecipeView)) { text = "Recipe" };
            _recipeTab.AddToClassList("z-pl-tab");
            _runsTab = new Button(() => Go(RunsViewName, _view == RunsViewName ? OpenRunId : null));
            _runsTab.AddToClassList("z-pl-tab");
            _runsTab.Add(new Label("Runs").Classes("z-pl-tab-label"));
            _runsCount = new Label().Classes("z-pl-tab-count");
            _runsTab.Add(_runsCount);
            _runsDots = new VisualElement().Classes("z-row");
            _runsTab.Add(_runsDots);
            tabs.Add(_recipeTab);
            tabs.Add(_runsTab);
            bar.Add(tabs);

            _recipeTools = new VisualElement().Classes("z-row");
            _undo = ZettaUI.Button("↶", Undo, "z-button--small", "z-button--ghost");
            _undo.tooltip = Application.platform == RuntimePlatform.OSXEditor ? "Undo (⌘Z)" : "Undo (Ctrl+Z)";
            _redo = ZettaUI.Button("↷", Redo, "z-button--small", "z-button--ghost");
            _redo.tooltip = Application.platform == RuntimePlatform.OSXEditor ? "Redo (⇧⌘Z)" : "Redo (Ctrl+Y)";
            _saveStatus = ZettaUI.Hint("").Classes("z-pl-save");
            _recipeTools.Add(_undo);
            _recipeTools.Add(_redo);
            _recipeTools.Add(_saveStatus);
            bar.Add(_recipeTools);

            bar.Add(ZettaUI.Spacer());
            _estimateLabel = new Label("—").Classes("z-pl-estimate");
            bar.Add(_estimateLabel);
            _newRun = ZettaUI.Button("+ New run…", OpenRunDialog, "z-button--small", "z-button--primary");
            bar.Add(_newRun);
            var web = ZettaUI.Button("Open on web", () => Application.OpenURL($"{ZettaConfig.SiteBase}/pipelines?pipeline={PipelineId}"), "z-button--small", "z-button--ghost");
            web.tooltip = "Open this pipeline on zetta3d.ai";
            bar.Add(web);
            Add(bar);

            _problemLine = ZettaUI.Hint("").Classes("z-pl-problems");
            _problemLine.Visible(false);
            Add(_problemLine);
            _notes = new VisualElement();
            Add(_notes);
        }

        void ShowNote(string text, string tone = null, bool dismissable = true)
        {
            var row = new VisualElement().Classes("z-row", "z-pl-banner", tone == null ? null : "z-pl-banner--" + tone);
            row.Add(new Label(text).Classes("z-grow", "z-pl-banner-text"));
            if (dismissable) row.Add(ZettaUI.Button("✕", () => row.RemoveFromHierarchy(), "z-button--small", "z-button--ghost"));
            _notes.Add(row);
        }

        // ─── Loading and saving ────────────────────────────────────────────────────

        /// <summary>Reads the pipeline and its runs, then opens on <paramref name="view"/> (and a run).</summary>
        public async Task LoadAsync(string view = RecipeView, long? runId = null)
        {
            try
            {
                var pipeline = await PipelinesApi.GetAsync(PipelineId);
                Apply(pipeline);
                _loaded = true;
                if (!SessionState.GetBool(WebWarningKey, false))
                {
                    SessionState.SetBool(WebWarningKey, true);
                    ShowNote("Saved as you go. Editing the same pipeline on zetta3d.ai at the same time overwrites — the last save wins.");
                }
                Go(_pendingView ?? view ?? RecipeView, _pendingView != null ? _pendingRun : runId);
                _pendingView = null;
                _pendingRun = null;
                _ = RefreshRunsAsync();
            }
            catch (ZettaApiException e)
            {
                _loading.text = e.Status == 404 ? "This pipeline could not be opened — it may have been deleted." : ZettaCatalog.Explain(e);
            }
            catch (Exception e)
            {
                _loading.text = e.Message;
            }
        }

        /// <summary>The pipeline as saved: normalised as the web editor loads it; a graph without requests is saved straight back.</summary>
        void Apply(Pipeline pipeline)
        {
            var normalized = RequestBuilder.Normalize(pipeline.Graph ?? new PipelineGraph());
            _title = pipeline.Title ?? "";
            _titleField.SetValueWithoutNotify(_title);
            _dirty = false;
            _save.Cancel();
            _stack.Load(normalized.Graph);
            // An agent-drafted graph holds settings only: built and saved on open, so the server can price and check it.
            if (normalized.NeedsRequestRebuild)
            {
                _dirty = true;
                _ = SaveAsync();
            }
        }

        void MarkDirty()
        {
            _dirty = true;
            _save.Poke();
            UpdateSaveStatus();
        }

        async Task SaveAsync()
        {
            if (!_dirty) return;
            if (_saving)
            {
                // One save at a time; the change waits for the next window.
                _save.Poke();
                return;
            }
            _dirty = false;
            _saving = true;
            UpdateSaveStatus();
            try
            {
                await PipelinesApi.SaveAsync(PipelineId, _stack.Present, _title, Gates);
                _saveFailed = false;
            }
            catch (Exception e)
            {
                _saveFailed = true;
                Debug.LogWarning("[Zetta 3D] The pipeline could not be saved: " + (e is ZettaApiException api ? ZettaCatalog.Explain(api) : e.Message));
            }
            finally
            {
                _saving = false;
                UpdateSaveStatus();
            }
        }

        void UpdateSaveStatus()
        {
            if (_saveStatus == null) return;
            _saveStatus.text = _saving || _save.Pending ? "Saving…" : _saveFailed ? "Not saved — retrying on the next change" : "Saved";
            _saveStatus.EnableInClassList("z-error", _saveFailed && !_saving);
        }

        /// <summary>The window came to the front: if nothing is waiting to be saved, read the pipeline again (the web may have changed it).</summary>
        public async void OnWindowFocus()
        {
            if (!_loaded || HasUnsavedChanges) return;
            try
            {
                var pipeline = await PipelinesApi.GetAsync(PipelineId);
                if (HasUnsavedChanges) return;
                var server = RequestBuilder.Normalize(pipeline.Graph ?? new PipelineGraph()).Graph;
                var same = RequestBuilder.WithRequests(server, Gates).ToString() == RequestBuilder.WithRequests(_stack.Present, Gates).ToString();
                if (same && pipeline.Title == _title) return;
                Apply(pipeline);
            }
            catch (Exception)
            {
                // Offline or gone: keep what is on screen.
            }
        }

        /// <summary>The agent saved this pipeline: its version replaces the editor's, never the other way round.</summary>
        public async void ReloadFromServer()
        {
            var hadEdits = HasUnsavedChanges;
            _save.Cancel();
            _dirty = false;
            try
            {
                var pipeline = await PipelinesApi.GetAsync(PipelineId);
                Apply(pipeline);
                ShowNote(hadEdits ? "Updated by the Agent — it replaced a change not saved yet." : "Updated by the Agent.", "signal");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Zetta 3D] The pipeline the Agent changed could not be read again: " + e.Message);
            }
        }

        // ─── Edits ────────────────────────────────────────────────────────────────

        void Commit(PipelineGraph next, string coalesce = null)
        {
            if (next == null || ReferenceEquals(next, _stack.Present)) return;
            _stack.Commit(next, coalesce);
            MarkDirty();
        }

        void Try(Func<PipelineGraph> edit, string coalesce = null)
        {
            try
            {
                Commit(edit(), coalesce);
            }
            catch (InvalidOperationException e)
            {
                ZettaUI.Toast(Window, e.Message);
            }
        }

        void Undo()
        {
            if (_stack.Undo()) MarkDirty();
        }

        void Redo()
        {
            if (_stack.Redo()) MarkDirty();
        }

        void OnKeyDown(KeyDownEvent e)
        {
            if (_view != RecipeView || !(e.actionKey || e.ctrlKey || e.commandKey) || e.altKey) return;
            if (e.keyCode != KeyCode.Z && e.keyCode != KeyCode.Y) return;
            // A text field keeps its own undo — the words, not the pipeline.
            if (PipelineUi.IsTyping(this)) return;
            if (e.keyCode == KeyCode.Y || e.shiftKey) Redo();
            else Undo();
            e.StopImmediatePropagation();
#if !UNITY_2023_2_OR_NEWER
            e.PreventDefault();
#endif
        }

        /// <summary>Adds a step: dropped where it was dropped, or clicked in after the selected step and wired to it.</summary>
        void AddStep(string kind, NodePosition? at = null)
        {
            try
            {
                var next = RecipeEdits.Add(_stack.Present, kind, _selected, at, Gates, out var id);
                if (next == null) return;
                Commit(next);
                Select(id);
            }
            catch (InvalidOperationException e)
            {
                ZettaUI.Toast(Window, e.Message);
            }
        }

        void Select(string nodeId)
        {
            _selected = nodeId;
            _canvas?.Select(nodeId);
            RenderInspector();
        }

        void Patch(string nodeId, JObject patch)
        {
            // Typing in one step's settings is one undo, not one per key.
            Try(() => RecipeEdits.Patch(_stack.Present, nodeId, patch, Gates), RecipeEdits.SettingsKey(nodeId));
        }

        void RemoveNodes(List<string> nodeIds, List<string> edgeIds)
        {
            if (nodeIds.Contains(_selected)) _selected = null;
            Commit(RecipeEdits.Remove(_stack.Present, nodeIds, edgeIds, Gates));
        }

        /// <summary>Isolate Parts learns its parts from a run — the editor's own doing: kept and saved, never undone.</summary>
        public void LearnParts(PipelineRun run)
        {
            if (run == null || !_loaded) return;
            var next = GraphEditing.LearnParts(_stack.Present, run);
            if (ReferenceEquals(next, _stack.Present)) return;
            _stack.Replace(next);
            MarkDirty();
        }

        // ─── Views ────────────────────────────────────────────────────────────────

        /// <summary>Recipe, or Runs with a run open.</summary>
        public void Go(string view, long? runId = null)
        {
            if (!_loaded)
            {
                // Asked for while loading (a deep link): applied once it has loaded.
                _pendingView = view ?? RecipeView;
                _pendingRun = runId;
                return;
            }
            _view = view == RunsViewName ? RunsViewName : RecipeView;
            _loading.RemoveFromHierarchy();
            _body.Clear();
            if (_view == RecipeView)
            {
                EnsureRecipe();
                _body.Add(_recipe);
            }
            else
            {
                if (_runsView == null)
                {
                    _runsView = new RunsView(this);
                    _runsView.RunOpened += id => _viewChanged?.Invoke(_view, id);
                    if (_runs != null) _runsView.SetRuns(_runs);
                }
                _body.Add(_runsView);
                _runsView.OpenRun(runId);
                _ = RefreshRunsAsync();
            }
            _recipeTab.EnableInClassList("z-pl-tab--active", _view == RecipeView);
            _runsTab.EnableInClassList("z-pl-tab--active", _view == RunsViewName);
            _viewChanged?.Invoke(_view, _view == RunsViewName ? runId : null);
            Render();
        }

        void EnsureRecipe()
        {
            if (_recipe != null) return;
            _palette = new NodePaletteView();
            _palette.AddRequested += kind => AddStep(kind);
            _canvas = new PipelineCanvas();
            _canvas.SelectionChanged += id =>
            {
                _selected = id;
                RenderInspector();
            };
            _canvas.ConnectRequested += (s, sh, t, th) => Try(() => GraphEditing.Connect(_stack.Present, s, sh, t, th, Gates));
            _canvas.RemoveRequested += RemoveNodes;
            _canvas.ReconnectRequested += (edge, s, sh, t, th) => Try(() => GraphEditing.Reconnect(_stack.Present, edge, s, sh, t, th, Gates));
            _canvas.Refused += Toast;
            _canvas.MoveRequested += moved =>
            {
                // One drag, one undo.
                var next = RecipeEdits.Move(_stack.Present, moved, out var coalesce);
                Commit(next, coalesce);
            };
            _canvas.DropRequested += (kind, at) => AddStep(kind, at);
            _inspector = new NodeInspector(OverlayHost);
            _inspector.PatchRequested += Patch;
            _inspector.LabelChanged += (id, label) => Try(() => GraphEditing.SetLabel(_stack.Present, id, label), RecipeEdits.SettingsKey(id));
            _inspector.RemoveRequested += id => RemoveNodes(new List<string> { id }, new List<string>());
            _inspector.ShowRunsRequested += () => Go(RunsViewName);

            var canvasBox = new VisualElement().Classes("z-pl-canvas-box");
            canvasBox.Add(_canvas);
            var inner = new TwoPaneSplitView(1, 340, TwoPaneSplitViewOrientation.Horizontal);
            inner.Add(canvasBox);
            inner.Add(_inspector);
            var outer = new TwoPaneSplitView(0, 190, TwoPaneSplitViewOrientation.Horizontal);
            outer.Add(_palette);
            outer.Add(inner);
            outer.AddToClassList("z-pl-split");
            _recipe = outer;
        }

        void OnStudioChanged() => Render();

        // ─── Drawing ──────────────────────────────────────────────────────────────

        void Render()
        {
            if (!_loaded) return;
            var graph = _stack.Present;
            if (_selected != null && graph.Node(_selected) == null) _selected = null;
            _problems = GraphRules.Problems(graph, Gates);
            _estimate = GraphRules.Estimate(graph, Prices, Gates);
            _fanOut = GraphRules.FanOutNodes(graph);
            var hasSeed = graph.Nodes.Any(n => NodeCatalog.IsSeed(n.Kind));

            _estimateLabel.text = _estimate.Label;
            _estimateLabel.tooltip = _estimate.PerPart ? "Counting every part at Max parts" : "";
            _newRun.SetEnabled(hasSeed && _problems.Count == 0);
            _newRun.tooltip = !hasSeed ? "Add a seed first" : _problems.FirstOrDefault()?.Message ?? "Choose the pictures or models to run on — one run each";
            _undo.SetEnabled(_stack.CanUndo);
            _redo.SetEnabled(_stack.CanRedo);
            _recipeTools.Visible(_view == RecipeView);
            var showProblems = _view == RecipeView && _problems.Count > 0 && graph.Nodes.Count > 0;
            _problemLine.text = showProblems
                ? $"{_problems.Count} thing{(_problems.Count == 1 ? "" : "s")} to finish before it can run — {_problems[0].Message}"
                : "";
            _problemLine.Visible(showProblems);
            UpdateSaveStatus();
            RenderRunsTab();

            if (_view != RecipeView || _canvas == null) return;
            _palette.SetHasSeed(hasSeed);
            _canvas.Show(graph, DataFor);
            _canvas.Select(_selected);
            RenderInspector();
        }

        void RenderInspector() => _inspector?.Show(_stack.Present, _selected, _problems, _runs?.Count ?? 0);

        NodeCardData DataFor(string nodeId)
        {
            var graph = _stack.Present;
            var node = graph.Node(nodeId);
            if (node == null) return null;
            var context = new NodeContext(GraphRules.IncomingType(graph, nodeId), Gates);
            return new NodeCardData
            {
                Node = node,
                Summary = NodeSettings.Summary(node),
                Price = RequestBuilder.PriceFor(node, Prices, context),
                PerPart = graph.Edges.Any(e => e.SourceHandle == "each") && _fanOut.Contains(nodeId),
                Problems = _problems.Where(p => p.NodeId == nodeId).Select(p => p.Message).ToList(),
                Warnings = GraphRules.Warnings(graph, nodeId),
                PerRun = NodeSettings.PerRunIn(graph, node),
            };
        }

        // ─── Runs ─────────────────────────────────────────────────────────────────

        public async Task RefreshRunsAsync()
        {
            try
            {
                var list = await PipelinesApi.RunsAsync(PipelineId);
                SetRuns(list);
                if (list.Any(r => r.IsLive)) RunFollower.FollowRuns(PipelineId);
            }
            catch (Exception)
            {
                // The list stays as it was; the next poll or action reads it again.
            }
        }

        void OnRunsUpdated(long pipelineId, List<RunSummary> list)
        {
            if (pipelineId == PipelineId) SetRuns(list);
        }

        void SetRuns(List<RunSummary> list)
        {
            _runs = list ?? new List<RunSummary>();
            _runsView?.SetRuns(_runs);
            RenderRunsTab();
            if (_view == RecipeView && _selected == null) RenderInspector();
        }

        void RenderRunsTab()
        {
            var list = _runs ?? new List<RunSummary>();
            _runsCount.text = list.Count > 0 ? list.Count.ToString() : "";
            _runsDots.Clear();
            var running = list.Count(r => r.Status is RunStatuses.Running or RunStatuses.Paused);
            var needYou = list.Count(r => r.Status is RunStatuses.Awaiting or RunStatuses.Draft);
            if (running > 0) _runsDots.Add(PipelineUi.RunDot(RunTone.Running, $"{running} running"));
            if (needYou > 0) _runsDots.Add(PipelineUi.RunDot(RunTone.Attention, $"{needYou} need{(needYou == 1 ? "s" : "")} you"));
        }

        void OpenRunDialog()
        {
            if (!_loaded) return;
            _save.Flush();
            RunDialog.Show(OverlayHost, this, _stack.Present, _estimate.Credits, async (created, import) =>
            {
                // The graph went with the batch (and was saved onto the pipeline): nothing is waiting any more.
                _dirty = false;
                _save.Cancel();
                UpdateSaveStatus();
                if (import)
                    foreach (var run in created)
                        RunFollower.Follow(run.Id, PipelineId, 0, RunImportMode.FinalSteps, null);
                await RefreshRunsAsync();
                Go(RunsViewName, created.FirstOrDefault()?.Id);
            });
        }

        public void Toast(string message) => ZettaUI.Toast(Window, message);
    }
}
