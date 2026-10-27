using UnityEngine.UIElements;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// "Thinking ● ● ●" at the end of the conversation while the agent works — three dots that light up in
    /// turn (eased by USS transitions), so a long turn reads as running, not stuck. It only animates while it
    /// is on a panel.
    /// </summary>
    public class ThinkingIndicator : VisualElement
    {
        readonly VisualElement[] _dots = new VisualElement[3];
        readonly Label _label;
        readonly Label _route;
        IVisualElementScheduledItem _tick;
        int _frame;

        public ThinkingIndicator()
        {
            AddToClassList("z-think");
            var dots = new VisualElement();
            dots.AddToClassList("z-think-dots");
            for (var i = 0; i < _dots.Length; i++)
            {
                _dots[i] = new VisualElement();
                _dots[i].AddToClassList("z-think-dot");
                dots.Add(_dots[i]);
            }
            Add(dots);
            _label = new Label("Thinking");
            _label.AddToClassList("z-think-label");
            Add(_label);
            _route = new Label();
            _route.AddToClassList("z-chip");
            _route.AddToClassList("z-think-route");
            _route.style.display = DisplayStyle.None;
            Add(_route);

            RegisterCallback<AttachToPanelEvent>(_ => _tick = schedule.Execute(Tick).Every(260));
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                _tick?.Pause();
                _tick = null;
            });
        }

        public string Text
        {
            get => _label.text;
            set => _label.text = value;
        }

        /// <summary>The model tier and thinking this turn runs on — "Capable · Low thinking" — with why as the tooltip; hidden when empty.</summary>
        public void SetRoute(string text, string why)
        {
            _route.text = text ?? "";
            _route.tooltip = why ?? "";
            _route.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        void Tick()
        {
            _frame = (_frame + 1) % _dots.Length;
            for (var i = 0; i < _dots.Length; i++) _dots[i].EnableInClassList("z-think-dot--on", i == _frame);
        }
    }
}
