using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Zetta3D.Editor.Studio;

namespace Zetta3D.Editor.Pipelines.UI
{
    /// <summary>
    /// RUN ON… (run-dialog.tsx): the seeds to run on — pictures or models chosen now, each its own run (a batch of up to
    /// 20) — and Auto or Manual. IT MAKES THE RUNS; IT DOES NOT START THEM: each opens in Runs not started (a draft), with
    /// any step set on each run marked Needs input; it starts on its own Start.
    /// </summary>
    public static class RunDialog
    {
        /// <summary>The most runs one press makes — the server's own cap.</summary>
        public const int MaxBatch = 20;

        public static PipelineOverlay Show(VisualElement host, PipelineEditorView editor, PipelineGraph graph, int? creditsPerRun,
            Func<List<RunSummary>, bool, Task> created)
        {
            var seedNode = graph.Seed;
            var pictures = seedNode?.Kind != NodeKinds.SeedModel;
            var overlay = new PipelineOverlay("Run on…",
                $"Each {(pictures ? "picture" : "model")} gets its own run of this pipeline, up to {MaxBatch} at once. The runs open ready to check — each starts when you press Start.");
            var seeds = new List<PipelineSeed>();
            var uploading = 0;
            var mode = RunStatuses.ModeManual;
            var busy = false;

            var grid = new VisualElement().Classes("z-row", "z-row--wrap", "z-pl-seeds");
            overlay.Body.Add(grid);
            var drop = new Label(pictures ? "Drop pictures here — from Finder or the Project window." : "Drop a .glb (or a model imported from Zetta 3D) here.").Classes("z-pl-dropzone");
            overlay.Body.Add(drop);
            var buttons = new VisualElement().Classes("z-row", "z-row--wrap");
            overlay.Body.Add(buttons);
            var problem = new Label().Classes("z-error").Visible(false);

            var modes = new VisualElement().Classes("z-row", "z-pl-modes");
            var import = ZettaUI.Toggle("Import results into the project when done", true, null,
                "Each run's final models are imported into your models folder as they finish (never a splat).");

            var footer = overlay.Footer;
            var total = new Label().Classes("z-pl-estimate");
            footer.Add(total);
            footer.Add(ZettaUI.Spacer());
            footer.Add(ZettaUI.Button("Cancel", overlay.Close, "z-button--small", "z-button--ghost"));
            var create = ZettaUI.Button("Create the run", null, "z-button--small", "z-button--primary");
            footer.Add(create);

            void Fail(string message)
            {
                problem.text = message ?? "";
                problem.Visible(!string.IsNullOrEmpty(message));
            }

            void Render()
            {
                grid.Clear();
                for (var i = 0; i < seeds.Count; i++)
                {
                    var seed = seeds[i];
                    var tile = new VisualElement().Classes("z-pl-seed");
                    var thumb = seed.Thumbnail != null ? PipelineUi.Thumb(seed.Thumbnail, "z-pl-thumb--seed-tile") : PipelineUi.Thumb(seed.ThumbnailUrl, "z-pl-thumb--seed-tile");
                    tile.Add(thumb);
                    tile.Add(new Label(seed.Name).Classes("z-pl-seed-name"));
                    var index = i;
                    var remove = ZettaUI.Button("✕", () =>
                    {
                        seeds.RemoveAt(index);
                        Render();
                    }, "z-mini-remove");
                    remove.tooltip = $"Remove {seed.Name}";
                    tile.Add(remove);
                    grid.Add(tile);
                }
                if (uploading > 0) grid.Add(new Label("Uploading…").Classes("z-pl-seed", "z-pl-seed--busy"));
                var full = seeds.Count + uploading >= MaxBatch;
                foreach (var button in buttons.Query<Button>().ToList()) button.SetEnabled(!full && !busy);
                total.text = creditsPerRun == null || seeds.Count == 0 ? "" : $"Up to {creditsPerRun * seeds.Count} credits";
                create.text = busy ? "Creating…" : seeds.Count > 1 ? $"Create {seeds.Count} runs" : "Create the run";
                create.SetEnabled(!busy && seeds.Count > 0 && uploading == 0);
            }

            void AddSeed(PipelineSeed seed)
            {
                if (seeds.Count >= MaxBatch) return;
                seeds.Add(seed);
                Render();
            }

            async Task UploadPicture(byte[] bytes, string contentType, string name)
            {
                if (seeds.Count + uploading >= MaxBatch) return;
                if (StudioAssets.CheckImage(bytes, contentType) is { } bad)
                {
                    Fail(bad);
                    return;
                }
                uploading++;
                Render();
                try
                {
                    AddSeed(await PipelineSeed.FromUploadAsync(bytes, contentType, name));
                }
                catch (ZettaApiException e)
                {
                    Fail(ZettaCatalog.Explain(e));
                }
                catch (Exception e)
                {
                    Fail(e.Message ?? "A picture could not be uploaded.");
                }
                finally
                {
                    uploading--;
                    Render();
                }
            }

            async Task UploadFile(string path)
            {
                var type = StudioAssets.ContentTypeOf(path);
                if (type == null || !type.StartsWith("image/", StringComparison.Ordinal))
                {
                    Fail("Choose a PNG, JPEG or WebP picture.");
                    return;
                }
                await UploadPicture(File.ReadAllBytes(path), type, Path.GetFileName(path));
            }

            async Task UseTexture(Texture2D texture)
            {
                var path = AssetDatabase.GetAssetPath(texture);
                var type = string.IsNullOrEmpty(path) ? null : StudioAssets.ContentTypeOf(path);
                if (type is "image/png" or "image/jpeg") await UploadFile(path);
                else await UploadPicture(ZettaCapture.EncodeReadable(texture), "image/png", texture.name + ".png");
            }

            async Task UseModelAsset(string path)
            {
                if (StudioAssets.GenerationIdOf(path) == null && !StudioAssets.IsGlb(path))
                {
                    Fail("That is not a .glb model. Select a .glb file, or a model imported from Zetta 3D.");
                    return;
                }
                if (seeds.Count + uploading >= MaxBatch) return;
                uploading++;
                Render();
                try
                {
                    var generation = await StudioAssets.SourceFromAssetAsync(path);
                    if (seeds.Any(s => s.Request["gen3dPostId"]?.ToObject<long>() == generation.Id)) return;
                    AddSeed(PipelineSeed.FromModel(generation));
                }
                catch (ZettaApiException e)
                {
                    Fail(ZettaCatalog.Explain(e));
                }
                catch (Exception e)
                {
                    Fail(e.Message);
                }
                finally
                {
                    uploading--;
                    Render();
                }
            }

            async void FromProject()
            {
                Fail(null);
                if (pictures)
                {
                    var textures = Selection.objects.OfType<Texture2D>().ToList();
                    if (textures.Count == 0)
                    {
                        Fail("Select one or more pictures (textures) in the Project window first.");
                        return;
                    }
                    foreach (var texture in textures.Take(MaxBatch - seeds.Count)) await UseTexture(texture);
                }
                else
                {
                    var paths = Selection.objects.Select(AssetDatabase.GetAssetPath).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
                    if (paths.Count == 0 && StudioAssets.SelectedModelPath() is { } one) paths.Add(one);
                    if (paths.Count == 0)
                    {
                        Fail("Select a model in the Project window first.");
                        return;
                    }
                    foreach (var path in paths.Take(MaxBatch - seeds.Count)) await UseModelAsset(path);
                }
            }

            if (pictures)
            {
                buttons.Add(ZettaUI.Button("Upload pictures", () =>
                {
                    Fail(null);
                    var path = EditorUtility.OpenFilePanelWithFilters("Choose a picture", "", new[] { "Images", "png,jpg,jpeg,webp" });
                    if (!string.IsNullOrEmpty(path)) _ = UploadFile(path);
                }, "z-button--small"));
                buttons.Add(ZettaUI.Button("From your images", () =>
                {
                    Fail(null);
                    var chosen = seeds.Select(s => s.Request["gen2dPostId"]).Where(t => t != null).Select(t => t.ToObject<long>()).ToList();
                    LibraryPickers.PickImages(host, true, chosen, picked =>
                    {
                        var existing = seeds.FindIndex(s => s.Request["gen2dPostId"]?.ToObject<long>() == picked.Id);
                        if (existing >= 0)
                        {
                            seeds.RemoveAt(existing);
                            Render();
                            return Task.FromResult(false);
                        }
                        if (seeds.Count >= MaxBatch) return Task.FromResult(false);
                        AddSeed(PipelineSeed.FromImagePost(picked.Id, picked.Name, picked.Url));
                        return Task.FromResult(true);
                    });
                }, "z-button--small"));
                var project = ZettaUI.Button("From the project", FromProject, "z-button--small");
                project.tooltip = "Uses the pictures selected in the Project window.";
                buttons.Add(project);
            }
            else
            {
                buttons.Add(ZettaUI.Button("From your library", () =>
                {
                    Fail(null);
                    var chosen = seeds.Select(s => s.Request["gen3dPostId"]).Where(t => t != null).Select(t => t.ToObject<long>()).ToList();
                    LibraryPickers.PickModels(host, chosen, generation =>
                    {
                        var existing = seeds.FindIndex(s => s.Request["gen3dPostId"]?.ToObject<long>() == generation.Id);
                        if (existing >= 0)
                        {
                            seeds.RemoveAt(existing);
                            Render();
                            return false;
                        }
                        if (seeds.Count >= MaxBatch) return false;
                        AddSeed(PipelineSeed.FromModel(generation));
                        return true;
                    });
                }, "z-button--small"));
                var project = ZettaUI.Button("From the project", FromProject, "z-button--small");
                project.tooltip = "Uses the model selected in the Project window: one imported from Zetta 3D as it is, any other .glb uploaded to your library (free).";
                buttons.Add(project);
            }

            // Pictures or models dragged in from Finder or the Project window.
            overlay.Card.RegisterCallback<DragUpdatedEvent>(_ =>
            {
                if (DragAndDrop.paths.Length > 0 || DragAndDrop.objectReferences.Length > 0) DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            });
            overlay.Card.RegisterCallback<DragPerformEvent>(async _ =>
            {
                DragAndDrop.AcceptDrag();
                Fail(null);
                var textures = DragAndDrop.objectReferences.OfType<Texture2D>().ToList();
                var paths = DragAndDrop.paths.ToList();
                if (pictures)
                {
                    if (textures.Count > 0)
                        foreach (var texture in textures) await UseTexture(texture);
                    else
                        foreach (var path in paths) await UploadFile(path);
                }
                else
                    foreach (var path in paths) await UseModelAsset(path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal) ? path : ProjectRelative(path));
            });

