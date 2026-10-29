using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Zetta3D.Editor.Agent;

namespace Zetta3D.Editor
{
    /// <summary>
    /// The line under the window's header when a newer Zetta 3D is out: what's new, Update, ✕. A required update is
    /// red, and ✕ only puts it away until Unity starts again.
    /// </summary>
    public sealed class ZettaUpdateBanner : VisualElement
    {
        readonly Label _text;
        readonly Button _notes;
        readonly Button _update;
        readonly Button _howTo;
        readonly Button _close;

        public ZettaUpdateBanner()
        {
            AddToClassList("z-update");
            _text = new Label().Classes("z-grow", "z-update-text");
            Add(_text);
            _notes = ZettaUI.Button("What's new", () => Application.OpenURL(ZettaUpdates.Known?.NotesUrl ?? ZettaUpdates.ReleasesUrl), "z-button--small", "z-button--ghost");
            _update = ZettaUI.Button("Update", Install, "z-button--small", "z-button--signal");
            _update.tooltip = "Install it through the Package Manager. Unity reloads its scripts when it's done.";
            _howTo = ZettaUI.Button("How to update", ZettaUpdates.OpenHowTo, "z-button--small", "z-button--ghost");
            _close = ZettaUI.Button("✕", ZettaUpdates.Dismiss, "z-button--small", "z-button--ghost");
            Add(_notes);
            Add(_update);
            Add(_howTo);
            Add(_close);
            ZettaUI.WhileOnScreen(this, h => ZettaUpdates.Changed += h, h => ZettaUpdates.Changed -= h, Render);
            Render();
        }

        void Render()
        {
            var known = ZettaUpdates.Known;
            var shown = ZettaUpdates.Installing || ZettaUpdates.Problem != null || ZettaUpdates.ShowBanner;
            this.Visible(shown && known != null);
            if (known == null) return;

            var required = known.Required;
            EnableInClassList("z-update--required", required);
            _close.tooltip = required ? "Hide until Unity starts again" : $"Don't remind me about {known.Latest}";

            if (ZettaUpdates.Installing)
                _text.text = $"Updating to Zetta 3D {known.Latest}… Unity reloads its scripts when it's done.";
            else if (ZettaUpdates.Problem != null)
                _text.text = ZettaUpdates.Problem;
            else if (required)
                _text.text = $"Update required: this version ({ZettaConfig.Version}) no longer works with Zetta 3D. Update to {known.Latest} to keep using it."
                             + (known.Message != null ? " " + known.Message : "");
            else
                _text.text = $"Zetta 3D {known.Latest} is out — you have {ZettaConfig.Version}." + (known.Message != null ? " " + known.Message : "");

            _notes.Visible(!ZettaUpdates.Installing);
            _update.Visible(!ZettaUpdates.Installing && ZettaUpdates.Problem == null);
            _howTo.Visible(ZettaUpdates.Problem != null);
            _close.Visible(!ZettaUpdates.Installing);
        }

        static void Install()
        {
            if (AgentRuntime.Busy && !EditorUtility.DisplayDialog("Update Zetta 3D",
                    "The agent is still working. Updating reloads Unity's scripts, which stops it.", "Update", "Cancel"))
                return;
            ZettaUpdates.Install();
        }
    }
}
