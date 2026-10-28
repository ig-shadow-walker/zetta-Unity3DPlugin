using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// "Add to Zetta Agent": tags the selection in the agent's chat box — from the Project window's and the Hierarchy's
    /// right-click menus, or with ⇧⌘M / Ctrl+Shift+M anywhere in the Editor (rebind it in Edit ▸ Shortcuts, under
    /// Main Menu ▸ Assets). Several selected items are all tagged at once.
    /// </summary>
    static class AgentContextMenu
    {
        const string Label = "Add to Zetta Agent";

        // The hotkey lives on this item, so the Project window's right-click menu shows it; it works on whatever
        // is selected, Hierarchy objects included.
        [MenuItem("Assets/" + Label + " %#m", false, 19)]
        static void AddSelection() => Add(Selection.objects);

        [MenuItem("Assets/" + Label + " %#m", true)]
        static bool CanAddSelection() => Selection.objects.Any(o => AgentMention.FromObject(o) != null);

        // The same, inside the Zetta 3D submenu next to the other Zetta actions (first, above a separator).
        [MenuItem("Assets/Zetta 3D/" + Label, false, 1980)]
        static void AddSelectionFromSubmenu() => Add(Selection.objects);

        [MenuItem("Assets/Zetta 3D/" + Label, true)]
        static bool CanAddSelectionFromSubmenu() => CanAddSelection();

        [InitializeOnLoadMethod]
        static void HookHierarchy()
        {
            SceneHierarchyHooks.addItemsToGameObjectContextMenu += (menu, clicked) =>
            {
                if (clicked == null) return;
                // Right-clicking inside the selection acts on all of it, as the Hierarchy's own items do.
                var targets = Selection.gameObjects.Contains(clicked) ? Selection.gameObjects : new[] { clicked };
                menu.AddSeparator("");
                menu.AddItem(new GUIContent(targets.Length > 1 ? $"{Label} ({targets.Length})" : Label), false, () => Add(targets));
            };
        }

        static void Add(Object[] objects)
        {
            var tagged = AgentPage.AddToChat(objects);
            if (tagged == 0) EditorWindow.focusedWindow?.ShowNotification(new GUIContent("Select scene objects or project assets to add them to the agent."));
        }
    }
}
