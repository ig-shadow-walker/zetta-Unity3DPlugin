using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// A finishing tool (Retopology, Segment, Texture, Auto Rig, Convert): a source model, the
    /// tool's options, the live price, and a job body with only the fields the web Studio sends.
    /// </summary>
    public abstract class PostProcessPanel : StudioPanel
    {
        public const string LockedText = "The finishing tools are available on a paid plan. Generate 3D works on every plan.";

        protected SourcePicker Picker;
        protected SubmitBar Bar;
        Label _lock;

        protected Generation Source => Picker?.Source;

        /// <summary>One line under the tab title saying what the tool does.</summary>
        protected abstract string Intro { get; }

        /// <summary>The operation code the job runs (and is priced by).</summary>
        protected abstract string Code { get; }

        /// <summary>The model the price row is looked up with (only retopology has model rows).</summary>
        protected virtual string PriceModel => null;

        /// <summary>The button text, without the price.</summary>
        protected abstract string ButtonText { get; }

        /// <summary>The job's label in the jobs list.</summary>
        protected virtual string JobLabel => $"{Title} · {Source?.DisplayTitle}";

        /// <summary>Accepts rows of any finished kind (Convert), not only the ones the tools list offers.</summary>
        protected virtual bool AnyFeature => false;

        protected abstract void BuildOptions(VisualElement container);

        /// <summary>The job's fields besides spec/sourcePostId, or throws a <see cref="StudioProblem"/>.</summary>
        protected abstract Task<JObject> OptionsAsync();

        /// <summary>Why the job cannot go yet, or null.</summary>
        protected virtual string Problem() => null;

        /// <summary>Why this tool refuses a row, or null.</summary>
        protected virtual string Refuse(Generation row)
        {
            if (!row.IsSuccess) return "That model is not finished yet.";
            if (!AnyFeature && !ZettaCatalog.ProcessableFeatures.Contains(row.Feature ?? ""))
                return $"{ZettaCatalog.FeatureLabel(row.Feature)} results cannot be used here. Pick a generated or uploaded model.";
            return null;
        }

        protected override void Build(VisualElement root)
        {
            var scroll = Scroll();
            scroll.Add(ZettaUI.Hint(Intro).Classes("z-intro"));
            _lock = new Label(LockedText).Classes("z-lock").Visible(false);
            scroll.Add(_lock);

            Picker = new SourcePicker(Refuse, AnyFeature);
            Picker.Changed += _ =>
            {
                OnSourceChanged();
                Refresh();
            };
            scroll.Add(Picker);

            var options = ZettaUI.Card();
            BuildOptions(options);
            scroll.Add(options);
            root.Add(scroll);

            Bar = new SubmitBar(ButtonText);
            Bar.OnSubmit(Submit);
            root.Add(Bar);
            Refresh();
        }

        public void SetSource(Generation generation)
        {
            if (Picker == null) _ = Root;
            Picker.Set(generation);
        }

        protected virtual void OnSourceChanged()
        {
        }

        public override void OnContextChanged() => Refresh();

        /// <summary>Re-price and re-check; call after any option changes.</summary>
        protected void Refresh()
        {
            if (Bar == null) return;
            var locked = Ctx?.Gates3D?.AllowsPostProcess == false;
            _lock.Visible(locked);
            Bar.ButtonText = ButtonText;
            Bar.SetPrice(Ctx?.Prices?.Price(Code, PriceModel));
            Bar.SetProblem(locked ? "Upgrade to use the finishing tools."
                : Source == null ? "Choose a source model."
                : Problem());
        }

        async Task Submit()
        {
            if (Source == null) throw new StudioProblem("Choose a source model.");
            var source = Source;
            var options = await OptionsAsync();
            var body = ZettaRequests.PostProcess(Code, source.Id, options);
            var result = await ZettaApi.CreateGenerationAsync(body);
            Bar.Track(result, JobLabel);
        }

        // ─── Small builders shared by the tools ──────────────────────────────────────

        protected static VisualElement Field(string label, VisualElement control, string hint = null)
        {
            var box = new VisualElement().Classes("z-field-group");
            if (!string.IsNullOrEmpty(label)) box.Add(ZettaUI.FieldLabel(label));
            box.Add(control);
            if (!string.IsNullOrEmpty(hint)) box.Add(ZettaUI.Hint(hint));
            return box;
        }
    }
}
