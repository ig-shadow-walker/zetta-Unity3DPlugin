using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Zetta3D.Editor.Agent;
using Zetta3D.Editor.Pipelines.UI;
using Zetta3D.Editor.Studio;

namespace Zetta3D.Editor
{
    /// <summary>
    /// The one Zetta 3D window: 3D Studio, My Generations, Pipelines and the Agent are pages of it, switched from
    /// the header — not four windows to find, dock and keep track of. Each page is built the first time it is shown
    /// and kept while the window is open, so a half-written message or an open pipeline survives a trip to the Studio.
    /// </summary>
    public sealed class ZettaWindow : EditorWindow
    {
        public const string Studio = "studio";
        public const string Library = "library";
        public const string Pipelines = "pipelines";
        public const string Agent = "agent";

        const string PageKey = "Zetta3D.Window.Page";

        StudioPage _studio;
        PipelinesPage _pipelines;
        AgentPage _agent;
        VisualElement _header;
        VisualElement _host;
        string _page;

        IEnumerable<ZettaPage> Pages => new ZettaPage[] { _studio, _pipelines, _agent };

        [MenuItem("Window/Zetta 3D/3D Studio", priority = 1)]
        static void MenuStudio() => Open(Studio);

        [MenuItem("Window/Zetta 3D/My Generations", priority = 2)]
        static void MenuLibrary() => Open(Library);

        [MenuItem("Window/Zetta 3D/Pipelines", priority = 3)]
        static void MenuPipelines() => Open(Pipelines);

        [MenuItem("Window/Zetta 3D/Agent", priority = 4)]
        static void MenuAgent() => Open(Agent);

        /// <summary>Opens (or brings forward) the window, on <paramref name="page"/> when given.</summary>
        public static ZettaWindow Open(string page = null)
        {
            var window = GetWindow<ZettaWindow>();
            window.Show();
            window.Focus();
            if (page != null) window.ShowPage(page);
            return window;
        }

        /// <summary>The open window, or null — for hand-offs that should not open one.</summary>
        public static ZettaWindow Existing => HasOpenInstances<ZettaWindow>() ? Resources.FindObjectsOfTypeAll<ZettaWindow>().FirstOrDefault() : null;

        /// <summary>
        /// Takes the place of a window from 0.6 or earlier that a saved layout reopened (the Studio, Pipelines or
        /// Agent windows): opens this one docked beside it, on its page, and closes it.
        /// </summary>
        internal static void Replace(EditorWindow legacy, string page)
        {
            if (legacy == null) return;
            var window = Existing ?? GetWindow<ZettaWindow>(legacy.GetType());
            window.ShowPage(page);
            legacy.Close();
        }

        public StudioPage StudioPage => _studio;
        public PipelinesPage PipelinesPage => _pipelines;
        public AgentPage AgentPage => _agent;

        /// <summary>The page shown (one of the page ids).</summary>
        public string Page => _page ?? EditorPrefs.GetString(PageKey, Studio);

        void OnEnable()
        {
            ShowTitle();
            ZettaUpdates.Changed += ShowTitle;
            minSize = new Vector2(320, 360);
            _studio = new StudioPage(this);
            _pipelines = new PipelinesPage(this);
            _agent = new AgentPage(this);
            foreach (var page in Pages) page.Enable();
        }

        void OnDisable()
        {
            ZettaUpdates.Changed -= ShowTitle;
            foreach (var page in Pages) page?.Disable();
        }

        const string Icons = "Packages/com.zetta3d.unity/Editor/UI/Icons/";

        /// <summary>
        /// The tab: "Zetta 3D" with the Zetta mark — carrying an upload badge while a newer version is out (blue; red
        /// when the update is required), as the banner shows. Hovering the tab says which version.
        /// The icons are drawn by Documentation~/art/tab-icons.mjs.
        /// </summary>
        void ShowTitle()
        {
            var known = ZettaUpdates.Known;
            if (known == null || !known.Available)
            {
                titleContent = new GUIContent("Zetta 3D", TabIcon("zetta-tab"));
                return;
            }
            var tip = known.Required
                ? $"Zetta 3D — update required: {known.Latest} (you have {ZettaConfig.Version})"
                : $"Zetta 3D — {known.Latest} is available (you have {ZettaConfig.Version})";
            titleContent = new GUIContent("Zetta 3D", TabIcon(known.Required ? "zetta-tab-required" : "zetta-tab-update"), tip);
        }

