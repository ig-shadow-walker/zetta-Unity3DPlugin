using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine.UIElements;
using Zetta3D.Editor.Studio;

namespace Zetta3D.Editor.Pipelines.UI
{
    /// <summary>
    /// What a node's form edits and how it reports a change. A form is built once for a node and then refreshed in
    /// place on every change of the graph (so a field being typed in keeps its focus and caret); every control writes
    /// a PATCH — the owner merges it (the recipe through <see cref="GraphEditing"/>, a run through
    /// <see cref="RequestBuilder.RunNodeChange"/>), the graph changes, and <see cref="Refresh"/> runs.
    /// </summary>
    public sealed class FormContext
    {
        public PipelineNode Node;
        public PipelineGraph Graph;
        /// <summary>The settings shown — the node's own over the Studio's defaults.</summary>
        public JObject Settings = new JObject();
        public string Incoming;
        /// <summary>In a run: a step set on each run asks for this run's own input here.</summary>
        public bool InRun;
        /// <summary>Applies a change to the node's settings (merged; for Generate, through applyFormPatch).</summary>
        public Action<JObject> Patch;
        /// <summary>Where pickers open as overlays (the window's root).</summary>
        public VisualElement OverlayHost;

        readonly List<Action> _refreshers = new List<Action>();

        public static StudioContext Studio => StudioContext.Shared;

        public void OnRefresh(Action refresh)
        {
            _refreshers.Add(refresh);
            refresh();
        }

        /// <summary>Shows the node's current settings in every control (values without notifying, visibility, locks).</summary>
        public void Refresh(PipelineNode node, PipelineGraph graph)
        {
            Node = node;
            Graph = graph;
            Settings = SettingsOf(node);
            Incoming = GraphRules.IncomingType(graph, node.Id);
            foreach (var refresh in _refreshers) refresh();
        }

        public void Refresh()
        {
            foreach (var refresh in _refreshers) refresh();
        }

        /// <summary>A node's settings for its form: its own (the load normalised them) or its kind's defaults.</summary>
        public static JObject SettingsOf(PipelineNode node) =>
            node.Settings != null ? (JObject)node.Settings.DeepClone() : NodeSettings.Defaults(node.Kind);

        public string Str(string key, string fallback = "") => Settings[key]?.Type == JTokenType.String ? Settings[key].ToString() : fallback;

        public bool Bool(string key, bool fallback = false) =>
            Settings[key]?.Type == JTokenType.Boolean ? Settings[key].Value<bool>() : Settings[key] == null || Settings[key].Type == JTokenType.Null ? fallback : ToolSettings.Truthy(Settings[key]);

        public void Set(string key, JToken value) => Patch?.Invoke(new JObject { [key] = value });
    }

    /// <summary>
    /// THE NODES' SETTINGS FORMS (node-settings.tsx, settings/*.tsx) — each Studio tool's own options, in the Studio's
    /// words, over the web-shaped settings a graph keeps. Option lists, labels and hints come from
    /// <see cref="ToolSettings"/> and <see cref="GenerateForm"/>, so a node offers exactly what the tool does.
    /// </summary>
    public static class NodeForms
    {
        /// <summary>Builds the form for the context's node into <paramref name="host"/>.</summary>
        public static void Build(VisualElement host, FormContext ctx)
        {
            switch (ctx.Node.Kind)
            {
                case NodeKinds.SeedViews:
                    host.Add(ZettaUI.Text(
                        "Each run gives its own pictures of each side — a front at least, and the back, left and right as you have them. Connect it to Multi-view - Generate 3D or Multi-view - Smart 3D and the model is made from all of them. Press New run to give the pictures; several sets make a run each.",
                        "z-muted"));
                    return;
                case NodeKinds.SeedPicture:
                case NodeKinds.SeedModel:
                    host.Add(ZettaUI.Text(
                        $"{(ctx.Node.Kind == NodeKinds.SeedModel ? "The model" : "The picture")} is chosen each time you start a run — press New run and pick one, or several for a run each. Every run keeps its own seed and results in Runs.",
                        "z-muted"));
                    return;
                case NodeKinds.RemoveBackground:
                    host.Add(ZettaUI.Text("Nothing to set. The subject is cut out of the picture coming in, as the Studio’s Remove background does.", "z-muted"));
                    return;
                case NodeKinds.AiEdit:
                    AiEdit(host, ctx);
                    return;
                case NodeKinds.DrawViews:
                    DrawViews(host, ctx);
                    return;
                case NodeKinds.IsolateParts:
                    Isolate(host, ctx);
                    return;
                case NodeKinds.Generate3D:
                case NodeKinds.GenerateSmart3D:
                case NodeKinds.Generate3DMultiview:
                case NodeKinds.GenerateSmart3DMultiview:
                    GenerateNodeForm.Build(host, ctx);
                    return;
                case NodeKinds.Retopology:
                    Retopology(host, ctx);
                    return;
                case NodeKinds.Segmentation:
                    Segmentation(host, ctx);
                    return;
                case NodeKinds.Texture:
                    Texture(host, ctx, false);
                    return;
                case NodeKinds.TextureMultiview:
                    Texture(host, ctx, true);
                    return;
                case NodeKinds.SmartUv:
                    SmartUv(host, ctx);
                    return;
                case NodeKinds.Rigging:
                    Rigging(host, ctx);
                    return;
            }
        }

        // ─── Shared controls ─────────────────────────────────────────────────────────

        internal static VisualElement Field(string label, VisualElement control, string aside = null)
        {
            var box = new VisualElement().Classes("z-pl-field");
            var head = new VisualElement().Classes("z-row");
            head.Add(ZettaUI.FieldLabel(label).Classes("z-grow"));
            if (aside != null) head.Add(ZettaUI.Hint(aside).Classes("z-pl-aside"));
            box.Add(head);
            box.Add(control);
            return box;
        }

