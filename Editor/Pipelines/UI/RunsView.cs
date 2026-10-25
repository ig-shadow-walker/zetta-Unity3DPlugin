using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Pipelines.UI
{
    /// <summary>
    /// RUNS (runs-view.tsx): every run of the pipeline, newest first, filtered by what it is doing; one open shows its
    /// steps exactly as they ran on its own copy of the graph, what each made, and what can still be done.
    /// </summary>
    public sealed class RunsView : VisualElement
    {
        static readonly (string key, string label, string[] statuses, RunTone tone)[] Filters =
        {
            ("all", "All", new string[0], RunTone.Idle),
            ("running", "Running", new[] { RunStatuses.Running, RunStatuses.Paused }, RunTone.Running),
            ("attention", "Needs you", new[] { RunStatuses.Draft, RunStatuses.Awaiting }, RunTone.Attention),
            ("done", "Done", new[] { RunStatuses.Done }, RunTone.Done),
            ("failed", "Stopped", new[] { RunStatuses.Failed, RunStatuses.Cancelled }, RunTone.Failed),
        };

        readonly PipelineEditorView _editor;
        readonly VisualElement _filters;
        readonly ScrollView _list;
        readonly VisualElement _right;
        List<RunSummary> _runs;
        string _filter = "all";
        RunPanel _panel;

        /// <summary>The open run changed (null = closed).</summary>
        public event Action<long?> RunOpened;

        public long? OpenRunId => _panel?.RunId;

        public RunsView(PipelineEditorView editor)
        {
            _editor = editor;
            AddToClassList("z-pl-runs");
            var left = new VisualElement().Classes("z-pl-runs-list");
            left.Add(new Label("Runs").Classes("z-pl-panel-title"));
            _filters = new VisualElement().Classes("z-row", "z-row--wrap", "z-pl-filters");
            left.Add(_filters);
            _list = new ScrollView(ScrollViewMode.Vertical).Classes("z-grow");
            left.Add(_list);
            _right = new VisualElement().Classes("z-pl-run-host");
            var split = new TwoPaneSplitView(0, 280, TwoPaneSplitViewOrientation.Horizontal);
            split.Add(left);
            split.Add(_right);
            split.AddToClassList("z-pl-split");
            Add(split);
            RenderList();
            RenderEmpty();
        }

        public void SetRuns(List<RunSummary> runs)
        {
            _runs = runs;
            RenderList();
            if (_panel != null) _panel.Summary = runs?.FirstOrDefault(r => r.Id == _panel.RunId);
            else RenderEmpty();
        }

        public void OpenRun(long? runId)
        {
            if (runId == _panel?.RunId && (runId != null || _panel == null)) return;
            _panel?.Dispose();
            _panel = null;
            _right.Clear();
            if (runId == null) RenderEmpty();
            else
            {
                _panel = new RunPanel(_editor, runId.Value, _runs?.FirstOrDefault(r => r.Id == runId), () => OpenRun(null));
                _right.Add(_panel);
            }
            RenderList();
            RunOpened?.Invoke(runId);
        }

        void RenderEmpty()
        {
            if (_panel != null) return;
            _right.Clear();
            var any = _runs is { Count: > 0 };
            var box = new VisualElement().Classes("z-pl-empty");
            box.Add(new Label(any ? "Open a run to see what it made" : "No runs yet").Classes("z-pl-empty-title"));
            box.Add(ZettaUI.Text(any
                ? "Each run started from its own seed and keeps its own results. One that needs you waits here until you run its next step."
                : "Press New run and pick a picture or a model. Every run of this pipeline is listed here — running, waiting for you, or done.", "z-muted"));
            _right.Add(box);
        }

        void RenderList()
        {
            _filters.Clear();
            _list.Clear();
            if (_runs == null)
            {
                _list.Add(ZettaUI.Hint("Loading the runs…"));
                return;
            }
            if (_runs.Count == 0)
            {
                _list.Add(ZettaUI.Hint("No runs yet — start one with New run."));
                return;
            }
            int Count((string key, string label, string[] statuses, RunTone tone) f) => f.statuses.Length == 0 ? _runs.Count : _runs.Count(r => f.statuses.Contains(r.Status));
            foreach (var filter in Filters)
            {
                var n = Count(filter);
                if (filter.key != "all" && n == 0) continue;
                var key = filter.key;
                var chip = new Button(() =>
                {
                    _filter = key;
                    RenderList();
                });
                chip.AddToClassList("z-pl-filter");
                chip.EnableInClassList("z-pl-filter--active", _filter == key);
                if (key != "all") chip.Add(PipelineUi.RunDot(filter.tone));
                chip.Add(new Label($"{filter.label} {n}"));
                _filters.Add(chip);
            }
            var statuses = Filters.First(f => f.key == _filter).statuses;
            foreach (var run in statuses.Length == 0 ? _runs : _runs.Where(r => statuses.Contains(r.Status)).ToList())
            {
                var row = new VisualElement().Classes("z-pl-run-row");
                row.EnableInClassList("z-pl-run-row--selected", run.Id == _panel?.RunId);
                row.EnableInClassList("z-pl-run-row--attention", RunStatuses.Tone(run.Status) == RunTone.Attention);
                var thumb = PipelineUi.Thumb(run.Seed?.ThumbnailUrl, "z-pl-thumb--row");
                if (string.IsNullOrEmpty(run.Seed?.ThumbnailUrl)) thumb.Add(new Label(run.Seed?.Type == "model" ? "Mdl" : "Pic").Classes("z-pl-thumb-glyph"));
                row.Add(thumb);
                var text = new VisualElement().Classes("z-grow", "z-pl-run-text");
                text.Add(new Label(string.IsNullOrEmpty(run.Seed?.Name) ? $"Run #{run.Id}" : run.Seed.Name).Classes("z-pl-run-name"));
                text.Add(new Label($"#{run.Id} · {Progress(run)} · {PipelineUi.WhenRun(run.CreatedAt)}").Classes("z-pl-run-detail"));
                row.Add(text);
                row.Add(PipelineUi.RunPill(run.Status));
                var id = run.Id;
                row.RegisterCallback<ClickEvent>(_ => OpenRun(id));
                _list.Add(row);
            }
        }

        static string Progress(RunSummary run)
        {
            if (run.Status == RunStatuses.Draft)
                return run.InputsNeeded > 0 ? $"{run.InputsNeeded} step{(run.InputsNeeded == 1 ? " needs" : "s need")} input" : "Ready to start";
            var p = run.Progress ?? new RunProgress();
            return $"{p.Done} of {p.Total} step{(p.Total == 1 ? "" : "s")}" + (p.Waiting > 0 ? $" · {p.Waiting} waiting" : "");
        }
    }

    /// <summary>One run, open: its steps on a read-only canvas, its controls, and the selected step's results and settings.</summary>
    sealed class RunPanel : VisualElement, IDisposable
    {
        public readonly long RunId;
        readonly PipelineEditorView _editor;
        readonly Action _close;
        RunSummary _summary;
        PipelineRun _run;
        PipelineGraph _graph;
        readonly Dictionary<string, PipelineNode> _drafts = new Dictionary<string, PipelineNode>();
        readonly Dictionary<string, Debouncer> _timers = new Dictionary<string, Debouncer>();
        bool _busy;
        bool _startTried;
        bool _confirmDelete;
        bool _disposed;
        string _selected;
        List<PipelineProblem> _needs = new List<PipelineProblem>();
        List<PipelineProblem> _serverNeeds;

        readonly VisualElement _header;
        readonly Label _error;
        readonly PipelineCanvas _canvas;
        readonly VisualElement _toolbar;
        readonly VisualElement _alert;
        readonly Label _sideTitle;
        readonly ScrollView _side;
        readonly Label _loading;

        // The selected step's settings form (built once per step, refreshed in place).
        string _formFor;
        FormContext _form;
        string _resultsSignature;

        public RunSummary Summary
        {
            set
            {
                _summary = value;
                if (_run != null) RenderHeader();
            }
        }

        public RunPanel(PipelineEditorView editor, long runId, RunSummary summary, Action close)
        {
            _editor = editor;
            RunId = runId;
            _summary = summary;
            _close = close;
            AddToClassList("z-pl-run");

            var main = new VisualElement().Classes("z-pl-run-main");
            _header = new VisualElement().Classes("z-row", "z-row--wrap", "z-pl-run-header");
            main.Add(_header);
            _error = new Label().Classes("z-error", "z-pl-run-error").Visible(false);
            main.Add(_error);
            var canvasBox = new VisualElement().Classes("z-pl-canvas-box");
            _canvas = new PipelineCanvas(readOnly: true);
            _canvas.SelectionChanged += id =>
            {
                _selected = id;
                RenderSide();
            };
            canvasBox.Add(_canvas);
            _loading = ZettaUI.Hint("Loading the run…").Classes("z-pl-loading");
            canvasBox.Add(_loading);
            main.Add(canvasBox);
            _alert = new VisualElement().Classes("z-pl-run-alert").Visible(false);
            main.Add(_alert);
            _toolbar = new VisualElement().Classes("z-row", "z-row--wrap", "z-pl-run-toolbar");
            main.Add(_toolbar);

            var side = new VisualElement().Classes("z-pl-inspector");
            _sideTitle = new Label("Run").Classes("z-pl-panel-title");
            side.Add(_sideTitle);
            _side = new ScrollView(ScrollViewMode.Vertical).Classes("z-pl-inspector-scroll");
            side.Add(_side);

            var split = new TwoPaneSplitView(1, 340, TwoPaneSplitViewOrientation.Horizontal);
            split.Add(main);
            split.Add(side);
            split.AddToClassList("z-pl-split");
            Add(split);

            // Listening while on screen (the Runs tab is taken off screen while the Recipe is shown, and put back).
            RegisterCallback<AttachToPanelEvent>(evt =>
            {
                if (_disposed) return;
                RunFollower.RunUpdated -= OnRunUpdated;
                RunFollower.RunUpdated += OnRunUpdated;
                _ = LoadAsync();
            });
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                RunFollower.RunUpdated -= OnRunUpdated;
                // What was typed for this run is sent, not dropped.
                foreach (var timer in _timers.Values) timer.Flush();
            });
        }

        /// <summary>Another run opened in its place.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            foreach (var timer in _timers.Values) timer.Flush();
            _disposed = true;
            RunFollower.RunUpdated -= OnRunUpdated;
        }

        async Task LoadAsync()
        {
            try
            {
                if (_run == null && RunFollower.LatestRun(RunId) is { } latest) Apply(latest);
                var run = await PipelinesApi.GetRunAsync(RunId);
                Apply(run);
                if ((run.IsLive || run.Status == RunStatuses.Draft) && !RunFollower.IsFollowing(RunId)) RunFollower.Follow(RunId, run.PipelineId);
                else if (RunFollower.IsFollowing(RunId)) _ = RunFollower.RefreshAsync(RunId);
            }
            catch (ZettaApiException e)
            {
                _loading.text = e.Status == 404 ? "This run is gone." : ZettaCatalog.Explain(e);
            }
            catch (Exception e)
            {
                _loading.text = e.Message;
            }
        }

        void OnRunUpdated(PipelineRun run)
        {
            if (!_disposed && run?.Id == RunId) Apply(run);
        }

        void Apply(PipelineRun run)
        {
            if (_disposed || run == null) return;
            var wasLive = _run?.IsLive ?? false;
            _run = run;
            _loading.Visible(false);
            _editor.LearnParts(run);
            // A run that becomes live (Start, Resume) is followed until it is over.
            if (run.IsLive && !wasLive && !RunFollower.IsFollowing(RunId)) RunFollower.Follow(RunId, run.PipelineId);
            Render();
        }

        /// <summary>Hands an action's answer to the view (and the follower), then re-reads the runs list.</summary>
        async Task Act(Func<Task<PipelineRun>> action)
        {
            if (_busy) return;
            _busy = true;
            ShowError(null);
            RenderToolbar();
            try
            {
                var run = await action();
                if (run != null)
                {
                    await RunFollower.Report(run);
                    Apply(run);
                }
                _ = _editor.RefreshRunsAsync();
            }
            catch (PipelineApiException e) when (e.Code == "RUN_NEEDS_INPUT")
            {
                _serverNeeds = e.Problems;
                _startTried = true;
                var first = e.Problems.FirstOrDefault(p => p.NodeId != null)?.NodeId;
                if (first != null) SelectStep(first);
                ShowError(ZettaCatalog.Explain(e));
            }
            catch (ZettaApiException e)
            {
                ShowError(ZettaCatalog.Explain(e));
            }
            catch (Exception e)
            {
                ShowError(e.Message);
            }
            finally
            {
                _busy = false;
                if (!_disposed) Render();
            }
        }

        void ShowError(string message)
        {
            _error.text = message ?? "";
            _error.Visible(!string.IsNullOrEmpty(message));
        }

        void SelectStep(string nodeId)
        {
            _selected = nodeId;
            _canvas.Select(nodeId);
            RenderSide();
        }

        // ─── The run's own graph, with this run's edits ahead of the server's copy ─────

        PipelineGraph BuildGraph()
        {
            var graph = _run.Graph?.Clone() ?? new PipelineGraph();
            for (var i = 0; i < graph.Nodes.Count; i++)
            {
                var node = graph.Nodes[i];
                if (_drafts.TryGetValue(node.Id, out var draft)) graph.Nodes[i] = draft.Clone();
                else if (!NodeCatalog.NotAvailableInUnity(node.Kind)) node.Settings = NodeSettings.WithDefaults(node.Kind, node.Settings);
            }
            return graph;
        }

        void ChangeNode(string nodeId, JObject patch)
        {
            try
            {
                var (settings, request) = RequestBuilder.RunNodeChange(_graph, nodeId, patch, _editor.Gates);
                var node = _graph.Node(nodeId).Clone();
                node.Settings = settings;
                node.Request = request;
                _drafts[nodeId] = node;
                if (!_timers.TryGetValue(nodeId, out var timer))
                {
                    timer = new Debouncer(700, () => _ = Send(nodeId));
                    _timers[nodeId] = timer;
                }
                timer.Poke();
                Render();
            }
            catch (ArgumentException e)
            {
                ShowError(e.Message);
            }
        }

        async Task Send(string nodeId)
        {
            if (!_drafts.TryGetValue(nodeId, out var node)) return;
            try
            {
                var run = await PipelinesApi.PatchRunNodeAsync(RunId, nodeId, node.Request, node.Settings);
                // Sent: the server's copy is the run's own again — unless more was typed meanwhile.
                if (_drafts.TryGetValue(nodeId, out var now) && ReferenceEquals(now, node)) _drafts.Remove(nodeId);
                if (run != null && !_disposed) Apply(run);
            }
            catch (ZettaApiException e)
            {
                if (!_disposed) ShowError(ZettaCatalog.Explain(e));
            }
        }

        async Task FlushDrafts()
        {
            foreach (var pair in _timers.ToList())
            {
                if (!pair.Value.Pending) continue;
                pair.Value.Cancel();
                await Send(pair.Key);
            }
        }

        // ─── Drawing ──────────────────────────────────────────────────────────────

        void Render()
        {
            if (_run == null || _disposed) return;
            _graph = BuildGraph();
            var draft = _run.Status == RunStatuses.Draft;
            _needs = draft ? GraphRules.RunInputProblems(_graph) : new List<PipelineProblem>();
            if (_needs.Count == 0) _startTried = false;
            var running = _graph.Nodes.Where(n => _run.NodeState(n.Id).Status == StepStatuses.Running).Select(n => n.Id).ToList();
            _canvas.SetRunning(running);
            _canvas.Show(_graph, DataFor, false);
            if (_selected != null && _graph.Node(_selected) == null) _selected = null;
            _canvas.Select(_selected);
            RenderHeader();
            RenderToolbar();
            RenderSide();
        }

        NodeCardData DataFor(string nodeId)
        {
            var node = _graph.Node(nodeId);
            if (node == null) return null;
            var seed = NodeCatalog.IsSeed(node.Kind);
            var steps = _run.StepsOf(nodeId).ToList();
            var need = _needs.FirstOrDefault(p => p.NodeId == nodeId)?.Message
                       ?? (_startTried ? _serverNeeds?.FirstOrDefault(p => p.NodeId == nodeId)?.Message : null);
            var data = new NodeCardData
            {
                Node = node,
                Summary = seed ? "This run’s seed" : NodeSettings.Summary(node),
                Price = RequestBuilder.PriceFor(node, _editor.Prices, new NodeContext(GraphRules.IncomingType(_graph, nodeId), _editor.Gates)),
                Run = NodeRunState.Of(steps),
                NeedsInput = _run.Status == RunStatuses.Draft ? need : null,
                Flash = _startTried,
            };
            if (seed)
            {
                var step = steps.FirstOrDefault();
                var url = step?.Output is PictureStepOutput picture ? picture.Picture?.Url : step?.Generation?.ThumbnailUrl ?? _summary?.Seed?.ThumbnailUrl;
                data.Seed = (url, _summary?.Seed?.Name ?? step?.Generation?.Title);
            }
            else data.Outputs = steps.SelectMany(OutputsOf).ToList();
            return data;
        }

        static IEnumerable<(string url, string label)> OutputsOf(PipelineRunStep step)
        {
            switch (step.Output)
            {
                case null:
                    return Enumerable.Empty<(string, string)>();
                case PictureStepOutput picture:
                    return new[] { (picture.Picture?.Url, "Picture") };
                case ViewsStepOutput views:
                    return views.Views.Select(v => (v.Picture?.Url, v.ViewType));
                case IsolatedStepOutput isolated:
                    return new[] { (isolated.Character?.Url, "Character") }.Concat(isolated.Parts.Select(p => (p.Picture?.Url, p.Label)));
                default:
                    return new[] { (step.Generation?.ThumbnailUrl, step.Generation?.Title ?? "Model") };
            }
        }

        string Name => string.IsNullOrEmpty(_summary?.Seed?.Name) ? $"Run #{RunId}" : _summary.Seed.Name;

        void RenderHeader()
        {
            _header.Clear();
            _header.Add(ZettaUI.Button("‹ All runs", () => _close(), "z-button--small", "z-button--ghost"));
            _header.Add(new Label(Name).Classes("z-pl-run-title"));
            _header.Add(ZettaUI.Hint($"#{RunId}"));
            _header.Add(PipelineUi.RunPill(_run.Status));
            _header.Add(ZettaUI.Hint($"{RunStatuses.ModeLabel(_run.Mode)} · {_run.SpentCredits} cr spent"));
            if (RunFollower.IsFollowing(RunId) && RunFollower.Runs.FirstOrDefault(r => r.RunId == RunId)?.Import != RunImportMode.None)
                _header.Add(ZettaUI.Chip("Importing results", "signal"));
        }

        VisualElement ModeSwitch()
        {
            var mode = new ZettaUI.Segmented(new[] { RunStatuses.ModeAuto, RunStatuses.ModeManual }, new[] { "Auto", "Manual" }, _run.Mode == RunStatuses.ModeAuto ? RunStatuses.ModeAuto : RunStatuses.ModeManual);
            mode.Changed += v => _ = Act(() => PipelinesApi.SetModeAsync(RunId, v));
            mode.SetEnabled(!_busy);
            mode.AddToClassList("z-pl-mode-switch");
            return mode;
        }

        void RenderToolbar()
        {
            _toolbar.Clear();
            _alert.Clear();
            _alert.Visible(false);
            if (_run == null) return;
            var status = _run.Status;
            if (status == RunStatuses.Draft)
            {
                if (_startTried && _needs.Count > 0)
                {
                    _alert.Visible(true);
                    _alert.Add(new Label($"This run can’t start yet — {(_needs.Count == 1 ? "one step needs" : $"{_needs.Count} steps need")} something from you.").Classes("z-pl-alert-title"));
                    foreach (var need in _needs)
                    {
                        var id = need.NodeId;
                        var link = ZettaUI.Button(need.Message, () => SelectStep(id), "z-button--small", "z-button--ghost", "z-pl-link");
                        _alert.Add(link);
                    }
                    _alert.Add(ZettaUI.Hint("Select the step marked Needs input, fill it in on the right, then press Start again."));
                }
                _toolbar.Add(ModeSwitch());
                var start = ZettaUI.Button(_busy ? "Starting…" : "▶ Start", () => _ = Start(), "z-button--small", "z-button--primary");
                start.SetEnabled(!_busy);
                _toolbar.Add(start);
                var discard = ZettaUI.Button("Discard", () => _ = Discard(), "z-button--small", "z-button--ghost");
                discard.tooltip = "A run that never started has nothing to keep: Discard deletes it.";
                discard.SetEnabled(!_busy);
                _toolbar.Add(discard);
            }
            else if (_run.IsLive)
            {
                _toolbar.Add(ModeSwitch());
                var waiting = _run.WaitingCount;
                if (waiting > 0)
                {
                    var run = ZettaUI.Button(waiting == 1 ? "▶ Run the next step" : $"▶ Run {waiting} waiting", RunWaiting, "z-button--small", "z-button--primary");
                    run.SetEnabled(!_busy);
                    _toolbar.Add(run);
                }
                var pause = _run.Paused
                    ? ZettaUI.Button("▶ Resume", () => _ = Act(() => PipelinesApi.ResumeAsync(RunId)), "z-button--small", "z-button--ghost")
                    : ZettaUI.Button("❚❚ Pause", () => _ = Act(() => PipelinesApi.PauseAsync(RunId)), "z-button--small", "z-button--ghost");
                pause.SetEnabled(!_busy);
                _toolbar.Add(pause);
                var cancel = ZettaUI.Button("■ Cancel", () =>
                {
                    if (EditorUtility.DisplayDialog("Cancel this run?",
                            "Steps not started are cancelled, and a 3D job still in the queue is cancelled and refunded. What it has made stays in your library.",
                            "Cancel the run", "Keep it running"))
                        _ = Act(() => PipelinesApi.CancelAsync(RunId));
                }, "z-button--small", "z-button--ghost");
                cancel.SetEnabled(!_busy);
                _toolbar.Add(cancel);
            }
            else
            {
                // A run that is over can be deleted — never one still going. Its models stay in the library.
                _toolbar.Add(ZettaUI.Hint(_confirmDelete ? "Its models stay in your library." : "This run is over."));
                var delete = ZettaUI.Button(_confirmDelete ? "Press again to delete" : "Delete run", () =>
                {
                    if (_confirmDelete)
                    {
                        _ = Delete();
                        return;
                    }
                    _confirmDelete = true;
                    RenderToolbar();
                    schedule.Execute(() =>
                    {
                        _confirmDelete = false;
                        if (!_disposed) RenderToolbar();
                    }).StartingIn(4000);
                }, "z-button--small", "z-button--ghost", _confirmDelete ? "z-button--danger" : null);
                delete.SetEnabled(!_busy);
                _toolbar.Add(delete);
            }
        }

        /// <summary>Start: what was just typed is sent first; a step still needing its input stops it and is pointed at.</summary>
        async Task Start()
        {
            if (_needs.Count > 0)
            {
                _startTried = true;
                SelectStep(_needs[0].NodeId);
                Render();
                return;
            }
            _busy = true;
            RenderToolbar();
            try
            {
                await FlushDrafts();
            }
            finally
            {
                _busy = false;
            }
            await Act(() => PipelinesApi.StartAsync(RunId));
        }

        void RunWaiting()
        {
            var waiting = _run.Steps.Where(s => s.Status == StepStatuses.Waiting).ToList();
            var lines = waiting.Select(step =>
            {
                var node = _graph.Node(step.NodeId);
                var name = node?.DisplayName ?? step.NodeId;
                if (!string.IsNullOrEmpty(step.PartKey)) name += $" ({_run.PartLabel(step.PartKey)})";
                var price = node == null ? null : RequestBuilder.PriceFor(node, _editor.Prices, new NodeContext(GraphRules.IncomingType(_graph, node.Id), _editor.Gates));
                return $"• {name} — {RequestBuilder.PriceLabel(price, false)}";
            });
            var total = waiting.Select(s => _graph.Node(s.NodeId)).Where(n => n != null)
                .Select(n => RequestBuilder.PriceFor(n, _editor.Prices, new NodeContext(GraphRules.IncomingType(_graph, n.Id), _editor.Gates))).ToList();
            var sum = total.Any(p => p == null) ? null : (int?)total.Sum(p => p.Value);
            var message = string.Join("\n", lines) + (sum == null ? "" : $"\n\nUp to {sum} credits.");
            var title = waiting.Count == 1 ? "Run the next step?" : $"Run {waiting.Count} waiting steps?";
            if (EditorUtility.DisplayDialog(title, message, "Run", "Cancel")) _ = Act(() => PipelinesApi.RunWaitingAsync(RunId));
        }

        async Task Discard()
        {
            foreach (var timer in _timers.Values) timer.Cancel();
            _drafts.Clear();
            await Delete();
        }

        async Task Delete()
        {
            _busy = true;
            RenderToolbar();
            try
            {
                await PipelinesApi.DeleteRunAsync(RunId);
                RunFollower.Unfollow(RunId);
                await _editor.RefreshRunsAsync();
                _close();
            }
            catch (ZettaApiException e)
            {
                ShowError(ZettaCatalog.Explain(e));
            }
            finally
            {
                _busy = false;
                if (!_disposed) RenderToolbar();
            }
        }

        // ─── The side panel ───────────────────────────────────────────────────────

        void RenderSide()
        {
            if (_run == null || _disposed) return;
            var node = _graph.Node(_selected);
            if (node == null)
            {
                _formFor = null;
                _form = null;
                _sideTitle.text = "Run";
                _side.Clear();
                _side.Add(Overview());
                return;
            }
            _sideTitle.text = "Step";
            var editable = _run.IsNodeEditable(node.Id) && !NodeCatalog.NotAvailableInUnity(node.Kind);
            var key = node.Id + "|" + editable;
            // A form being typed into is refreshed in place; anything else is drawn again.
            if (_formFor == key)
            {
                _form?.Refresh(node, _graph);
                var results = _side.Q(className: "z-pl-results");
                var signature = ResultsSignature(node);
                // Results are drawn again only when the steps changed — a part list being ticked survives a poll.
                if (results != null && signature != _resultsSignature)
                {
                    _resultsSignature = signature;
                    var index = results.parent.IndexOf(results);
                    var parent = results.parent;
                    results.RemoveFromHierarchy();
                    parent.Insert(index, StepResultsView.Build(node, _run, Actions(node)));
                }
                RefreshNeedsNote(node);
                return;
            }
            _side.Clear();
            _formFor = key;
            _form = null;
            _resultsSignature = ResultsSignature(node);
            var head = new VisualElement().Classes("z-pl-inspector-head");
            head.Add(PipelineUi.HueIcon(node.Kind, "md"));
            var text = new VisualElement().Classes("z-grow");
            text.Add(new Label(node.DisplayName).Classes("z-pl-step-title"));
            text.Add(ZettaUI.Hint(NodeCatalog.Spec(node.Kind)?.Blurb ?? ""));
            head.Add(text);
            _side.Add(head);
            _side.Add(StepResultsView.Build(node, _run, Actions(node)));
            if (NodeCatalog.IsSeed(node.Kind)) return;

            var section = new VisualElement().Classes("z-pl-section");
            section.Add(PipelineUi.SectionHeading(editable ? "Settings for this run" : "Settings it ran with"));
            section.Add(new VisualElement { name = "needs-note" });
            if (editable)
            {
                var host = new VisualElement().Classes("z-pl-form");
                var id = node.Id;
                _form = new FormContext
                {
                    Node = node,
                    Graph = _graph,
                    Settings = FormContext.SettingsOf(node),
                    Incoming = GraphRules.IncomingType(_graph, node.Id),
                    InRun = true,
                    Patch = patch => ChangeNode(id, patch),
                    OverlayHost = _editor.OverlayHost,
                };
                NodeForms.Build(host, _form);
                section.Add(host);
            }
            else
            {
                section.Add(ZettaUI.Text(NotHereOr(node) + "A step that has run keeps the settings it ran with — change the pipeline for the next run, or retry a failed step with new settings.", "z-muted"));
            }
            _side.Add(section);
            RefreshNeedsNote(node);
        }

        string ResultsSignature(PipelineNode node) =>
            _busy + "|" + _run.Status + "|" + string.Join(";", _run.StepsOf(node.Id).Select(s =>
                $"{s.Id}:{s.Status}:{s.Attempt}:{s.Credits}:{s.UpdatedAt.Ticks}:{s.Generation?.Status}:{s.Output?.Raw?.ToString(Newtonsoft.Json.Formatting.None)}"));

        static string NotHereOr(PipelineNode node) =>
            NodeCatalog.NotAvailableInUnity(node.Kind) ? "This step is not available in Unity. " : NodeSettings.Summary(node) + ". ";

        void RefreshNeedsNote(PipelineNode node)
        {
            var host = _side.Q("needs-note");
            if (host == null) return;
            host.Clear();
            var need = _needs.FirstOrDefault(p => p.NodeId == node.Id)?.Message;
            if (need == null) return;
            var colon = need.IndexOf(": ", StringComparison.Ordinal);
            var what = colon >= 0 ? need.Substring(colon + 2) : need;
            if (what.Length > 0) what = char.ToUpperInvariant(what[0]) + what.Substring(1);
            host.Add(PipelineUi.Note($"Needs input. {what} It is set on each run, so this run needs its own before it can start.", _startTried ? "error" : "amber"));
        }

        StepActions Actions(PipelineNode node) => new StepActions
        {
            Busy = _busy,
            PipelineId = _editor.PipelineId,
            Toast = _editor.Toast,
            Run = step => _ = Act(() => PipelinesApi.RunStepAsync(RunId, step.Id)),
            Retry = (step, withCurrent) =>
            {
                var current = _graph.Node(node.Id) ?? node;
                var settings = NodeSettings.Of(current);
                JObject request = null;
                if (withCurrent) request = RequestBuilder.RequestFor(current.Kind, settings, new NodeContext(GraphRules.IncomingType(_graph, current.Id), _editor.Gates));
                _ = Act(() => withCurrent ? PipelinesApi.RetryStepAsync(RunId, step.Id, request, settings) : PipelinesApi.RetryStepAsync(RunId, step.Id));
            },
            ConfirmParts = (step, keys, extra) => _ = Act(() => PipelinesApi.ChoosePartsAsync(RunId, step.Id, keys, extra)),
        };

        VisualElement Overview()
        {
            var box = new VisualElement().Classes("z-pl-overview");
            var head = new VisualElement().Classes("z-row");
            head.Add(PipelineUi.Thumb(_summary?.Seed?.ThumbnailUrl, "z-pl-thumb--overview"));
            var text = new VisualElement().Classes("z-grow");
            text.Add(new Label(Name).Classes("z-pl-step-title"));
            text.Add(ZettaUI.Hint($"{(_run.Status == RunStatuses.Draft ? "Made" : "Started")} {PipelineUi.WhenRun(_run.CreatedAt)}"));
            head.Add(text);
            box.Add(head);

            if (_run.Status == RunStatuses.Draft)
            {
                if (_needs.Count > 0)
                {
                    var note = new VisualElement().Classes("z-pl-note");
                    note.Add(new Label($"This run hasn’t started. {(_needs.Count == 1 ? "One step needs" : $"{_needs.Count} steps need")} its own input first:").Classes("z-pl-note-text"));
                    foreach (var need in _needs)
                    {
                        var id = need.NodeId;
                        note.Add(ZettaUI.Button(need.Message, () => { if (id != null) SelectStep(id); }, "z-button--small", "z-button--ghost", "z-pl-link"));
                    }
                    note.Add(new Label("Fill each in, then press Start below.").Classes("z-pl-note-text"));
                    box.Add(note);
                }
                else box.Add(PipelineUi.Note("This run hasn’t started. Everything it needs is set — check the steps, then press Start below."));
            }
            else if (_run.Status == RunStatuses.Awaiting)
                box.Add(PipelineUi.Note("This run is waiting for you. Select the step marked Waiting for you to run it, or press Run the next step below."));

            var table = new VisualElement().Classes("z-pl-props");
            void Row(string label, string value)
            {
                var row = new VisualElement().Classes("z-row");
                row.Add(new Label(label).Classes("z-pl-prop-label"));
                row.Add(new Label(value).Classes("z-pl-prop-value"));
                table.Add(row);
            }
            Row("State", RunStatuses.Label(_run.Status));
            Row("Mode", RunStatuses.ModeLabel(_run.Mode));
            if (_summary?.Progress != null) Row("Steps done", $"{_summary.Progress.Done} of {_summary.Progress.Total}");
            Row("Spent", $"{_run.SpentCredits} cr");
            Row("Estimated", $"up to {_run.EstimatedCredits} cr");
            box.Add(table);
            box.Add(ZettaUI.Hint("Select a step to see what it made."));
            return box;
        }
    }
}
