using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Zetta3D.Editor.Pipelines.UI
{
    /// <summary>
    /// The recipe editor's edits, as pure functions of the graph — what the palette, the canvas and the step form ask
    /// for, in <see cref="GraphEditing"/>'s terms, each with the undo coalesce key the editor commits it under.
    /// </summary>
    public static class RecipeEdits
    {
        /// <summary>
        /// A palette step: dropped at a place (unwired), or clicked in after the selected step and wired from it. Null
        /// when the pipeline already has a seed and this is one. Throws as <see cref="GraphEditing.AddStep(PipelineGraph,string,out string,string,string,JObject,NodePosition?,ThreeDEntitlements)"/>.
        /// </summary>
        public static PipelineGraph Add(PipelineGraph graph, string kind, string selected, NodePosition? at, ThreeDEntitlements gates, out string nodeId)
        {
            nodeId = null;
            if (NodeCatalog.IsSeed(kind) && graph.Nodes.Any(n => NodeCatalog.IsSeed(n.Kind))) return null;
            var after = at == null && selected != null && graph.Node(selected) != null ? selected : null;
            return GraphEditing.AddStep(graph, kind, out nodeId, after, null, null, at, gates);
        }

        /// <summary>A form's change to one step: merged over its settings — for Generate, through applyFormPatch.</summary>
        public static PipelineGraph Patch(PipelineGraph graph, string nodeId, JObject patch, ThreeDEntitlements gates)
        {
            var node = graph.Node(nodeId);
            if (node == null || patch == null) return null;
            var current = FormContext.SettingsOf(node);
            JObject next;
            if (node.Kind == NodeKinds.Generate3D || node.Kind == NodeKinds.GenerateSmart3D) next = GenerateForm.ApplyPatch(current, patch);
            else
            {
                next = current;
                foreach (var pair in patch) next[pair.Key] = pair.Value?.DeepClone();
            }
            return GraphEditing.SetSettings(graph, nodeId, next, gates);
        }

        /// <summary>Typing in one step's settings (or its name) is one undo, not one per key.</summary>
        public static string SettingsKey(string nodeId) => $"settings:{nodeId}";

        /// <summary>Steps and wires deleted on the canvas (a step's own wires go with it).</summary>
        public static PipelineGraph Remove(PipelineGraph graph, IEnumerable<string> nodeIds, IEnumerable<string> edgeIds, ThreeDEntitlements gates)
        {
            var next = graph;
            foreach (var id in nodeIds) next = GraphEditing.Remove(next, id);
            foreach (var id in edgeIds) next = GraphEditing.Disconnect(next, id, gates);
            return next;
        }

        /// <summary>Steps dragged to new places; one drag of the same steps is one undo.</summary>
        public static PipelineGraph Move(PipelineGraph graph, IDictionary<string, NodePosition> moved, out string coalesce)
        {
            var next = graph;
            foreach (var pair in moved)
                if (next.Node(pair.Key) != null)
                    next = GraphEditing.Move(next, pair.Key, pair.Value);
            coalesce = "move:" + string.Join(",", moved.Keys.OrderBy(k => k, System.StringComparer.Ordinal));
            return next;
        }
    }
}