            overlay.Body.Add(ZettaUI.FieldLabel("How it runs"));
            Button manualCard = null, autoCard = null;
            VisualElement ModeCard(string value)
            {
                var card = new Button(() =>
                {
                    mode = value;
                    manualCard.EnableInClassList("z-pl-mode--active", mode == RunStatuses.ModeManual);
                    autoCard.EnableInClassList("z-pl-mode--active", mode == RunStatuses.ModeAuto);
                });
                card.AddToClassList("z-pl-mode");
                card.Add(new Label(RunStatuses.ModeLabel(value)).Classes("z-pl-mode-name"));
                card.Add(new Label(RunStatuses.ModeHint(value)).Classes("z-pl-mode-hint"));
                return card;
            }
            manualCard = (Button)ModeCard(RunStatuses.ModeManual);
            autoCard = (Button)ModeCard(RunStatuses.ModeAuto);
            manualCard.AddToClassList("z-pl-mode--active");
            modes.Add(manualCard);
            modes.Add(autoCard);
            overlay.Body.Add(modes);
            overlay.Body.Add(import);
            overlay.Body.Add(problem);

            create.clicked += async () =>
            {
                if (busy || seeds.Count == 0 || uploading > 0) return;
                busy = true;
                Fail(null);
                Render();
                try
                {
                    var runGraph = RequestBuilder.WithRequests(RequestBuilder.ForNewRun(graph), editor.Gates);
                    var runs = await PipelinesApi.BatchAsync(editor.PipelineId, mode, seeds, runGraph, true);
                    overlay.Close();
                    await created(runs, import.value);
                }
                catch (ZettaApiException e)
                {
                    Fail(ZettaCatalog.Explain(e));
                }
                catch (Exception e)
                {
                    Fail(e.Message);
                }
                finally
                {
                    busy = false;
                    if (overlay.parent != null) Render();
                }
            };

            Render();
            return overlay.ShowIn(host);
        }

        static string ProjectRelative(string fullPath)
        {
            var root = Path.GetFullPath(Application.dataPath + "/..").Replace('\\', '/').TrimEnd('/') + "/";
            var path = fullPath.Replace('\\', '/');
            return path.StartsWith(root, StringComparison.Ordinal) ? path.Substring(root.Length) : path;
        }
    }
}
