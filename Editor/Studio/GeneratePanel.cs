using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// Generate 3D (Zetta N1 / N2.5) and Smart 3D (Zetta Np1 / Np1.5): from a prompt, a picture or
    /// several views. A prompt is drawn as a picture first when the model builds from pictures (and
    /// always with Control Mode, which stops to show it before any 3D credits are spent).
    /// </summary>
    public class GeneratePanel : StudioPanel
    {
        readonly bool _smart;

        public GeneratePanel(bool smart) => _smart = smart;

        public override string Id => _smart ? StudioPage.SmartTab : StudioPage.GenerateTab;
        public override string Title => _smart ? "Smart 3D" : "Generate 3D";

        const string PlaceholderUrl = "https://zetta3d.ai/placeholder.png";

        GenerateParameters _params;
        ZettaUI.Segmented _source;
        VisualElement _single;
        MultiViewInput _multi;
        TextField _prompt;
        Label _promptHint;
        ZettaUI.ImagePicker _picker;
        VisualElement _imageActions;
        Button _redraw;
        Button _aiEdit;
        Label _imageStatus;
        Label _inputError;
        Label _lock;
        Label _pictureState;
        Label _pictureProblem;
        Label _forkNote;
        SubmitBar _bar;

        /// <summary>
        /// A fork's pictures still on their way, by slot ("image", or a multi-view slot). One batch at a time: a newer
        /// fork starts a new batch, and a picture the user attaches or clears themselves drops its key — either way a
        /// late arrival from the old fetch is ignored, never laid over what the user chose.
        /// </summary>
        readonly HashSet<string> _loadingPictures = new HashSet<string>();
        int _pictureBatch;
        bool _placingForkPicture;

        /// <summary>The picture currently in the slot was drawn from this prompt (null = the user's own file).</summary>
        string _drawnFor;
        bool _settingImage;
        bool _busy;

        bool MultiView => _source?.Value == "multi";

        protected override void Build(VisualElement root)
        {
            var scroll = Scroll();
            _lock = new Label("Generate Smart 3D is available on a paid plan. Zetta N1 generates from text and images on every plan.").Classes("z-lock").Visible(false);
            scroll.Add(_lock);
            scroll.Add(ZettaUI.Hint(_smart
                ? "Clean, production-ready topology from a picture or a prompt."
                : "Turn a prompt or a picture into a textured 3D model.").Classes("z-intro"));

            _params = new GenerateParameters(_smart);
            _params.Changed += Refresh;

            var input = ZettaUI.Card();
            _source = new ZettaUI.Segmented(new[] { "single", "multi" }, new[] { "Text / Image", "Multi-View" }, "single");
            _source.Changed += _ =>
            {
                _single.Visible(!MultiView);
                _multi.Visible(MultiView);
                _params.SetMultiView(MultiView);
                _params.SetFile(MultiView ? _multi.ReferenceName : _drawnFor == null ? _picker.FileName : null);
                Refresh();
            };
            input.Add(_source);

            _single = new VisualElement().Classes("z-input");
            _prompt = ZettaUI.TextArea("A weathered medieval helmet, battle-worn steel", "", 1024);
            _prompt.RegisterValueChangedCallback(_ => Refresh());
            _single.Add(ZettaUI.FieldLabel("Describe the model"));
            _single.Add(_prompt);
            _promptHint = ZettaUI.Hint("");
            _single.Add(_promptHint);

            _single.Add(ZettaUI.FieldLabel("…or use a picture"));
            _picker = new ZettaUI.ImagePicker("Drop an image (or a texture from the Project window), or click to choose");
            _picker.Changed += OnPicked;
            _single.Add(_picker);
            _imageActions = new VisualElement().Classes("z-row", "z-row--wrap");
            _imageActions.Add(ZettaUI.Button("Remove image", ClearImage, "z-button--small", "z-button--ghost"));
            _redraw = ZettaUI.Button("Redraw", () => _ = Redraw(), "z-button--small");
            _redraw.tooltip = "Generate a different image from this prompt";
            _imageActions.Add(_redraw);
            _aiEdit = ZettaUI.Button("AI edit", OpenAiEdit, "z-button--small");
            _imageActions.Add(_aiEdit);
            _single.Add(_imageActions);
            _imageStatus = ZettaUI.Hint("");
            _single.Add(_imageStatus);
            _inputError = new Label().Classes("z-error").Visible(false);
            _single.Add(_inputError);
            input.Add(_single);

            _multi = new MultiViewInput().Visible(false);
            _multi.Changed += () =>
            {
                // A view the user put in themselves wins over a fork's picture still loading for that slot.
                if (!_placingForkPicture) _loadingPictures.RemoveWhere(key => key != "image" && _multi.Picker(key).Bytes != null);
                if (MultiView) _params.SetFile(_multi.ReferenceName);
                Refresh();
            };
            input.Add(_multi);
            _pictureState = ZettaUI.Hint("").Visible(false);
            input.Add(_pictureState);
            _pictureProblem = new Label().Classes("z-error").Visible(false);
            input.Add(_pictureProblem);
            scroll.Add(input);
            scroll.Add(_params);
            root.Add(scroll);

            _forkNote = new Label().Classes("z-fork-note").Visible(false);
            root.Add(_forkNote);

            _bar = new SubmitBar("Generate 3D");
            _bar.OnSubmit(Submit);
            root.Add(_bar);
            Refresh();
        }

        public override void OnContextChanged()
        {
            if (_params == null) return;
            _params.SetContext(Ctx);
            var maxViews = Ctx?.Gates3D?.MaxMultiViewImages;
            var multiLocked = maxViews is { } m && m <= 0;
            _source.SetOptionEnabled("multi", !multiLocked, multiLocked ? "Multi-view input is available on a paid plan." : null);
            if (multiLocked && MultiView) _source.Set("single", true);
            _multi.SetBudget(maxViews);
            _lock.Visible(_smart && Ctx?.Gates3D?.AllowsZettaNp1 == false);
            Refresh();
        }

        // ─── Input ───────────────────────────────────────────────────────────────────

        /// <summary>A picture handed over from elsewhere (Image Studio, the library, the Project window).</summary>
        public void SetImage(byte[] bytes, string contentType, string name)
        {
            if (_picker == null) _ = Root;
            _source.Set("single", true);
            _picker.Set(bytes, contentType, name);
        }

        // ─── Fork ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A fork (StudioFork): the row's settings onto the form, its prompt for a text job, its picture or angles
        /// fetched into the inputs — and nothing run. Generate stays disabled while the pictures load.
        /// </summary>
        public void ApplyFork(StudioFork.Setup fork)
        {
            if (fork == null) return;
            if (_params == null) _ = Root;
            if (_busy || _bar.Busy)
            {
                Ctx?.Window?.Toast("Wait for this job to start, then fork again.");
                return;
            }
            var multi = fork.Tool == StudioFork.MultiViewTool;
            var fromText = fork.Tool == StudioFork.TextTool;

            // What was here goes: the set-up is the input now.
            _bar.ClearMessages();
            ShowInputError(null);
            ShowPictureProblem(null);
            _pictureBatch++;
            _loadingPictures.Clear();
            _params.ResetInputs();
            SetPicture(null, null, null, null);
            _multi.ClearViews();
            _source.Set(multi ? "multi" : "single", true);
            _params.ApplySetUp(fork.Settings);
            // A picture job carries no prompt.
            _prompt.value = fromText ? fork.Settings?.Prompt ?? "" : "";

            var wanted = fromText ? new List<StudioFork.Picture>()
                : multi ? fork.Pictures.Where(p => p.View != null).ToList()
                : fork.Pictures.Take(1).ToList();
            var batch = _pictureBatch;
            foreach (var picture in wanted) _loadingPictures.Add(multi ? picture.View : "image");
            foreach (var picture in wanted) _ = LoadForkPicture(batch, picture, multi);

            _forkNote.text = $"Forked from “{fork.FromTitle}”. Nothing has run yet — change anything you like, then press Generate 3D.";
            _forkNote.Visible(true);
            Refresh();
        }

        async Task LoadForkPicture(int batch, StudioFork.Picture picture, bool multi)
        {
            var key = multi ? picture.View : "image";
            try
            {
                var bytes = await ZettaHttp.DownloadAsync(picture.Url);
                if (batch != _pictureBatch || !_loadingPictures.Contains(key)) return;
                if (bytes == null || bytes.Length == 0) throw new StudioProblem("empty");
                var type = StudioFork.SniffImageType(bytes);
                _placingForkPicture = true;
                try
                {
                    if (multi) _multi.Picker(picture.View).Set(bytes, type, picture.Name);
                    else _picker.Set(bytes, type, picture.Name);
                }
                finally
                {
                    _placingForkPicture = false;
                }
            }
            catch (Exception)
            {
                if (batch != _pictureBatch || !_loadingPictures.Contains(key)) return;
                ShowPictureProblem(multi
                    ? "Some of the reference angles could not be loaded — add them again."
                    : "The reference picture could not be loaded — attach it again.");
            }
            finally
            {
                if (batch == _pictureBatch)
                {
                    _loadingPictures.Remove(key);
                    Refresh();
                }
            }
        }

        void ShowPictureProblem(string message)
        {
            _pictureProblem.text = message ?? "";
            _pictureProblem.Visible(!string.IsNullOrEmpty(message));
        }

        void OnPicked()
        {
            // The user's own choice (a picture attached or removed) wins over a fork's picture still loading.
            if (!_placingForkPicture) _loadingPictures.Remove("image");
            if (_settingImage) return;
            // The user's own picture: check it, and it replaces any drawn one.
            _drawnFor = null;
            ShowInputError(null);
            if (_picker.Bytes != null)
            {
                var problem = StudioAssets.CheckImage(_picker.Bytes, _picker.ContentType);
                if (problem != null)
                {
                    ShowInputError(problem);
                    SetPicture(null, null, null, null);
                }
            }
            _params.SetFile(_picker.Bytes == null ? null : _picker.FileName);
            Refresh();
        }

        void SetPicture(byte[] bytes, string contentType, string name, string drawnFor)
        {
            _settingImage = true;
            try
            {
                _picker.Set(bytes, contentType, name);
            }
            finally
            {
                _settingImage = false;
            }
            _drawnFor = bytes == null ? null : drawnFor;
            _params.SetFile(bytes != null && drawnFor == null ? name : null);
            Refresh();
        }

        void ClearImage()
        {
            _loadingPictures.Remove("image");
            SetPicture(null, null, null, null);
            _imageStatus.text = "";
            ShowInputError(null);
        }

        /// <summary>
        /// AI edit on the picture in the input: a prompt's (the result still counts as drawn from that prompt) or
        /// the user's own. Kept on the converter's plain square, as the picture it replaces.
        /// </summary>
        void OpenAiEdit()
        {
            if (_picker.Bytes == null || _busy) return;
            var drawnFor = _drawnFor;
            var name = _picker.FileName;
            AiEditWindow.Open("AI edit", _picker.Bytes, _picker.ContentType, (bytes, type) =>
            {
                ShowInputError(null);
                if (drawnFor != null) SetPicture(bytes, type, "prompt" + StudioAssets.ExtensionFor(type), drawnFor);
                else SetPicture(bytes, type, AiEditWindow.Renamed(name, type), null);
            });
        }

        void ShowInputError(string message)
        {
            _inputError.text = message ?? "";
            _inputError.Visible(!string.IsNullOrEmpty(message));
        }

        string Prompt => (_prompt?.value ?? "").Trim();
        bool HasOwnImage => _picker.Bytes != null && _drawnFor == null;
        bool DrawnIsCurrent => _picker.Bytes != null && _drawnFor != null && _drawnFor == Prompt;

        /// <summary>Whether a prompt goes to the model as text, with no picture drawn (Zetta N1 without Control Mode).</summary>
        bool SendsBarePrompt => ZettaCatalog.Model(_params.O.Model).BarePrompt && !_params.O.ControlMode && !DrawnIsCurrent;

        /// <summary>The next press draws a picture rather than starting the 3D job.</summary>
        bool NextIsDrawing => !MultiView && !HasOwnImage && !DrawnIsCurrent && Prompt != "" && !SendsBarePrompt;

        // ─── Price and problems ──────────────────────────────────────────────────────

        GenerateOptions Preview()
        {
            var o = _params.Snapshot();
            if (MultiView)
            {
                foreach (var view in _multi.Views()) o.Views.Add((view.slot, PlaceholderUrl));
                return o;
            }
            if (HasOwnImage) o.ImageUrl = PlaceholderUrl;
            else if (Prompt != "")
            {
                o.Prompt = Prompt;
                if (!SendsBarePrompt)
                {
                    o.ImageUrl = PlaceholderUrl;
                    o.ImageFromPrompt = true;
                }
            }
            return o;
        }

        void Refresh()
        {
            if (_bar == null) return;
            var o = Preview();
            var built = ZettaRequests.Build(o, Ctx?.Gates3D);

            // Prompt / picture state lines.
            _prompt.SetEnabled(!HasOwnImage && !_busy);
            _promptHint.text = HasOwnImage ? "The picture is used; the prompt is ignored. Remove the picture to generate from text."
                : _picker.Bytes != null && _drawnFor != null && !DrawnIsCurrent ? "The prompt changed — the picture will be drawn again."
                : NextIsDrawing && _params.O.ControlMode ? "Control Mode: a picture is drawn from your prompt first, so you can check it before any 3D credits are spent."
                : NextIsDrawing ? "A picture is drawn from your prompt, then built into 3D."
                : "";
            _promptHint.Visible(_promptHint.text != "");
            _imageActions.Visible(_picker.Bytes != null && !_busy);
            _redraw.Visible(_drawnFor != null);
            _aiEdit.tooltip = Ctx?.Gates2D?.AllowsImageEditing == false
                ? "Change this picture with a sentence on a paid plan."
                : "Change this picture with a sentence";

            var loadingPictures = _loadingPictures.Count > 0;
            _pictureState.text = loadingPictures ? MultiView ? "Loading the reference angles…" : "Loading the reference picture…" : "";
            _pictureState.Visible(loadingPictures);

            string problem = null;
            if (loadingPictures) problem = "Loading the reference picture…";
            else if (MultiView && _multi.Filled == 0) problem = "Add at least one view to generate from.";
            else if (!MultiView && Prompt == "" && _picker.Bytes == null) problem = "Describe what to make, or attach an image.";
            else if (MultiView && _multi.Busy) problem = "Generating views…";
            // The typed count, said in full here; the field itself says "Out of range — …" (GenerateParameters).
            if (problem == null) problem = _params.CountProblem;
            if (problem == null) problem = built.Problems.FirstOrDefault(p => !p.Contains("draw one from the prompt"));
            if (problem == null)
            {
                var plan = _params.PlanProblems(built, MultiView ? _multi.Filled : 0);
                if (plan.Count > 0) problem = string.Join(" ", plan) + " Upgrade at zetta3d.ai/pricing.";
            }
            if (problem == null && Ctx?.Prices == null && Ctx?.Loading == true) problem = "Loading prices…";

            _bar.SetPrice(ZettaRequests.Price(Ctx?.Prices, built, o.Model));
            _bar.SetProblem(problem);
            if (!_busy) _bar.ButtonText = "Generate 3D";
        }

        // ─── Submit ──────────────────────────────────────────────────────────────────

        void Stage(string text)
        {
            _bar.ButtonText = text;
        }

        async Task Submit()
        {
            _busy = true;
            ShowInputError(null);
            Refresh();
            try
            {
                if (MultiView) await SubmitMultiView();
                else await SubmitSingle();
            }
            finally
            {
                _busy = false;
                _imageStatus.text = "";
                Refresh();
            }
        }

        async Task<bool> Draw()
        {
            var prompt = Prompt;
            Stage("Generating image…");
            _imageStatus.text = "Generating the image…";
            var result = await ZettaApi.GenerateImageAsync(prompt, for3D: true);
            var picture = result?.Images?.FirstOrDefault();
            if (picture == null || picture.Data.Length == 0)
                throw new StudioProblem("The image service returned no image for that prompt.");
            var type = picture.ContentType ?? "image/png";
            SetPicture(picture.Data, type, "prompt" + StudioAssets.ExtensionFor(type), prompt);
            _imageStatus.text = "";
            return true;
        }

        async Task Redraw()
        {
            if (_busy || Prompt == "") return;
            _busy = true;
            ShowInputError(null);
            Refresh();
            try
            {
                await Draw();
            }
            catch (ZettaApiException e)
            {
                ShowInputError(ZettaCatalog.Explain(e));
            }
            catch (Exception e)
            {
                ShowInputError(e.Message);
            }
            finally
            {
                _busy = false;
                _imageStatus.text = "";
                Refresh();
            }
        }

        async Task SubmitSingle()
        {
            var o = _params.Snapshot();
            var prompt = Prompt;

            if (!HasOwnImage && !DrawnIsCurrent && prompt != "" && !SendsBarePrompt)
            {
                await Draw();
                if (o.ControlMode)
                {
                    _bar.ShowDone("Here is the picture. Press Generate 3D to build it, or Redraw for a different one.");
                    return;
                }
            }

            if (_picker.Bytes != null)
            {
                var fromPrompt = _drawnFor != null;
                Stage(_params.RemoveBackground ? "Removing background…" : "Uploading…");
                _imageStatus.text = _params.RemoveBackground ? "Removing the background…" : "Uploading…";
                o.ImageUrl = await StudioAssets.UploadImageAsync(_picker.Bytes, _picker.ContentType, _params.RemoveBackground, p =>
                {
                    var text = StudioAssets.UploadingText(p);
                    Stage(text);
                    _imageStatus.text = text;
                });
                if (fromPrompt)
                {
                    o.Prompt = _drawnFor;
                    o.ImageFromPrompt = true;
                }
            }
            else o.Prompt = prompt;

            var label = !string.IsNullOrWhiteSpace(o.Title) ? o.Title : !string.IsNullOrEmpty(o.Prompt) ? o.Prompt : "Image to 3D";
            await Start(o, label);
            _prompt.value = "";
            SetPicture(null, null, null, null);
        }

        async Task SubmitMultiView()
        {
            var o = _params.Snapshot();
            var views = _multi.Views();
            if (views.Count == 0) throw new StudioProblem("Add at least one view to generate from.");
            foreach (var view in views)
            {
                var problem = StudioAssets.CheckImage(view.bytes, view.contentType);
                if (problem != null) throw new StudioProblem($"{char.ToUpperInvariant(view.slot[0])}{view.slot.Substring(1)} view: {problem}");
            }

            Stage(_params.RemoveBackground ? $"Removing backgrounds (0/{views.Count})…" : StudioAssets.UploadingText(0f, "Uploading views"));
            // One percentage for the batch: each view's share of the bytes going up.
            var shares = new float[views.Count];
            var uploads = views.Select(async (view, index) =>
            {
                var url = await StudioAssets.UploadImageAsync(view.bytes, view.contentType, _params.RemoveBackground, p =>
                {
                    shares[index] = p;
                    Stage(StudioAssets.UploadingText(shares.Sum() / shares.Length, "Uploading views"));
                });
                return (view.slot, url);
            }).ToList();
            var urls = await Task.WhenAll(uploads);
            foreach (var (slot, url) in urls) o.Views.Add((slot, url));

            var label = !string.IsNullOrWhiteSpace(o.Title) ? o.Title : $"Multi-view to 3D ({views.Count} views)";
            await Start(o, label);
            _multi.ClearViews();
        }

        async Task Start(GenerateOptions o, string label)
        {
            var built = ZettaRequests.Build(o, Ctx?.Gates3D);
            if (!built.Ok) throw new StudioProblem(built.Problems[0]);
            Stage("Starting…");
            var result = await ZettaApi.CreateGenerationAsync(built.Body);
            _bar.Track(result, label.Length > 60 ? label.Substring(0, 59) + "…" : label);
            _params.ResetInputs();
            // It ran: the fork note has said what it had to.
            _forkNote.text = "";
            _forkNote.Visible(false);
        }
    }
}
