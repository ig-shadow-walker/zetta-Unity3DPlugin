using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Zetta3D.Editor
{
    /// <summary>The Generate / Smart Generate form, in the Studio's (and the agent's) vocabulary.</summary>
    public class GenerateOptions
    {
        public string Prompt = "";
        /// <summary>An uploaded, engine-readable image URL (from <see cref="ZettaApi.UploadAsync"/>).</summary>
        public string ImageUrl = "";
        /// <summary>The image was drawn from <see cref="Prompt"/> (text-to-3D): both are sent, the row is txt23d.</summary>
        public bool ImageFromPrompt;
        /// <summary>Multi-view: (viewType, url) in submission order; front becomes the primary image server-side.</summary>
        public List<(string viewType, string url)> Views = new List<(string, string)>();

        public string Model = "n1";
        /// <summary>low-poly | high-res | ultra</summary>
        public string Quality = "high-res";
        /// <summary>textured | geometry</summary>
        public string Output = "textured";
        public bool Pbr = true;
        /// <summary>standard | detailed | extreme</summary>
        public string TextureQuality = "standard";
        /// <summary>Null = the engine's default for the rung (no custom-count charge).</summary>
        public int? FaceCount;
        /// <summary>triangle | quadrilateral</summary>
        public string PolygonType = "triangle";
        public string Title = "";
        public bool SegmentParts;
        public bool RealWorldScale;
        public bool Delight;
        public bool AutofixImage;
        public bool CompressGeometry;
        /// <summary>geometry | original_image — "Fit texture to mesh" is the web Studio's default since 2026-10.</summary>
        public string TextureAlignment = "geometry";
        public bool ExportUv = true;
        public bool ControlMode;
        /// <summary>null = server default (plan-aware), or public | private.</summary>
        public string Availability;

        public bool MultiView => Views.Count > 0;
    }

    public class BuiltRequest
    {
        public JObject Body;
        public string Base;
        public List<string> Modifiers = new List<string>();
        public List<string> Problems = new List<string>();
        public bool Ok => Problems.Count == 0;
    }

    /// <summary>
    /// A port of the web Studio's request builder (Zetta3D-Frontend src/lib/gen3d-request.ts
    /// deriveSpec / engineFields / buildCreateRequest / buildMultiViewRequest). Every cross-field rule
    /// here exists because the server refuses the combination after credits have moved — checking it
    /// on this side costs nothing.
    /// </summary>
    public static class ZettaRequests
    {
        public static (string baseCode, List<string> modifiers) Spec(GenerateOptions o, ThreeDEntitlements gates = null)
        {
            var model = ZettaCatalog.Model(o.Model);
            var baseCode = o.Quality == "low-poly" && model.LowPoly ? "lowpoly" : o.Output == "geometry" ? "geometry_only" : "textured";

            var modifiers = new List<string>();
            if (o.MultiView) modifiers.Add("multi_view");
            if (o.Pbr && o.Output == "textured") modifiers.Add("pbr");
            if (o.Quality == "ultra" && model.DetailedGeometry) modifiers.Add("detailed_geometry");
            if (o.SegmentParts && o.Output == "geometry" && o.Quality != "low-poly" && model.Parts) modifiers.Add("generate_parts");
            if (o.Output == "textured")
            {
                if (o.TextureQuality == "extreme" && model.ExtremeTexture) modifiers.Add("extreme_texture");
                else if (o.TextureQuality == "detailed" && model.DetailedTexture) modifiers.Add("detailed_texture");
            }
            if (CustomCount(o, gates)) modifiers.Add("custom_face_count");
            // No `quad_mesh` here: the web Studio never sends it, and the request's polygonType is what selects quads.
            return (baseCode, modifiers);
        }

        static bool MayChooseCount(ThreeDEntitlements gates) => gates?.AllowsCustomFaceCount != false;

        static bool CustomCount(GenerateOptions o, ThreeDEntitlements gates) =>
            MayChooseCount(gates) && o.FaceCount.HasValue && o.FaceCount.Value != ZettaCatalog.FacePreset(o.Model, o.Quality, o.PolygonType == "quadrilateral");

        static bool QuadsSent(GenerateOptions o, string baseCode, List<string> modifiers)
        {
            var model = ZettaCatalog.Model(o.Model);
            var parts = modifiers.Contains("generate_parts");
            return (baseCode == "lowpoly" || model.QuadAnyBase) && !parts;
        }

        public static BuiltRequest Build(GenerateOptions o, ThreeDEntitlements gates = null)
        {
            var result = new BuiltRequest();
            var model = ZettaCatalog.Model(o.Model);
            var (baseCode, modifiers) = Spec(o, gates);
            result.Base = baseCode;
            result.Modifiers = modifiers;

            if (o.FaceCount.HasValue && MayChooseCount(gates))
            {
                var (min, max) = ZettaCatalog.FaceRange(o.Model, o.Quality, o.PolygonType == "quadrilateral");
                if (o.FaceCount < min || o.FaceCount > max)
                    result.Problems.Add($"The polygon count must be between {min:N0} and {max:N0}.");
            }

            var body = new JObject
            {
                ["spec"] = new JObject { ["generation"] = new JObject { ["base"] = baseCode, ["modifiers"] = new JArray(modifiers) } },
            };

            var prompt = (o.Prompt ?? "").Trim();
            var image = (o.ImageUrl ?? "").Trim();
            if (o.MultiView)
            {
                body["multiViewImages"] = new JArray(o.Views.Select(v => new JObject { ["viewType"] = v.viewType, ["imageUrl"] = v.url }));
            }
            else
            {
                var hasImage = image != "";
                var hasPrompt = prompt != "";
                if (hasImage && hasPrompt && !o.ImageFromPrompt) result.Problems.Add("Use either a prompt or an image, not both.");
                if (!hasImage && !hasPrompt) result.Problems.Add("Describe the model or attach an image.");
                if (!hasImage && hasPrompt && !model.BarePrompt)
                    result.Problems.Add($"{model.Label} generates from a picture — draw one from the prompt first.");
                if (hasPrompt && prompt.Length > 1024) result.Problems.Add("The prompt is longer than 1,024 characters.");
                if (hasImage && o.ImageFromPrompt && hasPrompt)
                {
                    body["imageUrl"] = image;
                    body["prompt"] = prompt;
                    body["imageFromPrompt"] = true;
                }
                else if (hasImage) body["imageUrl"] = image;
                else body["prompt"] = prompt;
            }

            if (o.FaceCount.HasValue && MayChooseCount(gates)) body["faceCount"] = o.FaceCount.Value;

            // Engine fields (engineFields in the web Studio).
            if (o.PolygonType == "quadrilateral" && QuadsSent(o, baseCode, modifiers)) body["polygonType"] = "quadrilateral";
            else if ((baseCode == "lowpoly" || model.QuadAnyBase) && !modifiers.Contains("generate_parts")) body["polygonType"] = "triangle";
            if (baseCode == "lowpoly" && o.Output == "geometry" && o.Model == "n25") body["generationType"] = "Geometry";
            if (!string.IsNullOrEmpty(o.Availability)) body["availability"] = o.Availability;
            if (model.ImageTuning)
            {
                if (o.RealWorldScale) body["autoSize"] = true;
                if (o.TextureAlignment != "original_image") body["textureAlignment"] = o.TextureAlignment;
                if (o.AutofixImage) body["autofixImage"] = true;
                if (o.CompressGeometry) body["compressGeometry"] = true;
                if (o.Delight) body["delight"] = true;
                // Inverted on purpose: the engine defaults UVs ON, so only "off" is sent.
                if (!o.ExportUv) body["exportUv"] = false;
            }

            var title = (o.Title ?? "").Trim();
            if (title != "") body["title"] = title.Length > 255 ? title.Substring(0, 255) : title;
            if (o.ControlMode && !o.MultiView) body["advanceControl"] = true;
            body["model"] = o.Model;

            result.Body = body;
            return result;
        }

        /// <summary>The credits the job will cost, from the price list (the server's quote is authoritative).</summary>
        public static int? Price(PriceList prices, BuiltRequest request, string model) =>
            prices?.Total(model, new[] { request.Base }.Concat(request.Modifiers));

        /// <summary>A post-process job on an existing generation: exactly one operation code.</summary>
        public static JObject PostProcess(string code, long sourcePostId, JObject options = null)
        {
            var body = new JObject
            {
                ["spec"] = new JObject { ["postProcess"] = new JArray(code) },
                ["sourcePostId"] = sourcePostId,
            };
            if (options != null)
                foreach (var pair in options)
                    if (pair.Value != null && pair.Value.Type != JTokenType.Null) body[pair.Key] = pair.Value;
            return body;
        }
    }
}
