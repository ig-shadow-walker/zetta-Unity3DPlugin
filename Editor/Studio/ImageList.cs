using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// A small ordered set of pictures (style references, the image to edit, images to compose):
    /// thumbnails with remove buttons and an Add tile that takes files, project textures or drops.
    /// Items can also be library rows (a post id) so they are not uploaded again.
    /// </summary>
    public class ImageList : VisualElement
    {
        public class Item
        {
            public byte[] Bytes;
            public string ContentType;
            public string Name;
            public long? PostId;
            Texture2D _thumb;
            public Texture2D Thumb => _thumb != null ? _thumb : Bytes == null ? null : _thumb = ZettaUI.TextureFrom(Bytes);
        }

        readonly int _max;
        readonly bool _numbered;
        readonly VisualElement _row;
        readonly Label _counter;
        readonly Label _error;
        readonly List<Item> _items = new List<Item>();

        public IReadOnlyList<Item> Items => _items;
        public event Action Changed;

        public ImageList(string label, int max, string hint, bool numbered = false)
        {
            _max = max;
            _numbered = numbered;
            AddToClassList("z-image-list");
            var head = new VisualElement().Classes("z-row");
            head.Add(ZettaUI.FieldLabel(label));
            head.Add(ZettaUI.Spacer());
            _counter = ZettaUI.Hint("");
            head.Add(_counter);
            Add(head);
            _row = new VisualElement().Classes("z-row", "z-row--wrap");
            Add(_row);
            if (!string.IsNullOrEmpty(hint)) Add(ZettaUI.Hint(hint));
            _error = new Label().Classes("z-error").Visible(false);
            Add(_error);
            Render();
        }

        public void ClearImages()
        {
            _items.Clear();
            Render();
            Changed?.Invoke();
        }

        /// <summary>Adds (or, for a single-image list, replaces with) a picture. Returns false with a shown reason when refused.</summary>
        public bool Add(Item item)
        {
            if (item.PostId != null && _items.Any(i => i.PostId == item.PostId)) return true;
            if (item.Bytes != null)
            {
                var problem = StudioAssets.CheckImage(item.Bytes, item.ContentType);
                if (problem != null)
                {
                    ShowError(problem);
                    return false;
                }
            }
            if (_max == 1) _items.Clear();
            if (_items.Count >= _max)
            {
                ShowError($"Only {_max} images are allowed here, so one was left out.");
                return false;
            }
            ShowError(null);
            _items.Add(item);
            Render();
            Changed?.Invoke();
            return true;
        }

        void ShowError(string message)
        {
            _error.text = message ?? "";
            _error.Visible(!string.IsNullOrEmpty(message));
        }

        void Render()
        {
            _row.Clear();
            _counter.text = _max > 1 ? $"{_items.Count}/{_max}" : "";
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                var thumb = new VisualElement().Classes("z-mini-thumb");
                if (item.Thumb != null) thumb.style.backgroundImage = new StyleBackground(item.Thumb);
                thumb.tooltip = item.Name ?? "";
                if (_numbered && i > 0) thumb.Add(new Label((i + 1).ToString()).Classes("z-mini-badge"));
                var remove = ZettaUI.Button("×", () =>
                {
                    _items.Remove(item);
                    ShowError(null);
                    Render();
                    Changed?.Invoke();
                }, "z-button--small", "z-mini-remove");
                thumb.Add(remove);
                _row.Add(thumb);
            }
            if (_items.Count < _max || _max == 1)
            {
                var add = new VisualElement().Classes("z-mini-thumb", "z-mini-add");
                add.Add(new Label(_items.Count > 0 && _max == 1 ? "Replace" : "+ Add").Classes("z-muted"));
                add.tooltip = "Click to choose, or drop images (or textures from the Project window) here.";
                add.RegisterCallback<ClickEvent>(_ => Browse());
                _row.Add(add);
            }
        }

        void Browse()
        {
            var path = EditorUtility.OpenFilePanelWithFilters("Choose an image", "", new[] { "Images", "png,jpg,jpeg,webp" });
            if (!string.IsNullOrEmpty(path)) AddFile(path);
        }

        void AddFile(string path)
        {
            var type = StudioAssets.ContentTypeOf(path);
            if (type == null)
            {
                ShowError("That file is not a PNG, JPEG or WebP.");
                return;
            }
            Add(new Item { Bytes = File.ReadAllBytes(path), ContentType = type, Name = Path.GetFileName(path) });
        }

        /// <summary>Lets the whole list take drops of files or project textures.</summary>
        public void AcceptDrops()
        {
            RegisterCallback<DragUpdatedEvent>(_ => DragAndDrop.visualMode = DragAndDropVisualMode.Copy);
            RegisterCallback<DragPerformEvent>(_ =>
            {
                DragAndDrop.AcceptDrag();
                var textures = DragAndDrop.objectReferences.OfType<Texture2D>().ToList();
                if (textures.Count > 0)
                {
                    foreach (var texture in textures)
                    {
                        var path = AssetDatabase.GetAssetPath(texture);
                        if (StudioAssets.ContentTypeOf(path) is "image/png" or "image/jpeg") AddFile(path);
                        else Add(new Item { Bytes = ZettaCapture.EncodeReadable(texture), ContentType = "image/png", Name = texture.name + ".png" });
                    }
                    return;
                }
                foreach (var path in DragAndDrop.paths) AddFile(path);
            });
        }
    }
}
