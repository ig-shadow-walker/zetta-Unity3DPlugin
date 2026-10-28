using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Zetta3D.Editor.Studio;

namespace Zetta3D.Editor.Pipelines.UI
{
    /// <summary>
    /// PICTURES OF EACH SIDE (settings/view-slots.tsx) — the Studio's four multi-view slots, front, back, left and right,
    /// each uploaded, picked from your images, or dragged in from the Project window. Front is required: the engine
    /// treats it as the main picture. Used by a Multi-view seed in Run on… and by multi-view AI texturing.
    ///
    /// The set is keyed by side, as the web keeps it: <c>{ front: { uploadKey | gen2dPostId, name, thumbnail } }</c>.
    /// Changes go through an updater, so two uploads finishing together never overwrite each other.
    /// </summary>
    public sealed class ViewSlots : VisualElement
    {
        static readonly (string key, string label)[] Slots = { ("front", "Front"), ("back", "Back"), ("left", "Left"), ("right", "Right") };

        readonly VisualElement _overlayHost;
        readonly Func<JObject> _value;
        readonly Action<Func<JObject, JObject>> _update;
        readonly HashSet<string> _uploading = new HashSet<string>();
        readonly Dictionary<string, (VisualElement thumb, Label empty, Button remove, Button upload, Button pick, VisualElement slot)> _views =
            new Dictionary<string, (VisualElement, Label, Button, Button, Button, VisualElement)>();
        readonly Dictionary<string, string> _shown = new Dictionary<string, string>();
        readonly Label _problem;
        bool _disabled;

        public ViewSlots(VisualElement overlayHost, Func<JObject> value, Action<Func<JObject, JObject>> update, string label = "Pictures of each side")
        {
            _overlayHost = overlayHost;
            _value = value;
            _update = update;
            AddToClassList("z-pl-views");

            var head = new VisualElement().Classes("z-row");
            head.Add(ZettaUI.FieldLabel(label));
            head.Add(ZettaUI.Hint(" — the front at least"));
            Add(head);

            var grid = new VisualElement().Classes("z-row", "z-pl-view-grid");
            foreach (var (key, name) in Slots)
            {
                var slot = new VisualElement().Classes("z-pl-view-slot");
                var box = new VisualElement().Classes("z-pl-view-box");
                var thumb = new VisualElement().Classes("z-pl-view-thumb");
                box.Add(thumb);
                var empty = new Label("Empty").Classes("z-pl-view-empty");
                box.Add(empty);
                var remove = ZettaUI.Button("✕", () => Put(key, null), "z-mini-remove");
                remove.tooltip = $"Remove the {name.ToLowerInvariant()} picture";
                box.Add(remove);
                slot.Add(box);
                var caption = new VisualElement().Classes("z-row");
                caption.Add(new Label(name).Classes("z-pl-view-name"));
                if (key == "front") caption.Add(new Label(" · required").Classes("z-hint"));
                slot.Add(caption);
                var buttons = new VisualElement().Classes("z-row");
                var upload = ZettaUI.Button("Upload", () => _ = UploadFile(key), "z-button--small", "z-pl-view-button");
                upload.tooltip = $"Upload the {name.ToLowerInvariant()} picture — or drag a picture onto the slot";
                var pick = ZettaUI.Button("Images", () => Pick(key), "z-button--small", "z-pl-view-button");
                pick.tooltip = $"Pick the {name.ToLowerInvariant()} picture from your images";
                buttons.Add(upload);
                buttons.Add(pick);
                slot.Add(buttons);
                AcceptDrops(slot, key);
                grid.Add(slot);
                _views[key] = (thumb, empty, remove, upload, pick, slot);
            }
            Add(grid);
            _problem = new Label().Classes("z-error").Visible(false);
            Add(_problem);
            Refresh();
        }

        /// <summary>Locks every slot (a run that has started keeps the pictures it ran with).</summary>
        public void SetDisabled(bool disabled)
        {
            _disabled = disabled;
            Refresh();
        }

        /// <summary>Shows the set as it is now.</summary>
        public void Refresh()
        {
            var set = _value() ?? new JObject();
            foreach (var (key, _) in Slots)
            {
                var view = _views[key];
                var picture = set[key] as JObject;
                var busy = _uploading.Contains(key);
                var url = picture?["thumbnail"]?.Type == JTokenType.String ? picture["thumbnail"].ToString() : null;
                view.empty.text = busy ? "Uploading…" : picture == null ? "Empty" : url == null ? picture["name"]?.ToString() ?? "Chosen" : "";
                view.empty.Visible(busy || url == null);
                view.remove.Visible(picture != null && !_disabled && !busy);
                view.upload.SetEnabled(!_disabled && !busy);
                view.pick.SetEnabled(!_disabled && !busy && _overlayHost != null);
                view.slot.EnableInClassList("z-pl-view-slot--missing", key == "front" && picture == null);
                _shown.TryGetValue(key, out var shown);
                if (url == shown) continue;
                _shown[key] = url;
                PipelineUi.ShowImage(view.thumb, url);
            }
        }

