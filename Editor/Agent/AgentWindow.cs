using UnityEditor;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// The Zetta Agent window of 0.6 and earlier, kept only so a saved layout that still has it opens the
    /// <see cref="ZettaWindow"/> in its place. The Agent is now <see cref="AgentPage"/>.
    /// </summary>
    public class AgentWindow : EditorWindow
    {
        void OnEnable() => EditorApplication.delayCall += () => ZettaWindow.Replace(this, ZettaWindow.Agent);
    }
}
