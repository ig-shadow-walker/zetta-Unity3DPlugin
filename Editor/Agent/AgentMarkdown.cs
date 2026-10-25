using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// Enough Markdown for chat replies: paragraphs, headings, bullets, bold/italic, inline code, and
    /// fenced code blocks drawn as selectable text with a Copy button.
    /// </summary>
    public static class AgentMarkdown
    {
        static readonly Regex Fence = new Regex(@"```([\w#+.-]*)[^\n]*\n(.*?)(```|$)", RegexOptions.Singleline);

        public static VisualElement Render(string markdown)
        {
            var root = new VisualElement();
            markdown ??= "";
            var last = 0;
            foreach (Match match in Fence.Matches(markdown))
            {
                AddText(root, markdown.Substring(last, match.Index - last));
                AddCode(root, match.Groups[2].Value.TrimEnd('\n'), match.Groups[1].Value);
                last = match.Index + match.Length;
            }
            AddText(root, markdown.Substring(last));
            return root;
        }

        static void AddText(VisualElement root, string text)
        {
            text = text.Trim('\n');
            if (text.Trim().Length == 0) return;
            var label = new Label(Inline(text)) { enableRichText = true };
            label.AddToClassList("z-msg-text");
#if UNITY_2022_2_OR_NEWER
            label.selection.isSelectable = true;
#endif
            root.Add(label);
        }

        static void AddCode(VisualElement root, string code, string language)
        {
            var box = new VisualElement();
            box.style.backgroundColor = new Color(0.08f, 0.075f, 0.06f);
            box.style.borderTopLeftRadius = box.style.borderTopRightRadius = box.style.borderBottomLeftRadius = box.style.borderBottomRightRadius = 8;
            box.style.marginTop = 4;
            box.style.marginBottom = 4;
            box.style.paddingLeft = 8;
            box.style.paddingRight = 4;
            box.style.paddingTop = 4;
            box.style.paddingBottom = 6;

            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            var lang = new Label(string.IsNullOrEmpty(language) ? "code" : language);
            lang.AddToClassList("z-hint");
            lang.style.flexGrow = 1;
            bar.Add(lang);
            var copy = new Button(() => EditorGUIUtility.systemCopyBuffer = code) { text = "Copy" };
            copy.AddToClassList("z-button");
            copy.AddToClassList("z-button--ghost");
            copy.AddToClassList("z-button--small");
            bar.Add(copy);
            box.Add(bar);

            var field = new TextField { multiline = true, value = code, isReadOnly = true };
            field.style.whiteSpace = WhiteSpace.Normal;
            var input = field.Q(className: "unity-base-text-field__input");
            if (input != null)
            {
                input.style.backgroundColor = new Color(0, 0, 0, 0);
                input.style.borderTopWidth = input.style.borderBottomWidth = input.style.borderLeftWidth = input.style.borderRightWidth = 0;
                input.style.color = new Color(0.55f, 0.78f, 1f);
            }
            box.Add(field);
            root.Add(box);
        }

        static readonly Regex Bold = new Regex(@"\*\*(.+?)\*\*");
        static readonly Regex Italic = new Regex(@"(?<![\*\w])\*(?!\s)(.+?)(?<!\s)\*(?![\*\w])");
        static readonly Regex Code = new Regex(@"`([^`]+)`");
        static readonly Regex Link = new Regex(@"\[([^\]]+)\]\(([^)]+)\)");

        /// <summary>Inline Markdown → UI Toolkit rich text, with anything that looks like a tag neutralised.</summary>
        public static string Inline(string text)
        {
            var lines = text.Replace("\r", "").Split('\n');
            var output = new StringBuilder();
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                var indent = line.Length - trimmed.Length;
                string prefix = "", suffix = "";
                if (trimmed.StartsWith("### ") || trimmed.StartsWith("## ") || trimmed.StartsWith("# "))
                {
                    trimmed = trimmed.TrimStart('#').TrimStart();
                    prefix = "<b><size=115%>";
                    suffix = "</size></b>";
                }
                else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                {
                    trimmed = trimmed.Substring(2);
                    prefix = new string(' ', indent) + "  •  ";
                }
                else if (Regex.IsMatch(trimmed, @"^\d+\.\s"))
                {
                    var dot = trimmed.IndexOf('.');
                    prefix = new string(' ', indent) + "  " + trimmed.Substring(0, dot + 1) + " ";
                    trimmed = trimmed.Substring(dot + 1).TrimStart();
                }

                output.Append(prefix).Append(Spans(trimmed)).Append(suffix);
                if (i < lines.Length - 1) output.Append('\n');
            }
            return output.ToString();
        }

        static string Spans(string text)
        {
            // Protect inline code first so its contents are not formatted.
            var codes = new List<string>();
            text = Code.Replace(text, m =>
            {
                codes.Add(m.Groups[1].Value);
                return $"\u0001{codes.Count - 1}\u0001";
            });
            text = Escape(text);
            text = Link.Replace(text, "<u>$1</u>");
            text = Bold.Replace(text, "<b>$1</b>");
            text = Italic.Replace(text, "<i>$1</i>");
            text = Regex.Replace(text, "\u0001(\\d+)\u0001", m => $"<color=#8CC8FF>{Escape(codes[int.Parse(m.Groups[1].Value)])}</color>");
            return text;
        }

        static string Escape(string text) => text.Replace("<", "<noparse><</noparse>");
    }
}
