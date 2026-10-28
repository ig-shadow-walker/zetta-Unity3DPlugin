using System.Linq;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// Three small dots beside the header's Agent tab while the agent works, so it can be seen running from any
    /// other tab. They light up in turn while it works, and hold still in amber when it is waiting on the user (an
    /// approval or a question card). Hidden while it is idle; nothing animates then.
    /// </summary>
    public class AgentActivity : VisualElement
    {
        readonly VisualElement[] _dots = new VisualElement[3];
        IVisualElementScheduledItem _tick;
        int _frame;
        /// <summary>The tab's own tooltip, put back when the agent goes idle.</summary>
        string _idleTip;

        public AgentActivity()
        {
            AddToClassList("z-nav-activity");
            pickingMode = PickingMode.Ignore;
            for (var i = 0; i < _dots.Length; i++)
            {
                _dots[i] = new VisualElement { pickingMode = PickingMode.Ignore };
                _dots[i].AddToClassList("z-nav-dot");
                Add(_dots[i]);
            }
            Update();
            RegisterCallback<AttachToPanelEvent>(_ => _tick = schedule.Execute(Update).Every(260));
            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                _tick?.Pause();
                _tick = null;
            });
        }

        /// <summary>The tooltip for the tab it sits on: what the agent is doing, or null while it is idle.</summary>
        public static string Describe() =>
            !AgentRuntime.Busy ? null : WaitingForUser() ? "The agent is waiting for you" : "The agent is working";

        static bool WaitingForUser() =>
            AgentRuntime.State.Phase == "tools" && AgentRuntime.State.Items.Any(i => i.Kind is "approval" or "ask" && !i.Resolved);

        void Update()
        {
            var busy = AgentRuntime.Busy;
            style.display = busy ? DisplayStyle.Flex : DisplayStyle.None;
            if (parent != null)
            {
                _idleTip ??= parent.tooltip;
                parent.tooltip = busy ? Describe() : _idleTip;
            }
            if (!busy) return;
            var waiting = WaitingForUser();
            EnableInClassList("z-nav-activity--waiting", waiting);
            if (!waiting) _frame = (_frame + 1) % _dots.Length;
            for (var i = 0; i < _dots.Length; i++) _dots[i].EnableInClassList("z-nav-dot--on", !waiting && i == _frame);
        }
    }
}
