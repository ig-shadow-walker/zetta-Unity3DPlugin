using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// Image Studio: generate pictures from a prompt, edit one, or compose several into one — then
    /// save them into the project as textures or sprites, or send one on to Generate 3D.
    /// </summary>
    public class ImageStudioPanel : StudioPanel
    {
        const int MaxPrompt = 5000;
        const string EditLock = "Editing and composing are available on a paid plan. Generating stays free.";
        const string FourKLock = "4K is available on a paid plan. 1K and 2K stay free.";

        static readonly string[] Ratios = { "9:16", "2:3", "3:4", "4:5", "1:1", "5:4", "4:3", "3:2", "16:9", "21:9" };
        static readonly string[] RatioLabels = { "9:16 · Story", "2:3", "3:4", "4:5 · Post", "1:1 · Square", "5:4", "4:3", "3:2", "16:9 · Wide", "21:9 · Cinema" };

        public override string Id => ZettaStudioWindow.ImagesTab;
        public override string Title => "Image Studio";

        class Result
        {
            public byte[] Bytes;
            public string ContentType;
            public string Prompt;
            public string AspectRatio;
            public string Resolution;
            public long? PostId;
        }

        string _mode = "generate";
        string _ratio = "1:1";
        string _resolution = "1K";
        string _model;
        string _textureType = "default";
        bool _busy;

        ZettaUI.Segmented _modeSwitch;
        Label _modeHint;
        Label _lock;
        TextField _prompt;
        Label _promptCount;
        ImageList _styles;
        ImageList _editSource;
        ImageList _composeSources;
        ZettaUI.Segmented _resolutionSwitch;
        Label _resolutionHint;
        VisualElement _modelBox;
        PopupField<string> _modelField;
        Button _submit;
        Label _error;
        VisualElement _results;
        VisualElement _history;
        Label _historyNote;
        readonly List<Result> _shown = new List<Result>();
        bool _historyLoaded;

        protected override void Build(VisualElement root)
        {
            var scroll = Scroll();
            scroll.Add(ZettaUI.Hint("Make pictures for your game — textures, sprites, concept art — or a reference to build a 3D model from.").Classes("z-intro"));

            var form = ZettaUI.Card();
            _modeSwitch = new ZettaUI.Segmented(new[] { "generate", "edit", "compose" }, new[] { "Generate", "Edit", "Compose" }, _mode);
            _modeSwitch.Changed += v =>
            {
                _mode = v;
                Sync();
            };
            form.Add(_modeSwitch);
            _lock = new Label(EditLock).Classes("z-lock").Visible(false);
            form.Add(_lock);
            _modeHint = ZettaUI.Hint("");
            form.Add(_modeHint);

            _editSource = new ImageList("Image to edit", 1, null);
            _editSource.AcceptDrops();
            _editSource.Changed += Sync;
            form.Add(_editSource);
            _composeSources = new ImageList("Images to combine", 14, "At least two. They are built into one new picture, in this order.", numbered: true);
            _composeSources.AcceptDrops();
            _composeSources.Changed += Sync;
            form.Add(_composeSources);

            _prompt = ZettaUI.TextArea("", "", MaxPrompt);
            _prompt.RegisterValueChangedCallback(_ => Sync());
            _prompt.RegisterCallback<KeyDownEvent>(e =>
            {
                if ((e.keyCode is KeyCode.Return or KeyCode.KeypadEnter) && (e.actionKey || e.ctrlKey)) _ = Submit();
            });
            form.Add(ZettaUI.FieldLabel("Prompt"));
            form.Add(_prompt);
            _promptCount = ZettaUI.Hint("");
            form.Add(_promptCount);

            _styles = new ImageList("Style references", 3, "Used for look and feel only. The picture is still made from your prompt.");
            _styles.AcceptDrops();
            form.Add(_styles);

            form.Add(ZettaUI.FieldLabel("Aspect ratio"));
            form.Add(ZettaUI.Dropdown(null, Ratios.ToList(), RatioLabels.ToList(), _ratio, v => _ratio = v));
            form.Add(ZettaUI.FieldLabel("Resolution"));
            _resolutionSwitch = new ZettaUI.Segmented(ZettaCatalog.Resolutions, ZettaCatalog.Resolutions, _resolution);
            _resolutionSwitch.Changed += v =>
            {
                _resolution = v;
                Sync();
            };
            form.Add(_resolutionSwitch);
            _resolutionHint = ZettaUI.Hint("").Visible(false);
            form.Add(_resolutionHint);

            _modelBox = new VisualElement();
            form.Add(_modelBox);
            scroll.Add(form);

            var resultsCard = ZettaUI.Card();
            resultsCard.Add(ZettaUI.Title("Result"));
            var saveRow = new VisualElement().Classes("z-row", "z-row--wrap");
            saveRow.Add(ImageLibraryView.TextureTypeField(v => _textureType = v, _textureType));
            resultsCard.Add(saveRow);
            _results = new VisualElement().Classes("z-grid");
            resultsCard.Add(_results);
            scroll.Add(resultsCard);

            var historyCard = ZettaUI.Card();
            var historyHead = new VisualElement().Classes("z-row");
            historyHead.Add(ZettaUI.Title("Recent"));
            historyHead.Add(ZettaUI.Spacer());
            historyHead.Add(ZettaUI.Button("All images", () =>
            {
                if (Ctx.Window?.ShowTab(ZettaStudioWindow.LibraryTab) is LibraryPanel library) library.ShowImages();
            }, "z-button--small", "z-button--ghost"));
            historyHead.Add(ZettaUI.Button("Refresh", () => _ = LoadHistory(), "z-button--small", "z-button--ghost"));
            historyCard.Add(historyHead);
            _history = new VisualElement().Classes("z-row", "z-row--wrap");
            historyCard.Add(_history);
            _historyNote = ZettaUI.Hint("");
            historyCard.Add(_historyNote);
            scroll.Add(historyCard);
            root.Add(scroll);

            var footer = new VisualElement().Classes("z-footer");
            _error = new Label().Classes("z-error").Visible(false);
            footer.Add(_error);
            _submit = ZettaUI.Button("Generate image", () => _ = Submit(), "z-button--signal", "z-button--wide");
            footer.Add(_submit);
            root.Add(footer);

            RenderResults();
            Sync();
        }

        public override void OnShown()
        {
            if (!_historyLoaded) _ = LoadHistory();
        }

        public override void OnContextChanged()
        {
            if (_modelBox == null) return;
            BuildModelPicker();
            Sync();
        }

        // ─── Model and resolution ────────────────────────────────────────────────────

        List<ImageModel> Models => Ctx?.ImageModels?.Models ?? new List<ImageModel>();
        ImageModel ActiveModel => Models.FirstOrDefault(m => m.Id == (_model ?? Ctx?.ImageModels?.Default)) ?? Models.FirstOrDefault();

        void BuildModelPicker()
        {
            _modelBox.Clear();
            var models = Models;
            if (models.Count == 0) return;
            var ids = models.Select(m => m.Id).ToList();
            var labels = models.Select(m => m.Locked ? m.Label + " (locked)" : m.Label).ToList();
            var current = ActiveModel?.Id ?? ids[0];
            _modelField = ZettaUI.Dropdown("Model", ids, labels, current, v =>
            {
                var chosen = models.First(m => m.Id == v);
                if (chosen.Locked)
                {
                    _modelField.SetValueWithoutNotify(ActiveModel?.Id ?? ids[0]);
                    ShowError(string.IsNullOrEmpty(chosen.LockedReason) ? $"{chosen.Label} is available on a paid plan." : chosen.LockedReason);
                    return;
                }
                ShowError(null);
                _model = v;
                Sync();
            });
            _modelBox.Add(_modelField);
        }

        void SyncResolution()
        {
            var supported = ActiveModel?.Resolutions;
            var fourKLocked = Ctx?.Gates2D?.Allows4kImages == false;
            string hint = null;
            foreach (var size in ZettaCatalog.Resolutions)
            {
                string reason = null;
                if (supported is { Count: > 0 } && !supported.Contains(size)) reason = $"Not available on {ActiveModel.Label}.";
                else if (size == "4K" && fourKLocked) reason = FourKLock;
                _resolutionSwitch.SetOptionEnabled(size, reason == null, reason);
                if (reason != null && size == "4K") hint = reason;
            }
            // A size the model or plan cannot make moves to the largest one it can.
            var allowed = ZettaCatalog.Resolutions.Where(s => (supported is not { Count: > 0 } || supported.Contains(s)) && !(s == "4K" && fourKLocked)).ToList();
            if (allowed.Count > 0 && !allowed.Contains(_resolution))
            {
                _resolution = allowed.Last();
                _resolutionSwitch.Set(_resolution, false);
            }
            _resolutionHint.text = hint ?? "";
            _resolutionHint.Visible(hint != null);
        }

        // ─── Form state ──────────────────────────────────────────────────────────────

        string Prompt => (_prompt?.value ?? "").Trim();
        bool EditLocked => Ctx?.Gates2D?.AllowsImageEditing == false;

        void Sync()
        {
            if (_submit == null) return;
            var editing = _mode != "generate";
            _modeSwitch.Set(_mode, false);
            _lock.Visible(editing && EditLocked);
            _modeHint.text = _mode switch
            {
                "edit" => "The picture is changed. Describe what should be different.",
                "compose" => "Several pictures are combined into one new picture.",
                _ => "A new picture from your prompt.",
            };
            _editSource.Visible(_mode == "edit");
            _composeSources.Visible(_mode == "compose");
            _styles.Visible(_mode == "generate");
            ZettaUI.SetPlaceholder(_prompt, _mode switch
            {
                "edit" => "Make the sky stormy and add rain on the stone",
                "compose" => "Put the cat from the first image onto the throne in the second",
                _ => "A brushed-brass compass on weathered oak, morning light",
            });
            _promptCount.text = _prompt.value.Length > MaxPrompt * 0.8 ? $"{_prompt.value.Length:N0}/{MaxPrompt:N0}" : "";
            SyncResolution();

            _submit.text = _busy ? (_mode == "generate" ? "Generating…" : "Working…")
                : _mode switch { "edit" => "Edit image", "compose" => "Compose image", _ => "Generate image" };
            var ready = Prompt != "" && !_busy
                        && !(editing && EditLocked)
                        && (_mode != "edit" || _editSource.Items.Count == 1)
                        && (_mode != "compose" || _composeSources.Items.Count >= 2);
            _submit.SetEnabled(ready);
            _submit.tooltip = editing && EditLocked ? EditLock
                : Prompt == "" ? "Write a prompt first."
                : _mode == "edit" && _editSource.Items.Count != 1 ? "Add the image to edit."
                : _mode == "compose" && _composeSources.Items.Count < 2 ? "Add at least two images."
                : "";
        }

        void ShowError(string message)
        {
            _error.text = message ?? "";
            _error.Visible(!string.IsNullOrEmpty(message));
        }

        /// <summary>From elsewhere (a result, the library): open Edit with this picture.</summary>
        public void StartEdit(byte[] bytes, string contentType, long? postId, string label)
        {
            if (_editSource == null) _ = Root;
            _mode = "edit";
            _editSource.ClearImages();
            _editSource.Add(new ImageList.Item { Bytes = bytes, ContentType = contentType, PostId = postId, Name = label });
            Sync();
        }

        // ─── Submit ──────────────────────────────────────────────────────────────────

        async Task Submit()
        {
            if (_busy || !_submit.enabledSelf) return;
            var prompt = Prompt;
            var resolution = _resolution == "4K" && Ctx?.Gates2D?.Allows4kImages == false ? "2K" : _resolution;
            _busy = true;
            ShowError(null);
            Sync();
            try
            {
                ImageResult result;
                switch (_mode)
                {
                    case "edit":
                        var source = _editSource.Items[0];
                        result = source.PostId != null
                            ? await ZettaApi.EditImageAsync(prompt, sourcePostId: source.PostId, aspectRatio: _ratio, resolution: resolution, model: _model)
                            : await ZettaApi.EditImageAsync(prompt, InlineImage.From(source.Bytes, source.ContentType), aspectRatio: _ratio, resolution: resolution, model: _model);
                        break;
                    case "compose":
                        var items = _composeSources.Items;
                        result = await ZettaApi.ComposeImagesAsync(prompt,
                            items.Where(i => i.PostId == null).Select(i => InlineImage.From(i.Bytes, i.ContentType)),
                            items.Where(i => i.PostId != null).Select(i => i.PostId.Value),
                            _ratio, resolution, _model);
                        break;
                    default:
                        result = await ZettaApi.GenerateImageAsync(prompt, _ratio, resolution, _model,
                            _styles.Items.Select(i => InlineImage.From(i.Bytes, i.ContentType)));
                        break;
                }

                var pictures = result?.Images?.Where(i => i.Data.Length > 0).ToList() ?? new List<GeneratedImage>();
                if (pictures.Count == 0)
                    throw new StudioProblem(string.IsNullOrWhiteSpace(result?.Text) ? "No picture came back. Try a different prompt." : result.Text);
                _shown.Clear();
                foreach (var picture in pictures)
                    _shown.Add(new Result
                    {
                        Bytes = picture.Data, ContentType = picture.ContentType ?? "image/png", Prompt = prompt,
                        AspectRatio = _ratio, Resolution = resolution, PostId = result.PostId,
                    });
                RenderResults();
                StudioContext.RefreshCredits();
                _ = LoadHistory();
            }
            catch (ZettaApiException e)
            {
                ShowError(ZettaCatalog.Explain(e));
            }
            catch (Exception e)
            {
                ShowError(e.Message);
            }
            finally
            {
                _busy = false;
                Sync();
            }
        }

        // ─── Results ─────────────────────────────────────────────────────────────────

        void RenderResults()
        {
            _results.Clear();
            if (_shown.Count == 0)
            {
                _results.Add(ZettaUI.Hint("Your generated image will appear here."));
                return;
            }
            foreach (var result in _shown) _results.Add(ResultCard(result));
        }

        VisualElement ResultCard(Result result)
        {
            var card = new VisualElement().Classes("z-result");
            var image = new VisualElement().Classes("z-image-card");
            image.style.backgroundImage = new StyleBackground(ZettaUI.TextureFrom(result.Bytes));
            card.Add(image);
            var size = result.Bytes.Length >= 1024 * 1024 ? $"{result.Bytes.Length / 1024f / 1024f:0.0} MB" : $"{Math.Max(1, result.Bytes.Length / 1024)} KB";
            card.Add(ZettaUI.Hint(string.Join(" · ", new[] { result.AspectRatio, result.Resolution, size }.Where(s => !string.IsNullOrEmpty(s)))));

            var actions = new VisualElement().Classes("z-row", "z-row--wrap");
            var save = ZettaUI.Button("Save to project", null, "z-button--small", "z-button--signal");
            save.clicked += () => Save(result);
            actions.Add(save);
            actions.Add(ZettaUI.Button("Use for 3D", () =>
                Ctx.Window?.UseImageFor3D(result.Bytes, result.ContentType, ZettaImporter.Slug(result.Prompt) + StudioAssets.ExtensionFor(result.ContentType)), "z-button--small"));
            var edit = ZettaUI.Button("Edit this", () => StartEdit(result.Bytes, result.ContentType, result.PostId, result.Prompt), "z-button--small");
            var compose = ZettaUI.Button("Compose", () =>
            {
                _mode = "compose";
                _composeSources.Add(new ImageList.Item { Bytes = result.Bytes, ContentType = result.ContentType, PostId = result.PostId, Name = result.Prompt });
                Sync();
            }, "z-button--small");
            if (EditLocked)
            {
                edit.SetEnabled(false);
                edit.tooltip = EditLock;
                compose.SetEnabled(false);
                compose.tooltip = EditLock;
            }
            actions.Add(edit);
            actions.Add(compose);
            card.Add(actions);
            return card;
        }

        void Save(Result result)
        {
            try
            {
                var path = StudioAssets.SaveImage(result.Bytes, result.ContentType, string.IsNullOrWhiteSpace(result.Prompt) ? "image" : result.Prompt, _textureType);
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                EditorGUIUtility.PingObject(asset);
                Ctx.Window?.Toast("Saved to " + path);
            }
            catch (Exception e)
            {
                ShowError("Could not save the image: " + e.Message);
            }
        }

        // ─── History ─────────────────────────────────────────────────────────────────

        async Task LoadHistory()
        {
            _historyLoaded = true;
            try
            {
                var page = await ZettaApi.ListImagesAsync(1, 12);
                _history.Clear();
                foreach (var post in page.Items)
                {
                    var stored = post.Images.FirstOrDefault();
                    var thumb = new VisualElement().Classes("z-mini-thumb", "z-mini-thumb--large");
                    thumb.tooltip = string.IsNullOrWhiteSpace(post.Prompt) ? "Untitled" : post.Prompt;
                    if (stored == null)
                    {
                        thumb.Add(new Label("Failed").Classes("z-muted"));
                        _history.Add(thumb);
                        continue;
                    }
                    ZettaUI.LoadInto(thumb, stored.Url, "img-" + post.Id);
                    thumb.RegisterCallback<ClickEvent>(e => _ = Open(post, stored));
                    _history.Add(thumb);
                }
                _historyNote.text = page.Items.Count == 0 ? "Images you generate appear here." : "Click a picture to open it above.";
            }
            catch (ZettaApiException e)
            {
                _historyNote.text = "Your library could not be loaded. " + ZettaCatalog.Explain(e);
            }
        }

        async Task Open(ImagePost post, StoredImage stored)
        {
            try
            {
                var bytes = await ZettaHttp.DownloadAsync(stored.Url);
                _shown.Clear();
                _shown.Add(new Result
                {
                    Bytes = bytes, ContentType = stored.ContentType ?? "image/png", Prompt = post.Prompt,
                    AspectRatio = post.AspectRatio, Resolution = post.Resolution, PostId = post.Id,
                });
                RenderResults();
            }
            catch (Exception)
            {
                ShowError("That image could not be opened. Refresh and try again.");
            }
        }
    }
}
