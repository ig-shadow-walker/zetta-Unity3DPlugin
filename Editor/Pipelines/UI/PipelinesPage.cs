using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Zetta3D.Editor.Agent;
using Zetta3D.Editor.Studio;

namespace Zetta3D.Editor.Pipelines.UI
{
    /// <summary>
    /// 3D PIPELINES in Unity (routes/pipelines.tsx): the list of your pipelines, and one open — its recipe on a node
    /// canvas and its runs. The open pipeline (and tab, and run) is remembered across domain reloads. A page of the
    /// <see cref="ZettaWindow"/>.
    /// </summary>
    public sealed class PipelinesPage : ZettaPage
    {
        const string PipelineKey = "Zetta3D.Pipelines.OpenPipeline";
        const string ViewKey = "Zetta3D.Pipelines.OpenView";
        const string RunKey = "Zetta3D.Pipelines.OpenRun";

        VisualElement _host;
        PipelineEditorView _editor;
        PipelineListView _list;

        public PipelinesPage(ZettaWindow window) : base(window)
        {
        }

        /// <summary>
        /// Opens the Pipelines page — on a pipeline (and one of its runs) when given, otherwise where it was. Safe to
        /// call before sign-in: it opens there once the account is ready.
        /// </summary>
        public static PipelinesPage Open(long? pipelineId = null, long? runId = null)
        {
            if (pipelineId != null)
            {
                RememberedPipeline = pipelineId;
                SessionState.SetString(ViewKey, runId != null ? PipelineEditorView.RunsViewName : PipelineEditorView.RecipeView);
                SessionState.SetString(RunKey, runId?.ToString(CultureInfo.InvariantCulture) ?? "");
            }
            var page = ZettaWindow.Open(ZettaWindow.Pipelines).PipelinesPage;
            if (pipelineId != null && page._host != null) page.ShowPipeline(pipelineId.Value, SessionState.GetString(ViewKey, PipelineEditorView.RecipeView), runId);
            return page;
        }

        /// <summary>The pipeline the window shows (null = the list), kept for this editor session.</summary>
        public static long? RememberedPipeline
        {
            get => long.TryParse(SessionState.GetString(PipelineKey, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : (long?)null;
            set => SessionState.SetString(PipelineKey, value?.ToString(CultureInfo.InvariantCulture) ?? "");
        }

        static long? RememberedRun =>
            long.TryParse(SessionState.GetString(RunKey, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : (long?)null;

        protected override VisualElement BuildContent()
        {
            // The page is the pipeline editor's overlay host (its pop-ups and keys) — not the whole window, whose
            // other pages have text boxes of their own.
            _page = new VisualElement().Classes("z-pl-root");
            _host = new VisualElement().Classes("z-pl-host");
            _page.Add(_host);
            _editor = null;
            _list = null;
            var remembered = RememberedPipeline;
            if (remembered != null) ShowPipeline(remembered.Value, SessionState.GetString(ViewKey, PipelineEditorView.RecipeView), RememberedRun);
            else ShowList();
            return _page;
        }

        VisualElement _page;

        void ShowList()
        {
            RememberedPipeline = null;
            _editor = null;
            _host.Clear();
            _list = new PipelineListView(id => ShowPipeline(id, PipelineEditorView.RecipeView, null));
            _host.Add(_list);
        }

        void ShowPipeline(long pipelineId, string view, long? runId)
        {
            if (_host == null) return;
            if (_editor != null && _editor.PipelineId == pipelineId)
            {
                _editor.Go(view, runId);
                return;
            }
            RememberedPipeline = pipelineId;
            _list = null;
            _host.Clear();
            _editor = new PipelineEditorView(Window, _page, pipelineId, ShowList, (v, run) =>
            {
                SessionState.SetString(ViewKey, v);
                SessionState.SetString(RunKey, run?.ToString(CultureInfo.InvariantCulture) ?? "");
            });
            _host.Add(_editor);
            _ = _editor.LoadAsync(view, runId);
        }

        public override void OnWindowFocus() => _editor?.OnWindowFocus();

        /// <summary>The Agent saved a pipeline: a page showing it reads it again, so its autosave never overwrites the Agent's change.</summary>
        internal static void OnPipelineSaved(long pipelineId)
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<ZettaWindow>())
            {
                var page = window.PipelinesPage;
                if (page == null) continue;
                if (page._editor != null && page._editor.PipelineId == pipelineId) page._editor.ReloadFromServer();
                else if (page._list != null) _ = page._list.LoadAsync();
            }
        }

        /// <summary>The editor under test (scratch smoke only).</summary>
        internal PipelineEditorView Editor => _editor;
    }

    /// <summary>Lets the Studio's Library and the Agent open runs and pipelines here.</summary>
    [InitializeOnLoad]
    static class PipelinesHooks
    {
        static PipelinesHooks()
        {
            ZettaStudioHooks.OpenPipelineRun = (pipelineId, runId) => PipelinesPage.Open(pipelineId, runId);
            PipelineToolHooks.OpenPipeline = (pipelineId, runId) => PipelinesPage.Open(pipelineId, runId);
            PipelineToolHooks.PipelineSaved = PipelinesPage.OnPipelineSaved;
        }
    }
}
