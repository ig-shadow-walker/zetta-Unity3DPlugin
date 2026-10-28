using UnityEditor;

namespace Zetta3D.Editor
{
    /// <summary>
    /// First run after the package is installed: open the Studio, which shows the sign-in panel until
    /// the browser handoff completes. Once per machine — closing the window is respected afterwards.
    /// </summary>
    [InitializeOnLoad]
    static class ZettaWelcome
    {
        const string ShownKey = "Zetta3D.WelcomeShown." + ZettaConfig.Version;

        static ZettaWelcome()
        {
            if (EditorPrefs.GetBool(ShownKey, false)) return;
            if (UnityEngine.Application.isBatchMode) return;
            EditorApplication.delayCall += () =>
            {
                EditorPrefs.SetBool(ShownKey, true);
                if (!ZettaAuth.IsSignedIn || string.IsNullOrEmpty(ZettaPrefs.Token))
                    ZettaWindow.Open(ZettaWindow.Studio);
            };
        }
    }
}
