using System.Collections.Generic;

namespace Zetta3D.Editor
{
    /// <summary>
    /// What the Studio offers, in Zetta's own vocabulary. Mirrors the web Studio
    /// (Zetta3D-Frontend src/lib/model-capabilities.ts, post-process.ts) and the backend's
    /// model-capabilities.ts / face-limits.ts. Engine vendors are never named to users.
    /// </summary>
    public static class ZettaCatalog
    {
        public class ModelInfo
        {
            public string Id;
            public string Label;
            public string Blurb;
            public bool Paid;
            public bool LowPoly;
            public bool QuadAnyBase;
            public bool DetailedGeometry;
            public bool DetailedTexture;
            public bool ExtremeTexture;
            public bool ImageTuning;
            public bool Parts;
            public bool BarePrompt;
        }

        public static readonly ModelInfo N1 = new ModelInfo
        {
            Id = "n1", Label = "Zetta N1", Blurb = "Base model", LowPoly = true, BarePrompt = true,
        };

        public static readonly ModelInfo N25 = new ModelInfo
        {
            Id = "n25", Label = "Zetta N2.5", Blurb = "Highest quality of mesh and texture", Paid = true, LowPoly = true,
            QuadAnyBase = true, DetailedGeometry = true, DetailedTexture = true, ImageTuning = true, Parts = true,
        };

        public static readonly ModelInfo Np1 = new ModelInfo
        {
            Id = "np1", Label = "Zetta Np1", Blurb = "Clean topology, balanced quality", Paid = true,
            DetailedTexture = true, ExtremeTexture = true, ImageTuning = true,
        };

        public static readonly ModelInfo Np15 = new ModelInfo
        {
            Id = "np15", Label = "Zetta Np1.5", Blurb = "Clean topology — quad native", Paid = true, QuadAnyBase = true,
            DetailedTexture = true, ExtremeTexture = true, ImageTuning = true,
        };

        public static readonly ModelInfo[] GenerateModels = { N1, N25 };
        public static readonly ModelInfo[] SmartModels = { Np1, Np15 };

        public static ModelInfo Model(string id) => id switch
        {
            "n25" => N25,
            "np1" => Np1,
            "np15" => Np15,
            _ => N1,
        };

        public const int SmartUvMaxTriangles = 80_000;

        /// <summary>Rows that are Gaussian splats (made on the web) rather than meshes — not supported in Unity.</summary>
        public static bool IsSplatFeature(string feature) => feature is "imageToSplat" or "worldSplatExport";

        public static string ModelLabel(string model) => model switch
        {
            "n1" => "Zetta N1",
            "n25" => "Zetta N2.5",
            "np1" => "Zetta Np1",
            "np15" => "Zetta Np1.5",
            "r1" => "Zetta R1",
            "zs2" => "Zetta Splat 2",
            "zs21" => "Zetta Splat 2.1",
            _ => model,
        };

        /// <summary>Face-count bounds per model / quality rung / polygon type (backend face-limits.ts).</summary>
        public static (int min, int max) FaceRange(string model, string quality, bool quads)
        {
            switch (model)
            {
                case "n25":
                    if (quality == "low-poly") return quads ? (500, 10_000) : (1_000, 20_000);
                    if (quality == "ultra") return quads ? (1_000, 150_000) : (1_000, 2_000_000);
                    return quads ? (1_000, 150_000) : (1_000, 1_000_000);
                case "np1":
                    return (48, 20_000);
                case "np15":
                    return quads ? (48, 25_000) : (48, 50_000);
                default:
                    return (3_100, 1_500_000);
            }
        }

        public static int FacePreset(string quality) => quality switch
        {
            "low-poly" => 9_000,
            "ultra" => 1_000_000,
            _ => 200_000,
        };

        /// <summary>
        /// The count the web Studio pre-fills for a model / rung / topology (defaultPolygonCount) —
        /// the baseline the paid custom_face_count modifier is measured against.
        /// </summary>
        public static int FacePreset(string model, string quality, bool quads) => model switch
        {
            "n25" => FaceRange(model, quality, quads).max,
            "np1" => 20_000,
            "np15" => quads ? 25_000 : 50_000,
            _ => FacePreset(quality),
        };

        public static readonly string[] Qualities = { "low-poly", "high-res", "ultra" };
        public static readonly string[] QualityLabels = { "Low-Poly", "High-Res", "Ultra" };

