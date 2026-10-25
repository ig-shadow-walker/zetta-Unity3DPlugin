using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// One tab of the Studio. Built lazily the first time it is shown and kept alive afterwards,
    /// so switching tabs never loses what was typed.
    /// </summary>
    public abstract class StudioPanel
    {
        protected StudioContext Ctx { get; private set; }
        VisualElement _root;

        public abstract string Id { get; }
        public abstract string Title { get; }

        public void Attach(StudioContext context) => Ctx = context;

        public VisualElement Root
        {
            get
            {
                if (_root != null) return _root;
                _root = new VisualElement().Classes("z-panel");
                Build(_root);
                OnContextChanged();
                return _root;
            }
        }

        public bool IsBuilt => _root != null;

        protected abstract void Build(VisualElement root);

        /// <summary>Plan gates, prices or image models changed (or finished loading).</summary>
        public virtual void OnContextChanged()
        {
        }

        /// <summary>The tab became visible.</summary>
        public virtual void OnShown()
        {
        }

        /// <summary>A scroll area with the Studio's padding.</summary>
        protected static ScrollView Scroll() => new ScrollView(ScrollViewMode.Vertical).Classes("z-scroll");
    }
}
