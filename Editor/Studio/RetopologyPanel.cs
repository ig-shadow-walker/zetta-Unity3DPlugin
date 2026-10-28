using System.Globalization;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>Rebuilds a model's topology: Zetta R1 to an exact polygon budget, Zetta N1 at a level of detail.</summary>
    public class RetopologyPanel : PostProcessPanel
    {
        const int MinCount = 500;
        const int MaxTriangles = 20_000;
        const int MaxQuads = 10_000;

        public override string Id => StudioPage.RetopologyTab;
        public override string Title => "Retopology";
        protected override string Intro => "Rebuild the mesh with clean, even topology — lighter for games, easier to rig and UV.";
        protected override string Code => "smart_topology";
        protected override string PriceModel => _engine;
        protected override string ButtonText => "Retopologize";

        string _engine = "r1";
        string _polygonType = "triangle";
        /// <summary>The budget exactly as typed — free text, so a number over the ceiling stays and is named out of range.</summary>
        string _countText = "8000";
        string _level = "medium";
        bool _bake = true;

        Label _engineHint;
        ZettaUI.Segmented _polygons;
        VisualElement _r1;
        VisualElement _n1;
        TextField _countField;
        Label _countLabel;
        Label _countError;
        Label _countHint;
        Label _levelHint;
        Label _quadLock;

        protected override string Refuse(Generation row)
        {
            if (row.Feature == "retopology") return "This model is already a retopology result.";
            return base.Refuse(row);
        }

        protected override void BuildOptions(VisualElement c)
        {
            c.Add(ZettaUI.Title("Options"));

            var engine = new ZettaUI.Segmented(new[] { "r1", "n1" }, new[] { "Zetta R1", "Zetta N1" }, _engine);
            _engineHint = ZettaUI.Hint("");
            engine.Changed += v =>
            {
                _engine = v;
                Sync();
            };
            c.Add(Field("Engine", engine));
            c.Add(_engineHint);

            _polygons = new ZettaUI.Segmented(new[] { "triangle", "quadrilateral" }, new[] { "Triangles", "Quads" }, _polygonType);
            _polygons.Changed += v =>
            {
                // The number does not jump: a count over the new ceiling stays as typed and is reported out of range.
                _polygonType = v;
                Sync();
            };
            c.Add(Field("Polygons", _polygons));
            _quadLock = ZettaUI.Hint("Quad meshes are available from Basic.").Classes("z-hint--warn").Visible(false);
            c.Add(_quadLock);

            _r1 = new VisualElement();
            _countLabel = ZettaUI.FieldLabel("");
            _r1.Add(_countLabel);
            _countField = ZettaUI.TextInput("Polygon count", _countText, 12).Classes("z-field");
            _countField.RegisterValueChangedCallback(e =>
            {
                _countText = e.newValue ?? "";
                Sync();
            });
            _r1.Add(_countField);
            _countError = new Label().Classes("z-error", "z-count-error").Visible(false);
            _r1.Add(_countError);
            _countHint = ZettaUI.Hint("");
            _r1.Add(_countHint);
            _r1.Add(ZettaUI.Toggle("Bake textures onto the rebuilt mesh", _bake, v => _bake = v,
                "Carries the original colours over to the new mesh. Off returns bare geometry."));
            c.Add(_r1);

            _n1 = new VisualElement();
            var level = new ZettaUI.Segmented(new[] { "low", "medium", "high" }, new[] { "Low", "Medium", "High" }, _level);
            _levelHint = ZettaUI.Hint("");
            level.Changed += v =>
            {
                _level = v;
                Sync();
            };
            _n1.Add(Field("Level of detail", level));
            _n1.Add(_levelHint);
            c.Add(_n1);
            Sync();
        }

        int Max => _polygonType == "quadrilateral" ? MaxQuads : MaxTriangles;

        /// <summary>The typed budget as a whole number, or null for empty or anything that is not one.</summary>
        int? Count => int.TryParse(_countText.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var count) ? count : null;

        /// <summary>A budget outside the range (an empty field is not zero, and is out of range too). Only R1 takes a count.</summary>
        bool OutOfRange => _engine == "r1" && (Count is not { } count || count < MinCount || count > Max);

        void Sync()
        {
            _engineHint.text = _engine == "r1"
                ? "Up to 20,000 polygons. Faster, and less than half the price."
                : "No polygon ceiling — the option for a dense mesh.";
            _r1.Visible(_engine == "r1");
            _n1.Visible(_engine == "n1");
            _countLabel.text = $"Polygon count ({MinCount:N0}–{Max:N0})";
            _countError.text = $"Out of range — {MinCount:N0} to {Max:N0}{(_polygonType == "quadrilateral" ? ", as quads allow half as many" : "")}.";
            _countError.Visible(OutOfRange);
            _countHint.text = _polygonType == "quadrilateral"
                ? "Quads halve the ceiling. Zetta R1 aims for your count, then settles on the cleanest topology near it."
                : "Zetta R1 aims for this count, then settles on the cleanest topology near it.";
            _levelHint.text = _level switch
            {
                "low" => "Fewest polygons — game-ready.",
                "high" => "Keeps the most detail.",
                _ => "A balance of the two.",
            };
            Refresh();
        }

        public override void OnContextChanged()
        {
            if (_polygons != null)
            {
                var quadsAllowed = Ctx?.Gates3D?.AllowsQuadMesh != false;
                _polygons.SetOptionEnabled("quadrilateral", quadsAllowed, quadsAllowed ? null : "Quad meshes are available from Basic.");
                _quadLock.Visible(!quadsAllowed);
                if (!quadsAllowed && _polygonType == "quadrilateral") _polygons.Set("triangle", true);
            }
            base.OnContextChanged();
        }

        protected override string Problem()
        {
            if (OutOfRange) return $"Choose a polygon count between {MinCount:N0} and {Max:N0}.";
            return null;
        }

        protected override Task<JObject> OptionsAsync()
        {
            var options = new JObject { ["model"] = _engine, ["polygonType"] = _polygonType };
            if (_engine == "r1")
            {
                if (OutOfRange || Count is not { } count) throw new StudioProblem($"Choose a polygon count between {MinCount:N0} and {Max:N0}.");
                options["faceCount"] = count;
                if (!_bake) options["bake"] = false;
            }
            else options["faceLevel"] = _level;
            return Task.FromResult(options);
        }
    }
}
