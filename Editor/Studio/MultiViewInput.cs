using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// Four view slots (Front, Back, Left, Right). Any one is enough; the rest can be drawn from
    /// it ("Generate missing views"), which re-renders the same object from each missing angle.
    /// </summary>
    public class MultiViewInput : VisualElement
    {
        public static readonly string[] Slots = { "front", "back", "left", "right" };
        static readonly string[] SlotLabels = { "Front", "Back", "Left", "Right" };

        /// <summary>The order views are submitted in; the server promotes front to the primary image.</summary>
        public static readonly string[] SubmitOrder = { "left", "right", "back", "front" };

        const string Preamble =
            "The attached image is a reference photograph of a single object. Re-render that SAME object from a different camera angle. " +
            "Keep its shape, proportions, colours, materials and surface details identical — this is one object photographed again, not a new design, " +
            "so do not restyle it and do not add or remove any part of it. Place the camera ";

        static readonly Dictionary<string, string> Angles = new Dictionary<string, string>
        {
            ["front"] = "directly in front of the object at its own eye level, looking straight at it head-on.",
            ["back"] = "directly behind the object at its own eye level. The face that pointed at the camera in the reference is now hidden, and what was hidden is now facing us.",
            ["left"] = "directly beside the object's left flank at its own eye level, looking horizontally at it. A flat side-on profile: the front and the back are both hidden, " +
                       "and the opposite flank is entirely behind the object. In the frame the object's FRONT points to the LEFT edge and its BACK to the RIGHT edge.",
            ["right"] = "directly beside the object's right flank at its own eye level, looking horizontally at it. A flat side-on profile: the front and the back are both hidden, " +
                        "and the opposite flank is entirely behind the object. In the frame the object's FRONT points to the RIGHT edge and its BACK to the LEFT edge — " +
                        "this is the mirror image of the left-hand view, not a repeat of it.",
        };

        public static string EditPromptFor(string slot) => Preamble + Angles[slot];

        readonly Dictionary<string, ZettaUI.ImagePicker> _pickers = new Dictionary<string, ZettaUI.ImagePicker>();
        readonly Dictionary<string, Label> _badges = new Dictionary<string, Label>();
        readonly Dictionary<string, Button> _edits = new Dictionary<string, Button>();
        readonly Button _generate;
        readonly Label _helper;
        readonly Label _error;
        bool _generating;
        bool _setting;
        int _budget = 4;
        string _budgetReason;

        public event Action Changed;
        public bool Busy => _generating;

        public MultiViewInput()
        {
            AddToClassList("z-multiview");
            var grid = new VisualElement().Classes("z-view-grid");
            for (var i = 0; i < Slots.Length; i++)
            {
                var slot = Slots[i];
                var cell = new VisualElement().Classes("z-view-cell");
                var head = new VisualElement().Classes("z-row");
                head.Add(ZettaUI.FieldLabel(SlotLabels[i]));
                var badge = ZettaUI.Chip("Reference", "signal").Visible(false);
                _badges[slot] = badge;
                head.Add(badge);
                head.Add(ZettaUI.Spacer());
                var picker = new ZettaUI.ImagePicker("Drop or click");
                picker.AddToClassList("z-view-drop");
                var label = SlotLabels[i];
                var edit = ZettaUI.Button("AI edit", () => EditView(slot, label), "z-button--small");
                edit.tooltip = "Change this view with a sentence";
                _edits[slot] = edit;
                head.Add(edit);
                head.Add(ZettaUI.Button("×", () => picker.Reset(), "z-button--ghost", "z-button--small"));
                cell.Add(head);
                cell.Add(picker);
                picker.Changed += () => OnPicked(slot, picker);
                _pickers[slot] = picker;
                grid.Add(cell);
            }
            Add(grid);

            var row = new VisualElement().Classes("z-row", "z-row--wrap");
            _generate = ZettaUI.Button("Generate missing views", () => _ = GenerateViews(), "z-button--small");
            _generate.tooltip = "Draws the empty angles from your reference view, keeping the object the same.";
            row.Add(_generate);
            Add(row);
            _helper = ZettaUI.Hint("");
            Add(_helper);
            _error = new Label().Classes("z-error").Visible(false);
            Add(_error);
            Sync();
        }

        /// <summary>AI edit on a tile: the result replaces just this view (the web's multi-view tile popup).</summary>
        void EditView(string slot, string label)
        {
            var picker = _pickers[slot];
            if (picker.Bytes == null || _generating) return;
            var name = picker.FileName;
            AiEditWindow.Open($"{label} view", picker.Bytes, picker.ContentType, (bytes, type) =>
            {
                _setting = true;
                picker.Set(bytes, type, AiEditWindow.Renamed(name ?? slot, type));
                _setting = false;
                ShowError(null);
                Sync();
                Changed?.Invoke();
            }, "Use this view");
        }

        void OnPicked(string slot, ZettaUI.ImagePicker picker)
        {
            if (!_setting && picker.Bytes != null)
            {
                var problem = StudioAssets.CheckImage(picker.Bytes, picker.ContentType);
                if (problem != null)
                {
                    ShowError(problem);
                    _setting = true;
                    picker.Reset();
                    _setting = false;
                    Sync();
                    return;
                }
            }
            ShowError(null);
            Sync();
            Changed?.Invoke();
        }

        void ShowError(string message)
        {
            _error.text = message ?? "";
            _error.Visible(!string.IsNullOrEmpty(message));
        }

        /// <summary>Applies the plan's view allowance (null = unknown = all four).</summary>
        public void SetBudget(int? maxViews)
        {
            _budget = Mathf.Clamp(maxViews ?? 4, 0, 4);
            _budgetReason = _budget < 4 ? $"Your plan uses up to {_budget} views. All 4 are available from Basic." : null;
            Sync();
        }

        public int Filled => _pickers.Values.Count(p => p.Bytes != null);

        public string ReferenceSlot => Slots.FirstOrDefault(s => _pickers[s].Bytes != null);

        public ZettaUI.ImagePicker Picker(string slot) => _pickers[slot];

        /// <summary>(viewType, bytes, contentType) in submission order.</summary>
        public List<(string slot, byte[] bytes, string contentType)> Views() =>
            SubmitOrder.Where(s => _pickers[s].Bytes != null).Select(s => (s, _pickers[s].Bytes, _pickers[s].ContentType)).ToList();

        /// <summary>The file name of the reference view (for the title suggestion).</summary>
        public string ReferenceName => ReferenceSlot == null ? null : _pickers[ReferenceSlot].FileName;

        public void ClearViews()
        {
            _setting = true;
            foreach (var picker in _pickers.Values) picker.Reset();
            _setting = false;
            ShowError(null);
            Sync();
            Changed?.Invoke();
        }

        void Sync()
        {
            var filled = Filled;
            var reference = ReferenceSlot;
            foreach (var slot in Slots)
            {
                _badges[slot].Visible(slot == reference);
                var picker = _pickers[slot];
                _edits[slot].Visible(picker.Bytes != null);
                _edits[slot].SetEnabled(!_generating);
                var locked = picker.Bytes == null && filled >= _budget;
                picker.SetEnabled(!locked && !_generating);
                picker.tooltip = locked ? _budgetReason ?? "" : "";
            }

            var empty = Slots.Length - filled;
            _generate.SetEnabled(reference != null && empty > 0 && !_generating && filled < _budget);
            _generate.text = _generating ? "Generating views…" : "Generate missing views";
            if (reference == null) _helper.text = "Upload one view — the rest can be generated from it.";
            else if (filled >= _budget && _budgetReason != null) _helper.text = $"{filled} views ready. {_budgetReason}";
            else if (empty == 0) _helper.text = $"{filled} views ready.";
            else
            {
                var more = Math.Min(empty, _budget - filled);
                var label = SlotLabels[Array.IndexOf(Slots, reference)];
                _helper.text = $"{filled} views ready. {more} more can be generated from your {label} view.";
            }
        }

        async Task GenerateViews()
        {
            var reference = ReferenceSlot;
            if (reference == null || _generating) return;
            var targets = Slots.Where(s => _pickers[s].Bytes == null).Take(Math.Max(0, _budget - Filled)).ToList();
            if (targets.Count == 0) return;

            _generating = true;
            ShowError(null);
            Sync();
            var produced = 0;
            string lastError = null;
            try
            {
                var source = _pickers[reference];
                var (bytes, contentType) = Downscale(source.Bytes, source.ContentType);
                var image = InlineImage.From(bytes, contentType);
                foreach (var slot in targets)
                {
                    try
                    {
                        var result = await ZettaApi.EditImageAsync(EditPromptFor(slot), image, for3D: true, pipelineStep: true);
                        var picture = result?.Images?.FirstOrDefault();
                        if (picture == null || picture.Data.Length == 0)
                        {
                            lastError = string.IsNullOrWhiteSpace(result?.Text) ? "The image service returned no picture for this view." : result.Text;
                            continue;
                        }
                        var type = picture.ContentType ?? "image/png";
                        _setting = true;
                        _pickers[slot].Set(picture.Data, type, slot + StudioAssets.ExtensionFor(type));
                        _setting = false;
                        produced++;
                        Sync();
                        Changed?.Invoke();
                    }
                    catch (ZettaApiException e)
                    {
                        lastError = ZettaCatalog.Explain(e);
                        // A limit or plan problem will not change for the next angle.
                        if (e.Status is 402 or 403 or 429) break;
                    }
                }
                if (produced == 0) ShowError(lastError ?? "The views could not be generated. Try again.");
                else if (produced < targets.Count) ShowError($"{targets.Count - produced} view(s) could not be generated. {lastError}");
            }
            catch (Exception e)
            {
                ShowError(e.Message);
            }
            finally
            {
                _setting = false;
                _generating = false;
                Sync();
                Changed?.Invoke();
            }
        }

        /// <summary>A reference larger than 1536 px on its long side goes up as a JPEG of that size, if that is smaller.</summary>
        public static (byte[] bytes, string contentType) Downscale(byte[] bytes, string contentType)
        {
            var size = StudioAssets.ImageSize(bytes);
            if (size == null || Math.Max(size.Value.w, size.Value.h) <= 1536 || contentType == "image/webp") return (bytes, contentType);
            var texture = ZettaUI.TextureFrom(bytes);
            try
            {
                var scale = 1536f / Math.Max(texture.width, texture.height);
                var w = Mathf.Max(1, Mathf.RoundToInt(texture.width * scale));
                var h = Mathf.Max(1, Mathf.RoundToInt(texture.height * scale));
                var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(texture, rt);
                var previous = RenderTexture.active;
                RenderTexture.active = rt;
                var small = new Texture2D(w, h, TextureFormat.RGB24, false);
                small.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                small.Apply();
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                var jpeg = small.EncodeToJPG(90);
                UnityEngine.Object.DestroyImmediate(small);
                return jpeg.Length < bytes.Length ? (jpeg, "image/jpeg") : (bytes, contentType);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }
    }
}
