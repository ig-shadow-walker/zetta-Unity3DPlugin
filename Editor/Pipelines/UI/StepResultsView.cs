using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Zetta3D.Editor.Studio;

namespace Zetta3D.Editor.Pipelines.UI
{
    /// <summary>What a step's results can ask the run to do.</summary>
    public sealed class StepActions
    {
        public bool Busy;
        public Action<PipelineRunStep> Run;
        /// <summary>(step, with the settings shown above).</summary>
        public Action<PipelineRunStep, bool> Retry;
        public Action<PipelineRunStep, List<string>, List<string>> ConfirmParts;
        public Action<string> Toast;
        public long PipelineId;
    }

    /// <summary>
    /// WHAT ONE NODE DID IN THE RUN ON SCREEN (step-results.tsx): each of its steps (one per part downstream of
    /// <c>each</c>) with its result — pictures (saved into the project on a press), the model (imported into the project,
    /// or imported and placed), a waiting step's Run, a failed step's error and Retry, Isolate Parts' choice of parts.
    /// </summary>
    public static class StepResultsView
    {
        public static VisualElement Build(PipelineNode node, PipelineRun run, StepActions actions)
        {
            var box = new VisualElement().Classes("z-pl-results");
            var steps = run.StepsOf(node.Id).ToList();
            if (steps.Count == 0)
            {
                box.Add(ZettaUI.Text(run.Status == RunStatuses.Draft
                    ? "This run hasn’t started. Check this step’s settings for this run below, then press Start."
                    : run.Status is RunStatuses.Done or RunStatuses.Failed or RunStatuses.Cancelled
                        ? "This step did not run — the step before it did not reach it."
                        : "Not reached yet. It starts when the step before it finishes.", "z-muted"));
                return box;
            }
            foreach (var step in steps)
            {
                var card = new VisualElement().Classes("z-pl-step");
                var head = new VisualElement().Classes("z-row");
                head.Add(new Label(string.IsNullOrEmpty(step.PartKey) ? NodeCatalog.Label(node.Kind) : run.PartLabel(step.PartKey)).Classes("z-pl-step-title", "z-grow"));
                var status = new Label(NodeRunState.StatusLabel(step.Status) + (step.Credits > 0 ? $" · {step.Credits} cr" : "")).Classes("z-pl-step-status");
                if (step.Status == StepStatuses.Failed) status.AddToClassList("z-error");
                head.Add(status);
                card.Add(head);
                card.Add(Body(node, step, run, actions));
                box.Add(card);
            }
            return box;
        }

        static VisualElement Body(PipelineNode node, PipelineRunStep step, PipelineRun run, StepActions actions)
        {
            switch (step.Status)
            {
                case StepStatuses.Waiting:
                {
                    var button = ZettaUI.Button("▶ Run this step", () => actions.Run?.Invoke(step), "z-button--small", "z-button--primary");
                    button.SetEnabled(!actions.Busy);
                    return button;
                }
                case StepStatuses.Review:
                    return IsolateReview(step, actions);
                case StepStatuses.Failed:
                {
                    var box = new VisualElement();
                    box.Add(PipelineUi.Note(step.ErrorMessage ?? "This step failed.", "error"));
                    var row = new VisualElement().Classes("z-row", "z-row--wrap");
                    var retry = ZettaUI.Button("↻ Retry", () => actions.Retry?.Invoke(step, false), "z-button--small");
                    var withSettings = ZettaUI.Button("Retry with the settings above", () => actions.Retry?.Invoke(step, true), "z-button--small", "z-button--ghost");
                    retry.SetEnabled(!actions.Busy);
                    withSettings.SetEnabled(!actions.Busy);
                    row.Add(retry);
                    row.Add(withSettings);
                    box.Add(row);
                    return box;
                }
                case StepStatuses.Running:
                case StepStatuses.Ready:
                case StepStatuses.Submitted:
                {
                    var job = step.Generation?.Status;
                    return ZettaUI.Hint(step.Status == StepStatuses.Submitted && job != null ? $"The 3D job is {job}." : "Working…");
                }
            }
            return Output(node, step, actions);
        }

        // ─── Outputs ────────────────────────────────────────────────────────────────

