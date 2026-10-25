using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Zetta3D.Editor.Pipelines
{
    /// <summary>A choice a settings form offers: the stored value, its label and a hint.</summary>
    public sealed class SettingOption
    {
        public string Value;
        public string Label;
        public string Hint;

        public SettingOption(string value, string label, string hint = null)
        {
            Value = value;
            Label = label;
            Hint = hint;
        }
    }

    /// <summary>
    /// The Studio tools' settings as data — web-shaped values, their defaults, the request each builds and the price
    /// each quotes. PURE PORTS of the web's <c>*-settings-value.ts</c> (retopology, segment, texture, smart-uv,
    /// auto-rig, splat) and <c>postProcessRequestBody</c> (hooks/use-post-process.ts), without <c>origin</c> or
    /// <c>sourcePostId</c> — the server adds those.
    ///
    /// NOTE FOR THE STUDIO PANELS: Editor/Studio's Retopology/Segment/Texture/SmartUv/AutoRig panels build the same
    /// bodies inline from their fields; they could call these instead (map the panel's fields to the web-shaped value),
    /// so a panel and a pipeline node can never send different requests for the same choice.
    /// </summary>
    public static class ToolSettings
    {
        // ─── Operation codes (lib/post-process.ts) ───────────────────────────────

        public const string SmartTopology = "smart_topology";
        public const string AiSegmentation = "ai_segmentation";
        public const string SmartSegmentationCode = "smart_segmentation";
        public const string Retexture = "retexture";
        public const string RetextureHd = "retexture_hd";
        public const string Retexture8K = "retexture_8k";
        public const string AutoRigging = "auto_rigging";
        public const string SmartUvCode = "smart_uv";
        public const string SplatBase = "gaussian_splat";
        public const string SplatModel = "n25";
        public const int SmartUvMaxTriangles = 80_000;
        public const int DefaultRetopologyFaces = 8_000;

        // ─── Choices, in the Studio's words ──────────────────────────────────────

        public static readonly SettingOption[] RetopologyEngines =
        {
            new SettingOption("r1", "Zetta R1", "Up to 20,000 polygons. Faster, and less than half the price."),
            new SettingOption("n1", "Zetta N1", "No polygon ceiling — the option for a dense mesh."),
        };

        public static readonly SettingOption[] FaceLevels =
        {
            new SettingOption("low", "Low", "Fewest polygons — game-ready."),
            new SettingOption("medium", "Medium", "A balance of the two."),
            new SettingOption("high", "High", "Keeps the most detail."),
        };

        public static readonly SettingOption[] PolygonTypes =
        {
            new SettingOption("triangle", "Triangles"),
            new SettingOption("quadrilateral", "Quads"),
        };

        public static readonly SettingOption[] SegmentModes =
        {
            new SettingOption("segment", "Segment", "Splits the model you have into named parts."),
            new SettingOption("smart", "Smart Segment", "Rebuilds as it splits, for cleaner parts on a rough or incomplete mesh."),
        };

        public static readonly SettingOption[] SegmentGranularities =
        {
            new SettingOption("simple", "Few parts", "Broad, obvious divisions."),
            new SettingOption("balanced", "Balanced", "The usual answer."),
            new SettingOption("detailed", "Max parts", "Finer, smaller pieces."),
        };

        /// <summary>The texture tiers — the value IS the price code (TEXTURE_TIERS).</summary>
        public static readonly SettingOption[] TextureTiers =
        {
            new SettingOption(Retexture, "Standard", "2K"),
            new SettingOption(RetextureHd, "Detailed", "4K"),
            new SettingOption(Retexture8K, "8K", "8K"),
        };

        public static readonly SettingOption[] TextureAlignments =
        {
            new SettingOption("original_image", "Fit texture to picture"),
            new SettingOption("geometry", "Fit texture to mesh"),
        };

        public static readonly SettingOption[] SmartUvOrientations =
        {
            new SettingOption("as-is", "As is"), new SettingOption("+x", "+X"), new SettingOption("-x", "−X"),
            new SettingOption("+y", "+Y"), new SettingOption("-y", "−Y"),
        };

        public static readonly SettingOption[] RigTypes =
        {
            new SettingOption("biped", "Humanoid", "Two legs, two arms. Character presets."),
            new SettingOption("quadruped", "Four legs", "Dogs, horses, most mammals."),
            new SettingOption("avian", "Bird", "Wings and a tail."),
            new SettingOption("serpentine", "Snake", "A long spine, no limbs."),
            new SettingOption("aquatic", "Fish", "Fins and a tail."),
            new SettingOption("hexapod", "Six legs", "Insects."),
            new SettingOption("octopod", "Eight legs", "Spiders and octopods."),
            new SettingOption("others", "Other creature", "A body none of these describe."),
        };

        public static readonly SettingOption[] RigSpecs =
        {
            new SettingOption("standard", "Standard", "The rigger’s own skeleton, finger bones included."),
            new SettingOption("mixamo", "Mixamo", "For Mixamo and libraries that follow it."),
            new SettingOption("unity", "Unity Humanoid", "Maps straight onto a Unity Humanoid avatar."),
            new SettingOption("unreal", "UE5 Mannequin", "Unreal Engine 5’s Mannequin skeleton."),
            new SettingOption("vrm", "VRM 1.0", "For VRM avatars and VTuber tools."),
            new SettingOption("actorcore", "ActorCore", "For ActorCore motion libraries."),
        };

        public static readonly SettingOption[] RigOutFormats = { new SettingOption("glb", "GLB"), new SettingOption("fbx", "FBX") };

        public static readonly SettingOption[] IsolateSensitivities =
        {
            new SettingOption("low", "Low", "The whole character, and each thing it holds — weapon, shield. Anything not humanoid: its main body and what is attached to it."),
            new SettingOption("medium", "Medium", "Also the head and the hands. Anything else: also its major parts — wheels, doors, wings, a tail."),
            new SettingOption("high", "High", "Everything it can tell apart — helmet, armour pieces, belt, cape, boots…"),
        };

        public static readonly SettingOption[] IsolatePoses = { new SettingOption("t", "T-pose"), new SettingOption("a", "A-pose") };

        public static readonly SettingOption[] DrawViewsChoices =
        {
            new SettingOption("back", "Back"), new SettingOption("left", "Left"), new SettingOption("right", "Right"),
        };

        public static readonly SettingOption[] ExportFormats =
        {
            new SettingOption("glb", "GLB"), new SettingOption("fbx", "FBX"), new SettingOption("obj", "OBJ"), new SettingOption("stl", "STL"),
        };

        public const int IsolateMaxPartsLimit = 16;

        public static string LabelOf(SettingOption[] options, string value) => options.FirstOrDefault(o => o.Value == value)?.Label ?? value;

        // ─── Defaults ────────────────────────────────────────────────────────────

        public static JObject RetopologyDefaults() => new JObject
        {
            ["engine"] = "r1", ["faceLevel"] = "medium", ["polygonType"] = "triangle", ["faces"] = DefaultRetopologyFaces, ["bake"] = true,
        };

        public static JObject SegmentDefaults() => new JObject
        {
            ["mode"] = "segment", ["granularity"] = "balanced", ["splitByConnectivity"] = true, ["hint"] = "",
        };

        public static JObject TextureDefaults() => new JObject
        {
            ["tier"] = Retexture, ["alignment"] = "original_image", ["bake"] = true, ["pbr"] = true, ["fast"] = false, ["delight"] = true, ["removeBg"] = true,
        };

        public static JObject SmartUvDefaults() => new JObject { ["bake"] = true, ["orientation"] = "as-is", ["compress"] = false };

        public static JObject AutoRigDefaults() => new JObject { ["rigType"] = "biped", ["spec"] = "standard", ["outFormat"] = "glb" };

        public static JObject SplatDefaults() => new JObject
        {
            ["subject"] = "object", ["engine"] = SplatModel, ["removeBackground"] = true, ["ratio"] = "16:9", ["resolution"] = "1K",
        };

        public static JObject IsolateDefaults() => new JObject
        {
            ["identify"] = true, ["sensitivity"] = "low", ["maxParts"] = 6, ["pose"] = "t", ["extraParts"] = new JArray(), ["model"] = null,
        };

        // ─── Value helpers (JS truthiness, on purpose) ──────────────────────────

        internal static bool Truthy(JToken token) => token != null && token.Type switch
        {
            JTokenType.Boolean => token.Value<bool>(),
            JTokenType.String => token.ToString() != "",
            JTokenType.Integer => token.Value<long>() != 0,
            JTokenType.Float => token.Value<double>() != 0 && !double.IsNaN(token.Value<double>()),
            JTokenType.Null or JTokenType.Undefined => false,
            _ => true,
        };

        internal static string Text(JObject value, string key) => value?[key]?.Type == JTokenType.String ? value[key].ToString() : null;

        internal static bool IsNumber(JToken token) => token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float);

        static JObject Merge(JObject defaults, JObject value)
        {
            if (value != null)
                foreach (var pair in value)
                    defaults[pair.Key] = pair.Value?.DeepClone();
            return defaults;
        }

        /// <summary>postProcessRequestBody's opening: the spec with its one operation code.</summary>
        static JObject Body(string code) => new JObject { ["spec"] = new JObject { ["postProcess"] = new JArray(code) } };

        // ─── Retopology ──────────────────────────────────────────────────────────

        public static (int min, int max) RetopologyBounds(string polygonType) =>
            polygonType == "quadrilateral" ? (500, 10_000) : (500, 20_000);

        /// <summary>retopologyOutOfRange: an R1 budget outside the range (only R1 takes a count).</summary>
        public static bool RetopologyOutOfRange(JObject value)
        {
            var v = Merge(RetopologyDefaults(), value);
            if (Text(v, "engine") != "r1") return false;
            var faces = v["faces"];
            if (!IsNumber(faces)) return true;
            var count = faces.Value<double>();
            var (min, max) = RetopologyBounds(Text(v, "polygonType"));
            return double.IsNaN(count) || double.IsInfinity(count) || count < min || count > max;
        }

        public static string RetopologyRangeProblem(JObject value)
        {
            if (!RetopologyOutOfRange(value)) return null;
            var (min, max) = RetopologyBounds(Text(Merge(RetopologyDefaults(), value), "polygonType"));
            return $"The polygon count must be between {GenerateForm.Count(min)} and {GenerateForm.Count(max)}.";
        }

        /// <summary>retopologyRequest → postProcessRequestBody: R1 sends faceCount (and bake only as false); N1 a faceLevel.</summary>
        public static JObject RetopologyRequest(JObject value)
        {
            var v = Merge(RetopologyDefaults(), value);
            var body = Body(SmartTopology);
            var engine = v["engine"];
            if (Truthy(engine)) body["model"] = engine.DeepClone();
            if (Text(v, "engine") == "r1")
            {
                if (IsNumber(v["faces"])) body["faceCount"] = v["faces"].DeepClone();
            }
            else if (Truthy(v["faceLevel"])) body["faceLevel"] = v["faceLevel"].DeepClone();
            if (Truthy(v["polygonType"])) body["polygonType"] = v["polygonType"].DeepClone();
            if (Text(v, "engine") == "r1" && v["bake"]?.Type == JTokenType.Boolean && !v["bake"].Value<bool>()) body["bake"] = false;
            return body;
        }

        public static int? RetopologyPrice(PriceList prices, JObject value) => prices?.Price(SmartTopology, Text(Merge(RetopologyDefaults(), value), "engine"));

        // ─── Segmentation ────────────────────────────────────────────────────────

        public static string SegmentCode(JObject value) => Text(Merge(SegmentDefaults(), value), "mode") == "smart" ? SmartSegmentationCode : AiSegmentation;

        /// <summary>segmentRequest: one shape per tab — Smart sends its hint as the prompt, Segment sends splitByConnectivity only as false.</summary>
        public static JObject SegmentRequest(JObject value)
        {
            var v = Merge(SegmentDefaults(), value);
            var body = Body(SegmentCode(v));
            if (Text(v, "mode") == "smart")
            {
                var hint = (Text(v, "hint") ?? "").Trim();
                if (hint != "") body["prompt"] = hint;
            }
            if (Truthy(v["granularity"])) body["segmentGranularity"] = v["granularity"].DeepClone();
            if (Text(v, "mode") != "smart" && !Truthy(v["splitByConnectivity"])) body["splitByConnectivity"] = false;
            return body;
        }

        public static int? SegmentPrice(PriceList prices, JObject value) => prices?.Price(SegmentCode(value), null);

        // ─── AI texturing ────────────────────────────────────────────────────────

        /// <summary>
        /// textureRequest plus the node's own description (a picture replaces it) — what postProcessRequestBody
        /// sends. The node's reference picture and its cut-out are added by <see cref="RequestBuilder"/>.
        /// </summary>
        public static JObject TextureRequest(JObject value, string prompt = null)
        {
            var v = Merge(TextureDefaults(), value);
            var tier = Text(v, "tier") ?? v["tier"]?.ToString();
            var body = Body(tier);
            var text = (prompt ?? "").Trim();
            if (text != "") body["prompt"] = text;
            if (Truthy(v["alignment"])) body["textureAlignment"] = v["alignment"].DeepClone();
            if (!Truthy(v["bake"])) body["bake"] = false;
            if (v["pbr"] != null) body["enablePBR"] = v["pbr"].DeepClone();
            if (Truthy(v["fast"]) && tier == Retexture) body["textureFast"] = true;
            if (v["delight"] != null) body["delight"] = v["delight"].DeepClone();
            return body;
        }

        public static int? TexturePrice(PriceList prices, JObject value) => prices?.Price(Text(Merge(TextureDefaults(), value), "tier"), null);

        public static string TextureTierLabel(string tier) => TextureTiers.FirstOrDefault(t => t.Value == tier)?.Label ?? "Standard";

        // ─── Smart UV ────────────────────────────────────────────────────────────

        /// <summary>smartUvRequest: defaults travel as silence.</summary>
        public static JObject SmartUvRequest(JObject value)
        {
            var v = Merge(SmartUvDefaults(), value);
            var body = Body(SmartUvCode);
            var orientation = Text(v, "orientation");
            if (orientation != "as-is" && Truthy(v["orientation"])) body["exportOrientation"] = v["orientation"].DeepClone();
            if (Truthy(v["compress"])) body["compressGeometry"] = true;
            if (!Truthy(v["bake"])) body["bake"] = false;
            return body;
        }

        public static int? SmartUvPrice(PriceList prices) => prices?.Price(SmartUvCode, null);

        // ─── Auto rig ────────────────────────────────────────────────────────────

        /// <summary>autoRigRequest: each choice sent only when it is not the vendor's default.</summary>
        public static JObject AutoRigRequest(JObject value)
        {
            var v = Merge(AutoRigDefaults(), value);
            var body = Body(AutoRigging);
            if (Text(v, "rigType") != "biped" && Truthy(v["rigType"])) body["rigType"] = v["rigType"].DeepClone();
            // Skeleton presets are humanoid skeletons: sent only for a humanoid, and only a preset offered.
            var spec = Text(v, "spec");
            if (Text(v, "rigType") == "biped" && spec != "standard" && RigSpecs.Any(o => o.Value == spec)) body["rigSpec"] = spec;
            if (Text(v, "outFormat") != "glb" && Truthy(v["outFormat"])) body["rigOutFormat"] = v["outFormat"].DeepClone();
            return body;
        }

        public static int? AutoRigPrice(PriceList prices) => prices?.Price(AutoRigging, null);

        public static string RigTypeLabel(string rigType) => LabelOf(RigTypes, rigType);

        // ─── Splat (web-only; priced and rebuilt only so a graph that has one stays whole) ─────────

        public static JObject SplatRequest(JObject value)
        {
            var v = Merge(SplatDefaults(), value);
            var engine = Text(v, "engine");
            var cutOut = Text(v, "subject") == "object" && engine == SplatModel;
            var body = new JObject
            {
                ["spec"] = new JObject { ["generation"] = new JObject { ["base"] = SplatBase, ["modifiers"] = new JArray() } },
                ["model"] = v["engine"]?.DeepClone(),
                ["removeBackground"] = cutOut && Truthy(v["removeBackground"]),
            };
            var title = (Text(v, "title") ?? "").Trim();
            if (title != "") body["title"] = title;
            return body;
        }

        public static int? SplatPrice(PriceList prices, JObject value) =>
            prices?.Total(Text(Merge(SplatDefaults(), value), "engine"), new[] { SplatBase });
    }
}
