using System;
using System.Collections.Generic;
using System.Linq;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// The logic behind a pipeline run's group in the library (the web's run-groups.ts): its name, its models'
    /// order and their step tags. The server sends a run as one entry carrying its models (<c>groupRuns</c>), so a
    /// group is never split across pages.
    /// </summary>
    public static class RunGroups
    {
        /// <summary>What a run is called — the name its models share (the seed's) — and its detail line.</summary>
        public static (string name, string detail) Name(RunGroup group)
        {
            // Oldest first: the first model made carries the plainest title ("racingSeat"; later ones add " - Retopology").
            var first = group.Members.Count > 0 ? group.Members[group.Members.Count - 1] : null;
            var seed = first?.Title?.Split(new[] { " - " }, StringSplitOptions.None)[0].Trim();
            var name = !string.IsNullOrEmpty(seed) ? seed
                : !string.IsNullOrWhiteSpace(group.PipelineTitle) ? group.PipelineTitle
                : $"Pipeline run #{group.RunId}";
            var count = Count(group);
            var models = $"{count} model{(count == 1 ? "" : "s")}";
            var detail = !string.IsNullOrWhiteSpace(group.PipelineTitle)
                ? $"{models} · {group.PipelineTitle} · run #{group.RunId}"
                : $"{models} · run #{group.RunId}";
            return (name, detail);
        }

        public static int Count(RunGroup group) => group.Count > 0 ? group.Count : group.Members.Count;

        /// <summary>
        /// A run's models in the order it made them — the pipeline's own step order where the server knows it,
        /// made-at otherwise, the id breaking a tie. (The server sends them newest first.)
        /// </summary>
        public static List<Generation> InRunOrder(IEnumerable<Generation> members)
        {
            var list = members.ToList();
            list.Sort((a, b) =>
            {
                var pa = a.RunStep?.Position;
                var pb = b.RunStep?.Position;
                if (pa != null && pb != null && pa != pb) return pa.Value.CompareTo(pb.Value);
                if (pa != null && pb == null) return -1;
                if (pa == null && pb != null) return 1;
                var at = a.CreatedAt.CompareTo(b.CreatedAt);
                return at != 0 ? at : a.Id.CompareTo(b.Id);
            });
            return list;
        }

        /// <summary>A model's step tag: "Step 2 · Retopology · seat" — its number in the run and, when known, the step's name and part.</summary>
        public static string StepText(Generation member, int index)
        {
            var step = member.RunStep;
            var parts = step != null
                ? new[] { $"Step {step.Position}", step.Label, step.Part }
                : new[] { $"Step {index + 1}" };
            return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        /// <summary>The page's rows with each run's models in place of its entry — what select-all and bulk actions look through.</summary>
        public static IEnumerable<Generation> Flatten(IEnumerable<Generation> entries) =>
            entries.SelectMany(e => e.RunGroup != null && e.RunGroup.Members.Count > 0 ? (IEnumerable<Generation>)e.RunGroup.Members : new[] { e });
    }
}
