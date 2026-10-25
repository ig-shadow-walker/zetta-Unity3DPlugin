using System;
using System.Threading.Tasks;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// The footer every job panel ends with: the live total, the balance, what (if anything) stops
    /// the job, the import toggles, and the button. Errors from the API land here in plain words.
    /// </summary>
    public class SubmitBar : VisualElement
    {
        readonly Label _cost;
        readonly Label _balance;
        readonly Label _problem;
        readonly Label _error;
        readonly Label _done;
        readonly VisualElement _toggles;
        public readonly Button Button;

        public bool AutoImport = true;
        public bool Place = ZettaProjectSettings.Instance.PlaceImportedInScene;

        int? _price;
        string _problemText;
        bool _busy;
        Func<Task> _action;

        public SubmitBar(string buttonText, bool showImportToggles = true)
        {
            AddToClassList("z-footer");

            _toggles = new VisualElement().Classes("z-row", "z-row--wrap");
            Toggle place = null;
            _toggles.Add(ZettaUI.Toggle("Import into project when ready", AutoImport, v =>
            {
                AutoImport = v;
                place?.SetEnabled(v);
            }, "Download the finished model into your project automatically."));
            place = ZettaUI.Toggle("Place in the open scene", Place, v => Place = v, "Drop the imported model into the open scene, sitting on the ground.");
            place.AddToClassList("z-toggle--gap");
            _toggles.Add(place);
            _toggles.Visible(showImportToggles);
            Add(_toggles);

            var costRow = new VisualElement().Classes("z-cost");
            costRow.Add(new Label("Total").Classes("z-muted"));
            _cost = new Label("—").Classes("z-cost-value");
            costRow.Add(_cost);
            Add(costRow);

            _balance = ZettaUI.Hint("");
            Add(_balance);
            _problem = ZettaUI.Hint("").Classes("z-hint--warn");
            Add(_problem);
            _error = new Label().Classes("z-error").Visible(false);
            Add(_error);
            _done = new Label().Classes("z-success").Visible(false);
            Add(_done);

            Button = ZettaUI.Button(buttonText, () => _ = Submit(), "z-button--signal", "z-button--wide");
            Add(Button);

            Refresh();
            ZettaAuth.Changed += Refresh;
            RegisterCallback<DetachFromPanelEvent>(_ => ZettaAuth.Changed -= Refresh);
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                ZettaAuth.Changed -= Refresh;
                ZettaAuth.Changed += Refresh;
            });
        }

        public string ButtonText
        {
            get => Button.text;
            set => Button.text = value;
        }

        /// <summary>What the button does. Exceptions become the inline error line.</summary>
        public void OnSubmit(Func<Task> action)
        {
            _action = action;
            Refresh();
        }

        public bool Busy => _busy;

        /// <summary>Null = unknown (price list not loaded, or a row missing).</summary>
        public void SetPrice(int? credits)
        {
            _price = credits;
            Refresh();
        }

        /// <summary>Why the button is disabled; null when the job can go.</summary>
        public void SetProblem(string problem)
        {
            _problemText = string.IsNullOrEmpty(problem) ? null : problem;
            Refresh();
        }

        public void ShowError(string message)
        {
            _error.text = message ?? "";
            _error.Visible(!string.IsNullOrEmpty(message));
        }

        public void ShowDone(string message)
        {
            _done.text = message ?? "";
            _done.Visible(!string.IsNullOrEmpty(message));
        }

        public void ClearMessages()
        {
            ShowError(null);
            ShowDone(null);
        }

        void Refresh()
        {
            _cost.text = _price == null ? "—" : _price == 0 ? "Free" : $"{_price:N0} credits";
            var credits = StudioContext.Shared.Credits;
            _balance.text = ZettaAuth.IsSignedIn ? $"You have {credits:N0} credits" : "";
            var shortfall = _price != null && ZettaAuth.IsSignedIn && _price > credits;
            _balance.EnableInClassList("z-hint--warn", shortfall);
            if (shortfall) _balance.text += $" — this needs {_price:N0}.";
            _problem.text = _problemText ?? "";
            _problem.Visible(_problemText != null);
            Button.SetEnabled(!_busy && _problemText == null && _action != null);
        }

        async Task Submit()
        {
            if (_busy || _action == null) return;
            _busy = true;
            ClearMessages();
            Refresh();
            try
            {
                await _action();
            }
            catch (ZettaApiException e)
            {
                ShowError(ZettaCatalog.Explain(e));
            }
            catch (StudioProblem e)
            {
                ShowError(e.Message);
            }
            catch (Exception e)
            {
                ShowError(e.Message);
            }
            finally
            {
                _busy = false;
                Refresh();
            }
        }

        /// <summary>Hands a created job to the tracker with this bar's import choices, and says what it cost.</summary>
        public ZettaJobs.Job Track(CreateGenerationResult result, string label)
        {
            var autoImport = AutoImport;
            var place = Place && autoImport;
            var job = ZettaJobs.Track(result.Generation, label, j =>
            {
                j.Source = "studio";
                j.AutoImport = autoImport;
                j.Place = place;
            });
            var spent = result.Quote?.Credits ?? 0;
            ShowDone(result.Charged && spent > 0
                ? $"Started — {spent:N0} credits spent. Follow it in the jobs list below."
                : "Started. Follow it in the jobs list below.");
            StudioContext.RefreshCredits();
            return job;
        }
    }

    /// <summary>An expected, user-facing problem (bad input, nothing selected) — shown inline, never logged.</summary>
    public class StudioProblem : Exception
    {
        public StudioProblem(string message) : base(message)
        {
        }
    }
}
