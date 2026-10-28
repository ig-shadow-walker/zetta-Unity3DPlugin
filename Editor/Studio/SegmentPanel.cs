using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>Splits a model into named parts: Segment (as it is) or Smart Segment (rebuilds as it splits).</summary>
    public class SegmentPanel : PostProcessPanel
    {
        public override string Id => StudioPage.SegmentTab;
        public override string Title => "Segment";
        protected override string Intro => "Split a model into separate, named parts — each one imports as its own object.";
        protected override string Code => _mode == "smart" ? "smart_segmentation" : "ai_segmentation";
        protected override string ButtonText => _mode == "smart" ? "Smart Segment" : "Segment";

        string _mode = "segment";
        string _granularity = "balanced";
        bool _splitConnectivity = true;

        Label _modeHint;
        VisualElement _segmentOnly;
        VisualElement _smartOnly;
        TextField _prompt;

        protected override void BuildOptions(VisualElement c)
        {
            c.Add(ZettaUI.Title("Options"));
            var mode = new ZettaUI.Segmented(new[] { "segment", "smart" }, new[] { "Segment", "Smart Segment" }, _mode);
            _modeHint = ZettaUI.Hint("");
            mode.Changed += v =>
            {
                _mode = v;
                Sync();
            };
            c.Add(Field("Segmentation type", mode));
            c.Add(_modeHint);

            var granularity = new ZettaUI.Segmented(new[] { "simple", "balanced", "detailed" }, new[] { "Few parts", "Balanced", "Max parts" }, _granularity);
            granularity.Changed += v =>
            {
                _granularity = v;
                Refresh();
            };
            c.Add(Field("How many parts", granularity));

            _segmentOnly = new VisualElement();
            _segmentOnly.Add(ZettaUI.Toggle("Also split pieces that are not joined", _splitConnectivity, v => _splitConnectivity = v,
                "Pieces that do not touch each other become separate parts."));
            c.Add(_segmentOnly);

            _smartOnly = new VisualElement();
            _prompt = ZettaUI.TextArea("e.g. game character with sword and armor", "", 1024);
            _smartOnly.Add(Field("What is this? (Optional)", _prompt, "Naming the parts helps it find them."));
            c.Add(_smartOnly);
            Sync();
        }

        void Sync()
        {
            _modeHint.text = _mode == "smart"
                ? "Rebuilds as it splits, for cleaner parts on a rough or incomplete mesh."
                : "Splits the model you have into named parts.";
            _segmentOnly.Visible(_mode == "segment");
            _smartOnly.Visible(_mode == "smart");
            Refresh();
        }

        protected override Task<JObject> OptionsAsync()
        {
            var options = new JObject { ["segmentGranularity"] = _granularity };
            if (_mode == "segment")
            {
                if (!_splitConnectivity) options["splitByConnectivity"] = false;
            }
            else
            {
                var prompt = (_prompt.value ?? "").Trim();
                if (prompt != "") options["prompt"] = prompt;
            }
            return Task.FromResult(options);
        }
    }
}
