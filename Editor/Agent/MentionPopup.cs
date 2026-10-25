using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// The @ picker above the message box: first Scene or Project, then a list filtered by what is typed after
    /// the @. Driven by the composer (it owns the text); keyboard: ↑ ↓ to move, Enter or Tab to pick, Esc to go back (or close).
    /// </summary>
    public class MentionPopup : VisualElement
    {
        /// <summary>choose | scene | project</summary>
        public string Mode { get; private set; } = "choose";
        public bool IsOpen => style.display != DisplayStyle.None;
        /// <summary>The pointer is over the picker — a focus loss now came from a click in it, not from leaving.</summary>
        public bool PointerInside { get; private set; }

        public event Action<AgentMention> Picked;
        public event Action Closed;

        readonly Label _title;
        readonly Label _back;
        readonly ScrollView _scroll;
        readonly List<(VisualElement row, Action pick)> _rows = new List<(VisualElement, Action)>();
        int _selected;
        string _query = "";

        public MentionPopup()
        {
            AddToClassList("z-mention");
            var head = new VisualElement();
            head.AddToClassList("z-mention-head");
            // Not a Button: a button takes focus for itself. Like the rows, it acts on mouse down.
            _back = new Label("‹") { focusable = false, tooltip = "Back to Scene / Project" };
            _back.AddToClassList("z-mention-back");
            _back.RegisterCallback<MouseDownEvent>(e =>
            {
                e.StopPropagation();
                SetMode("choose");
            });
            head.Add(_back);
            _title = new Label();
            _title.AddToClassList("z-mention-title");
            head.Add(_title);
            Add(head);
            _scroll = new ScrollView(ScrollViewMode.Vertical);
            _scroll.AddToClassList("z-mention-list");
            Add(_scroll);
            style.display = DisplayStyle.None;

            RegisterCallback<PointerEnterEvent>(_ => PointerInside = true);
            RegisterCallback<PointerLeaveEvent>(_ => PointerInside = false);
            // Nothing in the picker may take focus from the message box: losing focus closes the picker.
            // Focus moves on the pointer press (before any mouse event), so it is stopped there, on the way down.
            RegisterCallback<PointerDownEvent>(KeepFocus, TrickleDown.TrickleDown);
            RegisterCallback<MouseDownEvent>(KeepFocus, TrickleDown.TrickleDown);
        }

        void KeepFocus(EventBase e)
        {
#if UNITY_2023_2_OR_NEWER
            focusController?.IgnoreEvent(e);
#else
            e.PreventDefault();
#endif
        }

        public void Open(string query)
        {
            var wasOpen = IsOpen;
            style.display = DisplayStyle.Flex;
            if (!wasOpen) Mode = "choose";
            Filter(query);
        }

        public void Close()
        {
            if (!IsOpen) return;
            style.display = DisplayStyle.None;
            Mode = "choose";
            PointerInside = false;
            Closed?.Invoke();
        }

        public void Filter(string query)
        {
            _query = query ?? "";
            Render();
        }

        public void SetMode(string mode)
        {
            Mode = mode;
            _selected = 0;
            Render();
        }

        /// <summary>Handles a key for the picker; returns true when it was the picker's (so the text field must not see it).</summary>
        public bool HandleKey(KeyCode key)
        {
            if (!IsOpen) return false;
            switch (key)
            {
                case KeyCode.Escape:
                    // In the Scene or Project list, Esc steps back to the first menu; there, it closes.
                    if (Mode != "choose") SetMode("choose");
                    else Close();
                    return true;
                case KeyCode.UpArrow:
                    Move(-1);
                    return true;
                case KeyCode.DownArrow:
                    Move(1);
                    return true;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                case KeyCode.Tab:
                    if (_rows.Count == 0) return false;
                    _rows[Mathf.Clamp(_selected, 0, _rows.Count - 1)].pick();
                    return true;
                default:
                    return false;
            }
        }

        void Move(int by)
        {
            if (_rows.Count == 0) return;
            _selected = (_selected + by + _rows.Count) % _rows.Count;
            Highlight();
            _scroll.ScrollTo(_rows[_selected].row);
        }

        void Render()
        {
            _scroll.Clear();
            _rows.Clear();
            _back.style.display = Mode == "choose" ? DisplayStyle.None : DisplayStyle.Flex;

            if (Mode == "choose")
            {
                _title.text = "Tag something for the agent";
                AddRow("🎬", "Scene", "Objects in the open scene", null, () => SetMode("scene"));
                AddRow("📁", "Project", "Files and assets in this project", null, () => SetMode("project"));
            }
            else
            {
                var scene = Mode == "scene";
                _title.text = scene ? "Scene" + (_query.Length > 0 ? $" · “{_query}”" : "") : "Project" + (_query.Length > 0 ? $" · “{_query}”" : "");
                var results = scene ? AgentMention.SearchScene(_query) : AgentMention.SearchProject(_query);
                if (results.Count == 0)
                {
                    var none = new Label(scene ? "No object in the open scene matches." : "No asset in the project matches.");
                    none.AddToClassList("z-mention-empty");
                    _scroll.Add(none);
                }
                foreach (var m in results)
                {
                    var mention = m;
                    AddRow(null, mention.Name, scene ? mention.Path : $"{mention.TypeName} · {mention.Path}", AgentMention.Icon(mention), () => Picked?.Invoke(mention));
                }
            }

            _selected = Mathf.Clamp(_selected, 0, Math.Max(0, _rows.Count - 1));
            Highlight();
        }

        void AddRow(string glyph, string title, string detail, Texture icon, Action pick)
        {
            var row = new VisualElement();
            row.AddToClassList("z-mention-row");
            if (icon != null)
            {
                var image = new Image { image = icon };
                image.AddToClassList("z-mention-icon");
                row.Add(image);
            }
            else if (glyph != null)
            {
                var g = new Label(glyph);
                g.AddToClassList("z-mention-glyph");
                row.Add(g);
            }
            var text = new VisualElement();
            text.AddToClassList("z-mention-text");
            var t = new Label(title);
            t.AddToClassList("z-mention-name");
            text.Add(t);
            if (!string.IsNullOrEmpty(detail))
            {
                var d = new Label(detail);
                d.AddToClassList("z-mention-detail");
                text.Add(d);
            }
            row.Add(text);
            var index = _rows.Count;
            row.RegisterCallback<MouseEnterEvent>(_ =>
            {
                _selected = index;
                Highlight();
            });
            // Mouse down, not click: the text field keeps focus, so typing carries on after the pick.
            row.RegisterCallback<MouseDownEvent>(e =>
            {
                e.StopPropagation();
                pick();
            });
            _rows.Add((row, pick));
            _scroll.Add(row);
        }

        void Highlight()
        {
            for (var i = 0; i < _rows.Count; i++) _rows[i].row.EnableInClassList("z-mention-row--on", i == _selected);
        }
    }
}
