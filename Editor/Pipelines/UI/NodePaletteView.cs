using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Pipelines.UI
{
    /// <summary>
    /// THE STEPS TO BUILD WITH (node-palette.tsx), in the groups Seed, Picture, Make, Refine — only what Unity offers.
    /// Click one to add it after the selected step; drag one onto the canvas to drop it there. The seeds step aside once
    /// the pipeline has one.
    /// </summary>
    public sealed class NodePaletteView : VisualElement
    {
        public event Action<string> AddRequested;

        readonly ScrollView _scroll;
        bool _hasSeed;

        public NodePaletteView()
        {
            AddToClassList("z-pl-palette");
            Add(new Label("Steps").Classes("z-pl-panel-title"));
            _scroll = new ScrollView(ScrollViewMode.Vertical).Classes("z-pl-palette-scroll");
            Add(_scroll);
            Render();
        }

        public void SetHasSeed(bool hasSeed)
        {
            if (hasSeed == _hasSeed) return;
            _hasSeed = hasSeed;
            Render();
        }

        void Render()
        {
            _scroll.Clear();
            foreach (var (group, label) in NodeCatalog.Groups)
            {
                var specs = NodeCatalog.OfferedInUnity.Where(s => s.Group == group).ToList();
                // A group with nothing on offer (Output) is not shown at all.
                if (specs.Count == 0) continue;
                _scroll.Add(new Label(label.ToUpperInvariant()).Classes("z-pl-palette-group"));
                foreach (var spec in specs)
                {
                    var disabled = group == NodeGroup.Seed && _hasSeed;
                    _scroll.Add(Item(spec, disabled));
                }
            }
        }

        VisualElement Item(NodeSpec spec, bool disabled)
        {
            var item = new VisualElement().Classes("z-pl-palette-item");
            item.Add(PipelineUi.HueIcon(spec.Kind));
            item.Add(new Label(spec.Label).Classes("z-pl-palette-label"));
            item.tooltip = disabled ? "A pipeline starts from one seed." : spec.Blurb;
            item.EnableInClassList("z-pl-palette-item--disabled", disabled);
            if (disabled) return item;

            var kind = spec.Kind;
            Vector2? down = null;
            var dragging = false;
            item.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                down = e.position;
                dragging = false;
            });
            item.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (down == null || dragging || (e.pressedButtons & 1) == 0) return;
                if (((Vector2)e.position - down.Value).sqrMagnitude < 36) return;
                dragging = true;
                down = null;
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.SetGenericData(PipelineCanvas.DragKindKey, kind);
                DragAndDrop.objectReferences = new UnityEngine.Object[0];
                DragAndDrop.paths = new string[0];
                DragAndDrop.StartDrag(spec.Label);
            });
            item.RegisterCallback<PointerUpEvent>(e =>
            {
                if (e.button != 0) return;
                var click = down != null && !dragging;
                down = null;
                dragging = false;
                if (click) AddRequested?.Invoke(kind);
            });
            item.RegisterCallback<PointerLeaveEvent>(_ =>
            {
                if (!dragging) return;
                dragging = false;
            });
            return item;
        }
    }
}