        /// <summary>A segmented choice bound to one key, with an optional hint under it that follows the value.</summary>
        internal static ZettaUI.Segmented Choice(VisualElement host, FormContext ctx, string label, string key, SettingOption[] options,
            Func<string, string> hint = null, string fallback = null)
        {
            var values = options.Select(o => o.Value).ToList();
            var segmented = new ZettaUI.Segmented(values, options.Select(o => o.Label).ToList(), ctx.Str(key, fallback ?? values[0]));
            segmented.Changed += v => ctx.Set(key, v);
            host.Add(Field(label, segmented));
            Label hintLabel = null;
            if (hint != null)
            {
                hintLabel = ZettaUI.Hint("");
                host.Add(hintLabel);
            }
            ctx.OnRefresh(() =>
            {
                var value = ctx.Str(key, fallback ?? values[0]);
                segmented.Set(values.Contains(value) ? value : values[0], false);
                if (hintLabel != null)
                {
                    hintLabel.text = hint(segmented.Value) ?? "";
                    hintLabel.Visible(!string.IsNullOrEmpty(hintLabel.text));
                }
            });
            return segmented;
        }

        internal static Toggle Check(VisualElement host, FormContext ctx, string label, string key, bool fallback, string tooltip = null)
        {
            var toggle = ZettaUI.Toggle(label, ctx.Bool(key, fallback), v => ctx.Set(key, v), tooltip);
            host.Add(toggle);
            ctx.OnRefresh(() => toggle.SetValueWithoutNotify(ctx.Bool(key, fallback)));
            return toggle;
        }

        internal static TextField Words(VisualElement host, FormContext ctx, string label, string key, string placeholder, int max, bool multiline = true)
        {
            var field = multiline ? ZettaUI.TextArea(placeholder, ctx.Str(key), max) : ZettaUI.TextInput(placeholder, ctx.Str(key), max);
            field.RegisterValueChangedCallback(e => ctx.Set(key, e.newValue ?? ""));
            host.Add(label == null ? (VisualElement)field : Field(label, field));
            ctx.OnRefresh(() =>
            {
                var value = ctx.Str(key);
                if (field.value != value) field.SetValueWithoutNotify(value);
            });
            return field;
        }

        /// <summary>"Set on each run" (per-run.tsx SetOnEachRun).</summary>
        static Toggle SetOnEachRun(VisualElement host, FormContext ctx, string what)
        {
            var toggle = Check(host, ctx, "Set on each run", "askAtRun", false);
            host.Add(ZettaUI.Hint($"Every run asks for its own {what} before it starts."));
            return toggle;
        }

        /// <summary>The pipeline leaves this step's input to each run (per-run.tsx GivenInEachRun).</summary>
        static Label GivenInEachRun(string what) => PipelineUi.Note(
            $"↻ The {what} is given in each run. New run opens the run without starting it, with this step marked Needs input — fill it in there, then press Start.",
            "dashed");

        /// <summary>The Image Studio's engines (GET /2d/models), with the locked ones disabled and saying why.</summary>
        static void ImageEngine(VisualElement host, FormContext ctx)
        {
            var box = new VisualElement();
            host.Add(box);
            string shownFor = null;
            ZettaUI.Segmented segmented = null;
            ctx.OnRefresh(() =>
            {
                var models = FormContext.Studio.ImageModels;
                var choices = models?.Models ?? new List<ImageModel>();
                box.Visible(choices.Count > 0);
                if (choices.Count == 0) return;
                var signature = string.Join(",", choices.Select(c => c.Id + (c.Locked ? "!" : "")));
                var value = ctx.Str("model", null) ?? models.Default ?? choices[0].Id;
                if (signature != shownFor)
                {
                    shownFor = signature;
                    box.Clear();
                    segmented = new ZettaUI.Segmented(choices.Select(c => c.Id).ToList(), choices.Select(c => c.Label).ToList(), value);
                    segmented.Changed += v => ctx.Set("model", v);
                    foreach (var choice in choices.Where(c => c.Locked)) segmented.SetOptionEnabled(choice.Id, false, choice.LockedReason);
                    box.Add(Field("Image engine", segmented));
                }
                segmented?.Set(value, false);
            });
        }

        // ─── Picture steps ───────────────────────────────────────────────────────────

        static void AiEdit(VisualElement host, FormContext ctx)
        {
            if (!ctx.InRun) SetOnEachRun(host, ctx, "instruction");
            var given = GivenInEachRun("instruction");
            host.Add(given);
            var prompt = new VisualElement();
            Words(prompt, ctx, "How to change it", "prompt", "Make the armour gold, keep everything else", 2000);
            host.Add(prompt);
            ImageEngine(host, ctx);
            Check(host, ctx, "Frame it for 3D — one object, centred, plain background", "for3D", true);
            host.Add(PipelineUi.Note("Image editing needs a plan that includes it, as in the Image Studio."));
            ctx.OnRefresh(() =>
            {
                var perRun = !ctx.InRun && ctx.Bool("askAtRun");
                given.Visible(perRun);
                prompt.Visible(!perRun);
            });
        }

        static void DrawViews(VisualElement host, FormContext ctx)
        {
            host.Add(ZettaUI.Text("The picture coming in is the front. These angles are drawn from it, as the Studio’s Generate views does — connect this to Generate 3D for a multi-view model.", "z-muted"));
            foreach (var option in ToolSettings.DrawViewsChoices)
            {
                var view = option.Value;
                List<string> Current() => (ctx.Settings["views"] as JArray)?.Select(v => v.ToString()).ToList() ?? new List<string>();
                var toggle = ZettaUI.Toggle(option.Label, Current().Contains(view), on =>
                {
                    var views = Current();
                    if (on && !views.Contains(view)) views.Add(view);
                    if (!on) views.Remove(view);
                    ctx.Set("views", new JArray(views));
                });
                host.Add(toggle);
                ctx.OnRefresh(() => toggle.SetValueWithoutNotify(Current().Contains(view)));
            }
        }

