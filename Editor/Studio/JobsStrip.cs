using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// The window's footer: every job the plugin is following (from the Studio or the agent),
    /// with its state, queue position, and what can be done with it next.
    /// </summary>
    public class JobsStrip : VisualElement
    {
        const string CollapsedKey = "Zetta3D.Studio.JobsCollapsed";

        readonly Label _title;
        readonly Button _toggle;
        readonly Button _clear;
        readonly ScrollView _list;
        readonly Label _error;
        readonly Label _notice;
        bool _collapsed;
        bool _scheduled;

        public JobsStrip()
        {
            AddToClassList("z-jobs");
            _collapsed = EditorPrefs.GetBool(CollapsedKey, false);

            var header = new VisualElement().Classes("z-row", "z-jobs-header");
            _title = new Label("Jobs").Classes("z-jobs-title");
            header.Add(_title);
            header.Add(ZettaUI.Spacer());
            _clear = ZettaUI.Button("Clear finished", ZettaJobs.ClearFinished, "z-button--ghost", "z-button--small");
            header.Add(_clear);
            _toggle = ZettaUI.Button("", ToggleCollapsed, "z-button--ghost", "z-button--small");
            header.Add(_toggle);
            header.RegisterCallback<ClickEvent>(e =>
            {
                if (e.target == header || e.target == _title) ToggleCollapsed();
            });
            Add(header);

            _error = new Label().Classes("z-error").Visible(false);
            Add(_error);
            _notice = ZettaUI.Hint("").Classes("z-pad").Visible(false);
            Add(_notice);
            _list = new ScrollView(ScrollViewMode.Vertical).Classes("z-jobs-list");
            Add(_list);

            Render();
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                ZettaJobs.Updated -= OnUpdated;
                ZettaJobs.Updated += OnUpdated;
                Render();
            });
            RegisterCallback<DetachFromPanelEvent>(_ => ZettaJobs.Updated -= OnUpdated);
        }

        void ToggleCollapsed()
        {
            _collapsed = !_collapsed;
            EditorPrefs.SetBool(CollapsedKey, _collapsed);
            Render();
        }

        void OnUpdated(ZettaJobs.Job _)
        {
            if (_scheduled) return;
            _scheduled = true;
            schedule.Execute(() =>
            {
                _scheduled = false;
                Render();
            });
        }

        void Render()
        {
            var jobs = ZettaJobs.All.ToList();
            var running = jobs.Count(j => !j.Finished);
            this.Visible(jobs.Count > 0);
            _title.text = running > 0 ? $"Jobs · {running} running" : $"Jobs · {jobs.Count}";
            _clear.Visible(jobs.Any(j => j.Finished) && !_collapsed);
            _toggle.text = _collapsed ? "Show" : "Hide";
            _list.Visible(!_collapsed);
            _list.Clear();
            if (_collapsed) return;
            foreach (var job in jobs) _list.Add(Row(job));
        }

        VisualElement Row(ZettaJobs.Job job)
        {
            var row = new VisualElement().Classes("z-job");
            var dot = new VisualElement().Classes("z-step-dot");
            if (job.Status == "success") dot.AddToClassList("z-step-dot--done");
            else if (job.Status is "failed" or "cancelled") dot.AddToClassList("z-step-dot--error");
            else if (job.Status is "pending" or "queued") dot.AddToClassList("z-step-dot--wait");
            row.Add(dot);

            var text = new VisualElement().Classes("z-job-text");
            text.Add(new Label(string.IsNullOrEmpty(job.Label) ? $"Job #{job.Id}" : job.Label).Classes("z-job-label"));
            text.Add(new Label(Describe(job)).Classes("z-step-text"));
            row.Add(text);

            var actions = new VisualElement().Classes("z-row", "z-job-actions");
            if (job.Status is "pending" or "queued")
                actions.Add(Small("Cancel", () => _ = Cancel(job), "z-button--danger"));
            if (job.Status == "success")
            {
                var generation = job.Latest;
                if (generation?.IsSplat == true)
                    actions.Add(Small("Open on zetta3d.ai", () => Application.OpenURL($"{ZettaConfig.SiteBase}/3d-studio?generation={job.Id}")));
                else if (_importing.TryGetValue(job.Id, out var progress))
                {
                    var busy = Small(progress >= 0.96f ? "Importing…" : $"Downloading {Mathf.RoundToInt(progress * 100f)}%", null);
                    busy.SetEnabled(false);
                    _progressButtons[job.Id] = busy;
                    actions.Add(busy);
                }
                else if (!string.IsNullOrEmpty(job.ImportedPath) && AssetDatabase.LoadMainAssetAtPath(job.ImportedPath) != null)
                {
                    actions.Add(Small("Show in Project", () => Ping(job.ImportedPath)));
                    actions.Add(Small("Place in scene", () => PlaceInScene(job)));
                }
                else if (ZettaProjectIndex.Find(job.Id) is { } existing && existing.Model != null)
                {
                    var path = AssetDatabase.GetAssetPath(existing.Model);
                    actions.Add(Small("Show in Project", () => Ping(path)));
                    actions.Add(Small("Place in scene", () =>
                    {
                        job.ImportedPath = path;
                        PlaceInScene(job);
                    }));
                }
                else
                {
                    var import = Small("Import", () => _ = Import(job));
                    import.tooltip = ZettaProjectSettings.Instance.PlaceImportedInScene
                        ? "Download into the project and place in the open scene (Place in scene is on)"
                        : "Download into the project (turn on Place in scene in the Library to also place it)";
                    actions.Add(import);
                }
                if (generation?.UvLayout != null)
                {
                    var uv = Small("UV layout", () => _ = SaveUvLayout(generation));
                    uv.tooltip = "Save the picture of the new UV layout into the project";
                    actions.Add(uv);
                }
            }
            if (job.Finished) actions.Add(Small("×", () => ZettaJobs.Forget(job.Id), "z-button--ghost"));
            row.Add(actions);
            return row;
        }

        static Button Small(string text, Action action, string extra = null) =>
            ZettaUI.Button(text, action, "z-button--small", extra);

        public static string Describe(ZettaJobs.Job job)
        {
            var who = job.Source == "agent" ? "Agent · " : "";
            var uv = job.Latest?.UvLayout != null ? " · UV layout picture ready" : "";
            switch (job.Status)
            {
                case "pending":
                    return who + "Waiting to start";
                case "queued":
                    return who + (job.QueuePosition is { } p && p > 0 ? $"Queued · #{p} in line" : "Queued");
                case "processing":
                    return who + "Generating…";
                case "saving":
                    return who + "Saving the result…";
                case "success":
                    if (!string.IsNullOrEmpty(job.Error)) return who + job.Error;
                    if (job.Latest?.IsSplat == true) return who + "Ready · " + ZettaImporter.SplatUnsupported;
                    if (!string.IsNullOrEmpty(job.ImportedPath)) return who + "Ready · imported to " + job.ImportedPath + uv;
                    return who + (job.AutoImport ? "Ready · importing…" : "Ready") + uv;
                case "failed":
                    return who + (job.Error ?? "Failed. Your credits were refunded.");
                case "cancelled":
                    return who + "Cancelled";
                default:
                    return who + job.Status;
            }
        }

        async Task Cancel(ZettaJobs.Job job)
        {
            await ZettaUI.Run(null, _error, async () =>
            {
                var generation = await ZettaApi.CancelGenerationAsync(job.Id);
                if (generation != null)
                {
                    job.Status = generation.Status;
                    job.Latest = generation;
                }
                Render();
            });
        }

        /// <summary>Imports in progress (job id → 0–1) and the button showing each one's percentage.</summary>
        readonly Dictionary<long, float> _importing = new Dictionary<long, float>();
        readonly Dictionary<long, Button> _progressButtons = new Dictionary<long, Button>();

        async Task Import(ZettaJobs.Job job)
        {
            if (_importing.ContainsKey(job.Id)) return;
            _importing[job.Id] = 0f;
            Render();
            try
            {
                await ImportNow(job);
            }
            finally
            {
                _importing.Remove(job.Id);
                _progressButtons.Remove(job.Id);
                Render();
            }
        }

        async Task ImportNow(ZettaJobs.Job job)
        {
            await ZettaUI.Run(null, _error, async () =>
            {
                var generation = job.Latest;
                if (generation == null || !generation.IsSuccess) (generation, _) = await ZettaApi.GetGenerationAsync(job.Id);
                var place = ZettaProjectSettings.Instance.PlaceImportedInScene ? new ZettaImporter.Placement() : null;
                var result = await ZettaImporter.ImportAsync(generation, null, place, null, p =>
                {
                    _importing[job.Id] = p;
                    if (_progressButtons.TryGetValue(job.Id, out var button))
                        button.text = p >= 0.96f ? "Importing…" : $"Downloading {Mathf.RoundToInt(p * 100f)}%";
                });
                job.ImportedPath = result.MainAssetPath;
                job.Latest = generation;
                job.Error = null;
                Ping(result.MainAssetPath);
                var note = result.Note;
                if (!string.IsNullOrEmpty(result.UvLayoutPath))
                    note = string.IsNullOrEmpty(note) ? $"UV layout picture saved to {result.UvLayoutPath}." : $"{note} UV layout picture: {result.UvLayoutPath}.";
                ShowNotice(note);
                Render();
            });
        }

        void ShowNotice(string message)
        {
            _notice.text = message ?? "";
            _notice.Visible(!string.IsNullOrEmpty(message));
        }

        async Task SaveUvLayout(Generation generation)
        {
            await ZettaUI.Run(null, _error, async () =>
            {
                var path = await ZettaImporter.ImportUvLayoutAsync(generation);
                if (path == null) throw new InvalidOperationException("This model has no UV layout picture.");
                Ping(path);
                ShowNotice("UV layout saved to " + path);
            });
        }

        static void Ping(string path)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null) return;
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        /// <summary>Places the imported copy (every part of a segmented result) in the open scene.</summary>
        public static GameObject PlaceInScene(ZettaJobs.Job job)
        {
            var folder = Path.GetDirectoryName(job.ImportedPath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder)) return null;
            var paths = AssetDatabase.FindAssets("t:GameObject", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => Path.GetDirectoryName(p)?.Replace('\\', '/') == folder)
                .Distinct()
                .OrderBy(p => p)
                .ToList();
            if (paths.Count == 0) paths.Add(job.ImportedPath);
            var generation = job.Latest ?? new Generation { Id = job.Id, Title = job.Label };
            return ZettaImporter.Place(generation, paths, new ZettaImporter.Placement());
        }
    }
}
