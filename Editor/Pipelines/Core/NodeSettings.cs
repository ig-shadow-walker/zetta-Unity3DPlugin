using System.Linq;
using Newtonsoft.Json.Linq;

namespace Zetta3D.Editor.Pipelines
{
    /// <summary>
    /// What each node holds in <c>settings</c> — the Studio form's own value, web-shaped — its defaults, and the
    /// line under its name. A port of node-config.ts defaultSettings / settingsWithDefaults / summaryFor.
    /// </summary>
    public static class NodeSettings
    {
        /// <summary>The value a node of this kind opens on — the Studio's own defaults (defaultSettings).</summary>
        public static JObject Defaults(string kind)
        {
            switch (kind)
            {
                case NodeKinds.SeedPicture:
                    return new JObject { ["source"] = null };
                case NodeKinds.SeedModel:
                case NodeKinds.SeedViews:
                    return new JObject();
                case NodeKinds.AiEdit:
                    return new JObject { ["prompt"] = "", ["model"] = null, ["for3D"] = true };
                case NodeKinds.DrawViews:
                    return new JObject { ["views"] = new JArray("back", "left", "right") };
                case NodeKinds.IsolateParts:
                    return ToolSettings.IsolateDefaults();
                case NodeKinds.Generate3D:
                case NodeKinds.Generate3DMultiview:
                {
                    // Spread, not applyFormPatch — so the count stays DEFAULT_FORM's 200,000 (see the report: on N2.5
                    // that reads as a custom count, exactly as on the web).
                    var form = GenerateForm.Defaults();
                    form["model"] = "n25";
                    return form;
                }
                case NodeKinds.GenerateSmart3D:
                case NodeKinds.GenerateSmart3DMultiview:
                    return GenerateForm.ApplyPatch(GenerateForm.Defaults(), new JObject { ["model"] = "np1" });
                case NodeKinds.GenerateSplat:
                {
                    var splat = ToolSettings.SplatDefaults();
                    splat["title"] = "";
                    return splat;
                }
                case NodeKinds.Retopology:
                    return ToolSettings.RetopologyDefaults();
                case NodeKinds.Segmentation:
                    return ToolSettings.SegmentDefaults();
                case NodeKinds.SmartSegmentation:
                {
                    var smart = ToolSettings.SegmentDefaults();
                    smart["mode"] = "smart";
                    return smart;
                }
                case NodeKinds.Texture:
                {
                    var texture = ToolSettings.TextureDefaults();
                    texture["prompt"] = "";
                    return texture;
                }
                case NodeKinds.TextureMultiview:
                {
                    var texture = ToolSettings.TextureDefaults();
                    texture["prompt"] = "";
                    texture["referenceViews"] = new JObject();
                    return texture;
                }
                case NodeKinds.SmartUv:
                    return ToolSettings.SmartUvDefaults();
                case NodeKinds.Rigging:
                    return ToolSettings.AutoRigDefaults();
                case NodeKinds.Export:
                    return new JObject { ["format"] = "glb" };
                default:
                    return new JObject();
            }
        }

        /// <summary>
        /// A node's settings over the Studio's defaults — for graphs that name only what they change (the agent's
        /// drafts). The Generate form goes through <see cref="GenerateForm.ApplyPatch"/>, so a named engine settles the
        /// options coupled to it exactly as picking it does (settingsWithDefaults). Returns a new object.
        /// </summary>
        public static JObject WithDefaults(string kind, JObject settings)
        {
            var defaults = Defaults(kind);
            if (settings == null) return defaults;
            if (NodeCatalog.IsGenerate(kind)) return GenerateForm.ApplyPatch(defaults, settings);
            foreach (var pair in settings) defaults[pair.Key] = pair.Value?.DeepClone();
            return defaults;
        }

        /// <summary>The settings a node is built from: its own, or its kind's defaults when it has none.</summary>
        public static JObject Of(PipelineNode node) => node.Settings ?? Defaults(node.Kind);

        /// <summary>
        /// Whether a step's input is given on each run rather than in the pipeline (setOnEachRun): AI texturing always
        /// is; AI edit when its "Set on each run" (settings.askAtRun) is on.
        /// </summary>
        public static bool SetOnEachRun(PipelineNode node) =>
            node.Kind == NodeKinds.Texture || node.Kind == NodeKinds.TextureMultiview ||
            (node.Kind == NodeKinds.AiEdit && node.Settings?["askAtRun"]?.Type == JTokenType.Boolean && node.Settings["askAtRun"].Value<bool>());

        /// <summary>
        /// <see cref="SetOnEachRun"/>, knowing the graph (perRunIn): Multi-view - AI texturing fed views asks nothing of a
        /// run — it textures from the pictures wired in.
        /// </summary>
        public static bool PerRunIn(PipelineGraph graph, PipelineNode node) =>
            !(node.Kind == NodeKinds.TextureMultiview && ViewsWired(graph, node.Id)) && SetOnEachRun(node);

        /// <summary>Whether something is wired into Multi-view - AI texturing's Views input.</summary>
        public static bool ViewsWired(PipelineGraph graph, string nodeId) =>
            graph?.Edges.Any(e => e.Target == nodeId && e.TargetHandle == "views") == true;

