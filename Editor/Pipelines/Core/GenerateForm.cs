using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Zetta3D.Editor.Pipelines
{
    /// <summary>
    /// The web Studio's Generate form (<c>Gen3DForm</c>), as a Generate / Generate Smart 3D node keeps it in
    /// <c>settings</c> — web-shaped JSON, so a graph means the same thing in both editors.
    ///
    /// A PORT OF THE FORM RULES the request builder does not own (Zetta3D-Frontend src/lib/gen3d-request.ts:
    /// applyFormPatch, polygonBounds, defaultPolygonCount, hasCustomPolygonCount, parameterProblems, engineFields'
    /// polygonType rule), plus the mapping onto the plugin's <see cref="GenerateOptions"/> so the body itself comes
    /// from <see cref="ZettaRequests.Build"/>. Capabilities are the web's MODEL_CAPABILITIES.
    /// </summary>
    public static class GenerateForm
    {
        public const int PolygonMin = 3_100;
        public const int PolygonMax = 1_480_000;
        const int LowPolyCeiling = 15_000;
        const int UltraFloor = 1_000_000;

        /// <summary>DEFAULT_FORM (generate-options.ts), field for field.</summary>
        public static JObject Defaults() => new JObject
        {
            ["quality"] = "high-res",
            ["output"] = "textured",
            ["pbrMaterials"] = true,
            ["polygonCount"] = "200000",
            ["polygonType"] = "triangle",
            ["textureQuality"] = "standard",
            ["segmentParts"] = false,
            ["realWorldScale"] = false,
            ["textureAlignment"] = "geometry",
            ["delight"] = false,
            ["availability"] = null,
            ["autofixImage"] = false,
            ["compressGeometry"] = false,
            ["exportUv"] = true,
            ["imageFromPrompt"] = false,
            ["removeBackground"] = true,
            ["controlMode"] = true,
            ["model"] = "n1",
            ["imageUrl"] = "",
            ["prompt"] = "",
            ["title"] = "",
        };

        // ─── Capabilities (model-capabilities.ts) ─────────────────────────────────

        sealed class Caps
        {
            public bool LowPoly, BareLowPoly, QuadAnyBase, ExtremeTexture, CustomFaceCount = true;
            public (int min, int max)? FaceRange, QuadFaceRange;
            public Dictionary<string, int> Ceiling, Floor, QuadCeiling, QuadFloor;
        }

        static readonly Dictionary<string, int> NoRungs = null;

        static Caps CapsOf(string model) => model switch
        {
            "n25" => new Caps
            {
                LowPoly = true, BareLowPoly = true, QuadAnyBase = true,
                Ceiling = new Dictionary<string, int> { ["low-poly"] = 20_000, ["high-res"] = 1_000_000, ["ultra"] = 2_000_000 },
                Floor = new Dictionary<string, int> { ["low-poly"] = 1_000, ["high-res"] = 1_000, ["ultra"] = 1_000 },
                QuadCeiling = new Dictionary<string, int> { ["low-poly"] = 10_000, ["high-res"] = 150_000, ["ultra"] = 150_000 },
                QuadFloor = new Dictionary<string, int> { ["low-poly"] = 500, ["high-res"] = 1_000, ["ultra"] = 1_000 },
            },
            "np1" => new Caps { ExtremeTexture = true, FaceRange = (48, 20_000) },
            "np15" => new Caps { ExtremeTexture = true, QuadAnyBase = true, FaceRange = (48, 50_000), QuadFaceRange = (48, 25_000) },
            "n1" => new Caps { LowPoly = true, Ceiling = NoRungs },
            // r1 / zs2 / zs21 and anything unknown: no generation options at all.
            _ => new Caps { CustomFaceCount = false },
        };

        // ─── Reading the form ─────────────────────────────────────────────────────

        static string Str(JObject form, string key) => form?[key]?.Type == JTokenType.String ? form[key].ToString() : Defaults()[key]?.Type == JTokenType.String ? Defaults()[key].ToString() : null;

        /// <summary>A form field as text — polygonCount may arrive as a number from the agent.</summary>
        static string Text(JObject form, string key)
        {
            var token = form?[key];
            if (token == null || token.Type == JTokenType.Null) return Str(null, key) ?? "";
            if (token.Type == JTokenType.Integer) return token.Value<long>().ToString(CultureInfo.InvariantCulture);
            if (token.Type == JTokenType.Float) return token.Value<double>().ToString(CultureInfo.InvariantCulture);
            return token.ToString();
        }

        static bool Flag(JObject form, string key)
        {
            var token = form?[key];
            if (token == null || token.Type == JTokenType.Null) return Defaults()[key]?.Type == JTokenType.Boolean && Defaults()[key].Value<bool>();
            return token.Type == JTokenType.Boolean ? token.Value<bool>() : Truthy(token);
        }

        static bool Truthy(JToken token) => token.Type switch
        {
            JTokenType.Boolean => token.Value<bool>(),
            JTokenType.String => token.ToString() != "",
            JTokenType.Integer => token.Value<long>() != 0,
            JTokenType.Float => token.Value<double>() != 0,
            JTokenType.Null or JTokenType.Undefined => false,
            _ => true,
        };

        /// <summary>The JS <c>Number(raw)</c> of a count field: null when it is not a number at all.</summary>
        static double? JsNumber(string raw)
        {
            var trimmed = (raw ?? "").Trim();
            if (trimmed == "") return 0;
            return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : (double?)null;
        }

        static bool IsInteger(double? value) => value.HasValue && !double.IsInfinity(value.Value) && System.Math.Floor(value.Value) == value.Value;

        // ─── The rules ───────────────────────────────────────────────────────────

        /// <summary>polygonBounds: the count range the form allows for its model, rung and topology.</summary>
        public static (int min, int max) PolygonBounds(JObject form)
        {
            var model = Str(form, "model");
            var caps = CapsOf(model);
            var quads = Str(form, "polygonType") == "quadrilateral";
            if (caps.FaceRange.HasValue) return quads && caps.QuadFaceRange.HasValue ? caps.QuadFaceRange.Value : caps.FaceRange.Value;
            var quality = Str(form, "quality");
            var ceilings = quads ? caps.QuadCeiling : caps.Ceiling;
            var floors = quads ? caps.QuadFloor : caps.Floor;
            var min = floors != null && floors.TryGetValue(quality, out var f) ? f : PolygonMin;
            var max = ceilings != null && ceilings.TryGetValue(quality, out var c) ? c : PolygonMax;
            return (min, max);
        }

        /// <summary>QUALITY_POLYGONS.</summary>
        public static int QualityPolygons(string quality) => quality switch
        {
            "low-poly" => 9_000,
            "ultra" => 1_000_000,
            _ => 200_000,
        };

        /// <summary>defaultPolygonCount: what a rung fills the field with — the count that is NOT custom.</summary>
        public static int DefaultPolygonCount(JObject form)
        {
            var caps = CapsOf(Str(form, "model"));
            if (caps.FaceRange.HasValue || caps.Ceiling != null) return PolygonBounds(form).max;
            return QualityPolygons(Str(form, "quality"));
        }

        /// <summary>qualityForPolygons: the rung a typed count implies, or null.</summary>
        public static string QualityForPolygons(string raw)
        {
            var parsed = JsNumber(raw);
            if ((raw ?? "").Trim() == "" || !IsInteger(parsed)) return null;
            if (parsed < PolygonMin || parsed > PolygonMax) return null;
            if (parsed >= UltraFloor) return "ultra";
            if (parsed > LowPolyCeiling) return "high-res";
            return "low-poly";
        }

        /// <summary>hasCustomPolygonCount: whether the job pays for <c>custom_face_count</c>.</summary>
        public static bool HasCustomPolygonCount(JObject form, bool mayChoose = true)
        {
            if (!mayChoose || !CapsOf(Str(form, "model")).CustomFaceCount) return false;
            var raw = Text(form, "polygonCount").Trim();
            if (raw == "") return false;
            var parsed = JsNumber(raw);
            return !(IsInteger(parsed) && parsed == DefaultPolygonCount(form));
        }

        /// <summary>parameterProblems: a custom count that is not a whole number, or out of range.</summary>
        public static List<string> ParameterProblems(JObject form, bool mayChoose = true)
        {
            var problems = new List<string>();
            if (!HasCustomPolygonCount(form, mayChoose)) return problems;
            var parsed = JsNumber(Text(form, "polygonCount"));
            if (!IsInteger(parsed)) problems.Add("The polygon count must be a whole number.");
            else
            {
                var (min, max) = PolygonBounds(form);
                if (parsed < min || parsed > max)
                    problems.Add($"The polygon count must be between {Count(min)} and {Count(max)}.");
            }
            return problems;
        }

        /// <summary>A count as the web's en-US toLocaleString writes it: 20,000.</summary>
        public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

        /// <summary>
        /// applyFormPatch: a change to the form with its coupled fields kept in step — the engine's legal values,
        /// low-poly's textured output, Segment parts' geometry, and the rung ↔ polygon count coupling. Pure:
        /// returns a new form. Fields the patch names that the rules do not know are kept.
        /// </summary>
        public static JObject ApplyPatch(JObject current, JObject patch)
        {
            current ??= Defaults();
            patch ??= new JObject();
            var next = (JObject)current.DeepClone();
            foreach (var pair in patch) next[pair.Key] = pair.Value?.DeepClone();

            var caps = CapsOf(Str(next, "model"));
            if (Str(next, "quality") == "low-poly" && !caps.LowPoly) next["quality"] = "high-res";
            if (Str(next, "textureQuality") == "extreme" && !caps.ExtremeTexture) next["textureQuality"] = "detailed";
            if (Str(next, "polygonType") == "quadrilateral" && !caps.QuadAnyBase && !caps.LowPoly) next["polygonType"] = "triangle";
            if (Str(next, "quality") == "low-poly")
            {
                if (!caps.BareLowPoly) next["output"] = "textured";
                next["segmentParts"] = false;
            }
            else if (Flag(next, "segmentParts"))
            {
                next["output"] = "geometry";
                next["polygonType"] = "triangle";
            }

            bool Changed(string key) => patch[key] != null && !JToken.DeepEquals(patch[key], current[key]);

            if (Changed("quality"))
            {
                next["polygonCount"] = DefaultPolygonCount(next).ToString(CultureInfo.InvariantCulture);
                next["polygonCountTyped"] = false;
                return next;
            }
            if (Changed("polygonType") && !Flag(next, "polygonCountTyped"))
            {
                next["polygonCount"] = DefaultPolygonCount(next).ToString(CultureInfo.InvariantCulture);
                return next;
            }
            if (Changed("model"))
            {
                var (min, max) = PolygonBounds(next);
                var count = JsNumber(Text(next, "polygonCount"));
                if (!count.HasValue || double.IsInfinity(count.Value) || count < min || count > max)
                {
                    next["polygonCount"] = DefaultPolygonCount(next).ToString(CultureInfo.InvariantCulture);
                    return next;
                }
            }
            if (patch["polygonCount"] != null && caps.Ceiling == null)
            {
                var implied = QualityForPolygons(Text(patch, "polygonCount"));
                if (implied != null) next["quality"] = implied;
            }
            return next;
        }

        /// <summary>The plugin's Generate options for this form (no picture, no prompt — the node's input is the server's).</summary>
        public static GenerateOptions ToOptions(JObject form, bool mayChooseFaceCount = true)
        {
            var model = Str(form, "model");
            var raw = Text(form, "polygonCount").Trim();
            var parsed = JsNumber(raw);
            int? faceCount = null;
            // buildCreateRequest sends the typed count whenever it is a whole number and the plan and engine allow a count.
            if (raw != "" && IsInteger(parsed) && mayChooseFaceCount && CapsOf(model).CustomFaceCount && parsed <= int.MaxValue && parsed >= int.MinValue)
                faceCount = (int)parsed.Value;
            var availability = form?["availability"]?.Type == JTokenType.String ? form["availability"].ToString() : null;
            return new GenerateOptions
            {
                Prompt = Text(form, "prompt"),
                ImageUrl = Text(form, "imageUrl"),
                ImageFromPrompt = Flag(form, "imageFromPrompt"),
                Model = model,
                Quality = Str(form, "quality"),
                Output = Str(form, "output"),
                Pbr = Flag(form, "pbrMaterials"),
                TextureQuality = Str(form, "textureQuality"),
                FaceCount = faceCount,
                PolygonType = Str(form, "polygonType"),
                Title = Text(form, "title"),
                SegmentParts = Flag(form, "segmentParts"),
                RealWorldScale = Flag(form, "realWorldScale"),
                Delight = Flag(form, "delight"),
                AutofixImage = Flag(form, "autofixImage"),
                CompressGeometry = Flag(form, "compressGeometry"),
                TextureAlignment = Str(form, "textureAlignment"),
                ExportUv = Flag(form, "exportUv"),
                ControlMode = Flag(form, "controlMode"),
                Availability = string.IsNullOrEmpty(availability) ? null : availability,
            };
        }

        /// <summary>
        /// What the web's engineFields sends as polygonType: the form's own value on a low-poly base or an engine that
        /// takes quads at any base — except quads beside generate_parts. Null = not sent.
        /// </summary>
        public static string SentPolygonType(JObject form, string baseCode, IList<string> modifiers)
        {
            var caps = CapsOf(Str(form, "model"));
            var parts = modifiers.Contains("generate_parts");
            var type = Str(form, "polygonType");
            return (baseCode == "lowpoly" || caps.QuadAnyBase) && !(parts && type == "quadrilateral") ? type : null;
        }

        /// <summary>The engine label as the Studio shows it (GEN_MODELS).</summary>
        public static string ModelLabel(string model) => ZettaCatalog.ModelLabel(model);

        /// <summary>The models each generate kind offers (GENERATE_TAB_MODELS / SMART_TAB_MODELS).</summary>
        public static string[] ModelsFor(string kind) => kind == NodeKinds.GenerateSmart3D ? new[] { "np1", "np15" } : new[] { "n1", "n25" };

        internal static bool IsKnownModel(string model) => model is "n1" or "n25" or "np1" or "np15";

        internal static string ModelOf(JObject form) => Str(form, "model");
    }
}
