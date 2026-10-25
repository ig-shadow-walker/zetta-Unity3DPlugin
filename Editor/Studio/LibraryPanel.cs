using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// The user's library: every 3D generation (import, place, drag into the scene, rename, delete — one or
    /// several at once — send to a tool, fork) and, on a second tab, their 2D images. Gaussian splats made on the
    /// web are listed too, but are not supported in Unity: they only open on zetta3d.ai or get deleted.
    /// A 3D Pipelines run's models arrive as one entry (groupRuns) and are drawn as one collapsible group row.
    /// </summary>
    public class LibraryPanel : StudioPanel
    {
        const int PageSize = 24;
        /// <summary>How many deletes run at once — quick, but not enough to trip the rate limit.</summary>
        const int DeletesAtOnce = 4;

        static readonly (string id, string label, string[] statuses)[] StatusFilters =
        {
            ("all", "All", null),
            ("ready", "Ready", new[] { "success" }),
            ("running", "In progress", new[] { "pending", "queued", "processing", "saving" }),
            ("failed", "Failed", new[] { "failed", "cancelled" }),
        };

        static readonly string[] Features =
        {
            "", "txt23d", "img23d", "multiView23d", "retopology", "segmentation", "smartSegmentation", "uvUnwrap",
            "retexture", "autoRigging", "convertFormat", "upload",
        };

        public override string Id => ZettaStudioWindow.LibraryTab;
        public override string Title => "My Generations";

        ZettaUI.Segmented _mode;
        VisualElement _models;
        ImageLibraryView _images;
        VisualElement _grid;
        Label _summary;
        Label _error;
        Label _notice;
        Button _prev;
        Button _next;
        Button _select;
        ToolbarSearch _search;
        ZettaUI.Segmented _statusFilter;
        VisualElement _selectionBar;
        Label _selectionCount;
        Button _toggleAll;
        Button _deleteSelected;
        Button _doneSelecting;
        string _feature = "";
        int _page = 1;
        int _total;
        bool _loaded;
        int _request;
        bool _picking;
        readonly HashSet<long> _picked = new HashSet<long>();
        /// <summary>The page's entries as the server sent them: a pipeline run is one entry carrying its models.</summary>
        readonly List<Generation> _items = new List<Generation>();
        /// <summary>Which run groups are open; kept across reloads.</summary>
        readonly HashSet<long> _openRuns = new HashSet<long>();
        (int done, int total)? _deleting;
        /// <summary>Imports under way (generation id → progress 0–1); outlives a redraw of the grid.</summary>
        static readonly Dictionary<long, float> Importing = new Dictionary<long, float>();
        /// <summary>The progress bar each importing card shows right now.</summary>
        readonly Dictionary<long, ZettaUI.ProgressBar> _bars = new Dictionary<long, ZettaUI.ProgressBar>();

        /// <summary>Every model on the page, a run's included — what select-all and the dialogs look through.</summary>
        List<Generation> Models => RunGroups.Flatten(_items).ToList();

        protected override void Build(VisualElement root)
        {
            var bar = new VisualElement().Classes("z-subbar", "z-toolbar");
            _mode = new ZettaUI.Segmented(new[] { "models", "images" }, new[] { "3D models", "Images" }, "models");
            _mode.AddToClassList("z-mode-switch");
            _mode.Changed += v =>
            {
                _models.Visible(v == "models");
                _images.Visible(v == "images");
                if (v == "images") _images.EnsureLoaded();
            };
            bar.Add(_mode);
            root.Add(bar);

            _models = new VisualElement().Classes("z-grow");
            root.Add(_models);

            // Two wrapping rows that reflow in a narrow docked window rather than squeeze:
            // search · kind · Select · Refresh, then status · Place in scene.
            var filters = new VisualElement().Classes("z-subbar", "z-toolbar");
            var top = new VisualElement().Classes("z-toolbar-row");
            _search = new ToolbarSearch("Search by title or prompt", () =>
            {
                _page = 1;
                _ = Load();
            });
            _search.AddToClassList("z-toolbar-search");
            top.Add(_search);
            var feature = ZettaUI.Dropdown(null, Features.ToList(), Features.Select(f => f == "" ? "All kinds" : ZettaCatalog.FeatureLabel(f)).ToList(), "", v =>
            {
                _feature = v;
                _page = 1;
                _ = Load();
            });
            feature.AddToClassList("z-library-feature");
            top.Add(feature);
            var buttons = new VisualElement().Classes("z-toolbar-buttons");
            _select = ZettaUI.Button("Select", () => SetPicking(true), "z-button--small", "z-button--ghost");
            _select.tooltip = "Select several models to delete them together";
            buttons.Add(_select);
            buttons.Add(ZettaUI.Button("Refresh", () => _ = Load(), "z-button--small", "z-button--ghost"));
            top.Add(buttons);
            filters.Add(top);

            var second = new VisualElement().Classes("z-toolbar-row");
            _statusFilter = new ZettaUI.Segmented(StatusFilters.Select(s => s.id).ToList(), StatusFilters.Select(s => s.label).ToList(), "all");
            _statusFilter.Changed += v =>
            {
                _page = 1;
                _ = Load();
            };
            _statusFilter.AddToClassList("z-status-filter");
            second.Add(_statusFilter);
            second.Add(ZettaUI.PlaceInSceneToggle());
            filters.Add(second);

            // The batch bar while selecting: how many, all on this page, delete, done.
            _selectionBar = new VisualElement().Classes("z-row", "z-row--wrap", "z-selection-bar").Visible(false);
            _selectionCount = new Label().Classes("z-grow", "z-text");
            _selectionBar.Add(_selectionCount);
            _toggleAll = ZettaUI.Button("Select all on this page", ToggleAllOnPage, "z-button--small");
            _selectionBar.Add(_toggleAll);
            _deleteSelected = ZettaUI.Button("Delete", () => _ = DeleteSelected(), "z-button--small", "z-button--danger");
            _selectionBar.Add(_deleteSelected);
            _doneSelecting = ZettaUI.Button("Done", () => SetPicking(false), "z-button--small", "z-button--ghost");
            _selectionBar.Add(_doneSelecting);
            filters.Add(_selectionBar);
            _models.Add(filters);

            _error = new Label().Classes("z-error", "z-pad").Visible(false);
            _models.Add(_error);
            _notice = ZettaUI.Hint("").Classes("z-pad").Visible(false);
            _models.Add(_notice);

            var scroll = Scroll();
            _grid = new VisualElement().Classes("z-grid");
            scroll.Add(_grid);
            _models.Add(scroll);

            var pager = new VisualElement().Classes("z-row", "z-pager");
            _prev = ZettaUI.Button("‹ Previous", () =>
            {
                _page = Math.Max(1, _page - 1);
                _ = Load();
            }, "z-button--small", "z-button--ghost");
            _next = ZettaUI.Button("Next ›", () =>
            {
                _page++;
                _ = Load();
            }, "z-button--small", "z-button--ghost");
            _summary = ZettaUI.Hint("");
            pager.Add(_prev);
            pager.Add(ZettaUI.Spacer());
            pager.Add(_summary);
            pager.Add(ZettaUI.Spacer());
            pager.Add(_next);
            _models.Add(pager);
            _models.Add(ZettaUI.Hint("Tip: drag a model into the Scene view or Hierarchy to import and place it there.").Classes("z-pad"));

            _images = new ImageLibraryView(() => Ctx?.Window);
            _images.Visible(false);
            root.Add(_images);

            ZettaJobs.Finished += OnJobFinished;
            ZettaProjectIndex.Changed += Redraw;
            root.RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                ZettaJobs.Finished -= OnJobFinished;
                ZettaProjectIndex.Changed -= Redraw;
            });
            root.RegisterCallback<AttachToPanelEvent>(_ =>
            {
                ZettaJobs.Finished -= OnJobFinished;
                ZettaJobs.Finished += OnJobFinished;
                ZettaProjectIndex.Changed -= Redraw;
                ZettaProjectIndex.Changed += Redraw;
            });
        }

        void OnJobFinished(ZettaJobs.Job job)
        {
            if (_loaded && _deleting == null && _grid?.panel != null) _ = Load();
        }

        public override void OnShown()
        {
            if (!_loaded) _ = Load();
        }

        public void ShowImages()
        {
            _mode.Set("images", true);
        }

        /// <summary>The importer's note for the user (a collider, the UV layout picture, …).</summary>
        void ShowNotice(string message)
        {
            _notice.text = message ?? "";
            _notice.Visible(!string.IsNullOrEmpty(message));
        }

        async Task Load()
        {
            var request = ++_request;
            _loaded = true;
            _error.Visible(false);
            _prev.SetEnabled(false);
            _next.SetEnabled(false);
            if (_grid.childCount == 0) _grid.Add(ZettaUI.Hint("Loading your library…"));
            try
            {
                var statuses = StatusFilters.First(s => s.id == _statusFilter.Value).statuses;
                var page = await ZettaApi.ListGenerationsAsync(_page, PageSize, _search.Value, _feature == "" ? null : _feature, statuses, groupRuns: true);
                if (request != _request) return;
                _total = page.Total;
                var pages = Math.Max(1, (int)Math.Ceiling(_total / (double)PageSize));
                if (_page > pages && _total > 0)
                {
                    _page = pages;
                    _ = Load();
                    return;
                }
                _items.Clear();
                _items.AddRange(page.Items);
                Redraw();
                _summary.text = _total == 0 ? "" : $"Page {_page} of {pages} · {_total:N0} models";
                _prev.SetEnabled(_page > 1);
                _next.SetEnabled(_page < pages);
                SyncSelection();
            }
            catch (ZettaApiException e)
            {
                if (request != _request) return;
                _grid.Clear();
                _error.text = ZettaCatalog.Explain(e);
                _error.Visible(true);
            }
        }

        /// <summary>Draws the page again from what is loaded — after an import, or when the project's imports change.</summary>
        void Redraw()
        {
            if (_grid == null || !_loaded) return;
            _bars.Clear();
            _grid.Clear();
            foreach (var row in _items) _grid.Add(row.RunGroup != null && row.RunGroup.Members.Count > 0 ? RunGroupRow(row.RunGroup) : Tile(row));
            if (_items.Count == 0)
                _grid.Add(ZettaUI.Hint(string.IsNullOrEmpty(_search.Value) && _feature == "" && _statusFilter.Value == "all"
                    ? "Models you generate appear here."
                    : "Nothing matches those filters."));
            SyncSelection();
        }

        // ─── Tiles ───────────────────────────────────────────────────────────────────

        /// <summary>"GLB · Textured", "GLB · Collider", or the type alone.</summary>
        public static string FormatLabel(ResultFile file)
        {
            var type = (string.IsNullOrEmpty(file.Type) ? file.Extension : file.Type).ToUpperInvariant();
            if (type == "") type = "FILE";
            if ((file.Key ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) type += " (zip)";
            return string.IsNullOrEmpty(file.Label) ? type : $"{type} · {file.Label}";
        }

        /// <summary>The engine's name for a badge — only for engines Zetta names; never a raw id.</summary>
        public static string ModelBadge(string model)
        {
            if (string.IsNullOrEmpty(model)) return null;
            var label = ZettaCatalog.ModelLabel(model);
            return label == model ? null : label;
        }

        /// <param name="group">The pipeline run the model belongs to, when it is drawn inside its group.</param>
        /// <param name="step">Its step tag inside the group ("Step 2 · Retopology").</param>
        VisualElement Tile(Generation row, RunGroup group = null, string step = null)
        {
            var splat = row.IsSplat;
            var tile = new VisualElement().Classes("z-tile");
            var image = new VisualElement().Classes("z-tile-image");
            ZettaUI.LoadInto(image, row.ThumbnailUrl, "gen-" + row.Id);
            tile.Add(image);
            var check = ZettaUI.Chip("Selected", "signal").Classes("z-tile-check").Visible(false);
            image.Add(check);

            var title = new Label(row.DisplayTitle).Classes("z-tile-title");
            tile.Add(title);
            var chips = new VisualElement().Classes("z-row", "z-row--wrap");
            if (step != null)
            {
                var stepChip = ZettaUI.Chip(step).Classes("z-step-chip");
                stepChip.tooltip = step;
                chips.Add(stepChip);
            }
            chips.Add(ZettaUI.Chip(row.StatusLabel, row.IsSuccess ? "success" : row.Status is "failed" or "cancelled" ? "error" : "amber"));
            // Made by a pipeline: told apart from models made by hand. Otherwise it behaves like any other row.
            if (row.PipelineRunId is { } runId) chips.Add(PipelineChip(runId));
            var inProject = !splat && row.IsSuccess ? ZettaProjectIndex.Find(row.Id) : null;
            if (inProject != null)
            {
                var here = ZettaUI.Chip("In project", "success");
                here.tooltip = $"Already imported to {inProject.Folder}";
                chips.Add(here);
            }
            chips.Add(ZettaUI.Chip(ZettaCatalog.FeatureLabel(row.Feature)));
            if (ModelBadge(row.Model) is { } badge) chips.Add(ZettaUI.Chip(badge));
            if (splat)
            {
                var unsupported = ZettaUI.Chip("Splat — not supported in Unity", "amber");
                unsupported.tooltip = "Gaussian splats made on zetta3d.ai are not supported in Unity. Open it on zetta3d.ai to view it.";
                chips.Add(unsupported);
            }
            // A world's mesh export holds both meshes: say which is which.
            if (row.Feature == "worldMeshExport")
                foreach (var file in row.MeshFiles.Where(f => !string.IsNullOrEmpty(f.Label)))
                    chips.Add(ZettaUI.Chip(FormatLabel(file)));
            tile.Add(chips);

            var actions = new VisualElement().Classes("z-row", "z-row--wrap", "z-tile-actions");
            if (splat) actions.Add(ZettaUI.Button("Open on zetta3d.ai", () => OpenOnSite(row), "z-button--small"));
            else if (row.IsSuccess && Importing.ContainsKey(row.Id))
            {
                var busy = ZettaUI.Button("Importing…", null, "z-button--small");
                busy.SetEnabled(false);
                actions.Add(busy);
            }
            else if (row.IsSuccess && inProject != null)
            {
                var show = ZettaUI.Button("Show", () => ShowInProject(inProject), "z-button--small");
                show.tooltip = $"Select it in the Project window ({inProject.Folder})";
                actions.Add(show);
                var place = ZettaUI.Button("Place", () => PlaceExisting(row, inProject), "z-button--small", "z-button--ghost");
                place.tooltip = "Put the imported model in the open scene — no download";
                actions.Add(place);
            }
            else if (row.IsSuccess)
            {
                var import = ZettaUI.Button("Import", () => _ = Import(row), "z-button--small");
                import.tooltip = "Download it into the project" + (ZettaProjectSettings.Instance.PlaceImportedInScene ? " and place it in the open scene" : "") +
                                 " — change this with Place in scene above";
                actions.Add(import);
            }
            actions.Add(ZettaUI.Spacer());
            var more = ZettaUI.Button("⋯", null, "z-button--small", "z-button--ghost");
            more.clicked += () => Menu(row, tile, title, group);
            actions.Add(more);
            tile.Add(actions);

            if (Importing.TryGetValue(row.Id, out var progress))
            {
                var bar = new ZettaUI.ProgressBar();
                bar.Set(progress);
                _bars[row.Id] = bar;
                tile.Add(bar);
            }

            if (row.IsSuccess && !splat)
            {
                tile.tooltip = inProject != null
                    ? "In the project. Double-click to place it in the scene. Drag into the Scene view or Hierarchy to place it there."
                    : "Double-click to import and place. Drag into the Scene view or Hierarchy to place it there.";
                tile.RegisterCallback<MouseDownEvent>(e =>
                {
                    if (_picking || e.clickCount != 2 || e.button != 0) return;
                    var existing = ZettaProjectIndex.Find(row.Id);
                    if (existing != null) PlaceExisting(row, existing);
                    else _ = Import(row, true);
                });
                LibraryDrag.MakeDraggable(tile, row);
            }

            // Select mode: a click on the tile (not on one of its buttons) picks it.
            tile.RegisterCallback<ClickEvent>(e =>
            {
                if (!_picking || _deleting != null || e.target is Button) return;
                if (!_picked.Remove(row.Id)) _picked.Add(row.Id);
                SyncSelection();
            });
            tile.userData = row.Id;
            return tile;
        }

        /// <summary>The "Pipeline" tag, in the pipelines' own hue; the run's number rides in the tooltip.</summary>
        static Label PipelineChip(long runId)
        {
            var chip = ZettaUI.Chip("Pipeline", "signal").Classes("z-pipeline-chip");
            chip.tooltip = $"Made by pipeline run #{runId}";
            return chip;
        }

        static void OpenOnSite(Generation row) => Application.OpenURL($"{ZettaConfig.SiteBase}/3d-studio?generation={row.Id}");

        void Fork(Generation row)
        {
            if (Ctx?.Window == null || !Ctx.Window.Fork(row))
            {
                _error.text = "This model cannot be forked.";
                _error.Visible(true);
            }
        }

        void Menu(Generation row, VisualElement tile, Label title, RunGroup group = null)
        {
            var menu = new GenericMenu();
            // A splat row is not supported in Unity: it only opens on the site, or is deleted.
            if (row.IsSplat)
            {
                menu.AddItem(new GUIContent("Open on zetta3d.ai"), false, () => OpenOnSite(row));
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Delete…"), false, () => _ = Delete(row));
                menu.ShowAsContext();
                return;
            }
            if (row.IsSuccess)
            {
                var existing = ZettaProjectIndex.Find(row.Id);
                if (existing != null)
                {
                    menu.AddItem(new GUIContent("Show in Project"), false, () => ShowInProject(existing));
                    menu.AddItem(new GUIContent("Place in scene"), false, () => PlaceExisting(row, existing));
                    menu.AddItem(new GUIContent("Import again (download a fresh copy)"), false, () => _ = Import(row));
                }
                else if (!Importing.ContainsKey(row.Id))
                    menu.AddItem(new GUIContent(ZettaProjectSettings.Instance.PlaceImportedInScene ? "Import and place in scene" : "Import"), false, () => _ = Import(row));
                if (row.UvLayout != null) menu.AddItem(new GUIContent("Save UV layout picture"), false, () => _ = SaveUvLayout(row));
                menu.AddSeparator("");
                var processable = ZettaCatalog.ProcessableFeatures.Contains(row.Feature ?? "");
                foreach (var (tab, label) in new[]
                         {
                             (ZettaStudioWindow.SegmentTab, "Segment"),
                             (ZettaStudioWindow.RetopologyTab, "Retopology"),
                             (ZettaStudioWindow.SmartUvTab, "Smart UV"),
                             (ZettaStudioWindow.TextureTab, "Texture"),
                             (ZettaStudioWindow.RigTab, "Auto Rig"),
                             (ZettaStudioWindow.ConvertTab, "Convert"),
                         })
                {
                    // The tool that made a row does not run on it again.
                    var made = (tab == ZettaStudioWindow.RetopologyTab && row.Feature == "retopology")
                               || (tab == ZettaStudioWindow.SmartUvTab && row.Feature == "uvUnwrap");
                    var content = new GUIContent("Send to/" + label);
                    if ((processable && !made) || tab == ZettaStudioWindow.ConvertTab)
                        menu.AddItem(content, false, () => Ctx.Window?.UseAsSource(row, tab));
                    else menu.AddDisabledItem(content);
                }
                menu.AddSeparator("");
            }
            // Fork: a new generation on the form, with this one's settings and pictures. Nothing runs.
            if (StudioFork.CanFork(row)) menu.AddItem(new GUIContent("Fork"), false, () => Fork(row));
            if (group?.PipelineId is { } pipelineId)
                menu.AddItem(new GUIContent($"Open run #{group.RunId}"), false, () => ZettaStudioHooks.OpenRun(pipelineId, group.RunId));
            menu.AddItem(new GUIContent("Open on zetta3d.ai"), false, () => OpenOnSite(row));
            menu.AddItem(new GUIContent("Rename…"), false, () => Rename(row, tile, title));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Delete…"), false, () => _ = Delete(row));
            menu.ShowAsContext();
        }

        // ─── Pipeline run groups ─────────────────────────────────────────────────────

        /// <summary>
        /// A pipeline run's models, together: one row — fanned thumbnails, the run's name, "N models · pipeline ·
        /// run #id", the Pipeline tag and a way to the run — that opens in place to show each model on its usual
        /// tile, in the order the run made them, each with its step.
        /// </summary>
        VisualElement RunGroupRow(RunGroup group)
        {
            var (name, detail) = RunGroups.Name(group);
            var running = group.Members.Any(m => m.IsRunning);
            var box = new VisualElement().Classes("z-run");
            box.userData = group;

            var header = new VisualElement().Classes("z-run-header");
            header.Add(new Label("▸").Classes("z-run-chevron"));

            // Up to three of the run's pictures, fanned; the first drawn last so it sits on top.
            var shown = group.Members.Take(3).ToList();
            var thumbs = new VisualElement().Classes("z-run-thumbs");
            thumbs.style.width = 40 + Math.Max(0, shown.Count - 1) * 8;
            for (var i = shown.Count - 1; i >= 0; i--)
            {
                var thumb = new VisualElement().Classes("z-run-thumb");
                thumb.style.left = i * 8;
                ZettaUI.LoadInto(thumb, shown[i].ThumbnailUrl, "gen-" + shown[i].Id);
                thumbs.Add(thumb);
            }
            header.Add(thumbs);

            var text = new VisualElement().Classes("z-run-text");
            text.Add(new Label(name).Classes("z-run-name"));
            text.Add(new Label(running ? $"Still making · {detail}" : detail).Classes("z-run-detail"));
            header.Add(text);
            header.Add(ZettaUI.Chip("Selected", "signal").Classes("z-run-check").Visible(false));
            header.Add(PipelineChip(group.RunId));

            if (group.PipelineId is { } pipelineId)
            {
                var open = ZettaUI.Button("Open run", () => ZettaStudioHooks.OpenRun(pipelineId, group.RunId), "z-button--small", "z-button--ghost");
                open.tooltip = $"Open run #{group.RunId} in 3D Pipelines";
                header.Add(open);
            }
            var more = ZettaUI.Button("⋯", null, "z-button--small", "z-button--ghost");
            more.tooltip = "Actions for the whole run";
            more.clicked += () => RunMenu(group, name);
            header.Add(more);
            header.tooltip = $"Pipeline run: {name} — {detail}";

            // A click on the header (not on one of its buttons) opens or closes the run — or, while selecting, picks all of it.
            header.RegisterCallback<ClickEvent>(e =>
            {
                if (e.target is Button || _deleting != null) return;
                if (_picking)
                {
                    PickRun(group);
                    return;
                }
                if (!_openRuns.Remove(group.RunId)) _openRuns.Add(group.RunId);
                OpenRunBody(box, group, _openRuns.Contains(group.RunId));
            });
            box.Add(header);
            OpenRunBody(box, group, _openRuns.Contains(group.RunId) || _picking);
            return box;
        }

        /// <summary>Shows or hides a run's models; their tiles are built the first time it opens.</summary>
        void OpenRunBody(VisualElement box, RunGroup group, bool open)
        {
            var chevron = box.Q<Label>(className: "z-run-chevron");
            if (chevron != null) chevron.text = open ? "▾" : "▸";
            var body = box.Q<VisualElement>(className: "z-run-members");
            if (open && body == null)
            {
                body = new VisualElement().Classes("z-grid", "z-run-members");
                var ordered = RunGroups.InRunOrder(group.Members);
                for (var i = 0; i < ordered.Count; i++) body.Add(Tile(ordered[i], group, RunGroups.StepText(ordered[i], i)));
                box.Add(body);
                // New tiles take the current picks.
                if (_picking) _grid.schedule.Execute(() => SyncSelection());
            }
            body?.Visible(open);
        }

        void RunMenu(RunGroup group, string name)
        {
            var menu = new GenericMenu();
            var importable = group.Members.Where(m => m.IsSuccess && !m.IsSplat).ToList();
            var importAll = new GUIContent(importable.Count > 0 ? $"Import all ({importable.Count})" : "Import all");
            if (importable.Count > 0) menu.AddItem(importAll, false, () => _ = ImportRun(group));
            else menu.AddDisabledItem(importAll);
            if (group.PipelineId is { } pipelineId)
                menu.AddItem(new GUIContent($"Open run #{group.RunId}"), false, () => ZettaStudioHooks.OpenRun(pipelineId, group.RunId));
            menu.AddSeparator("");
            var allPicked = _picking && group.Members.All(m => _picked.Contains(m.Id));
            menu.AddItem(new GUIContent(allPicked ? "Unselect this run" : "Select all in this run"), false, () =>
            {
                if (!_picking) SetPicking(true);
                PickRun(group);
            });
            menu.AddItem(new GUIContent("Delete all…"), false, () => _ = DeleteRun(group, name));
            menu.ShowAsContext();
        }

        /// <summary>Ticks every model in the run — or, when all of them already are, unticks them.</summary>
        void PickRun(RunGroup group)
        {
            if (_deleting != null) return;
            var all = group.Members.All(m => _picked.Contains(m.Id));
            foreach (var member in group.Members)
            {
                if (all) _picked.Remove(member.Id);
                else _picked.Add(member.Id);
            }
            SyncSelection();
        }

        /// <summary>Imports every finished mesh the run made, first step first. Splats are skipped (not supported in Unity).</summary>
        async Task ImportRun(RunGroup group)
        {
            ShowNotice(null);
            _error.Visible(false);
            // Already in the project: nothing to download again.
            var members = RunGroups.InRunOrder(group.Members)
                .Where(m => m.IsSuccess && !m.IsSplat && !ZettaProjectIndex.Contains(m.Id) && !Importing.ContainsKey(m.Id)).ToList();
            if (members.Count == 0)
            {
                Ctx.Window?.Toast("Every model from this run is already in the project");
                return;
            }
            var imported = 0;
            string lastError = null;
            foreach (var member in members)
            {
                var position = imported + 1;
                ShowNotice($"Importing {position} of {members.Count}…");
                Importing[member.Id] = 0f;
                Redraw();
                try
                {
                    await ZettaImporter.ImportAsync(member, null, null, null, p =>
                    {
                        Importing[member.Id] = p;
                        if (_bars.TryGetValue(member.Id, out var bar)) bar.Set(p);
                        ShowNotice($"Importing {position} of {members.Count}… {Mathf.RoundToInt(p * 100f)}%");
                    });
                    imported++;
                }
                catch (ZettaApiException e)
                {
                    lastError = ZettaCatalog.Explain(e);
                }
                catch (Exception e)
                {
                    lastError = e.Message;
                }
                finally
                {
                    Importing.Remove(member.Id);
                }
            }
            Redraw();
            ShowNotice(null);
            Ctx.Window?.Toast($"Imported {imported} of {members.Count} models");
            if (lastError != null)
            {
                _error.text = $"{members.Count - imported} of {members.Count} could not be imported. {lastError}";
                _error.Visible(true);
            }
        }

        /// <summary>"Delete all" for a run: every model it made, after the warning (the web's run delete dialog).</summary>
        async Task DeleteRun(RunGroup group, string name)
        {
            var ids = group.Members.Select(m => m.Id).ToList();
            if (ids.Count == 0 || _deleting != null) return;
            var (_, message, ok) = BulkDeleteText(ids.Count);
            var heading = $"Delete all {ids.Count:N0} {(ids.Count == 1 ? "model" : "models")} from “{name}”?";
            if (!EditorUtility.DisplayDialog(heading, message, ok, "Cancel")) return;

            _error.Visible(false);
            var (deleted, failed) = await RunDeletes(ids, (done, total) =>
            {
                _deleting = (done, total);
                ShowNotice($"Deleting {done} of {total}…");
                SyncSelection();
            }, ZettaApi.DeleteGenerationAsync);
            _deleting = null;
            ShowNotice(null);
            foreach (var id in deleted) _picked.Remove(id);
            SyncSelection();
            await Load();
            if (failed.Count > 0)
            {
                _error.text = $"{failed.Count} of {deleted.Count + failed.Count} could not be deleted. Try again in a moment.";
                _error.Visible(true);
            }
        }

        // ─── Import ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Downloads a model into the project, with a progress bar on its card. Whether it is also placed in the
        /// scene follows the Place in scene setting, unless <paramref name="placeOverride"/> says otherwise.
        /// </summary>
        async Task Import(Generation row, bool? placeOverride = null)
        {
            if (Importing.ContainsKey(row.Id)) return;
            var place = placeOverride ?? ZettaProjectSettings.Instance.PlaceImportedInScene;
            ShowNotice(null);
            _error.Visible(false);
            Importing[row.Id] = 0f;
            Redraw();
            try
            {
                var result = await ZettaImporter.ImportAsync(row, null, place ? new ZettaImporter.Placement() : null, null, p =>
                {
                    Importing[row.Id] = p;
                    if (_bars.TryGetValue(row.Id, out var bar)) bar.Set(p);
                });
                if (!place && result.MainAssetPath != null)
                    EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(result.MainAssetPath));
                Ctx.Window?.Toast(place && result.Placed != null ? $"Placed {row.DisplayTitle}" : $"Imported {row.DisplayTitle}");
                var note = result.Note;
                if (!string.IsNullOrEmpty(result.UvLayoutPath))
                    note = string.IsNullOrEmpty(note) ? $"UV layout picture saved to {result.UvLayoutPath}." : $"{note} UV layout picture: {result.UvLayoutPath}.";
                ShowNotice(note);
            }
            catch (ZettaApiException e)
            {
                _error.text = $"{row.DisplayTitle}: {ZettaCatalog.Explain(e)}";
                _error.Visible(true);
            }
            catch (Exception e)
            {
                _error.text = $"{row.DisplayTitle}: {e.Message}";
                _error.Visible(true);
            }
            finally
            {
                Importing.Remove(row.Id);
                Redraw();
            }
        }

        static void ShowInProject(ZettaProjectIndex.Entry entry)
        {
            var target = (UnityEngine.Object)entry.Model ?? AssetDatabase.LoadMainAssetAtPath(entry.Folder);
            if (target == null) return;
            Selection.activeObject = target;
            EditorGUIUtility.PingObject(target);
        }

        /// <summary>Places a model that is already in the project — no download.</summary>
        void PlaceExisting(Generation row, ZettaProjectIndex.Entry entry)
        {
            var paths = entry.ModelPaths;
            if (paths.Count == 0)
            {
                _ = Import(row, true);
                return;
            }
            var placed = ZettaImporter.Place(row, paths, new ZettaImporter.Placement());
            Ctx.Window?.Toast(placed != null ? $"Placed {row.DisplayTitle}" : $"{row.DisplayTitle} could not be placed");
        }

        async Task SaveUvLayout(Generation row)
        {
            await ZettaUI.Run(null, _error, async () =>
            {
                var path = await ZettaImporter.ImportUvLayoutAsync(row);
                if (path == null) throw new StudioProblem("This model has no UV layout picture.");
                EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(path));
                Ctx.Window?.Toast("Saved the UV layout to " + path);
            });
        }

        // ─── Rename, delete ──────────────────────────────────────────────────────────

        void Rename(Generation row, VisualElement tile, Label title)
        {
            var field = ZettaUI.TextInput("Title", row.DisplayTitle, 255);
            var index = tile.IndexOf(title);
            title.Visible(false);
            tile.Insert(index + 1, field);
            field.Focus();
            field.SelectAll();

            var done = false;
            async void Commit(bool save)
            {
                if (done) return;
                done = true;
                var text = (field.value ?? "").Trim();
                field.RemoveFromHierarchy();
                title.Visible(true);
                if (!save || text == "" || text == row.DisplayTitle) return;
                title.text = text;
                try
                {
                    var updated = await ZettaApi.UpdateGenerationAsync(row.Id, title: text);
                    row.Title = updated?.Title ?? text;
                    title.text = row.DisplayTitle;
                }
                catch (ZettaApiException e)
                {
                    title.text = row.DisplayTitle;
                    _error.text = ZettaCatalog.Explain(e);
                    _error.Visible(true);
                }
            }

            field.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode is KeyCode.Return or KeyCode.KeypadEnter) Commit(true);
                else if (e.keyCode == KeyCode.Escape) Commit(false);
            });
            field.RegisterCallback<FocusOutEvent>(_ => Commit(true));
        }

        async Task Delete(Generation row)
        {
            if (!EditorUtility.DisplayDialog("Delete model",
                    $"Delete “{row.DisplayTitle}” from your Zetta 3D library? Copies already imported into this project are kept.",
                    "Delete", "Cancel"))
                return;
            await ZettaUI.Run(null, _error, async () =>
            {
                await ZettaApi.DeleteGenerationAsync(row.Id);
                _picked.Remove(row.Id);
                await Load();
            });
        }

        // ─── Select mode and bulk delete ─────────────────────────────────────────────

        void SetPicking(bool picking)
        {
            if (_deleting != null) return;
            _picking = picking;
            if (!picking) _picked.Clear();
            SyncSelection();
        }

        bool AllOnPage => Models is { Count: > 0 } models && models.All(i => _picked.Contains(i.Id));

        void ToggleAllOnPage()
        {
            var all = AllOnPage;
            foreach (var item in Models)
            {
                if (all) _picked.Remove(item.Id);
                else _picked.Add(item.Id);
            }
            SyncSelection();
        }

        void SyncSelection()
        {
            if (_selectionBar == null) return;
            _select.Visible(!_picking && _items.Count > 0);
            _selectionBar.Visible(_picking);
            var count = _picked.Count;
            _selectionCount.text = _deleting is { } progress ? $"Deleting {progress.done} of {progress.total}…"
                : count == 0 ? "Click the models to select" : $"{count:N0} selected";
            _toggleAll.text = AllOnPage ? "Clear this page" : "Select all on this page";
            _toggleAll.SetEnabled(_deleting == null && _items.Count > 0);
            _deleteSelected.text = count > 0 ? $"Delete {count:N0}" : "Delete";
            _deleteSelected.SetEnabled(_deleting == null && count > 0);
            _doneSelecting.SetEnabled(_deleting == null);
            // Tiles sit in the grid and inside open run groups alike.
            _grid.Query<VisualElement>(className: "z-tile").ForEach(tile =>
            {
                if (tile.userData is not long id) return;
                var picked = _picking && _picked.Contains(id);
                tile.EnableInClassList("z-tile--picked", picked);
                tile.Q<Label>(className: "z-tile-check")?.Visible(picked);
            });
            foreach (var run in _grid.Query<VisualElement>(className: "z-run").ToList())
            {
                if (run.userData is not RunGroup group) continue;
                var all = _picking && group.Members.Count > 0 && group.Members.All(m => _picked.Contains(m.Id));
                run.EnableInClassList("z-run--picked", all);
                run.Q<Label>(className: "z-run-check")?.Visible(all);
                // Picking shows a run's models, so each can be ticked on its own too.
                OpenRunBody(run, group, _picking || _openRuns.Contains(group.RunId));
            }
        }

        /// <summary>The delete confirmation's words for a batch (the web's bulk-delete dialog).</summary>
        public static (string title, string message, string ok) BulkDeleteText(int count)
        {
            var noun = count == 1 ? "model" : "models";
            return ($"Delete {count:N0} {noun}?",
                $"{(count == 1 ? "Its" : "Their")} model files will be removed from storage and the records deleted. This cannot be undone, and the credits spent on {(count == 1 ? "it" : "them")} are not refunded.",
                $"Delete {count:N0} {noun}");
        }

        /// <summary>
        /// Deletes every picked model through the single-delete route, a few at a time. The ones that could not
        /// be deleted stay selected, and say so; the rest are gone either way.
        /// </summary>
        async Task DeleteSelected()
        {
            var ids = _picked.ToList();
            if (ids.Count == 0 || _deleting != null) return;
            var (title, message, ok) = BulkDeleteText(ids.Count);
            if (!EditorUtility.DisplayDialog(title, message, ok, "Cancel")) return;

            _error.Visible(false);
            var (deleted, failed) = await RunDeletes(ids, (done, total) =>
            {
                _deleting = (done, total);
                SyncSelection();
            }, ZettaApi.DeleteGenerationAsync);
            _deleting = null;
            _picked.Clear();
            foreach (var id in failed) _picked.Add(id);
            if (failed.Count > 0)
            {
                _error.text = $"{failed.Count} of {deleted.Count + failed.Count} could not be deleted — they are still selected. Try again in a moment.";
                _error.Visible(true);
            }
            SyncSelection();
            await Load();
            if (failed.Count > 0) _error.Visible(true);
        }

        /// <summary>Runs <paramref name="delete"/> over the ids, <see cref="DeletesAtOnce"/> at a time.</summary>
        public static async Task<(List<long> deleted, List<long> failed)> RunDeletes(IReadOnlyCollection<long> ids, Action<int, int> progress, Func<long, Task> delete)
        {
            var queue = new Queue<long>(ids);
            var deleted = new List<long>();
            var failed = new List<long>();
            progress?.Invoke(0, ids.Count);

            async Task Worker()
            {
                while (true)
                {
                    long id;
                    lock (queue)
                    {
                        if (queue.Count == 0) return;
                        id = queue.Dequeue();
                    }
                    try
                    {
                        await delete(id);
                        lock (deleted) deleted.Add(id);
                    }
                    catch (Exception)
                    {
                        lock (deleted) failed.Add(id);
                    }
                    int done;
                    lock (deleted) done = deleted.Count + failed.Count;
                    progress?.Invoke(done, ids.Count);
                }
            }

            await Task.WhenAll(Enumerable.Range(0, Math.Min(DeletesAtOnce, ids.Count)).Select(_ => Worker()));
            return (deleted, failed);
        }
    }

    /// <summary>The 2D half of the library: the user's images, ready to save into the project.</summary>
    public class ImageLibraryView : VisualElement
    {
        static readonly string[] Features = { "", "genImg", "editImg", "composeImg" };
        static readonly string[] FeatureLabels = { "All images", "Generated", "Edited", "Composed" };

        readonly Func<ZettaStudioWindow> _window;
        readonly VisualElement _grid;
        readonly Label _error;
        readonly Label _summary;
        readonly Button _more;
        string _feature = "";
        int _page = 1;
        bool _loaded;
        int _request;
        string _textureType = "default";

        public ImageLibraryView(Func<ZettaStudioWindow> window)
        {
            _window = window;
            AddToClassList("z-grow");
            var bar = new VisualElement().Classes("z-subbar", "z-row", "z-row--wrap");
            var filter = ZettaUI.Dropdown(null, Features.ToList(), FeatureLabels.ToList(), "", v =>
            {
                _feature = v;
                Reload();
            });
            filter.AddToClassList("z-grow");
            bar.Add(filter);
            bar.Add(TextureTypeField(v => _textureType = v));
            bar.Add(ZettaUI.Button("Refresh", Reload, "z-button--small", "z-button--ghost"));
            Add(bar);

            _error = new Label().Classes("z-error", "z-pad").Visible(false);
            Add(_error);
            var scroll = new ScrollView(ScrollViewMode.Vertical).Classes("z-scroll");
            _grid = new VisualElement().Classes("z-grid");
            scroll.Add(_grid);
            _summary = ZettaUI.Hint("");
            scroll.Add(_summary);
            _more = ZettaUI.Button("Load more", () => _ = Load(_page + 1), "z-button--small", "z-button--ghost").Visible(false);
            scroll.Add(_more);
            Add(scroll);
        }

        public static VisualElement TextureTypeField(Action<string> changed, string current = "default") =>
            ZettaUI.Dropdown("Save as", new List<string> { "default", "sprite", "normal" }, new List<string> { "Texture", "Sprite", "Normal map" }, current, changed)
                .Classes("z-texture-type");

        public void EnsureLoaded()
        {
            if (!_loaded) Reload();
        }

        void Reload()
        {
            _grid.Clear();
            _ = Load(1);
        }

        async Task Load(int page)
        {
            var request = ++_request;
            _loaded = true;
            _error.Visible(false);
            _more.SetEnabled(false);
            if (page == 1) _grid.Add(ZettaUI.Hint("Loading your library…"));
            try
            {
                var result = await ZettaApi.ListImagesAsync(page, 24, null, _feature == "" ? null : _feature);
                if (request != _request) return;
                if (page == 1) _grid.Clear();
                _page = page;
                foreach (var post in result.Items) _grid.Add(Tile(post));
                var shown = _grid.Query(className: "z-tile").ToList().Count;
                if (shown == 0) _grid.Add(ZettaUI.Hint(_feature == "" ? "Images you generate appear here." : "Nothing matches that filter."));
                _summary.text = result.Total > shown ? $"Showing {shown} of {result.Total}." : "";
                _more.Visible(result.Total > shown);
            }
            catch (ZettaApiException e)
            {
                if (request != _request) return;
                _grid.Clear();
                _error.text = "Your library could not be loaded. " + ZettaCatalog.Explain(e);
                _error.Visible(true);
            }
            finally
            {
                _more.SetEnabled(true);
            }
        }

        VisualElement Tile(ImagePost post)
        {
            var tile = new VisualElement().Classes("z-tile");
            var image = new VisualElement().Classes("z-tile-image");
            var stored = post.Images.FirstOrDefault();
            if (stored != null) ZettaUI.LoadInto(image, stored.Url, "img-" + post.Id);
            tile.Add(image);
            var caption = string.IsNullOrWhiteSpace(post.Prompt) ? "Untitled" : post.Prompt;
            tile.Add(new Label(caption).Classes("z-tile-title"));
            tile.tooltip = caption;

            if (stored == null)
            {
                tile.Add(ZettaUI.Chip("Failed", "error"));
                if (!string.IsNullOrEmpty(post.ErrorMessage)) tile.tooltip = post.ErrorMessage;
                return tile;
            }

            var actions = new VisualElement().Classes("z-row", "z-row--wrap", "z-tile-actions");
            var save = ZettaUI.Button("Save to project", null, "z-button--small");
            save.clicked += () => _ = Save(post, stored, save);
            actions.Add(save);
            var more = ZettaUI.Button("⋯", null, "z-button--small", "z-button--ghost");
            more.clicked += () =>
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Use for 3D"), false, () => _ = Handoff(stored, (w, b) => w.UseImageFor3D(b, stored.ContentType, caption)));
                menu.AddItem(new GUIContent("Edit this"), false, () => _ = Handoff(stored, (w, b) => w.EditImage(b, stored.ContentType, post.Id, caption)));
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Delete…"), false, () => _ = Delete(post));
                menu.ShowAsContext();
            };
            actions.Add(more);
            tile.Add(actions);
            return tile;
        }

        async Task Save(ImagePost post, StoredImage stored, Button button)
        {
            await ZettaUI.Run(button, _error, async () =>
            {
                var bytes = await ZettaHttp.DownloadAsync(stored.Url);
                var path = StudioAssets.SaveImage(bytes, stored.ContentType, string.IsNullOrWhiteSpace(post.Prompt) ? $"image-{post.Id}" : post.Prompt, _textureType);
                EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(path));
                _window()?.Toast("Saved to " + path);
            });
        }

        async Task Handoff(StoredImage stored, Action<ZettaStudioWindow, byte[]> use)
        {
            await ZettaUI.Run(null, _error, async () =>
            {
                var bytes = await ZettaHttp.DownloadAsync(stored.Url);
                var window = _window();
                if (window != null) use(window, bytes);
            });
        }

        async Task Delete(ImagePost post)
        {
            if (!EditorUtility.DisplayDialog("Delete image", "Delete this image from your Zetta 3D library? Copies saved into this project are kept.", "Delete", "Cancel"))
                return;
            await ZettaUI.Run(null, _error, async () =>
            {
                await ZettaApi.DeleteImageAsync(post.Id);
                Reload();
            });
        }
    }
}
