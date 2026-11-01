using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Pipelines.UI
{
    /// <summary>
    /// DESCRIBE IT, GET THE RECIPE (describe-recipe.tsx, 2026-10-30) — a box over the canvas: write what the pipeline
    /// should do and the recipe is built on the canvas. One shot, not a conversation: the canvas is the answer, with one
    /// line saying what changed.
    ///
    /// WITH A RECIPE ON THE CANVAS, IT EDITS IT: "add AI texturing at the end" adds one step; what is kept keeps its
    /// place and settings. Starting over is for when it is asked for. Either way it is one change, so Undo brings back
    /// what was there, and it saves like any edit.
    /// </summary>
    public sealed class DescribeRecipe : VisualElement
    {
        readonly Func<PipelineGraph> _current;
        readonly Func<ThreeDEntitlements> _gates;
        readonly VisualElement _box;
        readonly TextField _input;
        readonly Button _send;
        readonly Label _line;
        bool _busy;

        /// <summary>A recipe came back — already normalised (every step's settings over the Studio's defaults).</summary>
        public event Action<PipelineDraft> Drafted;

        public bool Busy => _busy;

        public DescribeRecipe(Func<PipelineGraph> current, Func<ThreeDEntitlements> gates)
        {
            _current = current;
            _gates = gates;
            AddToClassList("z-pl-describe");
            // The strip across the canvas lets clicks through; only the box and its line take them.
            pickingMode = PickingMode.Ignore;

            _box = new VisualElement().Classes("z-row", "z-pl-describe-box");
            _box.Add(new Label("✦").Classes("z-pl-describe-mark"));
            _input = new TextField { multiline = true, maxLength = 2000 };
            _input.AddToClassList("z-pl-describe-input");
            _input.AddToClassList("z-grow");
            // Enter builds; Shift+Enter is a new line. Both halves of the key press are taken, so no stray line break.
            _input.RegisterCallback<KeyDownEvent>(e =>
            {
                var enter = e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter || e.character == '\n' || e.character == '\r';
                if (!enter || e.shiftKey) return;
                e.StopImmediatePropagation();
#if UNITY_2023_2_OR_NEWER
                _input.focusController?.IgnoreEvent(e);
#else
                e.PreventDefault();
#endif
                if (e.keyCode != KeyCode.None) _ = SubmitAsync();
            }, TrickleDown.TrickleDown);
            _input.RegisterValueChangedCallback(_ => SyncSend());
            _box.Add(_input);
            _send = ZettaUI.Button("↑", () => _ = SubmitAsync(), "z-pl-describe-send");
            _send.tooltip = "Build the recipe (Enter)";
            _box.Add(_send);
            Add(_box);

            _line = new Label().Classes("z-pl-describe-line");
            _line.Visible(false);
            Add(_line);
            Refresh();
        }

        /// <summary>Steps beyond a seed: something to edit rather than a blank canvas.</summary>
        bool Editing => _current()?.Nodes.Any(n => !NodeCatalog.IsSeed(n.Kind)) == true;

        /// <summary>The placeholder follows the canvas: building a recipe, or changing the one there.</summary>
        public void Refresh()
        {
            ZettaUI.SetPlaceholder(_input, Editing
                ? "Change the recipe — e.g. add AI texturing at the end, or use quads"
                : "Describe the recipe — e.g. a game-ready character from a photo, rigged for Unity");
            SyncSend();
        }

        void SyncSend() => _send.SetEnabled(!_busy && !string.IsNullOrWhiteSpace(_input.value));

        void Say(string text, bool error = false)
        {
            _line.text = text ?? "";
            _line.EnableInClassList("z-error", error);
            _line.Visible(!string.IsNullOrEmpty(text));
        }

        /// <summary>Sends what is written (with the recipe on the canvas, when there is one) and hands back the recipe.</summary>
        public async Task SubmitAsync()
        {
            var description = (_input.value ?? "").Trim();
            if (_busy || description.Length == 0) return;
            if (description.Length < 3)
            {
                Say("Say a little more about what the recipe should do.", true);
                return;
            }
            var editing = Editing;
            SetBusy(true);
            Say(editing ? "Changing your recipe…" : "Designing your recipe…");
            try
            {
                var draft = await PipelinesApi.DraftAsync(description, editing ? _current() : null, _gates());
                // The server names only what each step changes; the Studio's defaults fill the rest.
                draft.Graph = RequestBuilder.Normalize(draft.Graph ?? new PipelineGraph()).Graph;
                if (panel == null) return;
                _input.SetValueWithoutNotify("");
                Say(string.IsNullOrWhiteSpace(draft.Summary) ? "Done. Undo brings back what was there." : $"{draft.Summary.Trim()} Undo brings back what was there.");
                Drafted?.Invoke(draft);
            }
            catch (ZettaApiException e)
            {
                Say(e.Status == 429 ? "That's a lot of recipes in a minute — wait a moment and try again." : ZettaCatalog.Explain(e), true);
            }
            catch (Exception e)
            {
                Say(e.Message ?? "That recipe could not be built.", true);
            }
            finally
            {
                SetBusy(false);
            }
        }

        void SetBusy(bool busy)
        {
            _busy = busy;
            _input.SetEnabled(!busy);
            _send.text = busy ? "…" : "↑";
            _box.EnableInClassList("z-pl-describe-box--busy", busy);
            Refresh();
        }
    }
}
