using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// The Zetta 3D Studio inside Unity: generate, refine and import 3D models and images, the same
    /// tools as zetta3d.ai's Studio, with results landing straight in the project. A page of the
    /// <see cref="ZettaWindow"/> — its 3D Studio and My Generations tabs (My Generations is the Studio's library tab).
    /// </summary>
    public class StudioPage : ZettaPage
    {
        const string TabKey = "Zetta3D.Studio.Tab";

        public const string GenerateTab = "generate";
        public const string SmartTab = "smart";
        public const string RetopologyTab = "retopology";
        public const string SegmentTab = "segment";
        public const string SmartUvTab = "smart-uv";
        public const string TextureTab = "texture";
        public const string RigTab = "rig";
        public const string ImagesTab = "images";
        public const string LibraryTab = "library";

        readonly List<StudioPanel> _panels = new List<StudioPanel>();
        readonly Dictionary<string, Button> _tabButtons = new Dictionary<string, Button>();
        VisualElement _host;
        VisualElement _tabs;
        const string LastToolKey = "Zetta3D.Studio.LastTool";
        Label _banner;
        string _active;
        StudioContext _ctx;

        public StudioPage(ZettaWindow window) : base(window)
        {
        }

        public override void Enable()
        {
            _ctx = StudioContext.Shared;
            _ctx.Window = this;
            _ctx.Changed += OnContextChanged;
            ZettaAuth.Changed += OnAuthChanged;
        }

        public override void Disable()
        {
            if (_ctx != null)
            {
                _ctx.Changed -= OnContextChanged;
                if (_ctx.Window == this) _ctx.Window = null;
            }
            ZettaAuth.Changed -= OnAuthChanged;
        }

        /// <summary>The window's tab was clicked: My Generations, or the tools — back on the last one used.</summary>
        internal void ShowArea(bool library)
        {
            if (library) ShowTab(LibraryTab);
            else if (_active == null || _active == LibraryTab) ShowTab(EditorPrefs.GetString(LastToolKey, GenerateTab));
        }

        void OnAuthChanged()
        {
            if (ZettaAuth.IsSignedIn) _ctx.EnsureLoaded();
        }

        protected override VisualElement BuildContent()
        {
            _panels.Clear();
            _tabButtons.Clear();
            _panels.AddRange(new StudioPanel[]
            {
                new GeneratePanel(smart: false),
                new GeneratePanel(smart: true),
                new SegmentPanel(),
                new RetopologyPanel(),
                new SmartUvPanel(),
                new TexturePanel(),
                new AutoRigPanel(),
                new ImageStudioPanel(),
                new LibraryPanel(),
            });
            foreach (var panel in _panels) panel.Attach(_ctx);

            var content = new VisualElement().Classes("z-studio");
            var tabs = new VisualElement().Classes("z-tabs");
            // My Generations and 3D Pipelines are in the header's links, so the strip holds only the tools.
            foreach (var panel in _panels.Where(p => p.Id != LibraryTab))
            {
                var id = panel.Id;
                var button = new Button(() => ShowTab(id));
                button.AddToClassList("z-tab");
                // The area's hue as a small dot (the web's DESIGN.md § Hues) — identity, not a control.
                button.Add(new VisualElement().Classes("z-tab-dot", "z-hue--" + HueOf(id)));
                button.Add(new Label(panel.Title).Classes("z-tab-label"));
                _tabButtons[id] = button;
                tabs.Add(button);
            }
            content.Add(tabs);
            _tabs = tabs;

            _banner = new Label().Classes("z-banner").Visible(false);
            _banner.RegisterCallback<ClickEvent>(e => _ = _ctx.LoadAsync());
            content.Add(_banner);

            _host = new VisualElement().Classes("z-panel-host");
            content.Add(_host);
            content.Add(new JobsStrip());

            _active = null;
            // A remembered tab that no longer exists (e.g. the removed "splat" tab) opens the default one.
            var remembered = EditorPrefs.GetString(TabKey, GenerateTab);
            ShowTab(_panels.Any(p => p.Id == remembered) ? remembered : GenerateTab);
            _ctx.EnsureLoaded();
            UpdateBanner();
            return content;
        }

        /// <summary>Each tab's area hue (web src/lib/hues.ts).</summary>
        public static string HueOf(string tab) => tab switch
        {
            SmartTab => "violet",
            SegmentTab or RetopologyTab or SmartUvTab => "lime",
            TextureTab => "amber",
            RigTab => "rose",
            ImagesTab => "magenta",
            _ => "signal",
        };

        void OnContextChanged()
        {
            UpdateBanner();
            foreach (var panel in _panels.Where(p => p.IsBuilt)) panel.OnContextChanged();
        }

        void UpdateBanner()
        {
            if (_banner == null) return;
            var error = _ctx.LoadError;
            _banner.text = error == null ? "" : $"Some account details did not load ({error}). Prices and plan limits may be missing — click to retry.";
            _banner.Visible(error != null && !_ctx.Loading);
        }

        public T Panel<T>() where T : StudioPanel => _panels.OfType<T>().FirstOrDefault();

        public StudioPanel ShowTab(string id)
        {
            var panel = _panels.FirstOrDefault(p => p.Id == id) ?? _panels.FirstOrDefault();
            if (panel == null || _host == null) return null;
            if (_active != panel.Id)
            {
                _active = panel.Id;
                EditorPrefs.SetString(TabKey, panel.Id);
                _host.Clear();
                _host.Add(panel.Root);
                foreach (var pair in _tabButtons) pair.Value.EnableInClassList("z-tab--active", pair.Key == panel.Id);
                if (panel.Id != LibraryTab) EditorPrefs.SetString(LastToolKey, panel.Id);
            }
            // My Generations is its own page: the header says where you are, and the tools' strip steps aside.
            var library = panel.Id == LibraryTab;
            _tabs?.Visible(!library);
            Window.SetPage(library ? ZettaWindow.Library : ZettaWindow.Studio);
            panel.OnShown();
            return panel;
        }

        // ─── Hand-offs between tabs (and from the Project window) ─────────────────────

        /// <summary>
        /// Opens the Studio — on My Generations when <paramref name="library"/> — signed in or not, and runs
        /// <paramref name="action"/> once its tabs exist.
        /// </summary>
        public static void WithStudio(System.Action<StudioPage> action, bool library = false)
        {
            var window = ZettaWindow.Open(library ? ZettaWindow.Library : ZettaWindow.Studio);
            var page = window.StudioPage;
            if (page._panels.Count > 0) action(page);
            else if (!ZettaAuth.IsSignedIn) window.Toast("Sign in to Zetta 3D first.");
            else EditorApplication.delayCall += () =>
            {
                if (window != null && page._panels.Count > 0) action(page);
            };
        }

        /// <summary>A picture for Generate 3D's image input.</summary>
        public void UseImageFor3D(byte[] bytes, string contentType, string name)
        {
            var panel = (GeneratePanel)ShowTab(GenerateTab);
            panel?.SetImage(bytes, contentType, name);
        }

        /// <summary>A library row as the source of a post-process tab.</summary>
        public void UseAsSource(Generation generation, string tabId)
        {
            if (ShowTab(tabId) is PostProcessPanel panel) panel.SetSource(generation);
        }

        /// <summary>
        /// Fork: the row's settings and pictures onto Generate 3D or Smart 3D (whichever offers its engine), with
        /// that tab shown. Nothing runs. Returns false when the row cannot be forked.
        /// </summary>
        public bool Fork(Generation generation)
        {
            var setup = StudioFork.Of(generation);
            if (setup == null) return false;
            if (ShowTab(setup.Tab) is not GeneratePanel panel) return false;
            panel.ApplyFork(setup);
            return true;
        }

        /// <summary>A picture to change in Image Studio's Edit mode.</summary>
        public void EditImage(byte[] bytes, string contentType, long? postId, string label)
        {
            if (ShowTab(ImagesTab) is ImageStudioPanel panel) panel.StartEdit(bytes, contentType, postId, label);
        }

        public void Toast(string message) => Window.Toast(message);
    }
}
