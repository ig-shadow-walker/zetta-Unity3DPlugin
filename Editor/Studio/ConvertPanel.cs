using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// Convert &amp; Export: another file format, a polygon limit, pivot, scale, axis and texture
    /// settings. Plain format changes and the advanced exporter are priced differently
    /// (the web's convertTierFor rule).
    /// </summary>
    public class ConvertPanel : PostProcessPanel
    {
        const int MinFaces = 3_100;
        const int MaxFaces = 1_500_000;

        static readonly string[] FormatHints =
        {
            "Game engines and animation. Rigs survive.",
            "The text flavour of glTF, for web pipelines.",
            "Read by almost everything. No animation.",
            "AR on iPhone and iPad — opens in Quick Look.",
            "Printing. Geometry only, no colour.",
            "Printing, with colour and materials kept.",
        };

        static readonly string[] OrientationValues = { "+x", "-x", "-y", "+y" };

        public override string Id => ZettaStudioWindow.ConvertTab;
        public override string Title => "Convert";
        protected override string Intro => "Export a model in another format, with a polygon limit, pivot, scale and texture settings.";
        protected override bool AnyFeature => true;
        protected override string Code => Advanced ? "convert_format_advanced" : "convert_format";
        protected override string ButtonText => $"Convert to {_format}";

        string _format = "FBX";
        bool _flatten;
        bool _pivot;
        string _orientation = "+x";
        bool _packUv;
        bool _vertexColors;
        string _textureFormat = "JPEG";
        bool _animation = true;
        bool _inPlace;
        string _fbxPreset = "blender";

        TextField _faces;
        TextField _flattenDepth;
        TextField _scale;
        TextField _textureSize;
        Label _formatHint;
        Label _advancedNote;
        VisualElement _flattenBox;
        Toggle _vertexToggle;
        Toggle _inPlaceToggle;
        VisualElement _fbxBox;

        protected override string Refuse(Generation row) =>
            !row.IsSuccess ? "That model is not finished yet."
            : row.IsSplat ? ZettaImporter.SplatUnsupported
            : null;

        protected override void BuildOptions(VisualElement c)
        {
            c.Add(ZettaUI.Title("Export"));
            var format = ZettaUI.Dropdown(null, ZettaCatalog.ConvertFormats.ToList(), ZettaCatalog.ConvertFormats.ToList(), _format, v =>
            {
                _format = v;
                if (v != "OBJ" && v != "GLTF") _vertexColors = false;
                _vertexToggle.SetValueWithoutNotify(_vertexColors);
                Sync();
            });
            c.Add(Field("Export as", format));
            _formatHint = ZettaUI.Hint("");
            c.Add(_formatHint);

            _fbxBox = new VisualElement();
            _fbxBox.Add(Field("Written for", ZettaUI.Dropdown(null, ZettaCatalog.FbxPresets.ToList(), ZettaCatalog.FbxPresetLabels.ToList(), _fbxPreset, v =>
            {
                _fbxPreset = v;
                Sync();
            })));
            c.Add(_fbxBox);

            c.Add(ZettaUI.Title("Geometry").Classes("z-section-gap"));
            _faces = NumberField("Keep as is");
            c.Add(Field("Polygon limit (optional)", _faces, $"Between {MinFaces:N0} and {MaxFaces:N0}. Leave empty to keep the model's own count."));

            c.Add(ZettaUI.Toggle("Flatten the bottom", _flatten, v =>
            {
                _flatten = v;
                Sync();
            }, "Cuts a flat base so the model stands level."));
            _flattenBox = new VisualElement();
            _flattenDepth = NumberField("0.01");
            _flattenBox.Add(Field("Flatten depth (default 0.01)", _flattenDepth, "Between 0 and 1."));
            c.Add(_flattenBox);

            c.Add(ZettaUI.Toggle("Move the pivot to the bottom centre", _pivot, v =>
            {
                _pivot = v;
                Sync();
            }, "Recommended for Unity: the model then stands on its origin and snaps to the ground."));
            _scale = NumberField("1");
            c.Add(Field("Scale (1 = unchanged)", _scale, "Between 0.001 and 1000."));
            c.Add(Field("Forward axis", ZettaUI.Dropdown(null, OrientationValues.ToList(), OrientationValues.ToList(), _orientation, v =>
            {
                _orientation = v;
                Sync();
            })));

            c.Add(ZettaUI.Title("Textures").Classes("z-section-gap"));
            c.Add(ZettaUI.Toggle("Pack UVs into one layout", _packUv, v => _packUv = v));
            _vertexToggle = ZettaUI.Toggle("Keep vertex colours", _vertexColors, v => _vertexColors = v, "Written to OBJ and GLTF only.");
            c.Add(_vertexToggle);
            _textureSize = NumberField("4096");
            c.Add(Field("Texture size (default 4096px)", _textureSize, "Between 128 and 4096."));
            var textureLabels = ZettaCatalog.TextureFormats.Select(f => f == "OPEN_EXR" ? "EXR" : f).ToList();
            c.Add(Field("Texture format", ZettaUI.Dropdown(null, ZettaCatalog.TextureFormats.ToList(), textureLabels, _textureFormat, v =>
            {
                _textureFormat = v;
                Sync();
            })));

            c.Add(ZettaUI.Title("Animation").Classes("z-section-gap"));
            c.Add(ZettaUI.Toggle("Keep skeleton and animation", _animation, v =>
            {
                _animation = v;
                Sync();
            }));
            _inPlaceToggle = ZettaUI.Toggle("Play in place, without root movement", _inPlace, v => _inPlace = v);
            c.Add(_inPlaceToggle);

            _advancedNote = ZettaUI.Hint("These settings use the advanced exporter, which costs more than a plain format change.").Classes("z-hint--warn");
            c.Add(_advancedNote);
            Sync();
        }

        TextField NumberField(string placeholder)
        {
            var field = ZettaUI.TextInput(placeholder, "", 16);
            field.RegisterValueChangedCallback(_ => Sync());
            return field;
        }

        static double? Number(TextField field)
        {
            var text = (field?.value ?? "").Trim();
            if (text == "") return null;
            return double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
        }

        bool Advanced
        {
            get
            {
                var size = Number(_textureSize);
                var scale = Number(_scale);
                return Number(_faces) != null
                       || _flatten
                       || (size != null && size != 4096)
                       || _textureFormat != "JPEG"
                       || _pivot
                       || (scale != null && scale != 1);
            }
        }

        void Sync()
        {
            if (_advancedNote == null) return;
            _formatHint.text = FormatHints[Math.Max(0, Array.IndexOf(ZettaCatalog.ConvertFormats, _format))]
                               + (_format == "FBX" ? " Unity imports FBX natively." : "");
            _fbxBox.Visible(_format == "FBX");
            _flattenBox.Visible(_flatten);
            _vertexToggle.Visible(_format is "OBJ" or "GLTF");
            _inPlaceToggle.Visible(_animation);
            _advancedNote.Visible(Advanced);
            Refresh();
        }

        protected override string Problem()
        {
            var faces = Number(_faces);
            if (faces != null && (double.IsNaN(faces.Value) || faces % 1 != 0 || faces < MinFaces || faces > MaxFaces))
                return $"The polygon limit must be a whole number between {MinFaces:N0} and {MaxFaces:N0}.";
            var size = Number(_textureSize);
            var scale = Number(_scale);
            var depth = _flatten ? Number(_flattenDepth) : null;
            if ((size != null && (double.IsNaN(size.Value) || size < 128 || size > 4096))
                || (scale != null && (double.IsNaN(scale.Value) || scale <= 0 || scale > 1000))
                || (depth != null && (double.IsNaN(depth.Value) || depth < 0 || depth > 1)))
                return "Check the numbers: texture size 128–4096, scale above 0, flatten depth 0–1.";
            return null;
        }

        protected override Task<JObject> OptionsAsync()
        {
            var options = new JObject { ["outputFormat"] = _format };
            if (Number(_faces) is { } faces) options["faceCount"] = (int)faces;
            if (_flatten)
            {
                options["flattenBottom"] = true;
                if (Number(_flattenDepth) is { } depth) options["flattenBottomThreshold"] = depth;
            }
            if (_pivot) options["pivotToCenterBottom"] = true;
            if (Number(_scale) is { } scale) options["scaleFactor"] = scale;
            if (_orientation != "+x") options["exportOrientation"] = _orientation;
            if (_packUv) options["packUv"] = true;
            if (_vertexColors && _format is "OBJ" or "GLTF") options["exportVertexColors"] = true;
            if (Number(_textureSize) is { } size) options["textureSize"] = (int)size;
            if (_textureFormat != "JPEG") options["textureFormat"] = _textureFormat;
            if (!_animation) options["withAnimation"] = false;
            else if (_inPlace) options["animateInPlace"] = true;
            if (_format == "FBX" && _fbxPreset != "blender") options["fbxPreset"] = _fbxPreset;
            return Task.FromResult(options);
        }
    }
}
