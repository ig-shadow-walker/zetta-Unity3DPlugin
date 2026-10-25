using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>Adds a skeleton and skin weights, ready to animate.</summary>
    public class AutoRigPanel : PostProcessPanel
    {
        static readonly string[] BodyHints =
        {
            "Two legs, two arms. Character presets.",
            "Dogs, horses, most mammals.",
            "Wings and a tail.",
            "A long spine, no limbs.",
            "Fins and a tail.",
            "Insects.",
            "Spiders and octopods.",
            "A body none of these describe.",
        };

        public override string Id => ZettaStudioWindow.RigTab;
        public override string Title => "Auto Rig";
        protected override string Intro => "Add a skeleton and skin weights so the model is ready to animate.";
        protected override string Code => "auto_rigging";
        protected override string ButtonText => "Auto Rig";

        string _body = "biped";
        string _spec = "standard";
        string _format = "glb";
        bool _detecting;
        bool _settingBody;

        PopupField<string> _bodyField;
        Label _bodyHint;
        Button _detect;
        Label _detectNote;
        PopupField<string> _specField;
        Label _specHint;
        Label _formatHint;

        protected override void BuildOptions(VisualElement c)
        {
            c.Add(ZettaUI.Title("Options"));

            var row = new VisualElement().Classes("z-row");
            _bodyField = ZettaUI.Dropdown(null, ZettaCatalog.RigTypes.ToList(), ZettaCatalog.RigTypeLabels.ToList(), _body, v =>
            {
                _body = v;
                if (!_settingBody) ShowNote(null);
                Sync();
            });
            _bodyField.AddToClassList("z-grow");
            row.Add(_bodyField);
            _detect = ZettaUI.Button("Detect", () => _ = Detect(), "z-button--small");
            _detect.tooltip = "Looks at the imported model and picks the body type for you.";
            row.Add(_detect);
            c.Add(ZettaUI.FieldLabel("Body"));
            c.Add(row);
            _bodyHint = ZettaUI.Hint("");
            c.Add(_bodyHint);
            _detectNote = ZettaUI.Hint("").Visible(false);
            c.Add(_detectNote);

            // A dropdown: six skeletons do not fit a segmented row in this panel's width.
            _specField = ZettaUI.Dropdown(null, ZettaCatalog.RigSpecs.ToList(), ZettaCatalog.RigSpecLabels.ToList(), _spec, v =>
            {
                _spec = v;
                Sync();
            });
            c.Add(Field("Bone naming", _specField));
            _specHint = ZettaUI.Hint("");
            c.Add(_specHint);

            var format = new ZettaUI.Segmented(new[] { "glb", "fbx" }, new[] { "GLB", "FBX" }, _format);
            format.Changed += v =>
            {
                _format = v;
                Sync();
            };
            c.Add(Field("Rigged file", format));
            _formatHint = ZettaUI.Hint("");
            c.Add(_formatHint);
            Sync();
        }

        protected override void OnSourceChanged()
        {
            ShowNote(null);
            Sync();
        }

        void ShowNote(string text)
        {
            if (_detectNote == null) return;
            _detectNote.text = text ?? "";
            _detectNote.Visible(!string.IsNullOrEmpty(text));
        }

        void Sync()
        {
            if (_bodyHint == null) return;
            _bodyHint.text = BodyHints[Math.Max(0, Array.IndexOf(ZettaCatalog.RigTypes, _body))];
            var humanoid = _body == ZettaCatalog.HumanoidRigType;
            _specField.SetEnabled(humanoid);
            _specHint.text = humanoid
                ? ZettaCatalog.RigSpecHints[Math.Max(0, Array.IndexOf(ZettaCatalog.RigSpecs, _spec))]
                : "Skeleton presets apply to humanoids.";
            _formatHint.text = _format == "fbx"
                ? "FBX imports into Unity with its skeleton — set its Rig to Humanoid for Mecanim."
                : "GLB imports with glTFast. For a Mecanim Humanoid avatar, choose FBX.";
            _detect.SetEnabled(!_detecting && Source != null);
            _detect.text = _detecting ? "Detecting…" : "Detect";
            Refresh();
        }

        protected override string Problem() => _detecting ? "Detecting the body type…" : null;

        async Task Detect()
        {
            if (Source == null || _detecting) return;
            var model = StudioAssets.FindImported(Source.Id);
            if (model == null)
            {
                ShowNote("Import the model into the project first to detect its body — or choose the body yourself.");
                return;
            }

            _detecting = true;
            ShowNote(null);
            Sync();
            try
            {
                var frames = ZettaCapture.AssetShots(model, 512);
                var (bodyType, _, reason) = await ZettaApi.RiggingPrecheckAsync(frames);
                var index = Array.IndexOf(ZettaCatalog.RigTypes, bodyType);
                if (index >= 0)
                {
                    _settingBody = true;
                    _body = bodyType;
                    _bodyField.value = bodyType;
                    _settingBody = false;
                    ShowNote($"Detected: {ZettaCatalog.RigTypeLabels[index]}. {reason}".Trim());
                }
                else ShowNote($"Could not tell what this is. {reason}".Trim());
            }
            catch (Exception)
            {
                ShowNote("That could not be detected. Choose the body yourself.");
            }
            finally
            {
                _settingBody = false;
                _detecting = false;
                Sync();
            }
        }

        protected override Task<JObject> OptionsAsync()
        {
            var options = new JObject();
            if (_body != "biped") options["rigType"] = _body;
            // A preset is a humanoid skeleton; on any other body the naming is not sent at all.
            if (_body == ZettaCatalog.HumanoidRigType && _spec != "standard" && ZettaCatalog.RigSpecs.Contains(_spec)) options["rigSpec"] = _spec;
            if (_format == "fbx") options["rigOutFormat"] = "fbx";
            return Task.FromResult(options);
        }
    }
}
