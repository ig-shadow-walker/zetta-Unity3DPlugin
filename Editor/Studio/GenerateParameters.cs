using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// The Generate form's settings, with the web Studio's defaults, visibility and lock rules per
    /// model capability and plan (generate-parameters.tsx / applyFormPatch). Owns the
    /// <see cref="GenerateOptions"/> the request is built from.
    /// </summary>
    public class GenerateParameters : VisualElement
    {
        const int N1Max = 1_480_000;

        public readonly GenerateOptions O;
        public bool RemoveBackground = true;
        public event Action Changed;

        readonly bool _smart;
        StudioContext _ctx;
        bool _multiView;
        bool _hasFile;
        bool _titleTouched;
        bool _modelPicked;
        bool _outputPicked;
        bool _syncing;
        /// <summary>The polygon count exactly as it reads in the field (free text, so an out-of-range number can be typed and kept).</summary>
        string _countText = "";
        /// <summary>The user typed the count: it survives a topology switch as typed. A rung change resets it (the web's polygonCountTyped).</summary>
        bool _countTyped;

        EngineChoice _model;
        VisualElement _titleBox;
        TextField _title;
        Toggle _removeBg;
        Toggle _autofix;
        ZettaUI.Segmented _output;
        Toggle _parts;
        VisualElement _qualityBox;
        Label _qualityLabel;
        ZettaUI.Segmented _qualityN1;
        ZettaUI.Segmented _qualityN25;
        Label _qualityLock;
        Foldout _polygonFold;
        VisualElement _countBox;
        Label _countLabel;
        TextField _count;
        Label _countError;
        Label _countLocked;
        Label _countRange;
        Label _planLine;
        VisualElement _polygonsBox;
        ZettaUI.Segmented _polygons;
        Label _polygonsLock;
        Toggle _pbr;
        VisualElement _textureBox;
        ZettaUI.Segmented _textureStd;
        ZettaUI.Segmented _textureExt;
        Label _textureHint;
        Toggle _private;
        Foldout _advanced;
        VisualElement _tuning;
        Toggle _scale;
        Toggle _compress;
        Toggle _exportUv;
        Toggle _delight;
        ZettaUI.Segmented _fit;
        Toggle _control;

        public GenerateParameters(bool smart)
        {
            _smart = smart;
            O = new GenerateOptions
            {
                Model = smart ? "np1" : "n1",
                Quality = "high-res",
                Output = "textured",
                Pbr = true,
                TextureQuality = "standard",
                PolygonType = "triangle",
                TextureAlignment = "geometry",
                ExportUv = true,
                ControlMode = true,
            };
            SetCount(DefaultCount, false);
            Build();
            Sync();
        }

        ZettaCatalog.ModelInfo Model => ZettaCatalog.Model(O.Model);
        bool QualityRungs => O.Model is "n1" or "n25";
        bool BareLowPoly => O.Model == "n25";
        bool Quads => O.PolygonType == "quadrilateral";
        ThreeDEntitlements Gates => _ctx?.Gates3D;
        PriceList Prices => _ctx?.Prices;

        public int DefaultCount => ZettaCatalog.FacePreset(O.Model, O.Quality, Quads);

        public (int min, int max) Bounds
        {
            get
            {
                var range = ZettaCatalog.FaceRange(O.Model, O.Quality, Quads);
                return O.Model == "n1" ? (range.min, Math.Min(range.max, N1Max)) : range;
            }
        }

        // ─── Build ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// "Generation Engine": the tab's engines as rows with a line on each (the web's EngineChoice),
        /// directly above Output. Every engine here takes several views, so multi-view keeps the full list.
        /// </summary>
        VisualElement BuildModelPicker()
        {
            var models = _smart ? ZettaCatalog.SmartModels : ZettaCatalog.GenerateModels;
            _model = new EngineChoice("Generation Engine", models.Select(m => new EngineChoice.Option
            {
                Id = m.Id,
                Label = m.Id == "np15" ? m.Label + " · New" : m.Label,
                Blurb = Blurb(m.Id),
            }), O.Model);
            _model.Changed += v =>
            {
                _modelPicked = true;
                SetModel(v);
            };
            return _model;
        }

        static string Blurb(string model) => model switch
        {
            "n1" => "Base Model",
            "n25" => "Highest quality of mesh and texture",
            "np1" => "Clean topology, Balanced quality",
            "np15" => "Clean topology – Quad Native",
            _ => ZettaCatalog.Model(model).Blurb,
        };

        void Build()
        {
            AddToClassList("z-card");
            Add(ZettaUI.Title("Settings"));

            _titleBox = new VisualElement();
            _titleBox.Add(ZettaUI.FieldLabel("Title"));
            _title = ZettaUI.TextInput("Name this generation", "", 255);
            _title.RegisterValueChangedCallback(e =>
            {
                if (_syncing) return;
                _titleTouched = true;
                O.Title = e.newValue;
            });
            _titleBox.Add(_title);
            Add(_titleBox);

            _removeBg = ZettaUI.Toggle("Remove background", RemoveBackground, v => Set(() => RemoveBackground = v),
                "The background is removed from the picture before it is uploaded.");
            Add(_removeBg);
            _autofix = ZettaUI.Toggle("Enhance input image  · Free", O.AutofixImage, v => Set(() => O.AutofixImage = v),
                "Cleans up the picture before the model is built from it.");
            Add(_autofix);

            Add(BuildModelPicker());

            _output = new ZettaUI.Segmented(new[] { "geometry", "textured" }, new[] { "Geometry", "Textured" }, O.Output);
            _output.Changed += v =>
            {
                // A deliberate pick: the plan default must not argue with it.
                _outputPicked = true;
                Set(() => O.Output = v);
            };
            Add(Group("Output", _output));

            _parts = ZettaUI.Toggle("Segmented mesh", O.SegmentParts, v => Set(() =>
            {
                O.SegmentParts = v;
                Invariants();
            }), "Split mesh to separate parts");
            Add(_parts);

            _qualityBox = new VisualElement();
            _qualityLabel = ZettaUI.FieldLabel("Quality");
            _qualityBox.Add(_qualityLabel);
            _qualityN1 = new ZettaUI.Segmented(ZettaCatalog.Qualities, ZettaCatalog.QualityLabels, O.Quality);
            _qualityN25 = new ZettaUI.Segmented(ZettaCatalog.Qualities, new[] { "Low-poly", "High", "Ultra" }, O.Quality);
            _qualityN1.Changed += SetQuality;
            _qualityN25.Changed += SetQuality;
            _qualityBox.Add(_qualityN1);
            _qualityBox.Add(_qualityN25);
            _qualityLock = ZettaUI.Hint("").Classes("z-hint--warn");
            _qualityBox.Add(_qualityLock);
            Add(_qualityBox);

            _polygonFold = new Foldout { text = "Polygon control", value = false }.Classes("z-foldout");
            _countBox = new VisualElement();
            _countLabel = ZettaUI.FieldLabel("Polygon count");
            _countBox.Add(_countLabel);
            // Free text, not an IntegerField: a number the engine refuses must stay as typed and be named
            // out of range under the field, never silently clamped.
            _count = ZettaUI.TextInput("The engine's default", _countText, 12).Classes("z-field");
            _count.RegisterValueChangedCallback(e =>
            {
                if (_syncing) return;
                Set(() => TypeCount(e.newValue));
            });
            _countBox.Add(_count);
            _countError = new Label().Classes("z-error", "z-count-error").Visible(false);
            _countBox.Add(_countError);
            _countLocked = ZettaUI.Hint("Model's default face count (choosing a count is available on a paid plan)");
            _countBox.Add(_countLocked);
            _countRange = ZettaUI.Hint("");
            _countBox.Add(_countRange);
            _planLine = ZettaUI.Hint("Your plan generates at the default quality of 500,000 polygons.");
            _countBox.Add(_planLine);
            _polygonFold.Add(_countBox);

            _polygonsBox = new VisualElement();
            _polygons = new ZettaUI.Segmented(new[] { "triangle", "quadrilateral" }, new[] { "Triangles", "Quads" }, O.PolygonType);
            _polygons.Changed += v => Set(() =>
            {
                O.PolygonType = v;
                // A typed count survives a topology switch exactly as typed (and is reported out of range if the
                // new topology refuses it); a count nobody typed follows the new topology's preset.
                if (!_countTyped) SetCount(DefaultCount, false);
            });
            _polygonsBox.Add(Group("Polygons", _polygons));
            _polygonsLock = ZettaUI.Hint("").Classes("z-hint--warn");
            _polygonsBox.Add(_polygonsLock);
            _polygonFold.Add(_polygonsBox);
            Add(_polygonFold);

            _pbr = ZettaUI.Toggle("PBR", O.Pbr, v => Set(() => O.Pbr = v), "Physically based materials: roughness, metalness and normal maps.");
            Add(_pbr);

            _textureBox = new VisualElement();
            _textureStd = new ZettaUI.Segmented(new[] { "standard", "detailed" }, new[] { "Standard", "Detailed" }, O.TextureQuality);
            _textureExt = new ZettaUI.Segmented(new[] { "standard", "detailed", "extreme" }, new[] { "Standard", "Detailed", "8K" }, O.TextureQuality);
            _textureStd.Changed += v => Set(() => O.TextureQuality = v);
            _textureExt.Changed += v => Set(() => O.TextureQuality = v);
            _textureBox.Add(ZettaUI.FieldLabel("Texture quality"));
            _textureBox.Add(_textureStd);
            _textureBox.Add(_textureExt);
            _textureHint = ZettaUI.Hint("");
            _textureBox.Add(_textureHint);
            Add(_textureBox);

            Add(ZettaUI.FieldLabel("Visibility"));
            _private = ZettaUI.Toggle("Keep this model private", false, v => Set(() => O.Availability = v ? "private" : "public"));
            Add(_private);

            _advanced = new Foldout { text = "Advanced controls", value = false }.Classes("z-foldout");
            _tuning = new VisualElement();
            _scale = ZettaUI.Toggle("Real-world scale  · Free", O.RealWorldScale, v => Set(() => O.RealWorldScale = v), "Sizes the model in metres, as the object would be.");
            _compress = ZettaUI.Toggle("Smaller file  · Free", O.CompressGeometry, v => Set(() => O.CompressGeometry = v), "Compresses the geometry for a lighter download.");
            _exportUv = ZettaUI.Toggle("Export UV map  · Free", O.ExportUv, v => Set(() => O.ExportUv = v),
                "Off is faster and produces a smaller file, but the mesh cannot be re-textured.");
            _delight = ZettaUI.Toggle("Remove baked-in lighting  · Free", O.Delight, v => Set(() => O.Delight = v),
                "Removes the lighting baked into the reference image, so the texture lights correctly in your own scene. Uses our newest texture engine.");
            _fit = new ZettaUI.Segmented(new[] { "geometry", "original_image" }, new[] { "Fit texture to mesh", "Fit texture to picture" }, O.TextureAlignment);
            _fit.Changed += v => Set(() => O.TextureAlignment = v);
            _tuning.Add(_scale);
            _tuning.Add(_compress);
            _tuning.Add(_exportUv);
            _tuning.Add(_delight);
            _tuning.Add(Group("Texture fit", _fit));
            _advanced.Add(_tuning);
            _control = ZettaUI.Toggle("Control Mode", O.ControlMode, v => Set(() => O.ControlMode = v),
                "Displays the reference before generating 3D: a picture drawn from your prompt is shown first, so you can redraw it or go ahead.");
            _advanced.Add(_control);
            _advanced.Add(ZettaUI.Hint("Control Mode displays the reference before generating 3D."));
            Add(_advanced);
        }

        static VisualElement Group(string label, VisualElement control)
        {
            var box = new VisualElement();
            box.Add(ZettaUI.FieldLabel(label));
            box.Add(control);
            return box;
        }

        // ─── State changes ───────────────────────────────────────────────────────────

        void Set(Action change)
        {
            if (_syncing) return;
            change();
            Sync();
            Changed?.Invoke();
        }

        void SetQuality(string quality)
        {
            Set(() =>
            {
                O.Quality = quality;
                Invariants();
                // A rung change is choosing a new density: the preset is what was asked for.
                SetCount(DefaultCount, false);
            });
        }

        // ─── Polygon count ───────────────────────────────────────────────────────────

        /// <summary>Sets the count from code (a preset or a fork), replacing whatever the field said.</summary>
        void SetCount(int? count, bool typed)
        {
            O.FaceCount = count;
            _countText = count?.ToString(CultureInfo.InvariantCulture) ?? "";
            _countTyped = typed;
        }

        static bool TryParseCount(string text, out long count) =>
            long.TryParse((text ?? "").Trim(), NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out count);

        /// <summary>What the user typed: kept verbatim; a whole number in reach becomes the count.</summary>
        void TypeCount(string text)
        {
            _countText = text ?? "";
            _countTyped = true;
            if (_countText.Trim() == "")
            {
                // Empty means "whatever the quality implies" — no custom count.
                O.FaceCount = null;
                return;
            }
            if (!TryParseCount(_countText, out var parsed) || parsed < int.MinValue || parsed > int.MaxValue)
            {
                O.FaceCount = null;
                return;
            }
            var value = (int)parsed;
            O.FaceCount = value;
            // On Zetta N1 the rung IS a count, so typing a count moves the rung with it.
            if (O.Model == "n1" && value >= 3_100 && value <= N1Max)
                O.Quality = value >= 1_000_000 ? "ultra" : value > 15_000 ? "high-res" : "low-poly";
        }

        /// <summary>
        /// Why the typed count cannot be sent (blocks Generate), or null. Asked of the field, not the number: a count
        /// the account cannot choose is never invalid, and an empty field is the engine's default.
        /// </summary>
        public string CountProblem
        {
            get
            {
                if (Gates?.AllowsCustomFaceCount == false) return null;
                var text = _countText.Trim();
                if (text == "") return null;
                if (!TryParseCount(text, out var count)) return "The polygon count must be a whole number.";
                var (min, max) = Bounds;
                return count < min || count > max ? $"The polygon count must be between {min:N0} and {max:N0}." : null;
            }
        }

        /// <summary>The short line under the field: "Out of range — min to max." or "…a whole number, please."</summary>
        string CountFieldError
        {
            get
            {
                var problem = CountProblem;
                if (problem == null) return null;
                if (problem.Contains("whole number")) return "Out of range — a whole number, please.";
                var (min, max) = Bounds;
                return $"Out of range — {min:N0} to {max:N0}.";
            }
        }

        /// <summary>A model switch keeps every choice the new model can honour (applyFormPatch).</summary>
        void SetModel(string model)
        {
            Set(() =>
            {
                ApplyModel(model);
            });
        }

        void ApplyModel(string model)
        {
            // An untouched count follows the model (so a switch never turns on the paid custom count by itself);
            // a count that no longer fits the new engine is refilled too.
            var untouched = O.FaceCount == null || O.FaceCount == DefaultCount;
            O.Model = model;
            var caps = Model;
            if (O.Quality == "low-poly" && !caps.LowPoly) O.Quality = "high-res";
            if (O.TextureQuality == "extreme" && !caps.ExtremeTexture) O.TextureQuality = "detailed";
            if (Quads && !caps.QuadAnyBase && !caps.LowPoly) O.PolygonType = "triangle";
            Invariants();
            var (min, max) = Bounds;
            if (untouched || CountProblem != null || O.FaceCount < min || O.FaceCount > max) SetCount(DefaultCount, false);
        }

        /// <summary>
        /// A fork's settings, applied onto the form in one go (the web's applySetUp / formWithAgentSettings): the
        /// engine named (or the tab's first), the rung, output, topology, PBR, parts and texture band, then the count.
        /// Counts as the user's own choice, so the plan defaults do not argue with it afterwards.
        /// </summary>
        public void ApplySetUp(StudioFork.Settings settings)
        {
            if (settings == null) return;
            _modelPicked = true;
            _outputPicked = true;
            var models = _smart ? ZettaCatalog.SmartModels : ZettaCatalog.GenerateModels;
            ApplyModel(models.Any(m => m.Id == settings.Model) ? settings.Model : models[0].Id);
            var caps = Model;
            if (settings.Quality != null && ZettaCatalog.Qualities.Contains(settings.Quality))
                O.Quality = settings.Quality == "low-poly" && !caps.LowPoly ? "high-res" : settings.Quality;
            if (settings.Output is "geometry" or "textured") O.Output = settings.Output;
            if (settings.PolygonType is "triangle" or "quadrilateral") O.PolygonType = settings.PolygonType;
            if (settings.TextureQuality is "standard" or "detailed" or "extreme")
                O.TextureQuality = settings.TextureQuality == "extreme" && !caps.ExtremeTexture ? "detailed" : settings.TextureQuality;
            O.Pbr = settings.Pbr;
            O.SegmentParts = settings.SegmentParts;
            Invariants();
            SetCount(DefaultCount, false);
            if (settings.FaceCount is { } faces)
            {
                TypeCount(faces.ToString(CultureInfo.InvariantCulture));
                _countTyped = false;
            }
            Sync();
            Changed?.Invoke();
        }

        void Invariants()
        {
            if (O.Quality == "low-poly" && QualityRungs)
            {
                if (!BareLowPoly) O.Output = "textured";
                O.SegmentParts = false;
            }
            else if (O.SegmentParts)
            {
                O.Output = "geometry";
                O.PolygonType = "triangle";
            }
        }

        /// <summary>
        /// The output the form opens on, by plan (the web's defaultOutputFor): Geometry for an account with
        /// no paid plan, Textured otherwise. Applied once the plan is known, never over the user's own pick.
        /// </summary>
        public static string DefaultOutputFor(ThreeDEntitlements gates) =>
            gates != null && gates.AllowsPrivateGenerations == false ? "geometry" : "textured";

        public void SetContext(StudioContext ctx)
        {
            _ctx = ctx;
            if (!_outputPicked && ctx?.Gates3D != null)
            {
                var wanted = DefaultOutputFor(ctx.Gates3D);
                if (wanted != O.Output && !(O.SegmentParts && wanted == "textured"))
                {
                    O.Output = wanted;
                    Invariants();
                }
            }
            // Generate 3D opens on N1 and moves to N2.5 once the plan is known to include it — unless someone chose.
            if (!_smart && !_modelPicked && ctx?.Gates3D?.AllowsZettaN25 == true && O.Model == "n1")
            {
                _syncing = true;
                _model?.Set("n25", false);
                _syncing = false;
                SetModel("n25");
                return;
            }
            Sync();
        }

        public void SetMultiView(bool multiView)
        {
            _multiView = multiView;
            Sync();
        }

        /// <summary>A picture file is attached (title shows, suggested from its name until edited).</summary>
        public void SetFile(string fileName)
        {
            _hasFile = !string.IsNullOrEmpty(fileName);
            if (_hasFile && !_titleTouched)
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(fileName);
                name = System.Text.RegularExpressions.Regex.Replace(name ?? "", @"[\s_]+", " ").Trim();
                O.Title = name.Length > 255 ? name.Substring(0, 255) : name;
            }
            if (!_hasFile && !_titleTouched) O.Title = "";
            Sync();
        }

        /// <summary>After a job starts: the inputs go, the settings stay.</summary>
        public void ResetInputs()
        {
            _titleTouched = false;
            O.Title = "";
            Sync();
        }

        // ─── Sync (values, visibility, locks) ─────────────────────────────────────────

        string Price(string code)
        {
            var credits = Prices?.Price(code, O.Model);
            return credits == null ? "" : credits == 0 ? "Free" : $"+{credits}";
        }

        public void Sync()
        {
            _syncing = true;
            try
            {
                var caps = Model;
                var gates = Gates;
                var textured = O.Output == "textured";

                // Model
                _model.Set(O.Model, false);
                var n25Locked = gates?.AllowsZettaN25 == false;
                var npLocked = gates?.AllowsZettaNp1 == false;
                _model.SetLock("n25", n25Locked ? "Zetta N2.5 is available on a paid plan." : null);
                _model.SetLock("np1", npLocked ? "Available on a paid plan." : null);
                _model.SetLock("np15", npLocked ? "Available on a paid plan." : null);

                // Title
                _titleBox.Visible(_hasFile);
                _title.SetValueWithoutNotify(O.Title ?? "");

                _removeBg.SetValueWithoutNotify(RemoveBackground);
                _autofix.Visible(caps.ImageTuning);
                _autofix.SetValueWithoutNotify(O.AutofixImage);

                // Output
                _output.Set(O.Output, false);
                var geometryBlocked = O.Quality == "low-poly" && QualityRungs && !BareLowPoly;
                _output.SetOptionEnabled("geometry", !geometryBlocked, geometryBlocked ? "Not available at this mesh quality." : null);
                _output.SetOptionEnabled("textured", !O.SegmentParts, O.SegmentParts ? "A segmented mesh comes out untextured. Turn off Segment first." : null);

                // Segmented mesh
                _parts.Visible(caps.Parts && O.Quality != "low-poly");
                _parts.text = "Segmented mesh" + (Price("generate_parts") is var partsPrice && partsPrice != "" ? $"  · {partsPrice}" : "");
                _parts.SetValueWithoutNotify(O.SegmentParts);

                // Quality
                _qualityBox.Visible(QualityRungs);
                _qualityLabel.text = O.Model == "n25" ? "Mesh quality" : "Quality";
                _qualityN1.Visible(O.Model != "n25");
                _qualityN25.Visible(O.Model == "n25");
                var quality = O.Model == "n25" ? _qualityN25 : _qualityN1;
                quality.Set(O.Quality, false);
                string qualityLock = null;
                var qualityLocked = gates?.AllowsCustomFaceCount == false;
                foreach (var rung in ZettaCatalog.Qualities)
                {
                    string reason = null;
                    if (qualityLocked) reason = "Your plan generates at the default quality — 500,000 polygons. Choosing a quality is available on a paid plan.";
                    else if (rung == "ultra" && gates?.MaxFaceCount is { } maxFaces && maxFaces < 1_000_000) reason = "Ultra detail is available from Basic.";
                    else if (rung == "low-poly" && gates?.AllowsLowPoly == false) reason = "Low-poly generation is available on a paid plan.";
                    else if (rung == "low-poly" && !caps.LowPoly) reason = "Low-poly generation needs Zetta N1.";
                    quality.SetOptionEnabled(rung, reason == null, reason);
                    if (reason != null && (qualityLocked || rung == O.Quality || qualityLock == null)) qualityLock = reason;
                }
                _qualityLock.text = qualityLock ?? "";
                _qualityLock.Visible(qualityLock != null);

                // Polygon count
                var mayChoose = gates?.AllowsCustomFaceCount != false;
                var (min, max) = Bounds;
                var custom = mayChoose && O.FaceCount.HasValue && O.FaceCount.Value != DefaultCount;
                var customPrice = custom ? Price("custom_face_count") : "";
                _countLabel.text = "Polygon count" + (customPrice != "" ? $"  · {customPrice}" : "");
                _count.Visible(mayChoose);
                if (_count.value != _countText) _count.SetValueWithoutNotify(_countText);
                _count.tooltip = $"{min:N0}–{max:N0}";
                var countError = mayChoose ? CountFieldError : null;
                _countError.text = countError ?? "";
                _countError.Visible(countError != null);
                _countLocked.Visible(!mayChoose);
                _countRange.text = $"Minimum {min:N0}, up to {max:N0}";
                _countRange.Visible(mayChoose && O.Model != "n1");
                _planLine.Visible(!caps.DetailedGeometry && !mayChoose);

                // Polygons (triangles / quads)
                _polygonsBox.Visible(caps.QuadAnyBase || caps.LowPoly);
                _polygons.Set(O.PolygonType, false);
                string polygonsLock = null;
                var groupOff = O.Quality != "low-poly" && !caps.QuadAnyBase;
                if (groupOff)
                {
                    _polygons.SetOptionEnabled("triangle", false, "Not used at this mesh quality.");
                    _polygons.SetOptionEnabled("quadrilateral", false, "Not used at this mesh quality.");
                    polygonsLock = "Not used at this mesh quality.";
                }
                else
                {
                    _polygons.SetOptionEnabled("triangle", true);
                    string quadReason = null;
                    if (O.SegmentParts) quadReason = "Not available while segmenting.";
                    else if (gates?.AllowsQuadMesh == false) quadReason = "Quad meshes are available from Basic.";
                    _polygons.SetOptionEnabled("quadrilateral", quadReason == null, quadReason);
                    polygonsLock = quadReason;
                }
                _polygonsLock.text = polygonsLock ?? "";
                _polygonsLock.Visible(polygonsLock != null);

                // PBR and texture quality
                _pbr.Visible(textured);
                _pbr.text = "PBR" + (O.Pbr && Price("pbr") is var pbrPrice && pbrPrice != "" ? $"  · {pbrPrice}" : "");
                _pbr.SetValueWithoutNotify(O.Pbr);
                _textureBox.Visible(caps.DetailedTexture && textured);
                _textureStd.Visible(!caps.ExtremeTexture);
                _textureExt.Visible(caps.ExtremeTexture);
                (caps.ExtremeTexture ? _textureExt : _textureStd).Set(O.TextureQuality, false);
                _textureHint.text = O.TextureQuality switch
                {
                    "detailed" => $"Up to 4K resolution ({Price("detailed_texture")})",
                    "extreme" when caps.ExtremeTexture => $"Up to 8K resolution ({Price("extreme_texture")})",
                    _ => "Up to 2K resolution (free)",
                };

                // Visibility
                var privateAllowed = gates?.AllowsPrivateGenerations != false;
                var isPrivate = O.Availability == null ? gates?.AllowsPrivateGenerations == true : O.Availability == "private";
                _private.SetValueWithoutNotify(privateAllowed && isPrivate);
                _private.SetEnabled(privateAllowed);
                _private.tooltip = privateAllowed
                    ? "Private models are yours alone. Public ones can appear in the community gallery."
                    : "Keeping a model private is available on a paid plan. Models made on your plan are public.";
                _private.text = privateAllowed ? "Keep this model private" : "Keep this model private (paid plan)";

                // Advanced
                var showControl = !_multiView;
                _advanced.Visible(caps.ImageTuning || showControl);
                _tuning.Visible(caps.ImageTuning);
                _scale.SetEnabled(textured);
                _scale.SetValueWithoutNotify(textured && O.RealWorldScale);
                _scale.tooltip = textured ? "Sizes the model in metres, as the object would be." : "Only applies to a textured output.";
                _compress.SetValueWithoutNotify(O.CompressGeometry);
                _exportUv.Visible(textured);
                _exportUv.SetValueWithoutNotify(O.ExportUv);
                _delight.Visible(textured);
                _delight.SetValueWithoutNotify(O.Delight);
                _fit.parent.Visible(textured);
                _fit.Set(O.TextureAlignment, false);
                _control.Visible(showControl);
                _control.SetValueWithoutNotify(O.ControlMode);
            }
            finally
            {
                _syncing = false;
            }
        }

        // ─── Checks ──────────────────────────────────────────────────────────────────

        /// <summary>The options as they will be sent: texture-only knobs are dropped on a geometry output.</summary>
        public GenerateOptions Snapshot()
        {
            var textured = O.Output == "textured";
            return new GenerateOptions
            {
                Model = O.Model,
                Quality = QualityRungs ? O.Quality : "high-res",
                Output = O.Output,
                Pbr = O.Pbr,
                TextureQuality = O.TextureQuality,
                FaceCount = O.FaceCount,
                PolygonType = O.PolygonType,
                Title = _hasFile ? O.Title : "",
                SegmentParts = O.SegmentParts && Model.Parts && O.Quality != "low-poly",
                RealWorldScale = textured && O.RealWorldScale,
                Delight = textured && O.Delight,
                AutofixImage = O.AutofixImage,
                CompressGeometry = O.CompressGeometry,
                TextureAlignment = textured ? O.TextureAlignment : "original_image",
                ExportUv = !textured || O.ExportUv,
                ControlMode = O.ControlMode && !_multiView,
                Availability = Gates?.AllowsPrivateGenerations == false ? null : O.Availability,
            };
        }

        /// <summary>What the plan does not include in this configuration (the web's plan blocks).</summary>
        public List<string> PlanProblems(BuiltRequest built, int views)
        {
            var problems = new List<string>();
            var gates = Gates;
            if (gates == null) return problems;
            if (O.Model == "n25" && gates.AllowsZettaN25 == false) problems.Add("Zetta N2.5 is available on a paid plan.");
            if (O.Model is "np1" or "np15" && gates.AllowsZettaNp1 == false) problems.Add("Generate Smart 3D is available on a paid plan.");
            if (built.Base == "lowpoly" && gates.AllowsLowPoly == false) problems.Add("Low-poly generation is available on a paid plan.");
            if ((string)built.Body?["polygonType"] == "quadrilateral" && gates.AllowsQuadMesh == false) problems.Add("Quad meshes are available from Basic.");
            if (gates.MaxFaceCount is { } maxFaces && maxFaces < 1_000_000)
            {
                if (built.Modifiers.Contains("detailed_geometry")) problems.Add("Ultra detail is available from Basic.");
                else if (built.Body?["faceCount"] is { } sent && (int)sent >= Math.Min(1_000_000, maxFaces + 1))
                    problems.Add($"Your plan generates up to {maxFaces:N0} polygons. Lower the polygon count, or upgrade.");
            }
            if (views > 0 && gates.MaxMultiViewImages is { } maxViews && views > maxViews)
                problems.Add($"You have {views} views and your plan uses {maxViews}.");
            return problems;
        }
    }
}
