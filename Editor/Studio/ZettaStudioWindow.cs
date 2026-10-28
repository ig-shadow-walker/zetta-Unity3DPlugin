using UnityEditor;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// The 3D Studio window of 0.6 and earlier, kept only so a saved layout that still has it opens the
    /// <see cref="ZettaWindow"/> in its place. The Studio itself is <see cref="StudioPage"/>.
    /// </summary>
    public class ZettaStudioWindow : EditorWindow
    {
        void OnEnable() => EditorApplication.delayCall += () => ZettaWindow.Replace(this, ZettaWindow.Studio);
    }
}
