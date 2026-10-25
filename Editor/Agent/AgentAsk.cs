using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zetta3D.Editor.Agent
{
    /// <summary>One question on an ask_user card (protocol v1.5).</summary>
    public class AskQuestion
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("question")] public string Question;
        /// <summary>object | choice | text</summary>
        [JsonProperty("kind")] public string Kind;
        /// <summary>object: any | scene | asset</summary>
        [JsonProperty("accept")] public string Accept = "any";
        /// <summary>object: "Prefab", "Material", a component type… (empty: anything).</summary>
        [JsonProperty("assetType")] public string AssetType;
        [JsonProperty("options")] public List<AskOption> Options = new List<AskOption>();
        [JsonProperty("multiple")] public bool Multiple;
        /// <summary>choice and object: a box for the user's own words.</summary>
        [JsonProperty("allowOther")] public bool AllowOther = true;
        [JsonProperty("placeholder")] public string Placeholder;
        [JsonProperty("optional")] public bool Optional;
    }

    public class AskOption
    {
        [JsonProperty("label")] public string Label;
        [JsonProperty("description")] public string Description;
    }

    /// <summary>What the user gave for one question.</summary>
    public class AskAnswer
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("choices")] public List<string> Choices = new List<string>();
        [JsonProperty("objects")] public List<AgentMention> Objects = new List<AgentMention>();
        /// <summary>Their own words: a text answer, or "something else" on a choice or an object question.</summary>
        [JsonProperty("text")] public string Text;

        [JsonIgnore] public bool IsEmpty => Choices.Count == 0 && Objects.Count == 0 && string.IsNullOrWhiteSpace(Text);
    }

    /// <summary>A card the agent wants answered before it carries on.</summary>
    public class AskRequest
    {
        public string Title;
        public List<AskQuestion> Questions = new List<AskQuestion>();
    }

    /// <summary>
    /// The ask_user client tool: shows a card in the chat — drop an object in, pick options, or write — and waits for
    /// the user's OK. The turn carries on with the answers as the result, so the agent never has to start over.
    /// </summary>
    public static class AskTools
    {
        public const int MaxQuestions = 4;

        public static async Task<ToolResult> Run(JObject input, ToolRunContext ctx)
        {
            var request = Parse(input);
            if (ctx.AskUser == null) return ToolResult.Error("This Editor cannot show questions right now. Ask in your reply instead.");
            var answers = await ctx.AskUser(request);
            return answers == null ? ToolResult.Ok(Skipped) : ToolResult.Ok(Format(request, answers));
        }

        public const string Skipped =
            "The user closed the card without answering (Skip). Carry on with your best judgment and say in a line what " +
            "you assumed — or, if you truly cannot go on without it, say in your reply what you need.";

        /// <summary>Reads and checks the call. Throws ToolException with what to fix.</summary>
        public static AskRequest Parse(JObject input)
        {
            var request = new AskRequest { Title = Fmt.Short(Args.Str(input, "title"), 80) };
            if (!(Args.Get(input, "questions") is JArray list) || list.Count == 0)
                throw new ToolException("ask_user needs `questions`: one to four, each with `question` and `kind` (object, choice or text).");
            if (list.Count > MaxQuestions) throw new ToolException($"ask_user takes at most {MaxQuestions} questions in one card.");

            var ids = new HashSet<string>();
            for (var n = 0; n < list.Count; n++)
            {
                if (!(list[n] is JObject q)) throw new ToolException($"Question {n + 1} must be an object.");
                var question = new AskQuestion
                {
                    Id = Args.Str(q, "id"),
                    Question = Fmt.Short(Args.Str(q, "question"), 300),
                    Kind = (Args.Str(q, "kind") ?? "").ToLowerInvariant(),
                    Accept = (Args.Str(q, "accept") ?? "any").ToLowerInvariant(),
                    AssetType = Args.Str(q, "assetType"),
                    Multiple = Args.Bool(q, "multiple", false),
                    AllowOther = Args.Bool(q, "allowOther", true),
                    Placeholder = Fmt.Short(Args.Str(q, "placeholder"), 120),
                    Optional = Args.Bool(q, "optional", false),
                };
                if (string.IsNullOrWhiteSpace(question.Question)) throw new ToolException($"Question {n + 1} has no `question` text.");
                if (question.Kind is not ("object" or "choice" or "text"))
                    throw new ToolException($"Question {n + 1}: `kind` must be object, choice or text.");
                if (question.Accept is not ("any" or "scene" or "asset"))
                    throw new ToolException($"Question {n + 1}: `accept` must be any, scene or asset.");
                if (string.IsNullOrWhiteSpace(question.Id) || !ids.Add(question.Id)) question.Id = $"q{n + 1}";
                ids.Add(question.Id);

                if (question.Kind == "choice")
                {
                    foreach (var o in Args.Get(q, "options") as JArray ?? new JArray())
                    {
                        var label = o.Type == JTokenType.String ? o.ToString() : o["label"]?.ToString();
                        if (string.IsNullOrWhiteSpace(label)) continue;
                        question.Options.Add(new AskOption { Label = Fmt.Short(label.Trim(), 80), Description = Fmt.Short(o.Type == JTokenType.Object ? o["description"]?.ToString() : null, 200) });
                    }
                    if (question.Options.Count < 2 || question.Options.Count > 6)
                        throw new ToolException($"Question {n + 1}: a choice needs two to six `options`.");
                }
                request.Questions.Add(question);
            }
            return request;
        }

        /// <summary>The answers as the agent reads them: one line per question, objects as targetable addresses.</summary>
        public static string Format(AskRequest request, IList<AskAnswer> answers)
        {
            var sb = new StringBuilder("The user answered");
            if (!string.IsNullOrEmpty(request.Title)) sb.Append($" (card \"{request.Title}\")");
            sb.Append(':');
            foreach (var question in request.Questions)
            {
                var answer = answers.FirstOrDefault(a => a.Id == question.Id);
                sb.Append($"\n- {question.Id} — {Fmt.Quote(question.Question)}: ");
                if (answer == null || answer.IsEmpty)
                {
                    sb.Append("left it empty");
                    continue;
                }
                var parts = new List<string>();
                if (answer.Objects.Count > 0) parts.Add(string.Join("; ", answer.Objects.Select(o => o.Describe())));
                if (answer.Choices.Count > 0) parts.Add("chose " + string.Join(", ", answer.Choices.Select(Fmt.Quote)));
                if (!string.IsNullOrWhiteSpace(answer.Text))
                    parts.Add((question.Kind == "text" ? "wrote: " : "in their own words: ") + Fmt.Quote(answer.Text.Trim()));
                sb.Append(string.Join(" — ", parts));
            }
            return sb.ToString();
        }

        public static string Describe(JObject input)
        {
            var title = Args.Str(input, "title");
            if (!string.IsNullOrWhiteSpace(title)) return $"Asking you: {Fmt.Short(title, 60)}";
            var first = (Args.Get(input, "questions") as JArray)?.FirstOrDefault()?["question"]?.ToString();
            return string.IsNullOrWhiteSpace(first) ? "Asking you" : $"Asking you: {Fmt.Short(first, 60)}";
        }

        // ─── Object questions ──────────────────────────────────────────────────────

        /// <summary>The type an object slot takes, from `assetType` (null: any object).</summary>
        public static Type SlotType(AskQuestion q)
        {
            var name = (q.AssetType ?? "").Trim();
            switch (name.ToLowerInvariant())
            {
                case "":
                case "any":
                case "object":
                    return typeof(Object);
                case "prefab":
                case "gameobject":
                case "model":
                    return typeof(GameObject);
                case "scene":
                    return typeof(SceneAsset);
                case "script":
                    return typeof(MonoScript);
                case "folder":
                    return typeof(DefaultAsset);
            }
            return FindType(name) ?? typeof(Object);
        }

        static readonly Dictionary<string, Type> TypeCacheByName = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        static Type FindType(string name)
        {
            if (TypeCacheByName.TryGetValue(name, out var cached)) return cached;
            var all = TypeCache.GetTypesDerivedFrom<Object>();
            var type = all.Where(t => t.FullName == name || t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.Namespace != null && t.Namespace.StartsWith("UnityEngine") ? 0 : t.Namespace != null && t.Namespace.StartsWith("UnityEditor") ? 2 : 1)
                .FirstOrDefault();
            TypeCacheByName[name] = type;
            return type;
        }

        /// <summary>Whether a dropped object fits the slot; the reason when it does not.</summary>
        public static bool Fits(AskQuestion q, Object obj, out string why)
        {
            why = null;
            if (obj == null)
            {
                why = "Nothing there.";
                return false;
            }
            var inScene = (obj is GameObject || obj is Component) && !EditorUtility.IsPersistent(obj);
            if (q.Accept == "scene" && !inScene)
            {
                why = "This needs an object from the scene (drag it from the Hierarchy).";
                return false;
            }
            if (q.Accept == "asset" && inScene)
            {
                why = "This needs an asset from the Project window, not a scene object.";
                return false;
            }
            var kind = (q.AssetType ?? "").Trim().ToLowerInvariant();
            if (kind == "prefab")
            {
                var go = obj as GameObject ?? (obj as Component)?.gameObject;
                if (go == null || (!EditorUtility.IsPersistent(go) && PrefabUtility.GetPrefabInstanceStatus(go) == PrefabInstanceStatus.NotAPrefab) ||
                    (EditorUtility.IsPersistent(go) && PrefabUtility.GetPrefabAssetType(go) == PrefabAssetType.NotAPrefab))
                {
                    why = "This needs a prefab.";
                    return false;
                }
                return true;
            }
            var type = SlotType(q);
            if (type == typeof(Object) || type.IsInstanceOfType(obj)) return true;
            if (typeof(Component).IsAssignableFrom(type))
            {
                var go = obj as GameObject ?? (obj as Component)?.gameObject;
                if (go != null && go.GetComponent(type) != null) return true;
            }
            why = $"This needs a {q.AssetType}.";
            return false;
        }

        /// <summary>A short "what goes here" line for an object slot.</summary>
        public static string SlotHint(AskQuestion q)
        {
            var what = string.IsNullOrWhiteSpace(q.AssetType) ? (q.Multiple ? "objects" : "an object") : (q.Multiple ? q.AssetType + "s" : "a " + q.AssetType);
            var from = q.Accept == "scene" ? "from the Hierarchy" : q.Accept == "asset" ? "from the Project window" : "from the Project window or the Hierarchy";
            return $"Drop {what} {from} here";
        }
    }
}