        static void Isolate(VisualElement host, FormContext ctx)
        {
            var identify = new ZettaUI.Segmented(new[] { "identify", "typed" }, new[] { "Identify", "Only my list" }, ctx.Bool("identify", true) ? "identify" : "typed");
            identify.Changed += v => ctx.Set("identify", v == "identify");
            host.Add(Field("Find the parts", identify));

            var sensitivityBox = new VisualElement();
            Choice(sensitivityBox, ctx, "Sensitivity", "sensitivity", ToolSettings.IsolateSensitivities,
                v => ToolSettings.IsolateSensitivities.FirstOrDefault(o => o.Value == v)?.Hint, "low");
            host.Add(sensitivityBox);

            // The parts typed in: up to 16, each up to 80 characters, one of each (case aside).
            List<string> Parts() => (ctx.Settings["extraParts"] as JArray)?.Select(p => p.ToString()).ToList() ?? new List<string>();
            var partsLabel = ZettaUI.FieldLabel("Also isolate").Classes("z-grow");
            var count = ZettaUI.Hint("0/16").Classes("z-pl-aside");
            var head = new VisualElement().Classes("z-row");
            head.Add(partsLabel);
            head.Add(count);
            host.Add(head);
            var typing = ZettaUI.TextInput("belt pouch", "", 80).Classes("z-grow");
            ZettaUI.SetPlaceholder(typing, "belt pouch");
            Button add = null;
            void AddPart()
            {
                var label = (typing.value ?? "").Trim();
                var parts = Parts();
                if (label == "" || parts.Any(p => string.Equals(p, label, StringComparison.OrdinalIgnoreCase))) return;
                parts.Add(label);
                typing.SetValueWithoutNotify("");
                add?.SetEnabled(false);
                ctx.Set("extraParts", new JArray(parts.Take(ToolSettings.IsolateMaxPartsLimit)));
            }
            add = ZettaUI.Button("+ Add", AddPart, "z-button--small", "z-button--ghost");
            add.SetEnabled(false);
            typing.RegisterValueChangedCallback(e => add.SetEnabled(!string.IsNullOrWhiteSpace(e.newValue)));
            typing.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode != UnityEngine.KeyCode.Return && e.keyCode != UnityEngine.KeyCode.KeypadEnter) return;
                AddPart();
                e.StopPropagation();
            }, TrickleDown.TrickleDown);
            host.Add(ZettaUI.Row(typing, add));
            var chips = new VisualElement().Classes("z-row", "z-row--wrap");
            host.Add(chips);

            var max = new SliderInt(1, ToolSettings.IsolateMaxPartsLimit) { showInputField = true };
            max.AddToClassList("z-pl-slider");
            max.RegisterValueChangedCallback(e => ctx.Set("maxParts", Math.Min(16, Math.Max(1, e.newValue))));
            host.Add(Field("Max parts", max, "Caps what one run isolates"));

            Choice(host, ctx, "Character pose", "pose", ToolSettings.IsolatePoses, null, "t");
            host.Add(ZettaUI.Hint("Humanoids only, with their hands emptied. Anything else keeps its pose and loses what was attached."));
            ImageEngine(host, ctx);
            host.Add(PipelineUi.Note("Wire Character on to its own chain, and Each part to a chain every part runs through. After a run, each part it found gets its own output too."));

            ctx.OnRefresh(() =>
            {
                var on = ctx.Bool("identify", true);
                identify.Set(on ? "identify" : "typed", false);
                sensitivityBox.Visible(on);
                partsLabel.text = on ? "Also isolate" : "Parts to isolate";
                var parts = Parts();
                count.text = $"{parts.Count}/{ToolSettings.IsolateMaxPartsLimit}";
                chips.Clear();
                foreach (var part in parts)
                {
                    var chip = new VisualElement().Classes("z-pl-tag");
                    chip.Add(new Label(part).Classes("z-pl-tag-text"));
                    var label = part;
                    chip.Add(ZettaUI.Button("✕", () => ctx.Set("extraParts", new JArray(Parts().Where(p => p != label))), "z-button--small", "z-button--ghost", "z-pl-tag-x"));
                    chips.Add(chip);
                }
                var maxParts = ToolSettings.IsNumber(ctx.Settings["maxParts"]) ? (int)ctx.Settings["maxParts"].Value<double>() : 6;
                if (max.value != maxParts) max.SetValueWithoutNotify(Math.Min(16, Math.Max(1, maxParts)));
            });
        }

        // ─── Studio tools ────────────────────────────────────────────────────────────

        static void Retopology(VisualElement host, FormContext ctx)
        {
            Choice(host, ctx, "Engine", "engine", ToolSettings.RetopologyEngines, v => ToolSettings.RetopologyEngines.FirstOrDefault(o => o.Value == v)?.Hint, "r1");
            var polygons = Choice(host, ctx, "Polygons", "polygonType", ToolSettings.PolygonTypes, null, "triangle");
            var quadLock = ZettaUI.Hint("Quad meshes are available from Basic.").Classes("z-hint--warn");
            host.Add(quadLock);

            var r1 = new VisualElement();
            var countLabel = ZettaUI.FieldLabel("");
            r1.Add(countLabel);
            var count = ZettaUI.TextInput("Polygon count", "", 12);
            count.RegisterValueChangedCallback(e =>
            {
                var text = (e.newValue ?? "").Trim();
                // A whole number is stored as one; anything else as typed — and named out of range.
                if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) ctx.Set("faces", n);
                else ctx.Set("faces", e.newValue ?? "");
            });
            r1.Add(count);
            var error = new Label().Classes("z-error", "z-count-error");
            r1.Add(error);
            var countHint = ZettaUI.Hint("");
            r1.Add(countHint);
            Check(r1, ctx, "Bake textures onto the rebuilt mesh", "bake", true, "Carries the original colours over to the new mesh. Off returns bare geometry.");
            host.Add(r1);

            var n1 = new VisualElement();
            Choice(n1, ctx, "Level of detail", "faceLevel", ToolSettings.FaceLevels, v => ToolSettings.FaceLevels.FirstOrDefault(o => o.Value == v)?.Hint, "medium");
            host.Add(n1);

            ctx.OnRefresh(() =>
            {
                var engine = ctx.Str("engine", "r1");
                var quads = ctx.Str("polygonType", "triangle") == "quadrilateral";
                var (min, max) = ToolSettings.RetopologyBounds(quads ? "quadrilateral" : "triangle");
                r1.Visible(engine == "r1");
                n1.Visible(engine != "r1");
                countLabel.text = $"Polygon count ({min:N0}–{max:N0})";
                var faces = ctx.Settings["faces"];
                var text = faces == null || faces.Type == JTokenType.Null ? "" : ToolSettings.IsNumber(faces) ? ((long)faces.Value<double>()).ToString(CultureInfo.InvariantCulture) : faces.ToString();
                if ((count.value ?? "").Trim() != text) count.SetValueWithoutNotify(text);
                var outOfRange = ToolSettings.RetopologyOutOfRange(ctx.Settings);
                error.text = $"Out of range — {min:N0} to {max:N0}{(quads ? ", as quads allow half as many" : "")}.";
                error.Visible(outOfRange);
                countHint.text = quads
                    ? "Quads halve the ceiling. Zetta R1 aims for your count, then settles on the cleanest topology near it."
                    : "Zetta R1 aims for this count, then settles on the cleanest topology near it.";
                var quadsAllowed = FormContext.Studio.Gates3D?.AllowsQuadMesh != false;
                polygons.SetOptionEnabled("quadrilateral", quadsAllowed, quadsAllowed ? null : "Quad meshes are available from Basic.");
                quadLock.Visible(!quadsAllowed);
            });
        }

        static void Segmentation(VisualElement host, FormContext ctx)
        {
            Choice(host, ctx, "Segmentation type", "mode", ToolSettings.SegmentModes, v => ToolSettings.SegmentModes.FirstOrDefault(o => o.Value == v)?.Hint, "segment");
            Choice(host, ctx, "How many parts", "granularity", ToolSettings.SegmentGranularities, v => ToolSettings.SegmentGranularities.FirstOrDefault(o => o.Value == v)?.Hint, "balanced");
            var segmentOnly = new VisualElement();
            Check(segmentOnly, ctx, "Also split pieces that are not joined", "splitByConnectivity", true, "Pieces that do not touch each other become separate parts.");
            host.Add(segmentOnly);
            var smartOnly = new VisualElement();
            Words(smartOnly, ctx, "What is this? (Optional)", "hint", "e.g. game character with sword and armor", 1024);
            smartOnly.Add(ZettaUI.Hint("Naming the parts helps it find them."));
            host.Add(smartOnly);
            ctx.OnRefresh(() =>
            {
                var smart = ctx.Str("mode", "segment") == "smart";
                segmentOnly.Visible(!smart);
                smartOnly.Visible(smart);
            });
        }

        /// <summary>
        /// AI texturing, or Multi-view - AI texturing (<paramref name="multiView"/>): pictures of each side in place of one
        /// picture or a description — the views wired into it, or, with none, each run's own four pictures.
        /// </summary>
        static void Texture(VisualElement host, FormContext ctx, bool multiView)
        {
            if (!multiView)
            {
                // Always given in each run: the pipeline itself never holds the description or the picture.
                if (!ctx.InRun) host.Add(GivenInEachRun("texture’s description or reference picture"));
                else TextureInput(host, ctx);
            }
            else
            {
                // Where its pictures come from: the views wired in (a Multi-view seed, Draw views), or each run's own.
                var from = ZettaUI.Hint("").Classes("z-pl-note");
                host.Add(from);
                var given = GivenInEachRun("set of pictures of each side");
                host.Add(given);
                ViewSlots slots = null;
                if (ctx.InRun)
                {
                    slots = new ViewSlots(ctx.OverlayHost, () => ctx.Settings["referenceViews"] as JObject ?? new JObject(),
                        update => ctx.Set("referenceViews", update(ctx.Settings["referenceViews"] as JObject ?? new JObject())));
                    host.Add(slots);
                }
                ctx.OnRefresh(() =>
                {
                    var wired = GraphRules.SourceOf(ctx.Graph, ctx.Node.Id, "views")?.Kind;
                    from.text = wired == NodeKinds.SeedViews
                        ? "Textured from the pictures of each side the Multi-view seed gives in each run — the same ones the model was made from, when it was."
                        : "Textured from the front and the back and sides Draw views draws.";
                    from.Visible(wired != null);
                    given.Visible(wired == null && !ctx.InRun);
                    if (slots == null) return;
                    slots.Visible(wired == null);
                    slots.Refresh();
                });
            }

            Choice(host, ctx, "Texture quality", "tier", ToolSettings.TextureTiers, v =>
            {
                var option = ToolSettings.TextureTiers.FirstOrDefault(o => o.Value == v);
                var price = FormContext.Studio.Prices?.Price(v, null);
                return $"Up to {option?.Hint ?? "2K"} resolution" + (price == null ? "" : $" ({price} credits)");
            }, ToolSettings.Retexture);
            var fast = Check(host, ctx, "Faster, at some detail — same price", "fast", false);
            Choice(host, ctx, "Texture fit", "alignment", new[] { ToolSettings.TextureAlignments[1], ToolSettings.TextureAlignments[0] }, null, "original_image");
            Check(host, ctx, "PBR materials", "pbr", true, "Roughness, metalness and normal maps as well as colour.");
            Check(host, ctx, "Remove the reference's baked-in lighting", "delight", true, "So the texture lights correctly in your own scene.");
            Check(host, ctx, "Bake effects into the textures", "bake", true);
            Check(host, ctx, "Remove background before applying", "removeBg", true);
            ctx.OnRefresh(() => fast.Visible(ctx.Str("tier", ToolSettings.Retexture) == ToolSettings.Retexture));
        }

        /// <summary>A run's own reference picture (uploaded or from your images), or its description.</summary>
        static void TextureInput(VisualElement host, FormContext ctx)
        {
            var row = new VisualElement().Classes("z-row");
            var thumb = new VisualElement().Classes("z-pl-thumb", "z-pl-thumb--ref");
            row.Add(thumb);
            var side = new VisualElement().Classes("z-grow");
            var name = ZettaUI.Hint("");
            side.Add(name);
            var buttons = new VisualElement().Classes("z-row", "z-row--wrap");
            var status = ZettaUI.Hint("").Visible(false);
            var error = new Label().Classes("z-error").Visible(false);
            Button upload = null;
            upload = ZettaUI.Button("Upload", async () =>
            {
                var path = EditorUtility.OpenFilePanelWithFilters("Choose a reference picture", "", new[] { "Images", "png,jpg,jpeg,webp" });
                if (string.IsNullOrEmpty(path)) return;
                error.Visible(false);
                upload.SetEnabled(false);
                status.Visible(true);
                try
                {
                    var seed = await PipelineSeed.FromUploadAsync(File.ReadAllBytes(path), StudioAssets.ContentTypeOf(path), Path.GetFileName(path),
                        progress: p => status.text = StudioAssets.UploadingText(p));
                    ctx.Set("reference", new JObject { ["uploadKey"] = seed.Request["uploadKey"], ["name"] = seed.Name, ["thumbnail"] = seed.ThumbnailDataUrl });
                }
                catch (ZettaApiException e)
                {
                    error.text = ZettaCatalog.Explain(e);
                    error.Visible(true);
                }
                catch (Exception e)
                {
                    error.text = e.Message;
                    error.Visible(true);
                }
                finally
                {
                    upload.SetEnabled(true);
                    status.Visible(false);
                }
            }, "z-button--small");
            buttons.Add(upload);
            buttons.Add(ZettaUI.Button("From your images", () =>
            {
                if (ctx.OverlayHost == null) return;
                LibraryPickers.PickImages(ctx.OverlayHost, false, new List<long>(), async picked =>
                {
                    var preview = await PipelineSeed.ThumbnailFromUrlAsync(picked.Url);
                    ctx.Set("reference", new JObject
                    {
                        ["gen2dPostId"] = picked.Id,
                        ["name"] = picked.Name,
                        ["thumbnail"] = preview == null ? null : "data:image/jpeg;base64," + Convert.ToBase64String(preview),
                    });
                    return true;
                });
            }, "z-button--small"));
            var remove = ZettaUI.Button("✕", () => ctx.Set("reference", JValue.CreateNull()), "z-button--small", "z-button--ghost");
            remove.tooltip = "Remove the reference picture";
            buttons.Add(remove);
            side.Add(buttons);
            row.Add(side);
            host.Add(Field("Reference picture", row));
            host.Add(status);
            host.Add(error);

            var describe = new VisualElement();
            Words(describe, ctx, "Describe the texture", "prompt", "Weathered bronze with green patina", 1000);
            host.Add(describe);

            string shownThumb = null;
            ctx.OnRefresh(() =>
            {
                var reference = ctx.Settings["reference"] as JObject;
                name.text = reference == null ? "Optional — or describe the texture below." : reference["name"]?.ToString() ?? "Your picture";
                remove.Visible(reference != null);
                describe.Visible(reference == null);
                var url = reference?["thumbnail"]?.Type == JTokenType.String ? reference["thumbnail"].ToString() : null;
                if (url == shownThumb) return;
                shownThumb = url;
                PipelineUi.ShowImage(thumb, url);
            });
        }

        static void SmartUv(VisualElement host, FormContext ctx)
        {
            Choice(host, ctx, "Orientation", "orientation", ToolSettings.SmartUvOrientations, _ => "Which way the model faces in the exported file.", "as-is");
            Check(host, ctx, "Bake textures", "bake", true);
            var bakeHint = ZettaUI.Hint("");
            host.Add(bakeHint);
            Check(host, ctx, "Compress geometry", "compress", false);
            host.Add(ZettaUI.Hint("A smaller file, through mesh compression."));
            host.Add(ZettaUI.Hint($"Takes models up to {GenerateForm.Count(ToolSettings.SmartUvMaxTriangles)} triangles.").Classes("z-section-gap"));
            ctx.OnRefresh(() => bakeHint.text = ctx.Bool("bake", true)
                ? "Keeps the textures, baked onto the new layout."
                : "Returns the geometry with its new UVs only — no textures.");
        }

        static void Rigging(VisualElement host, FormContext ctx)
        {
            var values = ToolSettings.RigTypes.Select(o => o.Value).ToList();
            var body = ZettaUI.Dropdown(null, values, ToolSettings.RigTypes.Select(o => o.Label).ToList(), ctx.Str("rigType", "biped"), v => ctx.Set("rigType", v));
            host.Add(Field("Body", body));
            var bodyHint = ZettaUI.Hint("");
            host.Add(bodyHint);
            // A dropdown: six skeletons do not fit a segmented row. Held, with a hint, on any body but a humanoid.
            var specs = ToolSettings.RigSpecs.Select(o => o.Value).ToList();
            var spec = ZettaUI.Dropdown(null, specs, ToolSettings.RigSpecs.Select(o => o.Label).ToList(), ctx.Str("spec", "standard"), v => ctx.Set("spec", v));
            host.Add(Field("Bone naming", spec));
            var specHint = ZettaUI.Hint("");
            host.Add(specHint);
            Choice(host, ctx, "Rigged file", "outFormat", ToolSettings.RigOutFormats, v => v == "fbx"
                ? "FBX imports into Unity with its skeleton — set its Rig to Humanoid for Mecanim."
                : "GLB imports with glTFast. For a Mecanim Humanoid avatar, choose FBX.", "glb");
            ctx.OnRefresh(() =>
            {
                var rig = ctx.Str("rigType", "biped");
                if (values.Contains(rig) && body.value != rig) body.SetValueWithoutNotify(rig);
                bodyHint.text = ToolSettings.RigTypes.FirstOrDefault(o => o.Value == rig)?.Hint ?? "";
                var naming = ctx.Str("spec", "standard");
                if (specs.Contains(naming) && spec.value != naming) spec.SetValueWithoutNotify(naming);
                var humanoid = rig == "biped";
                spec.SetEnabled(humanoid);
                specHint.text = humanoid
                    ? ToolSettings.RigSpecs.FirstOrDefault(o => o.Value == naming)?.Hint ?? ""
                    : "Skeleton presets apply to humanoids.";
            });
        }
    }

    /// <summary>
    /// Generate 3D and Generate Smart 3D as a node (tool-node-settings.tsx GenerateNodeSettingsForm): the Studio's Generate
    /// form whole — the tab's engines (narrowed to the multi-view engines when views come in), every parameter with the
    /// plan's locks, the title, and Remove background — without Control Mode. Every change goes through
    /// <see cref="GenerateForm.ApplyPatch"/>, so a coupled option settles exactly as the Studio's does.
    /// </summary>
    static class GenerateNodeForm
    {
        static readonly HashSet<string> MultiViewModels = new HashSet<string> { "n1", "n25", "np1", "np15" };

        static string Blurb(string model) => model switch
        {
            "n1" => "Base Model",
            "n25" => "Highest quality of mesh and texture",
            "np1" => "Clean topology, Balanced quality",
            "np15" => "Clean topology – Quad Native",
            _ => ZettaCatalog.Model(model).Blurb,
        };

        public static void Build(VisualElement host, FormContext ctx)
        {
            string Model() => ctx.Str("model", GenerateForm.ModelsFor(ctx.Node.Kind)[0]);
            ZettaCatalog.ModelInfo Caps() => ZettaCatalog.Model(Model());
            string Price(string code)
            {
                var credits = FormContext.Studio.Prices?.Price(code, Model());
                return credits == null ? "" : credits == 0 ? "Free" : $"+{credits}";
            }

            // A multi-view step says where its pictures come from — or what to connect.
            if (NodeCatalog.IsMultiViewGenerate(ctx.Node.Kind))
            {
                var from = ZettaUI.Hint("").Classes("z-pl-note");
                host.Add(from);
                ctx.OnRefresh(() =>
                {
                    var wired = GraphRules.SourceOf(ctx.Graph, ctx.Node.Id)?.Kind;
                    from.text = wired == NodeKinds.SeedViews
                        ? "Made from the pictures of each side the Multi-view seed gives in each run — the front, and the back, left and right as given."
                        : wired == NodeKinds.DrawViews
                            ? "Made from the front and the back and sides Draw views draws from it."
                            : "Connect a Multi-view seed (pictures of each side) or Draw views.";
                });
            }
            host.Add(ZettaUI.FieldLabel("Title"));
            NodeForms.Words(host, ctx, null, "title", "Name this generation", 255, false);
            NodeForms.Check(host, ctx, "Remove background", "removeBackground", true, "The background is removed from the picture coming in before the model is made from it.");
            var autofix = NodeForms.Check(host, ctx, "Enhance input image  · Free", "autofixImage", false, "Cleans up the picture before the model is built from it.");

            // Generation Engine.
            var engineBox = new VisualElement();
            host.Add(engineBox);
            EngineChoice engines = null;
            string enginesFor = null;

            var output = new ZettaUI.Segmented(new[] { "geometry", "textured" }, new[] { "Geometry", "Textured" }, ctx.Str("output", "textured"));
            output.Changed += v => ctx.Set("output", v);
            host.Add(NodeForms.Field("Output", output));

            var parts = NodeForms.Check(host, ctx, "Segmented mesh", "segmentParts", false, "Split mesh to separate parts");

            var qualityBox = new VisualElement();
            var qualityLabel = ZettaUI.FieldLabel("Quality");
            qualityBox.Add(qualityLabel);
            var qualityN1 = new ZettaUI.Segmented(ZettaCatalog.Qualities, ZettaCatalog.QualityLabels, ctx.Str("quality", "high-res"));
            var qualityN25 = new ZettaUI.Segmented(ZettaCatalog.Qualities, new[] { "Low-poly", "High", "Ultra" }, ctx.Str("quality", "high-res"));
            qualityN1.Changed += v => ctx.Set("quality", v);
            qualityN25.Changed += v => ctx.Set("quality", v);
            qualityBox.Add(qualityN1);
            qualityBox.Add(qualityN25);
            var qualityLock = ZettaUI.Hint("").Classes("z-hint--warn");
            qualityBox.Add(qualityLock);
            host.Add(qualityBox);

            var polygonFold = new Foldout { text = "Polygon control", value = false }.Classes("z-foldout");
            var countLabel = ZettaUI.FieldLabel("Polygon count");
            polygonFold.Add(countLabel);
            // Free text: a number the engine refuses stays as typed and is named out of range, never clamped.
            var count = ZettaUI.TextInput("The engine's default", ctx.Str("polygonCount"), 12);
            count.RegisterValueChangedCallback(e => ctx.Patch(new JObject { ["polygonCount"] = e.newValue ?? "", ["polygonCountTyped"] = true }));
            polygonFold.Add(count);
            var countError = new Label().Classes("z-error", "z-count-error");
            polygonFold.Add(countError);
            var countLocked = ZettaUI.Hint("Model's default face count (choosing a count is available on a paid plan)");
            polygonFold.Add(countLocked);
            var countRange = ZettaUI.Hint("");
            polygonFold.Add(countRange);
            var polygonsBox = new VisualElement();
            var polygons = new ZettaUI.Segmented(new[] { "triangle", "quadrilateral" }, new[] { "Triangles", "Quads" }, ctx.Str("polygonType", "triangle"));
            polygons.Changed += v => ctx.Set("polygonType", v);
            polygonsBox.Add(NodeForms.Field("Polygons", polygons));
            var polygonsLock = ZettaUI.Hint("").Classes("z-hint--warn");
            polygonsBox.Add(polygonsLock);
            polygonFold.Add(polygonsBox);
            host.Add(polygonFold);

            var pbr = NodeForms.Check(host, ctx, "PBR", "pbrMaterials", true, "Physically based materials: roughness, metalness and normal maps.");

            var textureBox = new VisualElement();
            textureBox.Add(ZettaUI.FieldLabel("Texture quality"));
            var textureStd = new ZettaUI.Segmented(new[] { "standard", "detailed" }, new[] { "Standard", "Detailed" }, ctx.Str("textureQuality", "standard"));
            var textureExt = new ZettaUI.Segmented(new[] { "standard", "detailed", "extreme" }, new[] { "Standard", "Detailed", "8K" }, ctx.Str("textureQuality", "standard"));
            textureStd.Changed += v => ctx.Set("textureQuality", v);
            textureExt.Changed += v => ctx.Set("textureQuality", v);
            textureBox.Add(textureStd);
            textureBox.Add(textureExt);
            var textureHint = ZettaUI.Hint("");
            textureBox.Add(textureHint);
            host.Add(textureBox);

            host.Add(ZettaUI.FieldLabel("Visibility"));
            var isPrivate = ZettaUI.Toggle("Keep this model private", false, v => ctx.Set("availability", v ? "private" : "public"));
            host.Add(isPrivate);

            var advanced = new Foldout { text = "Advanced controls", value = false }.Classes("z-foldout");
            var scale = NodeForms.Check(advanced, ctx, "Real-world scale  · Free", "realWorldScale", false, "Sizes the model in metres, as the object would be.");
            NodeForms.Check(advanced, ctx, "Smaller file  · Free", "compressGeometry", false, "Compresses the geometry for a lighter download.");
            var exportUv = NodeForms.Check(advanced, ctx, "Export UV map  · Free", "exportUv", true, "Off is faster and produces a smaller file, but the mesh cannot be re-textured.");
            var delight = NodeForms.Check(advanced, ctx, "Remove baked-in lighting  · Free", "delight", false,
                "Removes the lighting baked into the reference image, so the texture lights correctly in your own scene. Uses our newest texture engine.");
            var fit = new ZettaUI.Segmented(new[] { "geometry", "original_image" }, new[] { "Fit texture to mesh", "Fit texture to picture" }, ctx.Str("textureAlignment", "geometry"));
            fit.Changed += v => ctx.Set("textureAlignment", v);
            var fitBox = NodeForms.Field("Texture fit", fit);
            advanced.Add(fitBox);
            host.Add(advanced);

            ctx.OnRefresh(() =>
            {
                var model = Model();
                var caps = Caps();
                var gates = FormContext.Studio.Gates3D;
                var quality = ctx.Str("quality", "high-res");
                var textured = ctx.Str("output", "textured") == "textured";
                var segment = ctx.Bool("segmentParts");
                var rungs = model is "n1" or "n25";

                // Engines: the tab's, and only multi-view ones when views come in.
                var multiView = NodeCatalog.IsMultiViewGenerate(ctx.Node.Kind) || ctx.Incoming == PortTypes.Views;
                var ids = GenerateForm.ModelsFor(ctx.Node.Kind).Where(m => !multiView || MultiViewModels.Contains(m)).ToList();
                var signature = string.Join(",", ids);
                if (signature != enginesFor)
                {
                    enginesFor = signature;
                    engineBox.Clear();
                    engines = new EngineChoice("Generation Engine", ids.Select(id => new EngineChoice.Option
                    {
                        Id = id,
                        Label = id == "np15" ? ZettaCatalog.Model(id).Label + " · New" : ZettaCatalog.Model(id).Label,
                        Blurb = Blurb(id),
                    }), model);
                    engines.Changed += v => ctx.Set("model", v);
                    engineBox.Add(engines);
                }
                engines.Set(model, false);
                var npLocked = gates?.AllowsZettaNp1 == false;
                engines.SetLock("n25", gates?.AllowsZettaN25 == false ? "Zetta N2.5 is available on a paid plan." : null);
                engines.SetLock("np1", npLocked ? "Available on a paid plan." : null);
                engines.SetLock("np15", npLocked ? "Available on a paid plan." : null);

                autofix.Visible(caps.ImageTuning);

                output.Set(ctx.Str("output", "textured"), false);
                var geometryBlocked = quality == "low-poly" && rungs && model != "n25";
                output.SetOptionEnabled("geometry", !geometryBlocked, geometryBlocked ? "Not available at this mesh quality." : null);
                output.SetOptionEnabled("textured", !segment, segment ? "A segmented mesh comes out untextured. Turn off Segment first." : null);

                parts.Visible(caps.Parts && quality != "low-poly");
                var partsPrice = Price("generate_parts");
                parts.text = "Segmented mesh" + (partsPrice != "" ? $"  · {partsPrice}" : "");

                qualityBox.Visible(rungs);
                qualityLabel.text = model == "n25" ? "Mesh quality" : "Quality";
                qualityN1.Visible(model != "n25");
                qualityN25.Visible(model == "n25");
                var rung = model == "n25" ? qualityN25 : qualityN1;
                rung.Set(quality, false);
                string lockText = null;
                var qualityLocked = gates?.AllowsCustomFaceCount == false;
                foreach (var q in ZettaCatalog.Qualities)
                {
                    string reason = null;
                    if (qualityLocked) reason = "Your plan generates at the default quality — 500,000 polygons. Choosing a quality is available on a paid plan.";
                    else if (q == "ultra" && gates?.MaxFaceCount is { } maxFaces && maxFaces < 1_000_000) reason = "Ultra detail is available from Basic.";
                    else if (q == "low-poly" && gates?.AllowsLowPoly == false) reason = "Low-poly generation is available on a paid plan.";
                    else if (q == "low-poly" && !caps.LowPoly) reason = "Low-poly generation needs Zetta N1.";
                    rung.SetOptionEnabled(q, reason == null, reason);
                    if (reason != null && (qualityLocked || q == quality || lockText == null)) lockText = reason;
                }
                qualityLock.text = lockText ?? "";
                qualityLock.Visible(lockText != null);

                var mayChoose = gates?.AllowsCustomFaceCount != false;
                var (min, max) = GenerateForm.PolygonBounds(ctx.Settings);
                var customPrice = GenerateForm.HasCustomPolygonCount(ctx.Settings, mayChoose) ? Price("custom_face_count") : "";
                countLabel.text = "Polygon count" + (customPrice != "" ? $"  · {customPrice}" : "");
                count.Visible(mayChoose);
                var text = ctx.Str("polygonCount");
                if (ctx.Settings["polygonCount"] != null && ctx.Settings["polygonCount"].Type != JTokenType.String && ctx.Settings["polygonCount"].Type != JTokenType.Null)
                    text = ctx.Settings["polygonCount"].ToString();
                if ((count.value ?? "") != text) count.SetValueWithoutNotify(text);
                count.tooltip = $"{min:N0}–{max:N0}";
                var problem = mayChoose ? GenerateForm.ParameterProblems(ctx.Settings, mayChoose).FirstOrDefault() : null;
                countError.text = problem == null ? "" : problem.Contains("whole number") ? "Out of range — a whole number, please." : $"Out of range — {min:N0} to {max:N0}.";
                countError.Visible(problem != null);
                countLocked.Visible(!mayChoose);
                countRange.text = $"Minimum {min:N0}, up to {max:N0}";
                countRange.Visible(mayChoose && model != "n1");

                polygonsBox.Visible(caps.QuadAnyBase || caps.LowPoly);
                polygons.Set(ctx.Str("polygonType", "triangle"), false);
                string polygonsReason = null;
                if (quality != "low-poly" && !caps.QuadAnyBase)
                {
                    polygons.SetOptionEnabled("triangle", false, "Not used at this mesh quality.");
                    polygons.SetOptionEnabled("quadrilateral", false, "Not used at this mesh quality.");
                    polygonsReason = "Not used at this mesh quality.";
                }
                else
                {
                    polygons.SetOptionEnabled("triangle", true);
                    if (segment) polygonsReason = "Not available while segmenting.";
                    else if (gates?.AllowsQuadMesh == false) polygonsReason = "Quad meshes are available from Basic.";
                    polygons.SetOptionEnabled("quadrilateral", polygonsReason == null, polygonsReason);
                }
                polygonsLock.text = polygonsReason ?? "";
                polygonsLock.Visible(polygonsReason != null);

                pbr.Visible(textured);
                var pbrPrice = ctx.Bool("pbrMaterials", true) ? Price("pbr") : "";
                pbr.text = "PBR" + (pbrPrice != "" ? $"  · {pbrPrice}" : "");

                textureBox.Visible(caps.DetailedTexture && textured);
                textureStd.Visible(!caps.ExtremeTexture);
                textureExt.Visible(caps.ExtremeTexture);
                var textureQuality = ctx.Str("textureQuality", "standard");
                (caps.ExtremeTexture ? textureExt : textureStd).Set(textureQuality, false);
                textureHint.text = textureQuality switch
                {
                    "detailed" => $"Up to 4K resolution ({Price("detailed_texture")})",
                    "extreme" when caps.ExtremeTexture => $"Up to 8K resolution ({Price("extreme_texture")})",
                    _ => "Up to 2K resolution (free)",
                };

                var privateAllowed = gates?.AllowsPrivateGenerations != false;
                var availability = ctx.Str("availability", null);
                var privateOn = availability == null ? gates?.AllowsPrivateGenerations == true : availability == "private";
                isPrivate.SetValueWithoutNotify(privateAllowed && privateOn);
                isPrivate.SetEnabled(privateAllowed);
                isPrivate.text = privateAllowed ? "Keep this model private" : "Keep this model private (paid plan)";
                isPrivate.tooltip = privateAllowed
                    ? "Private models are yours alone. Public ones can appear in the community gallery."
                    : "Keeping a model private is available on a paid plan. Models made on your plan are public.";

                advanced.Visible(caps.ImageTuning);
                scale.SetEnabled(textured);
                exportUv.Visible(textured);
                delight.Visible(textured);
                fitBox.Visible(textured);
                fit.Set(ctx.Str("textureAlignment", "geometry"), false);
            });
        }
    }
}
