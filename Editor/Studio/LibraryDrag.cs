using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// Drag a library model out of the Studio and drop it in the Scene view (placed where it lands)
    /// or the Hierarchy (under the row it lands on). The drop imports it first if needed.
    /// </summary>
    [InitializeOnLoad]
    public static class LibraryDrag
    {
        const string DataKey = "Zetta3D.Generation";

        static LibraryDrag()
        {
            try
            {
                RegisterSceneHandler();
                RegisterHierarchyHandler();
            }
            catch (Exception)
            {
                // Drop handlers are a convenience; the library's Import / Place buttons still work.
            }
        }

        /// <summary>
        /// The drop-handler API moved from instance ids to entity ids in Unity 6 (AddDropHandlerV2),
        /// and the old overloads became compile errors. Binding by reflection keeps one source for
        /// 2022.3 through Unity 6.
        /// </summary>
        static void RegisterSceneHandler()
        {
            var handler = (DragAndDrop.SceneDropHandler)OnSceneDrop;
            var add = FindAdd(typeof(DragAndDrop.SceneDropHandler));
            add?.Invoke(null, new object[] { handler });
        }

        static void RegisterHierarchyHandler()
        {
            var type = typeof(DragAndDrop).GetNestedType("HierarchyDropHandlerV2") ?? typeof(DragAndDrop).GetNestedType("HierarchyDropHandler");
            if (type == null) return;
            var invoke = type.GetMethod("Invoke");
            var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
            if (parameters.Length != 4) return;
            var target = typeof(LibraryDrag).GetMethod(nameof(OnHierarchyDrop), BindingFlags.NonPublic | BindingFlags.Static);
            var call = Expression.Call(target, Expression.Convert(parameters[0], typeof(object)), parameters[1], parameters[2], parameters[3]);
            var handler = Expression.Lambda(type, call, parameters).Compile();
            FindAdd(type)?.Invoke(null, new object[] { handler });
        }

        static MethodInfo FindAdd(Type handlerType) =>
            typeof(DragAndDrop).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name is "AddDropHandlerV2" or "AddDropHandler")
                .OrderByDescending(m => m.Name == "AddDropHandlerV2")
                .FirstOrDefault(m =>
                {
                    var ps = m.GetParameters();
                    return ps.Length == 1 && ps[0].ParameterType == handlerType;
                });

        /// <summary>The object behind a Hierarchy row id (an int before Unity 6, an EntityId after).</summary>
        static UnityEngine.Object ObjectFromId(object id)
        {
            if (id == null) return null;
            var method = typeof(EditorUtility).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name is "EntityIdToObject" or "InstanceIDToObject"
                                     && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == id.GetType());
            return method?.Invoke(null, new[] { id }) as UnityEngine.Object;
        }

        public static void MakeDraggable(VisualElement element, Generation generation)
        {
            // A splat is not supported in Unity: it is never dropped into a scene.
            if (generation == null || generation.IsSplat) return;
            Vector2? start = null;
            element.RegisterCallback<MouseDownEvent>(e =>
            {
                if (e.button == 0) start = e.mousePosition;
            });
            element.RegisterCallback<MouseUpEvent>(_ => start = null);
            element.RegisterCallback<MouseLeaveEvent>(_ =>
            {
                if (start != null) Begin(generation);
                start = null;
            });
            element.RegisterCallback<MouseMoveEvent>(e =>
            {
                if (start == null || (e.pressedButtons & 1) == 0) return;
                if ((e.mousePosition - start.Value).sqrMagnitude < 64) return;
                start = null;
                Begin(generation);
            });
        }

        static void Begin(Generation generation)
        {
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = Array.Empty<UnityEngine.Object>();
            DragAndDrop.paths = Array.Empty<string>();
            DragAndDrop.SetGenericData(DataKey, generation);
            DragAndDrop.StartDrag(generation.DisplayTitle);
        }

        static Generation Dragged => DragAndDrop.GetGenericData(DataKey) as Generation;

        static DragAndDropVisualMode OnSceneDrop(UnityEngine.Object dropUpon, Vector3 worldPosition, Vector2 viewportPosition, Transform parentForDraggedObjects, bool perform)
        {
            var generation = Dragged;
            if (generation == null || generation.IsSplat) return DragAndDropVisualMode.None;
            if (perform)
            {
                DragAndDrop.AcceptDrag();
                DragAndDrop.SetGenericData(DataKey, null);
                Place(generation, new ZettaImporter.Placement { Parent = parentForDraggedObjects, Position = worldPosition });
            }
            return DragAndDropVisualMode.Copy;
        }

        static DragAndDropVisualMode OnHierarchyDrop(object dropTarget, HierarchyDropFlags dropMode, Transform parentForDraggedObjects, bool perform)
        {
            var generation = Dragged;
            if (generation == null || generation.IsSplat) return DragAndDropVisualMode.None;
            if (perform)
            {
                DragAndDrop.AcceptDrag();
                DragAndDrop.SetGenericData(DataKey, null);
                var parent = parentForDraggedObjects;
                if (parent == null && (dropMode & HierarchyDropFlags.DropUpon) != 0 && ObjectFromId(dropTarget) is GameObject target)
                    parent = target.transform;
                Place(generation, new ZettaImporter.Placement { Parent = parent, Position = parent != null ? parent.position : (Vector3?)null });
            }
            return DragAndDropVisualMode.Copy;
        }

        /// <summary>Uses the imported copy when there is one; otherwise imports first.</summary>
        static async void Place(Generation generation, ZettaImporter.Placement placement)
        {
            Notify($"Placing {generation.DisplayTitle}…");
            try
            {
                var existing = StudioAssets.FindImported(generation.Id);
                if (existing != null)
                {
                    var paths = new System.Collections.Generic.List<string> { AssetDatabase.GetAssetPath(existing) };
                    var job = new ZettaJobs.Job { Id = generation.Id, Label = generation.DisplayTitle, ImportedPath = paths[0], Latest = generation };
                    var placed = JobsStrip.PlaceInScene(job);
                    if (placed != null)
                    {
                        if (placement.Parent != null) Undo.SetTransformParent(placed.transform, placement.Parent, "Parent model");
                        if (placement.Position != null)
                        {
                            placed.transform.position = placement.Position.Value;
                            ZettaImporter.Ground(placed);
                        }
                        return;
                    }
                }
                await ZettaImporter.ImportAsync(generation, null, placement);
            }
            catch (ZettaApiException e)
            {
                Notify(ZettaCatalog.Explain(e));
            }
            catch (Exception e)
            {
                Notify("Could not place the model: " + e.Message);
            }
        }

        static void Notify(string message) =>
            SceneView.lastActiveSceneView?.ShowNotification(new GUIContent(message), 2.5);
    }
}
