using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zetta3D.Editor.Agent
{
    /// <summary>add_component, remove_component, set_properties — and component type lookup.</summary>
    static class ComponentTools
    {
        // ─── Type resolution ─────────────────────────────────────────────────────

        static HashSet<string> _userAssemblies;

        static HashSet<string> UserAssemblies()
        {
            if (_userAssemblies != null) return _userAssemblies;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Assembly-CSharp", "Assembly-CSharp-firstpass" };
            try
            {
                foreach (var a in CompilationPipeline.GetAssemblies())
                    if (a.sourceFiles.Any(f => f.StartsWith("Assets/")))
                        set.Add(a.name);
            }
            catch (Exception)
            {
                // Only a preference order; the defaults are enough.
            }
            return _userAssemblies = set;
        }

        static bool Preferred(Type t) =>
            (t.Namespace ?? "").StartsWith("UnityEngine") || UserAssemblies().Contains(t.Assembly.GetName().Name);

        /// <summary>A Component type by full name, then simple name; throws with an actionable message.</summary>
        public static Type ResolveType(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ToolException("A component type is required (e.g. \"Rigidbody\" or \"MyGame.PlayerController\").");
            if (name.Equals("GameObject", StringComparison.OrdinalIgnoreCase)) throw new ToolException("GameObject is not a component.");

            var all = TypeCache.GetTypesDerivedFrom<Component>()
                .Where(t => !t.IsGenericTypeDefinition)
                .ToList();

            var byFull = all.Where(t => t.FullName == name).ToList();
            if (byFull.Count == 0) byFull = all.Where(t => string.Equals(t.FullName, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byFull.Count >= 1) return Check(byFull[0]);

            var simple = name.Contains('.') ? name.Substring(name.LastIndexOf('.') + 1) : name;
            var byName = all.Where(t => t.Name == simple).ToList();
            if (byName.Count == 0) byName = all.Where(t => string.Equals(t.Name, simple, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1) return Check(byName[0]);
            if (byName.Count > 1)
            {
                var preferred = byName.Where(Preferred).ToList();
                if (preferred.Count == 1) return Check(preferred[0]);
                var list = string.Join(", ", (preferred.Count > 1 ? preferred : byName).Select(t => t.FullName).Take(8));
                throw new ToolException($"'{name}' is ambiguous: {list}. Use the full name.");
            }
            throw new ToolException($"Type '{name}' was not found. If you just wrote it, it will exist after compilation — add it in your next step.");
        }

        static Type Check(Type t)
        {
            if (t.IsAbstract) throw new ToolException($"{t.FullName} is abstract; add a concrete subclass.");
            if (t == typeof(Transform)) throw new ToolException("Every GameObject already has a Transform.");
            return t;
        }

        // ─── Adding ──────────────────────────────────────────────────────────────

        /// <summary>Adds a component with Undo and applies properties. Throws on failure.</summary>
        public static (Component component, PropertyWriter.Outcome outcome) Add(GameObject go, string typeName, JObject properties)
        {
            var type = ResolveType(typeName);
            if (type.GetCustomAttributes(typeof(DisallowMultipleComponent), true).Length > 0 && go.GetComponent(type) != null)
                throw new ToolException($"{go.name} already has a {type.Name} (only one is allowed). Use set_properties to change it.");
            var component = Undo.AddComponent(go, type);
            if (component == null)
                throw new ToolException($"Unity refused to add {type.Name} to {go.name} (it may conflict with an existing component, e.g. 2D vs 3D physics).");
            var outcome = PropertyWriter.Apply(component, properties);
            return (component, outcome);
        }

        public static ToolResult AddComponent(JObject input)
        {
            var go = TargetResolver.Resolve(Args.Required(input, "target"), out var note);
            var (component, outcome) = Add(go, Args.Required(input, "type"), Args.Get(input, "properties") as JObject);
            EditorSceneManager.MarkSceneDirty(go.scene);
            var index = go.GetComponents(component.GetType()).ToList().IndexOf(component);
            var text = $"Added {component.GetType().Name} to {TargetResolver.Label(go)}" + (index > 0 ? $" (index {index})." : ".");
            if (outcome.Applied.Count + outcome.Errors.Count > 0) text += " " + outcome.Describe();
            if (note != null) text += " Note: " + note;
            return ToolResult.Ok(text + SceneTools.PlayModeNote());
        }

        // ─── Removing ────────────────────────────────────────────────────────────

        public static ToolResult RemoveComponent(JObject input)
        {
            var go = TargetResolver.Resolve(Args.Required(input, "target"), out var note);
            var typeName = Args.Required(input, "component");
            var component = TargetResolver.FindComponent(go, typeName, Args.Int(input, "index", 0), out var error);
            if (component == null) throw new ToolException(error);
            if (component is Transform) throw new ToolException("A Transform cannot be removed; delete the GameObject instead.");

            var dependents = go.GetComponents<Component>()
                .Where(c => c != null && c != component)
                .Where(c => c.GetType().GetCustomAttributes(typeof(RequireComponent), true).Cast<RequireComponent>()
                    .Any(r => new[] { r.m_Type0, r.m_Type1, r.m_Type2 }.Any(t => t != null && t.IsInstanceOfType(component) &&
                        go.GetComponents(t).Length <= 1)))
                .Select(c => c.GetType().Name)
                .ToList();
            if (dependents.Count > 0)
                throw new ToolException($"{component.GetType().Name} cannot be removed: {string.Join(", ", dependents)} requires it. Remove those first.");

            var name = component.GetType().Name;
            Undo.DestroyObjectImmediate(component);
            if (component != null) throw new ToolException($"Unity did not remove {name} from {go.name}.");
            EditorSceneManager.MarkSceneDirty(go.scene);
            var text = $"Removed {name} from {TargetResolver.Label(go)}.";
            if (note != null) text += " Note: " + note;
            return ToolResult.Ok(text + SceneTools.PlayModeNote());
        }

        // ─── Setting properties ──────────────────────────────────────────────────

        public static ToolResult SetProperties(JObject input)
        {
            var go = TargetResolver.Resolve(Args.Required(input, "target"), out var note);
            var typeName = Args.Required(input, "component");
            if (!(Args.Get(input, "properties") is JObject properties) || properties.Count == 0)
                throw new ToolException("'properties' must be an object of { \"propertyPath\": value }.");

            Object target;
            if (typeName.Equals("GameObject", StringComparison.OrdinalIgnoreCase)) target = go;
            else
            {
                var component = TargetResolver.FindComponent(go, typeName, Args.Int(input, "index", 0), out var error);
                if (component == null) throw new ToolException(error + " Use add_component first.");
                target = component;
            }

            var outcome = PropertyWriter.Apply(target, properties);
            if (outcome.Applied.Count > 0) EditorSceneManager.MarkSceneDirty(go.scene);
            var text = $"{target.GetType().Name} on {TargetResolver.Label(go)}: {outcome.Describe()}";
            if (note != null) text += " Note: " + note;
            text += SceneTools.PlayModeNote();
            return outcome.Applied.Count == 0 ? ToolResult.Error(text) : ToolResult.Ok(text);
        }
    }
}
