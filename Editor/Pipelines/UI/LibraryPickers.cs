using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine.UIElements;
using Zetta3D.Editor.Studio;

namespace Zetta3D.Editor.Pipelines.UI
{
    /// <summary>
    /// A dialog inside the window (the web's PanelDialog): the window dims, a card sits in the middle. Docked or not,
    /// it never leaves the window it belongs to. Escape or a click on the dimmed area closes it.
    /// </summary>
    public sealed class PipelineOverlay : VisualElement
    {
        public readonly VisualElement Card;
        public readonly VisualElement Body;
        public readonly VisualElement Footer;
        public event Action Closed;

        public PipelineOverlay(string title, string description = null, bool wide = false)
        {
            AddToClassList("z-pl-overlay");
            focusable = true;
            Card = new VisualElement().Classes("z-pl-overlay-card", wide ? "z-pl-overlay-card--wide" : null);
            Card.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.target == this) Close();
            });
            RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode != UnityEngine.KeyCode.Escape) return;
                Close();
                e.StopPropagation();
            });
            Card.Add(new Label(title).Classes("z-pl-overlay-title"));
            if (description != null) Card.Add(ZettaUI.Hint(description).Classes("z-pl-overlay-description"));
            var scroll = new ScrollView(ScrollViewMode.Vertical).Classes("z-pl-overlay-scroll");
            Body = scroll.contentContainer;
            Card.Add(scroll);
            Footer = new VisualElement().Classes("z-row", "z-pl-overlay-footer");
            Card.Add(Footer);
            Add(Card);
        }

        public PipelineOverlay ShowIn(VisualElement host)
        {
            host.Add(this);
            schedule.Execute(() => Focus());
            return this;
        }

        public void Close()
        {
            if (parent == null) return;
            RemoveFromHierarchy();
            Closed?.Invoke();
        }
    }

    /// <summary>A picture from the user's Image Studio library.</summary>
    public sealed class PickedImage
    {
        public long Id;
        public string Url;
        public string Prompt;

        public string Name => string.IsNullOrWhiteSpace(Prompt) ? $"Picture #{Id}" : Prompt.Length > 60 ? Prompt.Substring(0, 60) : Prompt;
    }

    /// <summary>"From your images" and "From your library" (image-picker.tsx, model-picker.tsx), paged, as overlays.</summary>
    public static class LibraryPickers
    {
        /// <summary>
        /// "Selected" on a picked tile — shown by the tile's own class, so it is there while the pointer is still over the
        /// tile (the hover colour alone hid the pick until the pointer moved away).
        /// </summary>
        static Label PickedChip() => (Label)ZettaUI.Chip("Selected", "signal").Classes("z-tile-check", "z-pick-check");

        const int PageSize = 24;

        /// <summary>
        /// Your images, 24 a page. <paramref name="onPick"/> returns whether the picture is chosen now (so a second press
        /// can leave it out); a single pick closes the picker.
        /// </summary>
        public static PipelineOverlay PickImages(VisualElement host, bool multiple, ICollection<long> chosen, Func<PickedImage, Task<bool>> onPick)
        {
            var overlay = new PipelineOverlay(multiple ? "Choose pictures" : "Choose a picture",
                multiple ? "From your images — each becomes its own run." : "From your images.", true);
            var grid = new VisualElement().Classes("z-grid");
            var status = ZettaUI.Hint("Loading your images…");
            overlay.Body.Add(status);
            overlay.Body.Add(grid);
            var pager = Pager(overlay, out var previous, out var next, out var pageLabel);
            overlay.Footer.Add(ZettaUI.Spacer());
            overlay.Footer.Add(ZettaUI.Button(multiple ? "Done" : "Cancel", overlay.Close, "z-button--small"));
            var page = 1;
            var busy = false;

            async Task Load()
            {
                if (busy) return;
                busy = true;
                status.text = "Loading your images…";
                status.Visible(true);
                grid.Clear();
                try
                {
                    var result = await ZettaApi.ListImagesAsync(page, PageSize);
                    var items = result.Items.Where(i => i.Images.Count > 0 && !string.IsNullOrEmpty(i.Images[0].Url)).ToList();
                    var pages = Math.Max(1, (int)Math.Ceiling(result.Total / (double)PageSize));
                    pager.Visible(pages > 1);
                    pageLabel.text = $"{page} / {pages}";
                    previous.SetEnabled(page > 1);
                    next.SetEnabled(page < pages);
                    status.text = "No images yet — upload one or make one instead.";
                    status.Visible(items.Count == 0);
                    foreach (var item in items)
                    {
                        var picked = new PickedImage { Id = item.Id, Url = item.Images[0].Url, Prompt = item.Prompt };
                        var tile = new VisualElement().Classes("z-tile", "z-tile--compact");
                        var image = PipelineUi.Thumb(picked.Url, "z-tile-image");
                        tile.Add(image);
                        tile.Add(PickedChip());
                        tile.tooltip = picked.Prompt ?? "";
                        tile.EnableInClassList("z-tile--picked", chosen.Contains(item.Id));
                        tile.RegisterCallback<ClickEvent>(async _ =>
                        {
                            var now = await onPick(picked);
                            tile.EnableInClassList("z-tile--picked", now);
                            if (!multiple) overlay.Close();
                        });
                        grid.Add(tile);
                    }
                }
                catch (ZettaApiException e)
                {
                    status.text = ZettaCatalog.Explain(e);
                }
                catch (Exception e)
                {
                    status.text = e.Message;
                }
                finally
                {
                    busy = false;
                }
            }

            previous.clicked += () =>
            {
                page = Math.Max(1, page - 1);
                _ = Load();
            };
            next.clicked += () =>
            {
                page++;
                _ = Load();
            };
            overlay.ShowIn(host);
            _ = Load();
            return overlay;
        }

        /// <summary>Your finished models (never a splat), searchable, 24 a page; each press toggles one in or out.</summary>
        public static PipelineOverlay PickModels(VisualElement host, ICollection<long> chosen, Func<Generation, bool> onPick)
        {
            var overlay = new PipelineOverlay("Choose models", "From your models — each becomes its own run. Press one again to leave it out.", true);
            var page = 1;
            var version = 0;
            Func<Task> reload = null;
            var search = new ToolbarSearch("Search by title", () =>
            {
                page = 1;
                _ = reload?.Invoke();
            });
            overlay.Body.Add(search);
            var grid = new VisualElement().Classes("z-grid");
            var status = ZettaUI.Hint("Loading your models…");
            overlay.Body.Add(status);
            overlay.Body.Add(grid);
            var pager = Pager(overlay, out var previous, out var next, out var pageLabel);
            overlay.Footer.Add(ZettaUI.Spacer());
            overlay.Footer.Add(ZettaUI.Button("Done", overlay.Close, "z-button--small"));

            async Task Load()
            {
                var mine = ++version;
                status.text = "Loading your models…";
                status.Visible(true);
                grid.Clear();
                try
                {
                    var term = search.Value;
                    var result = await ZettaApi.ListGenerationsAsync(page, PageSize, term == "" ? null : term, statuses: new[] { "success" });
                    if (mine != version) return;
                    var items = result.Items.Where(g => !g.IsSplat && !ZettaCatalog.IsSplatFeature(g.Feature)).ToList();
                    var pages = Math.Max(1, (int)Math.Ceiling(result.Total / (double)PageSize));
                    pager.Visible(pages > 1);
                    pageLabel.text = $"{page} / {pages}";
                    previous.SetEnabled(page > 1);
                    next.SetEnabled(page < pages);
                    status.text = term != "" ? "No model matches that." : "No finished models yet — generate one in the Studio first.";
                    status.Visible(items.Count == 0);
                    foreach (var item in items)
                    {
                        var tile = new VisualElement().Classes("z-tile", "z-tile--compact");
                        var image = new VisualElement().Classes("z-tile-image");
                        ZettaUI.LoadInto(image, item.ThumbnailUrl, "gen-" + item.Id);
                        tile.Add(image);
                        tile.Add(new Label(item.DisplayTitle).Classes("z-tile-title"));
                        tile.Add(PickedChip());
                        tile.EnableInClassList("z-tile--picked", chosen.Contains(item.Id));
                        var row = item;
                        tile.RegisterCallback<ClickEvent>(_ => tile.EnableInClassList("z-tile--picked", onPick(row)));
                        grid.Add(tile);
                    }
                }
                catch (ZettaApiException e)
                {
                    if (mine == version) status.text = ZettaCatalog.Explain(e);
                }
                catch (Exception e)
                {
                    if (mine == version) status.text = e.Message;
                }
            }

            reload = Load;
            previous.clicked += () =>
            {
                page = Math.Max(1, page - 1);
                _ = Load();
            };
            next.clicked += () =>
            {
                page++;
                _ = Load();
            };
            overlay.ShowIn(host);
            _ = Load();
            return overlay;
        }

        static VisualElement Pager(PipelineOverlay overlay, out Button previous, out Button next, out Label label)
        {
            var pager = new VisualElement().Classes("z-row", "z-pl-pager");
            previous = ZettaUI.Button("‹ Previous", null, "z-button--small", "z-button--ghost");
            label = ZettaUI.Hint("1 / 1");
            next = ZettaUI.Button("Next ›", null, "z-button--small", "z-button--ghost");
            pager.Add(previous);
            pager.Add(label);
            pager.Add(next);
            pager.Visible(false);
            overlay.Footer.Add(pager);
            return pager;
        }
    }
}
