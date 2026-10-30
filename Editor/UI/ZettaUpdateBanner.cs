using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Zetta3D.Editor.Agent;

namespace Zetta3D.Editor
{
    /// <summary>
    /// The line under the window's header while a newer Zetta 3D is out — What's new and Update, on every page, until it
    /// is installed. It has no close button: it goes away when the update does. A required update is red.
    /// </summary>
    public sealed class ZettaUpdateBanner : VisualElement
    {
        readonly Label _text;
        readonly Button _notes;
        readonly Button _update;
        readonly Button _howTo;

        public ZettaUpdateBanner()
        {
            AddToClassList("z-update");
            _text = new Label().Classes("z-grow", "z-update-text");
            Add(_text);
            _notes = ZettaUI.Button("What's new", () => Application.OpenURL(ZettaUpdates.Known?.NotesUrl ?? ZettaUpdates.ReleasesUrl), "z-button--small", "z-button--ghost");
            _update = ZettaUI.Button("Update", Install, "z-button--small", "z-button--signal");
            _update.tooltip = "Install it through the Package Manager. Unity reloads its scripts when it's done.";
            _howTo = ZettaUI.Button("How to update", ZettaUpdates.OpenHowTo, "z-button--small", "z-button--ghost");
            Add(_notes);
            Add(_update);
            Add(_howTo);
            ZettaUI.WhileOnScreen(this, h => ZettaUpdates.Changed += h, h => ZettaUpdates.Changed -= h, Render);
            Render();
        }

        void Render()
        {
            var known = ZettaUpdates.Known;
            // Only while a newer version is out — then always, failed attempt or not; never once it is installed.
            this.Visible(ZettaUpdates.ShowBanner);
            if (known == null) return;

            var required = known.Required;
            EnableInClassList("z-update--required", required);

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
            // After a failed update: try again, or read how to update by hand.
            _update.text = ZettaUpdates.Problem != null ? "Try again" : "Update";
            _update.Visible(!ZettaUpdates.Installing);
            _howTo.Visible(ZettaUpdates.Problem != null);
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
