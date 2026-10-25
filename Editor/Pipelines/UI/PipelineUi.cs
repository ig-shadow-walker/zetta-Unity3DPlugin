using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Pipelines.UI
{
    /// <summary>
    /// The small pieces every 3D Pipelines view shares (the web's ui.tsx, run-status.tsx, node-visuals.ts): each step's
    /// hue and mark, run state pills and dots, notes, relative times, and a debouncer that survives a docked window.
    /// </summary>
    public static class PipelineUi
    {
        // ─── Hues (web lib/hues.ts, node specs' hue) ─────────────────────────────────

        public sealed class Hue
        {
            public Color Plate;
            public Color Edge;
            public Color Hi;

            public Hue(string plate, string edge, string hi)
            {
                Plate = Parse(plate);
                Edge = Parse(edge);
                Hi = Parse(hi);
            }
        }

        public static readonly Hue Signal = new Hue("#0d1d42", "#5ea4ff", "#a9d0ff");
        public static readonly Hue Teal = new Hue("#0a2827", "#42d3ba", "#a8f3e5");
        public static readonly Hue Amber = new Hue("#2e1f0a", "#f5b347", "#ffdca0");
        public static readonly Hue Violet = new Hue("#1f1842", "#a58bff", "#dccfff");
        public static readonly Hue Rose = new Hue("#36131e", "#ff7690", "#ffc6cf");
        public static readonly Hue Lime = new Hue("#1a2a0c", "#a8de52", "#dff7ad");
        public static readonly Hue Magenta = new Hue("#300f33", "#e57cf5", "#f8c6ff");
        public static readonly Hue Grey = new Hue("#2A2620", "#A8A196", "#FAF8F4");

        public static readonly Color ErrorColour = Parse("#E4756B");
        public static readonly Color SuccessColour = Parse("#1f8f80");

        /// <summary>The hue each kind wears (NODE_SPECS hue: image, library, generate, smart, splat, geometry, texture, rig).</summary>
        public static Hue HueOf(string kind) => kind switch
        {
            NodeKinds.SeedModel => Signal,
            NodeKinds.SeedPicture or NodeKinds.RemoveBackground or NodeKinds.AiEdit or NodeKinds.DrawViews or NodeKinds.IsolateParts => Magenta,
            NodeKinds.Generate3D => Signal,
            NodeKinds.GenerateSmart3D => Violet,
            NodeKinds.GenerateSplat => Teal,
            NodeKinds.Retopology or NodeKinds.Segmentation or NodeKinds.SmartSegmentation or NodeKinds.SmartUv or NodeKinds.Export => Lime,
            NodeKinds.Texture => Amber,
            NodeKinds.Rigging => Rose,
            _ => Grey,
        };

        /// <summary>A short mark for the kind's icon plate (Unity has no Lucide; these read at 11px in every editor font).</summary>
        public static string GlyphOf(string kind) => kind switch
        {
            NodeKinds.SeedPicture => "Pic",
            NodeKinds.SeedModel => "Mdl",
            NodeKinds.RemoveBackground => "Cut",
            NodeKinds.AiEdit => "Edit",
            NodeKinds.DrawViews => "Vw",
            NodeKinds.IsolateParts => "Iso",
            NodeKinds.Generate3D => "3D",
            NodeKinds.GenerateSmart3D => "S3D",
            NodeKinds.GenerateSplat => "Spl",
            NodeKinds.Retopology => "Top",
            NodeKinds.Segmentation or NodeKinds.SmartSegmentation => "Seg",
            NodeKinds.Texture => "Tex",
            NodeKinds.SmartUv => "UV",
            NodeKinds.Rigging => "Rig",
            NodeKinds.Export => "Exp",
            _ => "?",
        };

        /// <summary>The kind's icon: its mark on a plate in its area's hue (HueIcon).</summary>
        public static VisualElement HueIcon(string kind, string size = "sm")
        {
            var hue = HueOf(kind);
            var icon = new Label(GlyphOf(kind)).Classes("z-pl-icon", "z-pl-icon--" + size);
            icon.style.backgroundColor = hue.Plate;
            icon.style.color = hue.Hi;
            icon.style.borderTopColor = icon.style.borderBottomColor = icon.style.borderLeftColor = icon.style.borderRightColor = hue.Edge;
            icon.pickingMode = PickingMode.Ignore;
            return icon;
        }

        public static Color Parse(string hex) => ColorUtility.TryParseHtmlString(hex, out var colour) ? colour : Color.gray;

        // ─── Run state (run-status.tsx) ─────────────────────────────────────────────

        public static string ToneClass(RunTone tone) => tone switch
        {
            RunTone.Running => "running",
            RunTone.Attention => "attention",
            RunTone.Done => "done",
            RunTone.Failed => "failed",
            _ => "idle",
        };

        /// <summary>Just the dot — for counts and tight rows.</summary>
        public static VisualElement RunDot(RunTone tone, string tooltip = null)
        {
            var dot = new VisualElement().Classes("z-pl-dot", "z-pl-dot--" + ToneClass(tone));
            if (tooltip != null) dot.tooltip = tooltip;
            return dot;
        }

        /// <summary>A run's state as a small pill: its dot and its word.</summary>
        public static VisualElement RunPill(string status)
        {
            var tone = RunStatuses.Tone(status);
            var pill = new VisualElement().Classes("z-pl-pill", "z-pl-pill--" + ToneClass(tone));
            pill.Add(RunDot(tone));
            pill.Add(new Label(RunStatuses.Label(status)).Classes("z-pl-pill-text"));
            return pill;
        }

        // ─── Notes ────────────────────────────────────────────────────────────────

        /// <summary>A note in the panel's words; tone "error" for what stops something, "amber" for a needs-input note.</summary>
        public static Label Note(string text, string tone = null)
        {
            var note = new Label(text).Classes("z-pl-note");
            if (tone != null) note.AddToClassList("z-pl-note--" + tone);
            return note;
        }

        public static Label SectionHeading(string text) => new Label(text.ToUpperInvariant()).Classes("z-pl-heading");

        // ─── Time ─────────────────────────────────────────────────────────────────

        /// <summary>"just now", "5 min ago", "3 h ago", "2 days ago", then the date (run-time.ts whenRun).</summary>
        public static string WhenRun(DateTime at, DateTime? now = null)
        {
            if (at == default) return "";
            var utc = at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : at;
            var minutes = (int)Math.Round(((now ?? DateTime.UtcNow) - utc).TotalMinutes);
            if (minutes < 1) return "just now";
            if (minutes < 60) return $"{minutes} min ago";
            var hours = (int)Math.Round(minutes / 60.0);
            if (hours < 24) return $"{hours} h ago";
            var days = (int)Math.Round(hours / 24.0);
            if (days < 7) return $"{days} day{(days == 1 ? "" : "s")} ago";
            return utc.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture);
        }

        public static string Plural(int n, string one, string many = null) => n == 1 ? one : many ?? one + "s";

        // ─── Pictures ─────────────────────────────────────────────────────────────

        /// <summary>A picture tile loading a (signed) URL, cached by its address without the query.</summary>
        public static VisualElement Thumb(string url, string extraClass = null)
        {
            var thumb = new VisualElement().Classes("z-pl-thumb", extraClass);
            ShowImage(thumb, url);
            return thumb;
        }

        /// <summary>Shows a picture address (signed URL or data: URL) as an element's background; null clears it.</summary>
        public static void ShowImage(VisualElement element, string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                element.style.backgroundImage = StyleKeyword.Null;
                return;
            }
            if (url.StartsWith("data:", StringComparison.Ordinal)) SetDataUrl(element, url);
            else ZettaUI.LoadInto(element, url);
        }

        /// <summary>A tile showing bytes held in memory (an upload's preview).</summary>
        public static VisualElement Thumb(byte[] bytes, string extraClass = null)
        {
            var thumb = new VisualElement().Classes("z-pl-thumb", extraClass);
            if (bytes != null && bytes.Length > 0) thumb.style.backgroundImage = new StyleBackground(ZettaUI.TextureFrom(bytes));
            return thumb;
        }

        static readonly Dictionary<string, Texture2D> DataUrls = new Dictionary<string, Texture2D>();

        static void SetDataUrl(VisualElement element, string url)
        {
            if (!DataUrls.TryGetValue(url, out var texture) || texture == null)
            {
                var comma = url.IndexOf(',');
                if (comma < 0) return;
                try
                {
                    texture = ZettaUI.TextureFrom(Convert.FromBase64String(url.Substring(comma + 1)));
                    DataUrls[url] = texture;
                }
                catch (FormatException)
                {
                    return;
                }
            }
            element.style.backgroundImage = new StyleBackground(texture);
        }

        // ─── Typing ───────────────────────────────────────────────────────────────

        /// <summary>Whether keyboard focus is in a text field — where Cmd+Z belongs to the words, not the pipeline.</summary>
        public static bool IsTyping(VisualElement anyInPanel)
        {
            var focused = anyInPanel?.panel?.focusController?.focusedElement as VisualElement;
            for (var e = focused; e != null; e = e.parent)
                if (e is TextField || e is BaseField<int> || e is BaseField<float> || e is BaseField<string>)
                    return true;
            return false;
        }
    }

    /// <summary>
    /// Calls an action once, <c>delayMs</c> after the last <see cref="Poke"/>. Driven by EditorApplication.update so
    /// it fires whether or not the window is visible; <see cref="Flush"/> runs it now.
    /// </summary>
    public sealed class Debouncer : IDisposable
    {
        readonly Action _action;
        readonly int _delayMs;
        double _due = -1;
        bool _hooked;

        public Debouncer(int delayMs, Action action)
        {
            _delayMs = delayMs;
            _action = action;
        }

        public bool Pending => _due >= 0;

        public void Poke()
        {
            _due = EditorApplication.timeSinceStartup + _delayMs / 1000.0;
            if (_hooked) return;
            EditorApplication.update += Tick;
            _hooked = true;
        }

        public void Cancel()
        {
            _due = -1;
            Unhook();
        }

        public void Flush()
        {
            if (_due < 0) return;
            Cancel();
            _action();
        }

        void Tick()
        {
            if (_due < 0 || EditorApplication.timeSinceStartup < _due) return;
            Cancel();
            _action();
        }

        void Unhook()
        {
            if (!_hooked) return;
            EditorApplication.update -= Tick;
            _hooked = false;
        }

        public void Dispose() => Cancel();
    }
}
