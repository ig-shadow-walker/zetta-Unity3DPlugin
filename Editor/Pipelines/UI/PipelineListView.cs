using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Pipelines.UI
{
    /// <summary>
    /// YOUR PIPELINES (pipeline-list.tsx). Empty, it is one centred invitation — "Build your first pipeline" and New
    /// pipeline. Otherwise a card each: its title, its steps, its runs at a glance; Open, Duplicate, Delete (pressed twice).
    /// No templates.
    /// </summary>
    public sealed class PipelineListView : VisualElement
    {
        readonly Action<long> _open;
        readonly ScrollView _scroll;
        readonly Label _error;
        long? _confirming;
        bool _creating;
        List<PipelineSummary> _list;

        public PipelineListView(Action<long> open)
        {
            _open = open;
            AddToClassList("z-pl-list");
            _error = new Label().Classes("z-error", "z-pl-pad").Visible(false);
            Add(_error);
            _scroll = new ScrollView(ScrollViewMode.Vertical).Classes("z-scroll");
            Add(_scroll);
            _scroll.Add(ZettaUI.Hint("Loading your pipelines…"));
            _ = LoadAsync();
        }

        public async Task LoadAsync()
        {
            try
            {
                _list = await PipelinesApi.ListAsync();
                ShowError(null);
                Render();
            }
            catch (ZettaApiException e)
            {
                ShowError(ZettaCatalog.Explain(e));
                if (_list == null)
                {
                    _scroll.Clear();
                    _scroll.Add(ZettaUI.Button("Try again", () => _ = LoadAsync(), "z-button--small"));
                }
            }
        }

        void ShowError(string message)
        {
            _error.text = message ?? "";
            _error.Visible(!string.IsNullOrEmpty(message));
        }

        Button NewButton()
        {
            var button = ZettaUI.Button(_creating ? "Creating…" : "+ New pipeline", () => _ = CreateAsync(), "z-button--primary");
            button.SetEnabled(!_creating);
            return button;
        }

        /// <summary>A new pipeline is completely empty — no seed placed for you.</summary>
        async Task CreateAsync()
        {
            if (_creating) return;
            _creating = true;
            Render();
            try
            {
                var pipeline = await PipelinesApi.CreateAsync("Untitled pipeline", PipelineGraph.Empty());
                _open(pipeline.Id);
            }
            catch (ZettaApiException e)
            {
                ShowError(ZettaCatalog.Explain(e));
            }
            finally
            {
                _creating = false;
                if (panel != null) Render();
            }
        }

        void Render()
        {
            _scroll.Clear();
            if (_list == null) return;
            if (_list.Count == 0)
            {
                var empty = new VisualElement().Classes("z-pl-empty");
                empty.Add(new Label("Build your first pipeline").Classes("z-pl-empty-title"));
                empty.Add(ZettaUI.Text("Chain the Studio’s tools into one run from a picture or a model — generate, isolate parts, retopologise, unwrap, texture and rig — automatically to the end, or a step at a time.", "z-muted"));
                empty.Add(NewButton());
                _scroll.Add(empty);
                return;
            }

            var head = new VisualElement().Classes("z-row", "z-pl-list-head");
            var titles = new VisualElement().Classes("z-grow");
            titles.Add(new Label("3D Pipelines").Classes("z-pl-list-title"));
            titles.Add(ZettaUI.Hint($"{_list.Count} pipeline{(_list.Count == 1 ? "" : "s")} · the Studio’s tools, chained"));
            head.Add(titles);
            head.Add(NewButton());
            _scroll.Add(head);

            var grid = new VisualElement().Classes("z-pl-cards");
            foreach (var pipeline in _list) grid.Add(Card(pipeline));
            _scroll.Add(grid);
        }

        VisualElement Card(PipelineSummary pipeline)
        {
            var card = new VisualElement().Classes("z-pl-card");
            var id = pipeline.Id;
            var top = new VisualElement().Classes("z-row");
            var mark = new Label("◆").Classes("z-pl-icon", "z-pl-icon--md", "z-pl-icon--signal");
            top.Add(mark);
            var text = new VisualElement().Classes("z-grow");
            text.Add(new Label(pipeline.Title).Classes("z-pl-card-title"));
            text.Add(ZettaUI.Hint($"{pipeline.Steps} step{(pipeline.Steps == 1 ? "" : "s")}{(pipeline.LastRun == null ? " · Not run yet" : "")}"));
            top.Add(text);
            card.Add(top);
            top.RegisterCallback<ClickEvent>(_ => _open(id));

            var counts = RunCounts(pipeline.RunCounts);
            if (counts != null) card.Add(counts);

            var actions = new VisualElement().Classes("z-row", "z-pl-card-actions");
            actions.Add(ZettaUI.Button("Open", () => _open(id), "z-button--small"));
            actions.Add(ZettaUI.Spacer());
            var duplicate = ZettaUI.Button("Duplicate", null, "z-button--small", "z-button--ghost");
            duplicate.clicked += () => _ = ZettaUI.Run(duplicate, _error, async () =>
            {
                await PipelinesApi.DuplicateAsync(id);
                await LoadAsync();
            });
            duplicate.tooltip = $"Duplicate {pipeline.Title}";
            actions.Add(duplicate);
            var confirming = _confirming == id;
            var delete = ZettaUI.Button(confirming ? "Delete?" : "Delete", null, "z-button--small", "z-button--ghost", confirming ? "z-button--danger" : null);
            delete.tooltip = confirming ? $"Press again to delete {pipeline.Title}" : $"Delete {pipeline.Title}";
            delete.clicked += () =>
            {
                if (_confirming != id)
                {
                    _confirming = id;
                    Render();
                    return;
                }
                _confirming = null;
                _ = ZettaUI.Run(delete, _error, async () =>
                {
                    await PipelinesApi.DeleteAsync(id);
                    if (PipelinesWindow.RememberedPipeline == id) PipelinesWindow.RememberedPipeline = null;
                    await LoadAsync();
                });
            };
            actions.Add(delete);
            card.Add(actions);
            return card;
        }

        /// <summary>Its runs at a glance: running, need you, done, stopped — the Runs tab's dots.</summary>
        static VisualElement RunCounts(Dictionary<string, int> counts)
        {
            if (counts == null) return null;
            int N(string status) => counts.TryGetValue(status, out var n) ? n : 0;
            var chips = new[]
            {
                (RunTone.Running, N(RunStatuses.Running) + N(RunStatuses.Paused), "running"),
                (RunTone.Attention, N(RunStatuses.Awaiting) + N(RunStatuses.Draft), "need you"),
                (RunTone.Done, N(RunStatuses.Done), "done"),
                (RunTone.Failed, N(RunStatuses.Failed) + N(RunStatuses.Cancelled), "stopped"),
            }.Where(c => c.Item2 > 0).ToList();
            if (chips.Count == 0) return null;
            var row = new VisualElement().Classes("z-row", "z-row--wrap");
            foreach (var (tone, n, label) in chips)
            {
                var chip = new VisualElement().Classes("z-pl-count");
                chip.Add(PipelineUi.RunDot(tone));
                chip.Add(new Label($"{n} {(tone == RunTone.Attention && n == 1 ? "needs you" : label)}"));
                row.Add(chip);
            }
            return row;
        }
    }
}