        public static readonly string[] RigTypes = { "biped", "quadruped", "avian", "serpentine", "aquatic", "hexapod", "octopod", "others" };
        public static readonly string[] RigTypeLabels = { "Humanoid", "Four legs", "Bird", "Snake", "Fish", "Six legs", "Eight legs", "Other creature" };

        /// <summary>The bone namings a humanoid can be rigged with (2026-10-21) — every preset is a humanoid skeleton.</summary>
        public static readonly string[] RigSpecs = { "standard", "mixamo", "unity", "unreal", "vrm", "actorcore" };
        public static readonly string[] RigSpecLabels = { "Standard", "Mixamo", "Unity Humanoid", "UE5 Mannequin", "VRM 1.0", "ActorCore" };
        public static readonly string[] RigSpecHints =
        {
            "The rigger's own skeleton, finger bones included.",
            "For Mixamo and libraries that follow it.",
            "Maps straight onto a Unity Humanoid avatar.",
            "Unreal Engine 5's Mannequin skeleton.",
            "For VRM avatars and VTuber tools.",
            "For ActorCore motion libraries.",
        };
        public const string HumanoidRigType = "biped";

        /// <summary>The facings an export can be turned to (Auto Rig's exportOrientation).</summary>
        public static readonly string[] Orientations = { "+x", "-x", "+y", "-y" };

        public static readonly string[] AspectRatios = { "1:1", "16:9", "9:16", "4:3", "3:4", "3:2", "2:3", "4:5", "5:4", "21:9" };
        public static readonly string[] Resolutions = { "1K", "2K", "4K" };

        /// <summary>Features whose rows can be post-processed (post-process.ts).</summary>
        public static readonly HashSet<string> ProcessableFeatures = new HashSet<string>
        {
            "txt23d", "img23d", "multiView23d", "retopology", "upload", "uvUnwrap", "worldMeshExport",
        };

        public static string FeatureLabel(string feature) => feature switch
        {
            "img23d" => "Image to 3D",
            "txt23d" => "Text to 3D",
            "multiView23d" => "Multi-view to 3D",
            "retopology" => "Retopology",
            "segmentation" => "Segmentation",
            "smartSegmentation" => "Smart Segmentation",
            "uvUnwrap" => "Smart UV",
            "imageToSplat" => "Gaussian Splat",
            "worldSplatExport" => "Splat export (PLY)",
            "worldMeshExport" => "World mesh",
            "retexture" => "AI Texturing",
            "autoRigging" => "AI Rigging",
            "convertFormat" => "Converted",
            "upload" => "Uploaded model",
            _ => "Model",
        };

        /// <summary>Plain-language messages for the API's error codes.</summary>
        public static string Explain(ZettaApiException e)
        {
            switch (e.Code)
            {
                case "INSUFFICIENT_CREDITS":
                    var required = e.Body?["required"]?.ToString();
                    var available = e.Body?["available"]?.ToString();
                    return required != null
                        ? $"This needs {required} credits and you have {available}. Top up or upgrade at zetta3d.ai/pricing."
                        : "Not enough credits. Top up or upgrade at zetta3d.ai/pricing.";
                case "JOB_LIMIT_REACHED":
                    var limit = e.Body?["limit"]?.ToString();
                    return $"You already have {limit} job(s) running — the most your plan runs at once. Wait for one to finish, or upgrade.";
                case "PLAN_CAPABILITY_REQUIRED":
                    var violations = e.Body?["violations"] as Newtonsoft.Json.Linq.JArray;
                    var first = violations?.First?["message"]?.ToString();
                    return (string.IsNullOrEmpty(first) ? "Your plan does not include this option." : first) + " Upgrade at zetta3d.ai/pricing.";
                case "MODEL_CAPABILITY_UNSUPPORTED":
                    return e.Message;
                case "GEN3D_NOT_READY":
                    return "That model is not ready yet.";
                case "EARLY_ACCESS_REQUIRED":
                    return "This feature is in early access. Request access and we'll let you know the moment you're in. Nothing was charged.";
                case "FEATURE_UNAVAILABLE":
                    return "This kind of generation is temporarily unavailable while we work on it. Nothing was charged — please try again later.";
                default:
                    return e.Message;
            }
        }
    }
}