        static Texture TabIcon(string name) =>
            AssetDatabase.LoadAssetAtPath<Texture2D>(Icons + name + ".png") ?? EditorGUIUtility.IconContent("d_Prefab Icon").image;

        void OnFocus()
        {
            ZettaUpdates.CheckIfDue();
            foreach (var page in Pages) page?.OnWindowFocus();
        }

        void CreateGUI()
        {
            var root = ZettaUI.Root(rootVisualElement);
            _header = ZettaUI.Header(ShowPage);
            ShowAgentActivity(_header);
            root.Add(_header);
            root.Add(new ZettaUpdateBanner());
            root.Add(ZettaUI.Gate(BuildPages));
            ZettaUI.SetActiveNav(_header, Page);
            // Escape stops the agent, whatever it is doing — the same as its Stop button.
            root.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode != KeyCode.Escape || Page != Agent || !AgentRuntime.Busy) return;
                AgentRuntime.Stop();
                e.StopPropagation();
            }, TrickleDown.TrickleDown);
        }

        /// <summary>The Agent tab carries the agent's activity dots, so it can be seen working from the other tabs.</summary>
        static void ShowAgentActivity(VisualElement header)
        {
            var tab = header.Q<Button>("nav-" + Agent);
            if (tab == null) return;
            var label = tab.text;
            tab.text = "";
            tab.AddToClassList("z-nav--activity");
            tab.Add(new Label(label).Classes("z-nav-label"));
            tab.Add(new AgentActivity());
        }

        /// <summary>The signed-in content: the page host. Built again after signing out and in, so every page is too.</summary>
        VisualElement BuildPages()
        {
            _host = new VisualElement().Classes("z-pages");
            foreach (var page in Pages) page.Unbuild();
            ShowPage(Page);
            return _host;
        }

        /// <summary>Shows a page: <see cref="Studio"/>, <see cref="Library"/>, <see cref="Pipelines"/> or <see cref="Agent"/>.</summary>
        public void ShowPage(string id)
        {
            if (id is not (Studio or Library or Pipelines or Agent)) id = Studio;
            SetPage(id);
            if (_host == null) return; // signed out: shown once the account is ready

            var page = PageFor(id);
            if (page.Root == null) _host.Add(page.Build());
            foreach (var other in Pages.Where(p => p.Root != null)) other.Root.Visible(other == page);
            if (page == _studio) _studio.ShowArea(library: id == Library);
            page.OnShown();
        }

        /// <summary>Records the page shown and lights its tab — also when a page moves itself (the Studio into My Generations).</summary>
        internal void SetPage(string id)
        {
            _page = id;
            EditorPrefs.SetString(PageKey, id);
            ZettaUI.SetActiveNav(_header, id);
        }

        internal ZettaPage PageFor(string id) => id switch
        {
            Pipelines => _pipelines,
            Agent => _agent,
            _ => _studio,
        };

        public void Toast(string message) => ShowNotification(new GUIContent(message), 2.5);
    }

    /// <summary>
    /// One page of the <see cref="ZettaWindow"/>. Plain C#, not a window: the window forwards what a page needs from
    /// it (enable, disable, focus) and builds the page's UI the first time it is shown.
    /// </summary>
    public abstract class ZettaPage
    {
        protected ZettaPage(ZettaWindow window) => Window = window;

        public ZettaWindow Window { get; }

        /// <summary>The page's UI, or null until it is first shown (and again after signing out).</summary>
        public VisualElement Root { get; private set; }

        internal VisualElement Build()
        {
            Root = BuildContent();
            Root.AddToClassList("z-page");
            return Root;
        }

        internal void Unbuild() => Root = null;

        protected abstract VisualElement BuildContent();

        /// <summary>The window's OnEnable — subscriptions that live as long as the window.</summary>
        public virtual void Enable()
        {
        }

        public virtual void Disable()
        {
        }

        public virtual void OnWindowFocus()
        {
        }

        public virtual void OnShown()
        {
        }

        /// <summary>Whether this page is the one on screen.</summary>
        public bool Showing => Root != null && Window != null && Window.PageFor(Window.Page) == this;

        protected void ShowNotification(GUIContent content) => Window.ShowNotification(content);
    }
}
