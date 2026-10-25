using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// The ask_user card in the chat: a slot to drop objects into, options to pick, or a box to write in — and OK to
    /// let the agent carry on. What the user has filled in lives here, so a redraw of the conversation keeps it.
    /// </summary>
    public static class AskCard
    {
        class Draft
        {
            public readonly HashSet<string> Choices = new HashSet<string>();
            public readonly List<Object> Objects = new List<Object>();
            public string Text = "";
            public bool OtherOpen;

            public bool IsEmpty => Choices.Count == 0 && Objects.Count(o => o != null) == 0 && string.IsNullOrWhiteSpace(Text);
        }

        static readonly Dictionary<ChatItem, Dictionary<string, Draft>> Drafts = new Dictionary<ChatItem, Dictionary<string, Draft>>();

        static List<AskQuestion> Questions(ChatItem item) =>
            JsonConvert.DeserializeObject<List<AskQuestion>>(item.QuestionsJson ?? "[]") ?? new List<AskQuestion>();

        public static VisualElement Build(ChatItem item)
        {
            var questions = Questions(item);
            var card = new VisualElement().Classes("z-ask");
            // Drops on the card belong to its slots — not to the message box behind it.
            card.RegisterCallback<DragUpdatedEvent>(e => e.StopPropagation());
            card.RegisterCallback<DragPerformEvent>(e => e.StopPropagation());

            var head = new VisualElement().Classes("z-row");
            head.Add(ZettaUI.Chip(item.Resolved ? StatusLabel(item.Status) : "Needs your input", item.Resolved ? null : "signal"));
            card.Add(head);
            if (!string.IsNullOrEmpty(item.Text)) card.Add(ZettaUI.Title(item.Text));

            if (item.Resolved)
            {
                Summary(card, questions, item);
                Drafts.Remove(item);
                return card;
            }

            if (!Drafts.TryGetValue(item, out var drafts)) Drafts[item] = drafts = new Dictionary<string, Draft>();
            foreach (var q in questions)
                if (!drafts.ContainsKey(q.Id)) drafts[q.Id] = new Draft();

            Button ok = null;
            void Refresh() => ok?.SetEnabled(questions.All(q => q.Optional || !drafts[q.Id].IsEmpty));

            foreach (var q in questions) card.Add(Question(q, drafts[q.Id], Refresh));

            var row = new VisualElement().Classes("z-row", "z-ask-actions");
            ok = ZettaUI.Button("OK", () => AgentRuntime.SubmitAnswers(item, Collect(questions, drafts)), "z-button--primary");
            ok.tooltip = "Send your answers — the agent carries on";
            row.Add(ok);
            var skip = ZettaUI.Button("Skip", () => AgentRuntime.SubmitAnswers(item, null), "z-button--ghost");
            skip.tooltip = "Let the agent carry on without an answer";
            row.Add(skip);
            card.Add(row);
            Refresh();
            return card;
        }

        static string StatusLabel(string status) => status switch
        {
            "answered" => "Answered",
            "skipped" => "Skipped",
            "stopped" => "Stopped",
            "superseded" => "Answered in the chat",
            _ => "Closed",
        };

        static VisualElement Question(AskQuestion q, Draft draft, Action changed)
        {
            var box = new VisualElement().Classes("z-ask-question");
            box.Add(new Label(q.Question + (q.Optional ? "  (optional)" : "")).Classes("z-ask-label"));
            switch (q.Kind)
            {
                case "object":
                    box.Add(ObjectSlot(q, draft, changed));
                    if (q.AllowOther) box.Add(Other(draft, changed, "Describe it instead", "Or say which one in your own words", null));
                    break;
                case "choice":
                    box.Add(Choices(q, draft, changed));
                    break;
                default:
                    box.Add(TextBox(draft, changed, q.Placeholder));
                    break;
            }
            return box;
        }

        // ─── Object slot ────────────────────────────────────────────────────────────

        static VisualElement ObjectSlot(AskQuestion q, Draft draft, Action changed)
        {
            var slot = new VisualElement().Classes("z-ask-slot");
            var chips = new VisualElement().Classes("z-row", "z-row--wrap");
            var error = new Label().Classes("z-error");
            error.Visible(false);

            var field = new ObjectField
            {
                objectType = AskTools.SlotType(q),
                allowSceneObjects = q.Accept != "asset",
                tooltip = AskTools.SlotHint(q),
            };
            field.AddToClassList("z-ask-field");

            void Show(string why)
            {
                error.text = why ?? "";
                error.Visible(!string.IsNullOrEmpty(why));
            }

            void Draw()
            {
                chips.Clear();
                if (!q.Multiple) return;
                foreach (var o in draft.Objects.Where(o => o != null).ToList())
                {
                    var obj = o;
                    var chip = new VisualElement().Classes("z-attach-chip", "z-attach-chip--asset");
                    var icon = AssetPreview.GetMiniThumbnail(obj);
                    if (icon != null) chip.Add(new Image { image = icon }.Classes("z-attach-icon"));
                    chip.Add(new Label(obj.name).Classes("z-attach-label"));
                    var x = new Button(() =>
                    {
                        draft.Objects.Remove(obj);
                        Draw();
                        changed();
                    }) { text = "✕" };
                    x.AddToClassList("z-attach-x");
                    chip.Add(x);
                    chips.Add(chip);
                }
            }

            /// <summary>Takes what fits; says why the rest did not. Returns whether anything was taken.</summary>
            bool Take(IEnumerable<Object> objects)
            {
                string why = null;
                var took = false;
                foreach (var obj in objects)
                {
                    if (!AskTools.Fits(q, obj, out var reason))
                    {
                        why ??= $"“{obj?.name}”: {reason}";
                        continue;
                    }
                    if (!q.Multiple) draft.Objects.Clear();
                    if (!draft.Objects.Contains(obj)) draft.Objects.Add(obj);
                    took = true;
                    if (!q.Multiple) break;
                }
                Show(took ? null : why);
                if (took && !q.Multiple) field.SetValueWithoutNotify(draft.Objects[0]);
                Draw();
                changed();
                return took;
            }

            field.RegisterValueChangedCallback(e =>
            {
                if (e.newValue == null)
                {
                    if (!q.Multiple) draft.Objects.Clear();
                    Show(null);
                    changed();
                    return;
                }
                if (!Take(new[] { e.newValue }) && !q.Multiple) field.SetValueWithoutNotify(e.previousValue);
                if (q.Multiple) field.SetValueWithoutNotify(null); // the field adds; the chips hold what was added
            });

            // Several dragged at once: all of them go in (the field alone would keep only the first).
            if (q.Multiple)
            {
                slot.RegisterCallback<DragUpdatedEvent>(e =>
                {
                    if (DragAndDrop.objectReferences is { Length: > 0 }) DragAndDrop.visualMode = DragAndDropVisualMode.Link;
                    slot.AddToClassList("z-ask-slot--drop");
                    e.StopPropagation();
                }, TrickleDown.TrickleDown);
                slot.RegisterCallback<DragLeaveEvent>(_ => slot.RemoveFromClassList("z-ask-slot--drop"));
                slot.RegisterCallback<DragPerformEvent>(e =>
                {
                    slot.RemoveFromClassList("z-ask-slot--drop");
                    DragAndDrop.AcceptDrag();
                    Take(DragAndDrop.objectReferences ?? new Object[0]);
                    e.StopPropagation();
                }, TrickleDown.TrickleDown);
            }

            var row = new VisualElement().Classes("z-row", "z-ask-slot-row");
            row.Add(field);
            var selection = ZettaUI.Button("Use selection", () =>
            {
                if (Selection.objects.Length == 0) Show("Nothing is selected.");
                else Take(Selection.objects);
            }, "z-button--ghost", "z-button--small");
            selection.tooltip = "Put what is selected in the Project window or the Hierarchy here";
            row.Add(selection);

            slot.Add(new Label(AskTools.SlotHint(q)).Classes("z-hint"));
            slot.Add(row);
            slot.Add(chips);
            slot.Add(error);
            if (!q.Multiple && draft.Objects.Count > 0) field.SetValueWithoutNotify(draft.Objects[0]);
            Draw();
            return slot;
        }

        // ─── Choices ────────────────────────────────────────────────────────────────

        static VisualElement Choices(AskQuestion q, Draft draft, Action changed)
        {
            var list = new VisualElement().Classes("z-ask-options");
            var rows = new List<(VisualElement row, string label)>();
            VisualElement otherBox = null;

            void Mark()
            {
                foreach (var (row, label) in rows) row.EnableInClassList("z-ask-option--on", draft.Choices.Contains(label));
            }

            foreach (var option in q.Options)
            {
                var label = option.Label;
                var row = new VisualElement().Classes("z-ask-option");
                row.Add(new Label(q.Multiple ? "☐" : "○").Classes("z-ask-mark"));
                var text = new VisualElement().Classes("z-ask-option-text");
                text.Add(new Label(label).Classes("z-ask-option-label"));
                if (!string.IsNullOrEmpty(option.Description)) text.Add(new Label(option.Description).Classes("z-hint"));
                row.Add(text);
                row.RegisterCallback<ClickEvent>(_ =>
                {
                    if (q.Multiple)
                    {
                        if (!draft.Choices.Remove(label)) draft.Choices.Add(label);
                    }
                    else
                    {
                        // One answer: a pick replaces the last one, and their own words with it.
                        draft.Choices.Clear();
                        draft.Choices.Add(label);
                        draft.Text = "";
                        draft.OtherOpen = false;
                        otherBox?.Q<TextField>()?.SetValueWithoutNotify("");
                        otherBox?.Q(className: "z-ask-other-input")?.Visible(false);
                    }
                    Mark();
                    MarkGlyphs(rows, draft, q.Multiple);
                    changed();
                });
                rows.Add((row, label));
                list.Add(row);
            }

            if (q.AllowOther)
            {
                otherBox = Other(draft, changed, "Something else…", "Describe what you want", () =>
                {
                    // Their own words instead of an option, when only one answer is taken.
                    if (q.Multiple) return;
                    draft.Choices.Clear();
                    Mark();
                    MarkGlyphs(rows, draft, false);
                });
                list.Add(otherBox);
            }
            Mark();
            MarkGlyphs(rows, draft, q.Multiple);
            return list;
        }

        static void MarkGlyphs(List<(VisualElement row, string label)> rows, Draft draft, bool multiple)
        {
            foreach (var (row, label) in rows)
            {
                var mark = row.Q<Label>(className: "z-ask-mark");
                var on = draft.Choices.Contains(label);
                if (mark != null) mark.text = multiple ? (on ? "☑" : "☐") : (on ? "●" : "○");
            }
        }

        /// <summary>"Something else…": a link that opens a box for the user's own words.</summary>
        static VisualElement Other(Draft draft, Action changed, string link, string placeholder, Action typed)
        {
            var box = new VisualElement().Classes("z-ask-other");
            var input = TextBox(draft, () =>
            {
                if (!string.IsNullOrWhiteSpace(draft.Text)) typed?.Invoke();
                changed();
            }, placeholder);
            input.AddToClassList("z-ask-other-input");
            input.Visible(draft.OtherOpen || !string.IsNullOrEmpty(draft.Text));
            var toggle = ZettaUI.Button(link, () =>
            {
                draft.OtherOpen = !draft.OtherOpen || !string.IsNullOrEmpty(draft.Text);
                input.Visible(draft.OtherOpen);
                if (draft.OtherOpen) input.Q<TextField>()?.Focus();
            }, "z-button--ghost", "z-button--small");
            box.Add(toggle);
            box.Add(input);
            return box;
        }

        static VisualElement TextBox(Draft draft, Action changed, string placeholder)
        {
            var wrap = new VisualElement();
            var field = new TextField { multiline = true, value = draft.Text ?? "" };
            field.AddToClassList("z-ask-text");
#if UNITY_2023_1_OR_NEWER
            if (!string.IsNullOrEmpty(placeholder)) field.textEdition.placeholder = placeholder;
#else
            if (!string.IsNullOrEmpty(placeholder)) wrap.Add(ZettaUI.Hint(placeholder));
#endif
            field.RegisterValueChangedCallback(e =>
            {
                draft.Text = e.newValue;
                changed();
            });
            // Enter in the box is a new line, not the composer's Send.
            field.RegisterCallback<KeyDownEvent>(e => e.StopPropagation());
            wrap.Add(field);
            return wrap;
        }

        // ─── Answers ────────────────────────────────────────────────────────────────

        static List<AskAnswer> Collect(List<AskQuestion> questions, Dictionary<string, Draft> drafts) =>
            questions.Select(q =>
            {
                var d = drafts[q.Id];
                return new AskAnswer
                {
                    Id = q.Id,
                    // Kept in the order the agent listed them.
                    Choices = q.Options.Select(o => o.Label).Where(d.Choices.Contains).ToList(),
                    Objects = d.Objects.Where(o => o != null).Select(AgentMention.FromObject).Where(m => m != null).ToList(),
                    Text = string.IsNullOrWhiteSpace(d.Text) ? null : d.Text.Trim(),
                };
            }).ToList();

        static void Summary(VisualElement card, List<AskQuestion> questions, ChatItem item)
        {
            if (item.Status != "answered") return;
            var answers = JsonConvert.DeserializeObject<List<AskAnswer>>(item.AnswersJson ?? "[]") ?? new List<AskAnswer>();
            foreach (var q in questions)
            {
                var a = answers.FirstOrDefault(x => x.Id == q.Id);
                var parts = new List<string>();
                if (a != null)
                {
                    parts.AddRange(a.Objects.Select(o => "@" + o.Name));
                    parts.AddRange(a.Choices);
                    if (!string.IsNullOrWhiteSpace(a.Text)) parts.Add("“" + a.Text + "”");
                }
                var box = new VisualElement().Classes("z-ask-question");
                box.Add(new Label(q.Question).Classes("z-hint"));
                box.Add(new Label(parts.Count == 0 ? "—" : string.Join(", ", parts)).Classes("z-ask-answer"));
                card.Add(box);
            }
        }
    }
}
