using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Zetta3D.Editor
{
    /// <summary>
    /// Scene queries that include inactive objects and every loaded scene, without the
    /// FindObjectsByType overloads that differ between Unity 2022.3 and Unity 6.
    /// </summary>
    public static class ZettaScene
    {
        public static IEnumerable<GameObject> Roots()
        {
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects()) yield return root;
            }
        }

        public static IEnumerable<T> All<T>() where T : Component =>
            Roots().SelectMany(root => root.GetComponentsInChildren<T>(true));

        /// <summary>"Parent/Child" from a root of any loaded scene (active scene first), inactive included.</summary>
        public static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var colon = path.IndexOf(".unity:");
            if (colon >= 0) path = path.Substring(colon + ".unity:".Length);
            var parts = path.Trim('/').Split('/');
            var active = SceneManager.GetActiveScene();
            var roots = Roots().OrderBy(r => r.scene == active ? 0 : 1);
            foreach (var root in roots)
            {
                if (root.name != parts[0]) continue;
                var current = root.transform;
                for (var i = 1; i < parts.Length && current != null; i++)
                {
                    Transform next = null;
                    foreach (Transform child in current)
                        if (child.name == parts[i])
                        {
                            next = child;
                            break;
                        }
                    current = next;
                }
                if (current != null) return current.gameObject;
            }
            return null;
        }
    }
}