        static VisualElement Output(PipelineNode node, PipelineRunStep step, StepActions actions)
        {
            switch (step.Output)
            {
                case PictureStepOutput picture:
                    return Pictures(new[] { (picture.Picture, "Result") }, node, actions);
                case ViewsStepOutput views:
                    return Pictures(views.Views.Select(v => (v.Picture, v.ViewType)), node, actions);
                case IsolatedStepOutput isolated:
                    return Pictures(new[] { (isolated.Character, "Character") }.Concat(isolated.Parts.Select(p => (p.Picture, p.Label))), node, actions);
                case MeshStepOutput mesh:
                    return Model(step.Generation, mesh.Gen3dPostId, actions);
                case SplatStepOutput _:
                case FileStepOutput _:
                {
                    var box = new VisualElement();
                    box.Add(ZettaUI.Hint(step.Output is SplatStepOutput ? "A splat — not available in Unity." : "A file export — not available in Unity."));
                    box.Add(ZettaUI.Button("Open on web", () => Application.OpenURL($"{ZettaConfig.SiteBase}/pipelines?pipeline={actions.PipelineId}&view=runs"), "z-button--small", "z-button--ghost"));
                    return box;
                }
                default:
                    return new VisualElement();
            }
        }

        static VisualElement Pictures(IEnumerable<(SignedPicture picture, string label)> pictures, PipelineNode node, StepActions actions)
        {
            var grid = new VisualElement().Classes("z-row", "z-row--wrap", "z-pl-pictures");
            foreach (var (picture, label) in pictures)
            {
                if (string.IsNullOrEmpty(picture?.Url)) continue;
                var figure = new VisualElement().Classes("z-pl-figure");
                figure.Add(PipelineUi.Thumb(picture.Url, "z-pl-thumb--result"));
                figure.Add(new Label(label).Classes("z-pl-caption"));
                var url = picture.Url;
                var name = $"{node.DisplayName} {label}";
                Button save = null;
                save = ZettaUI.Button("Save to project", async () =>
                {
                    save.SetEnabled(false);
                    try
                    {
                        var bytes = await ZettaHttp.DownloadAsync(url);
                        var type = bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? "image/jpeg" : bytes.Length > 12 && bytes[8] == 'W' && bytes[9] == 'E' ? "image/webp" : "image/png";
                        var path = StudioAssets.SaveImage(bytes, type, name, "default");
                        EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path));
                        actions.Toast?.Invoke($"Saved to {path}");
                    }
                    catch (Exception e)
                    {
                        actions.Toast?.Invoke("The picture could not be saved: " + (e is ZettaApiException api ? ZettaCatalog.Explain(api) : e.Message));
                    }
                    finally
                    {
                        save.SetEnabled(true);
                    }
                }, "z-button--small", "z-button--ghost");
                figure.Add(save);
                grid.Add(figure);
            }
            return grid;
        }

        /// <summary>The model a step made: its library row, and the Unity way to use it — import, import and place.</summary>
        static VisualElement Model(Generation generation, long postId, StepActions actions)
        {
            var box = new VisualElement();
            var row = new VisualElement().Classes("z-row");
            var thumb = new VisualElement().Classes("z-pl-thumb", "z-pl-thumb--model");
            if (generation != null) ZettaUI.LoadInto(thumb, generation.ThumbnailUrl, "gen-" + generation.Id);
            row.Add(thumb);
            var text = new VisualElement().Classes("z-grow");
            text.Add(new Label(generation?.DisplayTitle ?? $"#{postId}").Classes("z-pl-step-title"));
            text.Add(ZettaUI.Hint("Model · in your library"));
            row.Add(text);
            box.Add(row);

            var properties = new VisualElement();
            var buttons = new VisualElement().Classes("z-row", "z-row--wrap");
            if (generation == null || !generation.IsSuccess)
            {
                box.Add(ZettaUI.Hint("The model is still being prepared."));
                return box;
            }
            if (generation.IsSplat)
            {
                box.Add(ZettaUI.Hint(ZettaImporter.SplatUnsupported));
                return box;
            }

            var progress = new ZettaUI.ProgressBar().Visible(false);

            async Task Import(Button button)
            {
                var place = ZettaProjectSettings.Instance.PlaceImportedInScene;
                button.SetEnabled(false);
                progress.Set(0f);
                progress.Visible(true);
                try
                {
                    var result = await ZettaImporter.ImportAsync(generation, null, place ? new ZettaImporter.Placement() : null, null, p => progress.Set(p));
                    var main = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(result.MainAssetPath);
                    if (!place && main != null) EditorGUIUtility.PingObject(main);
                    actions.Toast?.Invoke(place && result.Placed != null ? "Imported and placed in the scene." : $"Imported to {result.Folder}");
                    ShowProperties(properties, generation);
                }
                catch (Exception e)
                {
                    actions.Toast?.Invoke("The import failed: " + (e is ZettaApiException api ? ZettaCatalog.Explain(api) : e.Message));
                }
                finally
                {
                    progress.Visible(false);
                    button.SetEnabled(true);
                }
            }

            var existing = ZettaProjectIndex.Find(generation.Id);
            if (existing != null && existing.Model != null)
            {
                buttons.Add(ZettaUI.Chip("In project", "success"));
                buttons.Add(ZettaUI.Button("Show in Project", () =>
                {
                    Selection.activeObject = existing.Model;
                    EditorGUIUtility.PingObject(existing.Model);
                }, "z-button--small", "z-button--primary"));
                buttons.Add(ZettaUI.Button("Place in scene", () =>
                {
                    var placed = ZettaImporter.Place(generation, existing.ModelPaths, new ZettaImporter.Placement());
                    actions.Toast?.Invoke(placed != null ? "Placed in the scene." : "It could not be placed.");
                }, "z-button--small"));
            }
            else
            {
                Button import = null;
                import = ZettaUI.Button("Import into project", () => _ = Import(import), "z-button--small", "z-button--primary");
                buttons.Add(import);
                buttons.Add(ZettaUI.PlaceInSceneToggle());
            }
            buttons.Add(ZettaUI.Button("Show in Library", () => ZettaWindow.Open(ZettaWindow.Library), "z-button--small", "z-button--ghost"));
            box.Add(buttons);
            box.Add(progress);
            box.Add(properties);
            ShowProperties(properties, generation);
            return box;
        }

        /// <summary>A model's properties, read from its imported asset (model-properties.tsx, Unity-side).</summary>
        static void ShowProperties(VisualElement host, Generation generation)
        {
            host.Clear();
            var model = StudioAssets.FindImported(generation.Id);
            if (model == null)
            {
                host.Add(ZettaUI.Hint("Import the model to read its polygons and vertices."));
                return;
            }
            long triangles = 0, vertices = 0;
            var meshes = 0;
            foreach (var filter in model.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null) continue;
                meshes++;
                triangles += Triangles(filter.sharedMesh);
                vertices += filter.sharedMesh.vertexCount;
            }
            var skinned = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var skin in skinned)
            {
                if (skin.sharedMesh == null) continue;
                meshes++;
                triangles += Triangles(skin.sharedMesh);
                vertices += skin.sharedMesh.vertexCount;
            }
            var materials = model.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().Count();
            var table = new VisualElement().Classes("z-pl-props");
            void Row(string label, string value)
            {
                var row = new VisualElement().Classes("z-row");
                row.Add(new Label(label).Classes("z-pl-prop-label"));
                row.Add(new Label(value).Classes("z-pl-prop-value"));
                table.Add(row);
            }
            Row("Polygons", $"{triangles:N0} · {(generation.PolygonType == "quadrilateral" ? "Quads" : "Triangles")}");
            Row("Vertices", vertices.ToString("N0"));
            Row("Meshes", meshes.ToString("N0"));
            Row("Materials", materials.ToString("N0"));
            Row("Rigged", skinned.Any(s => s.bones != null && s.bones.Length > 0) ? "Yes" : "No");
            Row("Made by", ZettaCatalog.FeatureLabel(generation.Feature));
            var open = ZettaUI.Button("Select in project", () =>
            {
                Selection.activeObject = model;
                EditorGUIUtility.PingObject(model);
            }, "z-button--small", "z-button--ghost");
            table.Add(open);
            host.Add(table);
        }

        static long Triangles(Mesh mesh)
        {
            long count = 0;
            for (var i = 0; i < mesh.subMeshCount; i++)
                if (mesh.GetTopology(i) == MeshTopology.Triangles) count += (long)mesh.GetIndexCount(i) / 3;
            return count;
        }

        // ─── Isolate Parts, paused for the user ─────────────────────────────────────

        static readonly Dictionary<string, string> KindLabel = new Dictionary<string, string> { ["held"] = "Held", ["attached"] = "Attached", ["detail"] = "Part" };

        /// <summary>
        /// ISOLATE PARTS, PAUSED FOR YOU (isolate-review.tsx): what the identify pass found, boxed on the picture, with the
        /// sensitivity's proposal ticked. Untick, type more, and isolate.
        /// </summary>
        static VisualElement IsolateReview(PipelineRunStep step, StepActions actions)
        {
            var review = step.Review;
            var parts = review?.Identification?.Parts ?? new List<IdentifiedPart>();
            var chosen = new HashSet<string>(review?.ProposedKeys ?? new List<string>());
            var typed = new List<string>();
            var box = new VisualElement().Classes("z-pl-review");

            var subject = review?.Identification?.Subject;
            box.Add(ZettaUI.Text((string.IsNullOrEmpty(subject) ? "Choose the parts to isolate." : $"Found in {subject}:") + " The character is always made, re-posed with its hands empty."));

            var boxes = new Dictionary<string, VisualElement>();
            if (!string.IsNullOrEmpty(review?.Source?.Url))
            {
                var picture = new VisualElement().Classes("z-pl-review-picture");
                ZettaUI.LoadInto(picture, review.Source.Url);
                // The boxes are fractions of the picture, so the element takes the picture's own proportions once it loads.
                void Fit()
                {
                    var texture = picture.style.backgroundImage.value.texture;
                    var width = picture.resolvedStyle.width;
                    if (texture != null && texture.width > 0 && !float.IsNaN(width) && width > 0)
                        picture.style.height = width * texture.height / texture.width;
                }
                picture.schedule.Execute(Fit).Every(250).Until(() => picture.style.height.value.value > 0);
                picture.RegisterCallback<GeometryChangedEvent>(e =>
                {
                    if (Math.Abs(e.oldRect.width - e.newRect.width) > 0.5f) Fit();
                });
                foreach (var part in parts.Where(p => p.Box != null && p.Box.Length == 4))
                {
                    var frame = new VisualElement().Classes("z-pl-review-box");
                    frame.pickingMode = PickingMode.Ignore;
                    frame.style.left = Length.Percent((float)part.Box[0] * 100);
                    frame.style.top = Length.Percent((float)part.Box[1] * 100);
                    frame.style.width = Length.Percent((float)part.Box[2] * 100);
                    frame.style.height = Length.Percent((float)part.Box[3] * 100);
                    var tag = new Label(part.Label).Classes("z-pl-review-tag");
                    tag.pickingMode = PickingMode.Ignore;
                    frame.Add(tag);
                    picture.Add(frame);
                    boxes[part.Key] = frame;
                }
                box.Add(picture);
            }

            void Light(string hover)
            {
                foreach (var pair in boxes)
                {
                    pair.Value.EnableInClassList("z-pl-review-box--lit", pair.Key == hover || chosen.Contains(pair.Key));
                    pair.Value.EnableInClassList("z-pl-review-box--hover", pair.Key == hover);
                }
            }

            Button confirm = null;
            void Count()
            {
                var n = chosen.Count + typed.Count;
                confirm.text = n == 0 ? "Choose a part" : $"Isolate {n} part{(n == 1 ? "" : "s")} and the character";
                confirm.SetEnabled(!actions.Busy && n > 0);
            }

            if (parts.Count > 0)
            {
                foreach (var part in parts)
                {
                    var key = part.Key;
                    var row = new VisualElement().Classes("z-row", "z-pl-review-row");
                    var toggle = ZettaUI.Toggle(part.Label, chosen.Contains(key), on =>
                    {
                        if (on) chosen.Add(key);
                        else chosen.Remove(key);
                        Light(null);
                        Count();
                    });
                    toggle.AddToClassList("z-grow");
                    toggle.SetEnabled(!actions.Busy);
                    row.Add(toggle);
                    row.Add(ZettaUI.Hint($"{(part.Kind != null && KindLabel.TryGetValue(part.Kind, out var kind) ? kind : "Part")} · {part.Level}"));
                    row.RegisterCallback<PointerEnterEvent>(_ => Light(key));
                    row.RegisterCallback<PointerLeaveEvent>(_ => Light(null));
                    box.Add(row);
                }
            }
            else box.Add(ZettaUI.Text("Nothing was found to isolate — type the parts you want.", "z-muted"));

            var field = ZettaUI.TextInput("Another part", "", 80).Classes("z-grow");
            var chips = new VisualElement().Classes("z-row", "z-row--wrap");
            void RenderChips()
            {
                chips.Clear();
                foreach (var label in typed)
                {
                    var chip = new VisualElement().Classes("z-pl-tag");
                    chip.Add(new Label(label).Classes("z-pl-tag-text"));
                    var text = label;
                    chip.Add(ZettaUI.Button("✕", () =>
                    {
                        typed.Remove(text);
                        RenderChips();
                        Count();
                    }, "z-button--small", "z-button--ghost", "z-pl-tag-x"));
                    chips.Add(chip);
                }
            }
            void Add()
            {
                var label = (field.value ?? "").Trim();
                if (label == "" || typed.Contains(label)) return;
                typed.Add(label);
                field.SetValueWithoutNotify("");
                RenderChips();
                Count();
            }
            var add = ZettaUI.Button("+ Add", Add, "z-button--small", "z-button--ghost");
            field.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
                Add();
                e.StopPropagation();
            }, TrickleDown.TrickleDown);
            field.SetEnabled(!actions.Busy);
            box.Add(ZettaUI.Row(field, add));
            box.Add(chips);

            confirm = ZettaUI.Button("", () => actions.ConfirmParts?.Invoke(step, chosen.ToList(), typed.ToList()), "z-button--primary");
            box.Add(confirm);
            Light(null);
            Count();
            return box;
        }
    }
}