        /// <summary>One line under a node's name: what it is set to, in the Studio's words (summaryFor).</summary>
        public static string Summary(PipelineNode node)
        {
            var s = Of(node);
            string Text(string key) => s[key]?.Type == JTokenType.String ? s[key].ToString() : "";
            switch (node.Kind)
            {
                case NodeKinds.SeedPicture:
                    return "A picture, chosen for each run";
                case NodeKinds.SeedModel:
                    return "A model, chosen for each run";
                case NodeKinds.SeedViews:
                    return "Pictures of each side, chosen for each run";
                case NodeKinds.RemoveBackground:
                    return "Cut-out";
                case NodeKinds.AiEdit:
                {
                    if (s["askAtRun"]?.Type == JTokenType.Boolean && s["askAtRun"].Value<bool>() && Text("prompt") == "") return "Set on each run";
                    var prompt = Text("prompt");
                    return prompt == "" ? "Say how to change it" : Cut(prompt, 48);
                }
                case NodeKinds.DrawViews:
                {
                    var views = (s["views"] as JArray)?.Select(v => v.ToString()).ToList();
                    return views is { Count: > 0 } ? "Front + " + string.Join(", ", views) : "No views chosen";
                }
                case NodeKinds.IsolateParts:
                {
                    var v = NodeSettings.WithDefaults(node.Kind, node.Settings);
                    var sensitivity = v["sensitivity"]?.ToString() ?? "low";
                    var how = v["identify"]?.Type == JTokenType.Boolean && !v["identify"].Value<bool>()
                        ? "Your list"
                        : sensitivity.Length > 0 ? char.ToUpperInvariant(sensitivity[0]) + sensitivity.Substring(1) : sensitivity;
                    var pose = v["pose"]?.ToString() == "a" ? "A" : "T";
                    return $"{how} · up to {v["maxParts"]} · {pose}-pose";
                }
                case NodeKinds.Generate3D:
                case NodeKinds.GenerateSmart3D:
                case NodeKinds.Generate3DMultiview:
                case NodeKinds.GenerateSmart3DMultiview:
                    return $"{GenerateForm.ModelLabel(Text("model"))} · {(Text("output") == "geometry" ? "Geometry" : "Textured")}";
                case NodeKinds.GenerateSplat:
                {
                    var engine = Text("engine");
                    var label = engine switch { "n25" => "Zetta Splat", "zs2" => "Zetta Splat 2", "zs21" => "Zetta Splat 2.1", _ => engine };
                    return $"{label} · {(Text("subject") == "environment" ? "Environment" : "Object")}";
                }
                case NodeKinds.Retopology:
                {
                    var engine = Text("engine");
                    var label = ToolSettings.LabelOf(ToolSettings.RetopologyEngines, engine);
                    return engine == "r1"
                        ? $"{label} · {(ToolSettings.IsNumber(s["faces"]) ? GenerateForm.Count((long)s["faces"].Value<double>()) : s["faces"]?.ToString())} faces"
                        : $"{label} · {Text("faceLevel")}";
                }
                case NodeKinds.Segmentation:
                case NodeKinds.SmartSegmentation:
                    return Text("mode") == "smart" ? "Smart" : "AI segmentation";
                case NodeKinds.Texture:
                {
                    var tier = ToolSettings.TextureTierLabel(Text("tier"));
                    if (ToolSettings.Truthy(s["reference"])) return $"{tier} · from a picture";
                    var prompt = Text("prompt").Trim();
                    return prompt != "" ? $"{tier} · {Cut(prompt, 32)}" : $"{tier} · set on each run";
                }
                case NodeKinds.TextureMultiview:
                {
                    var tier = ToolSettings.TextureTierLabel(Text("tier"));
                    var given = ViewsRequest(s["referenceViews"] as JObject).Count;
                    return given > 0 ? $"{tier} · {given} view{(given == 1 ? "" : "s")}" : $"{tier} · pictures of each side";
                }
                case NodeKinds.SmartUv:
                    return ToolSettings.Truthy(s["bake"]) ? "Textures re-baked" : "Layout only";
                case NodeKinds.Rigging:
                    return $"{s["rigType"]} rig";
                case NodeKinds.Export:
                {
                    var format = Text("format").ToUpperInvariant();
                    return format == "" ? "GLB" : format;
                }
                default:
                    return "";
            }
        }

        static string Cut(string text, int length) => text.Length <= length ? text : text.Substring(0, length);

        /// <summary>The sides of a multi-view set, in the Studio's slot order (lib/multi-view.ts MULTI_VIEW_SLOTS).</summary>
        public static readonly string[] ViewKeys = { "front", "back", "left", "right" };

        /// <summary>
        /// A multi-view set as the server reads it (viewsRequest): each given side once, in the slots' order. The set is
        /// keyed by side — <c>{ front: { uploadKey | gen2dPostId, name, thumbnail } }</c>, as the web keeps it.
        /// </summary>
        public static JArray ViewsRequest(JObject set)
        {
            var list = new JArray();
            if (set == null) return list;
            foreach (var key in ViewKeys)
            {
                if (!(set[key] is JObject view)) continue;
                var upload = view["uploadKey"]?.Type == JTokenType.String ? view["uploadKey"].ToString() : null;
                var post = ToolSettings.IsNumber(view["gen2dPostId"]) ? view["gen2dPostId"] : null;
                if (upload == null && post == null) continue;
                var entry = new JObject { ["viewType"] = key };
                if (upload != null) entry["uploadKey"] = upload;
                else entry["gen2dPostId"] = post.DeepClone();
                if (view["name"]?.Type == JTokenType.String && view["name"].ToString() != "") entry["name"] = view["name"].ToString();
                list.Add(entry);
            }
            return list;
        }
    }
}