        void Put(string key, JObject picture)
        {
            _update(current =>
            {
                var next = current != null ? (JObject)current.DeepClone() : new JObject();
                if (picture != null) next[key] = picture;
                else next.Remove(key);
                return next;
            });
            Refresh();
        }

        void Fail(string message)
        {
            _problem.text = message ?? "";
            _problem.Visible(!string.IsNullOrEmpty(message));
        }

        async Task UploadFile(string key)
        {
            var path = EditorUtility.OpenFilePanelWithFilters($"Choose the {key} picture", "", new[] { "Images", "png,jpg,jpeg,webp" });
            if (string.IsNullOrEmpty(path)) return;
            var type = StudioAssets.ContentTypeOf(path);
            if (type == null || !type.StartsWith("image/", StringComparison.Ordinal))
            {
                Fail("Choose a PNG, JPEG or WebP picture.");
                return;
            }
            await Upload(key, File.ReadAllBytes(path), type, Path.GetFileName(path));
        }

        async Task Upload(string key, byte[] bytes, string contentType, string name)
        {
            Fail(null);
            if (StudioAssets.CheckImage(bytes, contentType) is { } bad)
            {
                Fail(bad);
                return;
            }
            _uploading.Add(key);
            Refresh();
            try
            {
                var seed = await PipelineSeed.FromUploadAsync(bytes, contentType, name);
                Put(key, new JObject { ["uploadKey"] = seed.Request["uploadKey"], ["name"] = seed.Name, ["thumbnail"] = seed.ThumbnailDataUrl });
            }
            catch (ZettaApiException e)
            {
                Fail(ZettaCatalog.Explain(e));
            }
            catch (Exception e)
            {
                Fail(e.Message ?? "The picture could not be uploaded.");
            }
            finally
            {
                _uploading.Remove(key);
                if (panel != null) Refresh();
            }
        }

        void Pick(string key)
        {
            if (_overlayHost == null) return;
            Fail(null);
            LibraryPickers.PickImages(_overlayHost, false, new List<long>(), async picked =>
            {
                var preview = await PipelineSeed.ThumbnailFromUrlAsync(picked.Url);
                Put(key, new JObject
                {
                    ["gen2dPostId"] = picked.Id,
                    ["name"] = picked.Name,
                    ["thumbnail"] = preview == null ? null : "data:image/jpeg;base64," + Convert.ToBase64String(preview),
                });
                return true;
            });
        }

        /// <summary>A picture dragged onto a slot — a texture from the Project window, or a file from Finder.</summary>
        void AcceptDrops(VisualElement slot, string key)
        {
            slot.RegisterCallback<DragUpdatedEvent>(_ =>
            {
                if (_disabled) return;
                if (DragAndDrop.objectReferences.OfType<Texture2D>().Any() || DragAndDrop.paths.Length > 0) DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            });
            slot.RegisterCallback<DragPerformEvent>(async e =>
            {
                if (_disabled) return;
                DragAndDrop.AcceptDrag();
                e.StopPropagation();
                var texture = DragAndDrop.objectReferences.OfType<Texture2D>().FirstOrDefault();
                if (texture != null)
                {
                    var path = AssetDatabase.GetAssetPath(texture);
                    var type = string.IsNullOrEmpty(path) ? null : StudioAssets.ContentTypeOf(path);
                    if (type is "image/png" or "image/jpeg") await Upload(key, File.ReadAllBytes(path), type, Path.GetFileName(path));
                    else await Upload(key, ZettaCapture.EncodeReadable(texture), "image/png", texture.name + ".png");
                    return;
                }
                var file = DragAndDrop.paths.FirstOrDefault();
                var fileType = file == null ? null : StudioAssets.ContentTypeOf(file);
                if (fileType == null || !fileType.StartsWith("image/", StringComparison.Ordinal))
                {
                    Fail("Drop a PNG, JPEG or WebP picture.");
                    return;
                }
                await Upload(key, File.ReadAllBytes(file), fileType, Path.GetFileName(file));
            });
        }

        /// <summary>Whether a set has its front — what makes it a run (or a texturing reference).</summary>
        public static bool HasFront(JObject set) => set?["front"] is JObject front && (front["uploadKey"]?.Type == JTokenType.String || ToolSettings.IsNumber(front["gen2dPostId"]));
    }
}
