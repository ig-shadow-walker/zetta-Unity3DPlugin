using UnityEditor;

namespace Zetta3D.Editor.Pipelines.UI
{
    /// <summary>
    /// The 3D Pipelines window of 0.6 and earlier, kept only so a saved layout that still has it opens the
    /// <see cref="ZettaWindow"/> in its place. Pipelines are now <see cref="PipelinesPage"/>.
    /// </summary>
    public sealed class PipelinesWindow : EditorWindow
    {
        void OnEnable() => EditorApplication.delayCall += () => ZettaWindow.Replace(this, ZettaWindow.Pipelines);
    }
}
