using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// "Generation Engine": every engine on show as a row, with its name and one line on what it is for
    /// (the web Studio's engine-choice.tsx). A locked engine keeps its row, shows the reason in place of its
    /// blurb, and cannot be chosen.
    /// </summary>
    public class EngineChoice : VisualElement
    {
        public class Option
        {
            public string Id;
            public string Label;
            public string Blurb;
        }

        class Row
        {
            public VisualElement Root;
            public Label Blurb;
            public Option Option;
            public string LockReason;
        }

        readonly Dictionary<string, Row> _rows = new Dictionary<string, Row>();
        readonly List<string> _order = new List<string>();

        public string Value { get; private set; }
        public event Action<string> Changed;

        public EngineChoice(string label, IEnumerable<Option> options, string current)
        {
            AddToClassList("z-engines");
            Add(ZettaUI.FieldLabel(label));
            foreach (var option in options)
            {
                var row = new Row { Option = option, Root = new VisualElement().Classes("z-engine") };
                row.Root.Add(new VisualElement().Classes("z-engine-dot"));
                var text = new VisualElement().Classes("z-engine-text");
                text.Add(new Label(option.Label).Classes("z-engine-name"));
                row.Blurb = new Label(option.Blurb).Classes("z-engine-blurb");
                text.Add(row.Blurb);
                row.Root.Add(text);
                var id = option.Id;
                row.Root.RegisterCallback<ClickEvent>(_ => Choose(id));
                _rows[id] = row;
                _order.Add(id);
                Add(row.Root);
            }
            Set(current, false);
        }

        public IReadOnlyList<string> Ids => _order;

        void Choose(string id)
        {
            if (!_rows.TryGetValue(id, out var row) || row.LockReason != null || id == Value) return;
            Set(id, true);
        }

        public void Set(string value, bool notify)
        {
            if (!_rows.ContainsKey(value ?? "")) value = _order.Count > 0 ? _order[0] : null;
            Value = value;
            foreach (var pair in _rows) pair.Value.Root.EnableInClassList("z-engine--active", pair.Key == value);
            if (notify) Changed?.Invoke(value);
        }

        /// <summary>Null opens the row; a reason locks it and replaces its blurb.</summary>
        public void SetLock(string id, string reason)
        {
            if (!_rows.TryGetValue(id, out var row)) return;
            row.LockReason = string.IsNullOrEmpty(reason) ? null : reason;
            row.Root.EnableInClassList("z-engine--locked", row.LockReason != null);
            row.Blurb.text = row.LockReason ?? row.Option.Blurb;
            row.Root.tooltip = row.LockReason ?? "";
        }
    }
}
