using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine.UIElements;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// Chooses the model a post-process tool works on: a finished row from the library, or a model
    /// in the project (its own generation if Zetta imported it, otherwise an upload of the .glb).
    /// </summary>
    public class SourcePicker : VisualElement
    {
        const int PageSize = 24;

        readonly Func<Generation, string> _refuse;
        readonly bool _anyFeature;
        readonly VisualElement _current;
        readonly VisualElement _browser;
        readonly VisualElement _grid;
        readonly Label _status;
        readonly Label _error;
        readonly Button _more;
        readonly Button _useSelected;
        readonly ToolbarSearch _search;
        int _page;
        bool _loading;
        bool _hasMore;
        readonly List<Generation> _rows = new List<Generation>();

        public Generation Source { get; private set; }
        public event Action<Generation> Changed;

        /// <param name="refuse">Why a row cannot be used by this tool, or null when it can.</param>
        /// <param name="anyFeature">List every finished row, not only generated and uploaded models.</param>
        public SourcePicker(Func<Generation, string> refuse, bool anyFeature = false)
        {
            _refuse = refuse;
            _anyFeature = anyFeature;
            AddToClassList("z-card");
            Add(ZettaUI.Title("Source model"));

            _current = new VisualElement().Classes("z-source");
            Add(_current);

            var buttons = new VisualElement().Classes("z-row", "z-row--wrap");
            buttons.Add(ZettaUI.Button("Pick from library", ToggleBrowser, "z-button--small"));
            _useSelected = ZettaUI.Button("Use selected project model", () => _ = UseSelected(), "z-button--small");
            _useSelected.tooltip = "Select a model in the Project window. Models imported from Zetta 3D are used directly; other .glb files are uploaded to your library (free).";
            buttons.Add(_useSelected);
            Add(buttons);

            _status = ZettaUI.Hint("").Visible(false);
            Add(_status);
            _error = new Label().Classes("z-error").Visible(false);
            Add(_error);

            _browser = new VisualElement().Classes("z-source-browser").Visible(false);
            _search = new ToolbarSearch("Search your library", () => Reload());
            _browser.Add(_search);
            _grid = new VisualElement().Classes("z-grid", "z-grid--compact");
            _browser.Add(_grid);
            _more = ZettaUI.Button("Load more", () => _ = LoadPage(_page + 1), "z-button--small", "z-button--ghost").Visible(false);
            _browser.Add(_more);
            Add(_browser);

            RenderCurrent();
        }

        public void Set(Generation generation)
        {
            if (generation != null)
            {
                var reason = _refuse?.Invoke(generation);
                if (reason != null)
                {
                    ShowError(reason);
                    return;
                }
            }
            ShowError(null);
            Source = generation;
            RenderCurrent();
            _browser.Visible(false);
            Changed?.Invoke(generation);
        }

        void ShowError(string message)
        {
            _error.text = message ?? "";
            _error.Visible(!string.IsNullOrEmpty(message));
        }

        void RenderCurrent()
        {
            _current.Clear();
            if (Source == null)
            {
                _current.Add(ZettaUI.Hint("Choose a finished model from your library, or select a model in the Project window."));
                return;
            }
            var thumb = new VisualElement().Classes("z-source-thumb");
            ZettaUI.LoadInto(thumb, Source.ThumbnailUrl, "gen-" + Source.Id);
            _current.Add(thumb);
            var text = new VisualElement().Classes("z-grow");
            text.Add(new Label(Source.DisplayTitle).Classes("z-tile-title"));
            var chips = new VisualElement().Classes("z-row", "z-row--wrap");
            chips.Add(ZettaUI.Chip(ZettaCatalog.FeatureLabel(Source.Feature)));
            if (Source.FaceCount is { } faces && faces > 0) chips.Add(ZettaUI.Chip($"{faces:N0} faces"));
            text.Add(chips);
            _current.Add(text);
            _current.Add(ZettaUI.Button("×", () => Set(null), "z-button--ghost", "z-button--small"));
        }

        void ToggleBrowser()
        {
            var show = _browser.style.display == DisplayStyle.None;
            _browser.Visible(show);
            if (show && _rows.Count == 0) Reload();
        }

        void Reload()
        {
            _rows.Clear();
            _grid.Clear();
            _ = LoadPage(1);
        }

        async Task LoadPage(int page)
        {
            if (_loading) return;
            _loading = true;
            _more.SetEnabled(false);
            ShowError(null);
            try
            {
                var result = await ZettaApi.ListGenerationsAsync(page, PageSize, _search.Value, null, new[] { "success" });
                _page = page;
                _hasMore = result.Total > page * PageSize;
                foreach (var row in result.Items.Where(r => r.IsSuccess && (_anyFeature || ZettaCatalog.ProcessableFeatures.Contains(r.Feature ?? ""))))
                {
                    _rows.Add(row);
                    _grid.Add(Tile(row));
                }
                if (_rows.Count == 0 && !_hasMore)
                    _grid.Add(ZettaUI.Hint(string.IsNullOrWhiteSpace(_search.Value)
                        ? "No finished models yet. Generate one first, or use a model from your project."
                        : "Nothing matches that search."));
            }
            catch (ZettaApiException e)
            {
                ShowError(ZettaCatalog.Explain(e));
            }
            finally
            {
                _loading = false;
                _more.Visible(_hasMore);
                _more.SetEnabled(true);
            }
        }

        VisualElement Tile(Generation row)
        {
            var tile = new VisualElement().Classes("z-tile", "z-tile--compact");
            var image = new VisualElement().Classes("z-tile-image");
            ZettaUI.LoadInto(image, row.ThumbnailUrl, "gen-" + row.Id);
            tile.Add(image);
            tile.Add(new Label(row.DisplayTitle).Classes("z-tile-title"));
            var reason = _refuse?.Invoke(row);
            if (reason != null)
            {
                tile.AddToClassList("z-tile--disabled");
                tile.tooltip = reason;
            }
            tile.RegisterCallback<ClickEvent>(_ => Set(row));
            return tile;
        }

        async Task UseSelected()
        {
            var path = StudioAssets.SelectedModelPath();
            if (path == null)
            {
                ShowError("Select a model in the Project window first.");
                return;
            }
            if (StudioAssets.GenerationIdOf(path) == null && !StudioAssets.IsGlb(path))
            {
                ShowError("That is not a .glb model. Select a .glb file, or a model imported from Zetta 3D.");
                return;
            }

            _useSelected.SetEnabled(false);
            ShowError(null);
            try
            {
                var generation = await StudioAssets.SourceFromAssetAsync(path, s =>
                {
                    _status.text = s;
                    _status.Visible(true);
                });
                Set(generation);
            }
            catch (ZettaApiException e)
            {
                ShowError(ZettaCatalog.Explain(e));
            }
            catch (Exception e)
            {
                ShowError(e.Message);
            }
            finally
            {
                _status.Visible(false);
                _useSelected.SetEnabled(true);
            }
        }
    }

    /// <summary>A search box that reports after typing stops (and on Enter).</summary>
    public class ToolbarSearch : VisualElement
    {
        readonly TextField _field;
        IVisualElementScheduledItem _pending;

        public string Value => (_field.value ?? "").Trim();

        public ToolbarSearch(string placeholder, Action changed)
        {
            AddToClassList("z-search");
            _field = ZettaUI.TextInput(placeholder, "", 200);
            _field.AddToClassList("z-grow");
            Add(_field);
            _field.RegisterValueChangedCallback(_ =>
            {
                _pending?.Pause();
                _pending = schedule.Execute(changed).StartingIn(400);
            });
            _field.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode != UnityEngine.KeyCode.Return && e.keyCode != UnityEngine.KeyCode.KeypadEnter) return;
                _pending?.Pause();
                changed();
            });
        }
    }
}
