using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// The pure halves of the agent's paid tool (process_model): input
    /// validation and the exact job bodies, with no network and no UI. The wire rules are the web Studio's
    /// and the Unity Studio panels' (RetopologyPanel, TexturePanel, AutoRigPanel, SegmentPanel) — only the
    /// fields those send, and only in the combinations the server accepts, because a refused combination
    /// is found after the charge. Public so tests can assert the bodies.
    /// </summary>
    public static class AgentRequestBuilders
    {
        // ─── process_model ──────────────────────────────────────────────────────

        public static readonly string[] Operations = { "retopologize", "smart_uv", "texture", "rig", "segment" };

        public const int RetopologyMin = 500;
        public const int RetopologyMaxTriangles = 20_000;
        public const int RetopologyMaxQuads = 10_000;
        /// <summary>The web Studio's starting count (DEFAULT_RETOPOLOGY_FACES): under both ceilings.</summary>
        public const int RetopologyDefaultCount = 8_000;

        static readonly Dictionary<string, string[]> AllowedOptions = new Dictionary<string, string[]>
        {
            ["retopologize"] = new[] { "engine", "model", "polygonType", "faceCount", "detail", "faceLevel", "bake" },
            ["smart_uv"] = new[] { "bake", "exportOrientation", "compressGeometry" },
            ["texture"] = new[] { "prompt", "imageId", "quality", "pbr", "fast", "textureAlignment", "delight" },
            ["rig"] = new[] { "rigType", "rigSpec", "rigOutFormat" },
            ["segment"] = new[] { "smart", "granularity", "hint", "splitByConnectivity" },
        };

        /// <summary>A validated post-process: what to bill, what to send, and how to say it.</summary>
        public class ProcessPlan
        {
            /// <summary>retopologize | smart_uv | texture | rig | segment</summary>
            public string Operation;
            /// <summary>The server's operation code (smart_topology, smart_uv, retexture…, auto_rigging, ai_/smart_segmentation).</summary>
            public string Code;
            /// <summary>The model the price row is looked up with — only smart_topology has model rows (r1 / n1).</summary>
            public string PriceModel;
            /// <summary>The job's fields besides spec / sourcePostId (a texture's imageUrl is added later).</summary>
            public JObject Options = new JObject();
            /// <summary>Plain-language lines for the approval card.</summary>
            public List<string> Summary = new List<string>();
            /// <summary>texture: the surface description, drawn into a reference picture before the job.</summary>
            public string Prompt = "";
            /// <summary>texture: the reference picture (from generate_image).</summary>
            public long? ImageId;

            public bool NeedsPicture => Operation == "texture";
        }

        public static string NormalizeOperation(string operation)
        {
            var op = (operation ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            return op switch
            {
                "retopologize" or "retopology" or "retopo" or "smart_topology" or "remesh" => "retopologize",
                "smart_uv" or "uv" or "uvs" or "uv_unwrap" or "unwrap" => "smart_uv",
                "texture" or "retexture" or "texturing" or "paint" => "texture",
                "rig" or "auto_rig" or "autorig" or "auto_rigging" or "rigging" => "rig",
                "segment" or "segmentation" or "ai_segmentation" or "smart_segmentation" or "split" => "segment",
                _ => throw new ToolException($"'operation' must be one of {string.Join(", ", Operations)}."),
            };
        }

        /// <summary>
        /// Validates an operation's options and returns the plan. <paramref name="options"/> keys are the
        /// protocol's (the Unity agent protocol, process_model); unknown keys are refused rather
        /// than dropped, so the model learns what is not honoured before anything is charged.
        /// </summary>
        public static ProcessPlan Process(string operation, JObject options)
        {
            var op = NormalizeOperation(operation);
            options ??= new JObject();
            var allowed = AllowedOptions[op];
            var unknown = options.Properties().Select(p => p.Name).Where(k => !allowed.Contains(k)).ToList();
            if (unknown.Count > 0)
                throw new ToolException($"Unknown option{(unknown.Count > 1 ? "s" : "")} {string.Join(", ", unknown.Select(u => $"'{u}'"))} for {op}. It takes: {string.Join(", ", allowed)}.");

            var plan = new ProcessPlan { Operation = op };
            switch (op)
            {
                case "retopologize": Retopologize(options, plan); break;
                case "smart_uv": SmartUv(options, plan); break;
                case "texture": Texture(options, plan); break;
                case "rig": Rig(options, plan); break;
                default: Segment(options, plan); break;
            }
            return plan;
        }

        static string Pick(JObject o, string key, string[] allowed, string fallback, Func<string, string> alias = null)
        {
            var v = Args.Str(o, key);
            if (string.IsNullOrWhiteSpace(v)) return fallback;
            v = v.Trim().ToLowerInvariant();
            if (alias != null) v = alias(v);
            if (!allowed.Contains(v)) throw new ToolException($"'{key}' must be one of {string.Join(", ", allowed)}.");
            return v;
        }

        static string PolygonAlias(string v) => v switch
        {
            "quad" or "quads" => "quadrilateral",
            "tri" or "tris" or "triangles" => "triangle",
            _ => v,
        };

        static void Retopologize(JObject o, ProcessPlan plan)
        {
            if (Args.Present(o, "engine") && Args.Present(o, "model") &&
                !string.Equals(Args.Str(o, "engine")?.Trim(), Args.Str(o, "model")?.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new ToolException("Give the engine once ('engine': r1 or n1).");
            var engine = Pick(o, Args.Present(o, "engine") ? "engine" : "model", new[] { "r1", "n1" }, "r1");
            var polygonType = Pick(o, "polygonType", new[] { "triangle", "quadrilateral" }, "triangle", PolygonAlias);
            var quads = polygonType == "quadrilateral";
            var levelKey = Args.Present(o, "detail") ? "detail" : "faceLevel";
            var level = Pick(o, levelKey, new[] { "low", "medium", "high" }, null);
            var countToken = Args.Get(o, "faceCount");
            var count = Args.ToInt(countToken);
            if (countToken != null && count == null) throw new ToolException("'faceCount' must be a whole number.");
            var bake = Args.BoolN(o, "bake");

            plan.Code = "smart_topology";
            plan.PriceModel = engine;
            // The Studio's order: model, polygonType, then the density field, then bake.
            plan.Options["model"] = engine;
            plan.Options["polygonType"] = polygonType;

            if (engine == "r1")
            {
                if (level != null)
                    throw new ToolException("'detail' (low/medium/high) is a Zetta N1 option. Zetta R1 takes 'faceCount' (500–20,000) — send one or the other, or set engine \"n1\".");
                var max = quads ? RetopologyMaxQuads : RetopologyMaxTriangles;
                var faces = count ?? RetopologyDefaultCount;
                if (faces < RetopologyMin || faces > max)
                    throw new ToolException($"'faceCount' must be between {RetopologyMin:N0} and {max:N0} for Zetta R1{(quads ? " in quads (quads halve the ceiling)" : "")}." +
                                            (faces > max ? " For a denser mesh use engine \"n1\" with 'detail'." : ""));
                plan.Options["faceCount"] = faces;
                // True by default at the engine: only "off" travels.
                if (bake == false) plan.Options["bake"] = false;
                plan.Summary.Add($"Zetta R1 · {faces:N0} {(quads ? "quads" : "triangles")}{(count == null ? " (default)" : "")}");
                plan.Summary.Add(bake == false ? "Bare geometry (textures not baked onto the new mesh)" : "The original textures are baked onto the rebuilt mesh");
            }
            else
            {
                if (count != null)
                    throw new ToolException("'faceCount' is a Zetta R1 option — Zetta N1 takes 'detail' (low, medium or high) instead.");
                if (bake != null)
                    throw new ToolException("'bake' is a Zetta R1 option — Zetta N1 rebuilds topology without one.");
                var faceLevel = level ?? "medium";
                plan.Options["faceLevel"] = faceLevel;
                plan.Summary.Add($"Zetta N1 · {faceLevel} detail · {(quads ? "quads" : "triangles")}");
            }
        }

        static void SmartUv(JObject o, ProcessPlan plan)
        {
            var bake = Args.BoolN(o, "bake");
            var orientation = Args.Str(o, "exportOrientation");
            var compress = Args.BoolN(o, "compressGeometry");
            plan.Code = "smart_uv";
            if (!string.IsNullOrWhiteSpace(orientation))
            {
                orientation = orientation.Trim().ToLowerInvariant();
                if (orientation == "x" || orientation == "y") orientation = "+" + orientation;
                if (!ZettaCatalog.Orientations.Contains(orientation))
                    throw new ToolException($"'exportOrientation' must be one of {string.Join(", ", ZettaCatalog.Orientations)} (leave it out to keep the model's own facing).");
            }
            else orientation = "as-is";
            // The Studio panel's own rule: defaults travel as silence (bake only as false, a turn only when given, compression only when on).
            plan.Options = Studio.SmartUvPanel.Options(orientation, bake != false, compress == true);

            plan.Summary.Add(bake == false ? "A clean new UV layout, bare (textures not re-baked)" : "A clean new UV layout, with the textures, rig and animation carried across");
            if (plan.Options["exportOrientation"] != null) plan.Summary.Add($"Facing {orientation}");
            if (compress == true) plan.Summary.Add("Geometry compression (smaller file)");
        }

        /// <summary>Smart UV refuses sources over 80,000 triangles (a quad counts as two); null when the count is fine or unknown.</summary>
        public static string SmartUvProblem(Generation source)
        {
            if (Studio.SmartUvPanel.Triangles(source) is not { } triangles || triangles <= ZettaCatalog.SmartUvMaxTriangles) return null;
            var faces = source.FaceCount ?? 0;
            var quads = source.PolygonType == "quadrilateral";
            return $"#{source.Id} has {triangles:N0} triangles{(quads ? $" ({faces:N0} quads, each counting as two)" : "")}; Smart UV takes at most {ZettaCatalog.SmartUvMaxTriangles:N0}. " +
                   "Retopologize it first (process_model operation retopologize), then run smart_uv on the result.";
        }

        static readonly string[] TextureQualities = { "standard", "hd", "8k" };

        public static string TextureCode(string quality) => quality switch
        {
            "hd" => "retexture_hd",
            "8k" => "retexture_8k",
            _ => "retexture",
        };

        static void Texture(JObject o, ProcessPlan plan)
        {
            var prompt = (Args.Str(o, "prompt") ?? "").Trim();
            if (prompt.Length > 1024) throw new ToolException("'prompt' is longer than 1,024 characters.");
            var imageToken = Args.Get(o, "imageId");
            var imageId = Args.LongN(o, "imageId");
            if (imageToken != null && imageId == null) throw new ToolException("'imageId' must be the number generate_image returned.");
            if (prompt.Length == 0 && imageId == null)
                throw new ToolException("texture needs a 'prompt' describing the surface, or an 'imageId' from generate_image (or both: the picture, changed as described).");

            var quality = Pick(o, "quality", TextureQualities, "standard", v => v switch
            {
                "detailed" or "4k" => "hd",
                "extreme" => "8k",
                "2k" or "normal" => "standard",
                _ => v,
            });
            var pbr = Args.Bool(o, "pbr", true);
            var fast = Args.BoolN(o, "fast");
            if (fast == true && quality != "standard")
                throw new ToolException("'fast' only applies to standard quality (it is the quicker standard tier).");
            var alignment = Args.Str(o, "textureAlignment");
            var delight = Args.BoolN(o, "delight");

            plan.Code = TextureCode(quality);
            plan.Prompt = prompt;
            plan.ImageId = imageId;
            if (!string.IsNullOrWhiteSpace(alignment))
            {
                alignment = alignment.Trim().ToLowerInvariant();
                if (alignment is "image" or "picture") alignment = "original_image";
                if (alignment is "mesh") alignment = "geometry";
                if (alignment != "original_image" && alignment != "geometry")
                    throw new ToolException("'textureAlignment' must be original_image (fit to the picture) or geometry (fit to the mesh).");
                plan.Options["textureAlignment"] = alignment;
            }
            plan.Options["enablePBR"] = pbr;
            // On by default at the engine; the Studio sends it whichever way it is set, so an explicit "off" is honoured.
            if (delight != null) plan.Options["delight"] = delight.Value;
            if (fast == true) plan.Options["textureFast"] = true;

            var resolution = quality switch { "hd" => "4K", "8k" => "8K", _ => "2K" };
            var line = $"{(quality == "standard" ? "Standard" : quality == "hd" ? "Detailed" : "8K")} texture (up to {resolution})";
            if (fast == true) line += ", faster";
            line += pbr ? " · PBR maps" : " · colour only";
            if (plan.Options["textureAlignment"] != null) line += alignment == "geometry" ? " · fit to the mesh" : " · fit to the picture";
            if (delight == true) line += " · baked-in lighting removed";
            if (delight == false) line += " · keeps the picture's lighting";
            plan.Summary.Add(line);
            if (imageId != null) plan.Summary.Add($"Reference: picture #{imageId}");
            if (prompt.Length > 0)
            {
                plan.Summary.Add($"Surface: {Fmt.Short(prompt, 300)}");
                plan.Summary.Add(imageId != null
                    ? "The picture is changed as described first, then applied."
                    : "A reference picture is drawn from the description first, then applied.");
            }
        }

        static void Rig(JObject o, ProcessPlan plan)
        {
            var rigType = Pick(o, "rigType", ZettaCatalog.RigTypes, "biped", v => v switch
            {
                "humanoid" or "human" => "biped",
                "bird" => "avian",
                "snake" => "serpentine",
                "fish" => "aquatic",
                _ => v,
            });
            var rigSpec = Pick(o, "rigSpec", ZettaCatalog.RigSpecs, "standard", v => v switch
            {
                "ue5" or "unreal engine" or "mannequin" => "unreal",
                "unity humanoid" or "mecanim" => "unity",
                _ => v,
            });
            // A preset is a humanoid skeleton — on any other body it is not sent.
            if (rigType != ZettaCatalog.HumanoidRigType) rigSpec = "standard";
            var format = Pick(o, "rigOutFormat", new[] { "glb", "fbx" }, "fbx");

            plan.Code = "auto_rigging";
            // Each sent only when it differs from the web default (biped, the rigger's own naming, GLB).
            if (rigType != "biped") plan.Options["rigType"] = rigType;
            if (rigSpec != "standard") plan.Options["rigSpec"] = rigSpec;
            if (format != "glb") plan.Options["rigOutFormat"] = format;

            var body = ZettaCatalog.RigTypeLabels[Array.IndexOf(ZettaCatalog.RigTypes, rigType)];
            plan.Summary.Add($"{body} skeleton · {(rigSpec == "standard" ? "standard" : ZettaCatalog.RigSpecLabels[Array.IndexOf(ZettaCatalog.RigSpecs, rigSpec)])} bone naming · {format.ToUpperInvariant()} file");
            if (format == "fbx") plan.Summary.Add("FBX imports with its skeleton — set its Rig to Humanoid for Mecanim.");
        }

        static void Segment(JObject o, ProcessPlan plan)
        {
            var smart = Args.Bool(o, "smart", false);
            var granularity = Pick(o, "granularity", new[] { "simple", "balanced", "detailed" }, "balanced", v => v switch
            {
                "few" or "coarse" => "simple",
                "max" or "fine" or "many" => "detailed",
                "medium" => "balanced",
                _ => v,
            });
            var split = Args.BoolN(o, "splitByConnectivity");
            var hint = (Args.Str(o, "hint") ?? "").Trim();
            if (smart && split != null) throw new ToolException("'splitByConnectivity' applies to plain segmentation, not smart: true.");
            if (!smart && hint.Length > 0) throw new ToolException("'hint' applies to smart segmentation — set smart: true, or leave the hint out.");
            if (hint.Length > 1024) throw new ToolException("'hint' is longer than 1,024 characters.");

            plan.Code = smart ? "smart_segmentation" : "ai_segmentation";
            plan.Options["segmentGranularity"] = granularity;
            if (!smart)
            {
                if (split == false) plan.Options["splitByConnectivity"] = false;
            }
            else if (hint.Length > 0) plan.Options["prompt"] = hint;

            var parts = granularity switch { "simple" => "few parts", "detailed" => "as many parts as it finds", _ => "a balanced number of parts" };
            plan.Summary.Add($"{(smart ? "Smart Segment (rebuilds as it splits)" : "Segment")} · {parts}");
            if (!smart) plan.Summary.Add(split == false ? "Pieces that are not joined stay together" : "Pieces that are not joined become separate parts");
            if (hint.Length > 0) plan.Summary.Add($"What it is: {Fmt.Short(hint, 200)}");
        }

        /// <summary>Why a row cannot be post-processed by this operation, or null.</summary>
        public static string RefuseSource(string operation, Generation source)
        {
            if (source == null) return "The source model was not found.";
            var id = source.Id;
            if (!source.IsSuccess)
                return $"#{id} is {source.StatusLabel.ToLowerInvariant()} ({source.Status}); only finished models can be processed." + (source.IsRunning ? " Wait for it to finish." : "");
            if (source.IsSplat)
                return $"#{id} is a Gaussian splat ({ZettaCatalog.FeatureLabel(source.Feature)}), not a mesh — process_model works on meshes only. {ZettaImporter.SplatUnsupported}";
            if (!ZettaCatalog.ProcessableFeatures.Contains(source.Feature ?? ""))
                return $"#{id} is a {ZettaCatalog.FeatureLabel(source.Feature)} result, which cannot be processed further. Pick a generated, uploaded, retopologized or Smart UV model.";
            var op = NormalizeOperation(operation);
            if (op == "retopologize" && source.Feature == "retopology") return $"#{id} is already a retopology result.";
            if (op == "smart_uv" && source.Feature == "uvUnwrap") return $"#{id} is already a Smart UV result.";
            return null;
        }

        /// <summary>The job body: the plan's options (plus a texture's uploaded reference) under spec.postProcess.</summary>
        public static JObject ProcessBody(ProcessPlan plan, long sourcePostId, string imageUrl = null)
        {
            var options = (JObject)plan.Options.DeepClone();
            if (plan.NeedsPicture)
            {
                if (string.IsNullOrWhiteSpace(imageUrl)) throw new ToolException("The texture's reference picture is missing.");
                options["imageUrl"] = imageUrl;
            }
            return ZettaRequests.PostProcess(plan.Code, sourcePostId, options);
        }

        public static string OperationLabel(string operation) => operation switch
        {
            "retopologize" => "Retopology",
            "smart_uv" => "Smart UV",
            "texture" => "AI Texturing",
            "rig" => "Auto Rig",
            "segment" => "Segmentation",
            _ => "Processing",
        };
    }
}
