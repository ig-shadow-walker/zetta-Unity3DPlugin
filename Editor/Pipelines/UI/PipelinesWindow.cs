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
    /// canvas and its runs. The open pipeline (and tab, and run) is remembered across domain reloads.
    /// </summary>
    public sealed class PipelinesWindow : EditorWindow
    {
        const string PipelineKey = "Zetta3D.Pipelines.OpenPipeline";
        const string ViewKey = "Zetta3D.Pipelines.OpenView";
        const string RunKey = "Zetta3D.Pipelines.OpenRun";

        VisualElement _host;
        PipelineEditorView _editor;
        PipelineListView _list;

        [MenuItem("Window/Zetta 3D/Pipelines", priority = 3)]
        static void Menu() => Open();

        /// <summary>
        /// Opens the window — on a pipeline (and one of its runs) when given, otherwise where it was. Safe to call before
        /// sign-in: it opens there once the account is ready.
        /// </summary>
        public static PipelinesWindow Open(long? pipelineId = null, long? runId = null)
        {
            if (pipelineId != null)
            {
                RememberedPipeline = pipelineId;
                SessionState.SetString(ViewKey, runId != null ? PipelineEditorView.RunsViewName : PipelineEditorView.RecipeView);
                SessionState.SetString(RunKey, runId?.ToString(CultureInfo.InvariantCulture) ?? "");
            }
            var window = GetWindow<PipelinesWindow>();
            window.titleContent = new GUIContent("3D Pipelines");
            window.minSize = new Vector2(520, 380);
            window.Show();
            window.Focus();
            if (pipelineId != null && window._host != null) window.ShowPipeline(pipelineId.Value, SessionState.GetString(ViewKey, PipelineEditorView.RecipeView), runId);
            return window;
        }

        /// <summary>The pipeline the window shows (null = the list), kept for this editor session.</summary>
        public static long? RememberedPipeline
        {
            get => long.TryParse(SessionState.GetString(PipelineKey, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : (long?)null;
            set => SessionState.SetString(PipelineKey, value?.ToString(CultureInfo.InvariantCulture) ?? "");
        }

        static long? RememberedRun =>
            long.TryParse(SessionState.GetString(RunKey, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : (long?)null;

        void OnEnable() => titleContent = new GUIContent("3D Pipelines");

        void CreateGUI()
        {
            var root = ZettaUI.Root(rootVisualElement);
            root.AddToClassList("z-pl-root");
            root.Add(ZettaUI.Header("3D Pipelines"));
            root.Add(ZettaUI.Gate(BuildContent));
        }

        VisualElement BuildContent()
        {
            _host = new VisualElement().Classes("z-pl-host");
            var remembered = RememberedPipeline;
            if (remembered != null) ShowPipeline(remembered.Value, SessionState.GetString(ViewKey, PipelineEditorView.RecipeView), RememberedRun);
            else ShowList();
            return _host;
        }

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
            _editor = new PipelineEditorView(this, rootVisualElement, pipelineId, ShowList, (v, run) =>
            {
                SessionState.SetString(ViewKey, v);
                SessionState.SetString(RunKey, run?.ToString(CultureInfo.InvariantCulture) ?? "");
            });
            _host.Add(_editor);
            _ = _editor.LoadAsync(view, runId);
        }

        void OnFocus() => _editor?.OnWindowFocus();

        /// <summary>The Agent saved a pipeline: a window showing it reads it again, so its autosave never overwrites the Agent's change.</summary>
        internal static void OnPipelineSaved(long pipelineId)
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<PipelinesWindow>())
            {
                if (window._editor != null && window._editor.PipelineId == pipelineId) window._editor.ReloadFromServer();
                else if (window._list != null) _ = window._list.LoadAsync();
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
            ZettaStudioHooks.OpenPipelineRun = (pipelineId, runId) => PipelinesWindow.Open(pipelineId, runId);
            PipelineToolHooks.OpenPipeline = (pipelineId, runId) => PipelinesWindow.Open(pipelineId, runId);
            PipelineToolHooks.PipelineSaved = PipelinesWindow.OnPipelineSaved;
        }
    }
}
