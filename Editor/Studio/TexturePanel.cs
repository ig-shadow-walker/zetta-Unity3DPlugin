using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// Re-textures a model from a reference picture (or four views). A description of the surface
    /// is first turned into a new reference picture you approve ("Preview"), then applied.
    /// </summary>
    public class TexturePanel : PostProcessPanel
    {
        static readonly string[] Tiers = { "retexture", "retexture_hd", "retexture_8k" };
        static readonly string[] TierLabels = { "Standard", "Detailed", "8K" };
        static readonly string[] TierResolutions = { "2K", "4K", "8K" };

        public override string Id => StudioPage.TextureTab;
        public override string Title => "Texture";
        protected override string Intro => "Paint a new texture onto a model from a picture, or from a description of the surface.";
        protected override string Code => _tier;
        protected override string ButtonText => "Apply texture";

        string _mode = "single";
        string _tier = "retexture";
        bool _fast;
        string _alignment = "original_image";
        bool _pbr = true;
        bool _delight = true;
        bool _bake = true;
        bool _removeBg = true;

        VisualElement _single;
        MultiViewInput _multi;
        ZettaUI.ImagePicker _picker;
        Label _referenceHint;
        TextField _describe;
        Button _preview;
        VisualElement _previewBox;
        VisualElement _previewImage;
        Label _previewError;
        Label _tierHint;
        Toggle _fastToggle;

        byte[] _approved;
        string _approvedType;
        string _approvedFor;
        bool _previewing;

        protected override void BuildOptions(VisualElement c)
        {
            c.Add(ZettaUI.Title("Reference"));
            var mode = new ZettaUI.Segmented(new[] { "single", "multi" }, new[] { "Single image", "Multi-view" }, _mode);
            mode.Changed += v =>
            {
                _mode = v;
                _single.Visible(v == "single");
                _multi.Visible(v == "multi");
                Refresh();
            };
            c.Add(mode);

            _single = new VisualElement();
            _picker = new ZettaUI.ImagePicker("Use my own picture — drop an image or click to choose");
            _picker.Changed += () =>
            {
                if (_picker.Bytes != null && StudioAssets.CheckImage(_picker.Bytes, _picker.ContentType) is { } problem)
                {
                    _previewError.text = problem;
                    _previewError.Visible(true);
                    _picker.Reset();
                    return;
                }
                ClearPreview();
                Sync();
            };
            _single.Add(_picker);
            _referenceHint = ZettaUI.Hint("");
            _single.Add(_referenceHint);

            _describe = ZettaUI.TextArea("e.g. worn brass with green patina settling in the recesses", "", 1000);
            _describe.RegisterValueChangedCallback(_ => Sync());
            _single.Add(Field("Describe the surface", _describe, "Optional. A description is turned into a new reference picture you can check before applying."));
            var previewRow = new VisualElement().Classes("z-row", "z-row--wrap");
            _preview = ZettaUI.Button("Preview", () => _ = Preview(), "z-button--small");
            previewRow.Add(_preview);
            _single.Add(previewRow);
            _previewBox = new VisualElement().Visible(false);
            _previewImage = new VisualElement().Classes("z-image-card");
            _previewBox.Add(_previewImage);
            _previewBox.Add(ZettaUI.Hint("This picture is applied as the new surface. Press Preview again for a different one."));
            _single.Add(_previewBox);
            _previewError = new Label().Classes("z-error").Visible(false);
            _single.Add(_previewError);
            c.Add(_single);

            _multi = new MultiViewInput().Visible(false);
            _multi.Changed += Refresh;
            c.Add(_multi);

            c.Add(ZettaUI.Toggle("Remove background before applying", _removeBg, v => _removeBg = v));

            c.Add(ZettaUI.Title("Options").Classes("z-section-gap"));
            var tier = new ZettaUI.Segmented(Tiers, TierLabels, _tier);
            tier.Changed += v =>
            {
                _tier = v;
                Sync();
            };
            c.Add(Field("Texture quality", tier));
            _tierHint = ZettaUI.Hint("");
            c.Add(_tierHint);
            _fastToggle = ZettaUI.Toggle("Faster, at some detail — same price", _fast, v => _fast = v);
            c.Add(_fastToggle);

            var fit = new ZettaUI.Segmented(new[] { "geometry", "original_image" }, new[] { "Fit texture to mesh", "Fit texture to picture" }, _alignment);
            fit.Changed += v => _alignment = v;
            c.Add(Field("Texture fit", fit));
            c.Add(ZettaUI.Toggle("PBR materials", _pbr, v => _pbr = v, "Roughness, metalness and normal maps as well as colour."));
            c.Add(ZettaUI.Toggle("Remove the reference's baked-in lighting", _delight, v => _delight = v,
                "So the texture lights correctly in your own scene."));
            c.Add(ZettaUI.Toggle("Bake effects into the textures", _bake, v => _bake = v));
            Sync();
        }

        protected override void OnSourceChanged()
        {
            _importedFor = -1;
            ClearPreview();
            Sync();
        }

        void ClearPreview()
        {
            _approved = null;
            _approvedType = null;
            _approvedFor = null;
            if (_previewBox == null) return;
            _previewBox.Visible(false);
            _previewError.Visible(false);
        }

        long _importedFor = -1;
        UnityEngine.GameObject _imported;

        /// <summary>The source's imported copy in this project (looked up once per source).</summary>
        UnityEngine.GameObject Imported
        {
            get
            {
                if (Source == null) return null;
                if (_importedFor != Source.Id || (_imported == null && !ReferenceEquals(_imported, null)))
                {
                    _importedFor = Source.Id;
                    _imported = StudioAssets.FindImported(Source.Id);
                }
                return _imported;
            }
        }

        string Description => (_describe?.value ?? "").Trim();
        string SourceImageUrl => Source?.InputImages?.FirstOrDefault(i => !string.IsNullOrEmpty(i.Url))?.Url;

        void Sync()
        {
            if (_tierHint == null) return;
            var index = System.Array.IndexOf(Tiers, _tier);
            var price = Ctx?.Prices?.Price(_tier, null);
            _tierHint.text = $"Up to {TierResolutions[index]} resolution" + (price == null ? "" : $" ({price} credits)");
            _fastToggle.Visible(_tier == "retexture");

            _referenceHint.text = _picker.Bytes != null ? "Your picture is the reference."
                : SourceImageUrl != null ? "No picture chosen: the model's own input picture is the reference."
                : Imported != null ? "No picture chosen: a render of the imported model is the reference."
                : "Choose a reference picture, or describe the surface.";
            _preview.SetEnabled(!_previewing && Description != "" && Source != null);
            _preview.text = _previewing ? "Drawing…" : _approved != null ? "Try again" : "Preview";
            Refresh();
        }

        public override void OnContextChanged()
        {
            Sync();
            base.OnContextChanged();
        }

        protected override string Problem()
        {
            if (_mode == "multi")
            {
                if (_multi.Picker("front").Bytes == null) return "Add a front view — it is required.";
                return _multi.Busy ? "Generating views…" : null;
            }
            if (_previewing) return "Drawing the preview…";
            if (Description != "" && (_approved == null || _approvedFor != Description)) return "Press Preview to see the new surface first.";
            if (Description == "" && _picker.Bytes == null && SourceImageUrl == null && Imported == null)
                return "Choose a reference picture, or describe the surface.";
            return null;
        }

        /// <summary>The picture a description is applied to: the user's, the model's input, or a render of the imported copy.</summary>
        async Task<(byte[] bytes, string type)> ReferenceAsync(bool required)
        {
            if (_picker.Bytes != null) return (_picker.Bytes, _picker.ContentType);
            var url = SourceImageUrl;
            if (url != null)
            {
                var bytes = await ZettaHttp.DownloadAsync(url);
                var type = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? "image/jpeg"
                    : bytes.Length > 12 && bytes[8] == 'W' && bytes[9] == 'E' ? "image/webp" : "image/png";
                return (bytes, type);
            }
            _importedFor = -1;
            var imported = Imported;
            if (imported != null)
            {
                var shots = ZettaCapture.AssetShots(imported, 768);
                if (shots.Count > 0) return (shots[0], "image/jpeg");
            }
            if (required) throw new StudioProblem("Choose a reference picture, or describe the surface.");
            return (null, null);
        }

        async Task Preview()
        {
            var description = Description;
            if (description == "" || _previewing) return;
            _previewing = true;
            _previewError.Visible(false);
            Sync();
            try
            {
                var (bytes, type) = await ReferenceAsync(false);
                var result = bytes != null
                    ? await ZettaApi.EditImageAsync(description, InlineImage.From(bytes, type), for3D: true, pipelineStep: true)
                    : await ZettaApi.GenerateImageAsync(description, for3D: true);
                var picture = result?.Images?.FirstOrDefault();
                if (picture == null || picture.Data.Length == 0)
                    throw new StudioProblem(string.IsNullOrWhiteSpace(result?.Text) ? "No picture came back. Try again." : result.Text);
                _approved = picture.Data;
                _approvedType = picture.ContentType ?? "image/png";
                _approvedFor = description;
                _previewImage.style.backgroundImage = new StyleBackground(ZettaUI.TextureFrom(_approved));
                _previewBox.Visible(true);
            }
            catch (ZettaApiException e)
            {
                _previewError.text = ZettaCatalog.Explain(e);
                _previewError.Visible(true);
            }
            catch (System.Exception e)
            {
                _previewError.text = e.Message;
                _previewError.Visible(true);
            }
            finally
            {
                _previewing = false;
                Sync();
            }
        }

        protected override async Task<JObject> OptionsAsync()
        {
            var options = new JObject
            {
                ["textureAlignment"] = _alignment,
                ["enablePBR"] = _pbr,
                ["delight"] = _delight,
            };
            if (!_bake) options["bake"] = false;
            if (_fast && _tier == "retexture") options["textureFast"] = true;

            try
            {
                if (_mode == "multi")
                {
                    var views = _multi.Views();
                    if (views.All(v => v.slot != "front")) throw new StudioProblem("Add a front view — it is required.");
                    // One percentage for the batch: each view's share of the bytes going up.
                    var shares = new float[views.Count];
                    void Report(int index, float p)
                    {
                        shares[index] = p;
                        Bar.ButtonText = StudioAssets.UploadingText(shares.Sum() / shares.Length, "Uploading views");
                    }
                    var uploads = await Task.WhenAll(views.Select(async (v, i) =>
                        new JObject
                        {
                            ["viewType"] = v.slot,
                            ["imageUrl"] = await StudioAssets.UploadImageAsync(v.bytes, v.contentType, _removeBg, p => Report(i, p)),
                        }));
                    options["multiViewImages"] = new JArray(uploads.Cast<object>().ToArray());
                    return options;
                }

                byte[] bytes;
                string type;
                if (Description != "")
                {
                    if (_approved == null || _approvedFor != Description) throw new StudioProblem("Press Preview to see the new surface first.");
                    (bytes, type) = (_approved, _approvedType);
                }
                else (bytes, type) = await ReferenceAsync(true);
                options["imageUrl"] = await StudioAssets.UploadImageAsync(bytes, type, _removeBg, p => Bar.ButtonText = StudioAssets.UploadingText(p));
                return options;
            }
            finally
            {
                Bar.ButtonText = ButtonText;
            }
        }
    }
}
