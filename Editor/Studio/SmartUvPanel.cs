using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// Smart UV (the web Studio's smart-uv-panel.tsx): a clean new UV layout, with the model's textures, rig
    /// and animation carried across onto it. The result is a new library row (uvUnwrap); the original stays.
    /// The server refuses a model over 80,000 triangles, so that is said before the press.
    /// </summary>
    public class SmartUvPanel : PostProcessPanel
    {
        static readonly string[] Orientations = { "as-is", "+x", "-x", "+y", "-y" };
        static readonly string[] OrientationLabels = { "As is", "+X", "−X", "+Y", "−Y" };

        public override string Id => StudioPage.SmartUvTab;
        public override string Title => "Smart UV";
        protected override string Intro => "Lays the model’s UVs out again as a clean new layout. Its textures, rig and animation are carried across onto it.";
        protected override string Code => "smart_uv";
        protected override string ButtonText => "Smart UV";

        string _orientation = "as-is";
        bool _bake = true;
        bool _compress;

        Label _count;
        Label _bakeHint;

        /// <summary>The source's triangle count: quads count twice. Null when the row does not say.</summary>
        public static long? Triangles(Generation row)
        {
            if (row?.FaceCount is not { } faces || faces <= 0) return null;
            return row.PolygonType == "quadrilateral" ? faces * 2 : faces;
        }

        protected override string Refuse(Generation row)
        {
            if (row.Feature == "uvUnwrap") return "This model already has a Smart UV layout.";
            return base.Refuse(row);
        }

        protected override void BuildOptions(VisualElement c)
        {
            c.Add(ZettaUI.Title("Options"));
            _count = ZettaUI.Hint("");
            c.Add(_count);

            var orientation = new ZettaUI.Segmented(Orientations, OrientationLabels, _orientation);
            orientation.Changed += v => _orientation = v;
            c.Add(Field("Orientation", orientation, "Which way the model faces in the exported file."));

            c.Add(ZettaUI.Toggle("Bake textures", _bake, v =>
            {
                _bake = v;
                Sync();
            }));
            _bakeHint = ZettaUI.Hint("");
            c.Add(_bakeHint);
            c.Add(ZettaUI.Toggle("Compress geometry", _compress, v => _compress = v));
            c.Add(ZettaUI.Hint("A smaller file, through mesh compression."));
            c.Add(ZettaUI.Hint("The result comes with a picture of its UV layout, which can be saved into the project beside the model.").Classes("z-section-gap"));
            Sync();
        }

        protected override void OnSourceChanged() => Sync();

        void Sync()
        {
            if (_count == null) return;
            var triangles = Triangles(Source);
            _count.text = triangles is { } n
                ? n > ZettaCatalog.SmartUvMaxTriangles ? "" : $"{n:N0} / {ZettaCatalog.SmartUvMaxTriangles:N0} triangles"
                : $"Takes models up to {ZettaCatalog.SmartUvMaxTriangles:N0} triangles.";
            _count.Visible(_count.text != "");
            _bakeHint.text = _bake
                ? "Keeps the textures, baked onto the new layout."
                : "Returns the geometry with its new UVs only — no textures.";
            Refresh();
        }

        protected override string Problem()
        {
            if (Triangles(Source) is { } n && n > ZettaCatalog.SmartUvMaxTriangles)
                return $"Smart UV takes models up to {ZettaCatalog.SmartUvMaxTriangles:N0} triangles — this one has {n:N0}. Run Retopology first to bring it down.";
            return null;
        }

        /// <summary>Defaults travel as silence: only an untick, a turn or a tick is sent.</summary>
        public static JObject Options(string orientation, bool bake, bool compress)
        {
            var options = new JObject();
            if (!bake) options["bake"] = false;
            if (orientation != "as-is") options["exportOrientation"] = orientation;
            if (compress) options["compressGeometry"] = true;
            return options;
        }

        protected override Task<JObject> OptionsAsync()
        {
            if (Problem() is { } problem) throw new StudioProblem(problem);
            return Task.FromResult(Options(_orientation, _bake, _compress));
        }
    }
}
