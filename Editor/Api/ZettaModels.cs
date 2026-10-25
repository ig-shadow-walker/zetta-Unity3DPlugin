using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Zetta3D.Editor
{
    public class CreditPool
    {
        [JsonProperty("monthly")] public long Monthly;
        [JsonProperty("purchased")] public long Purchased;
        [JsonProperty("bonusMonthly")] public long BonusMonthly;
        [JsonProperty("bonus")] public long Bonus;
        public long Total => Monthly + Purchased + BonusMonthly + Bonus;
    }

    public class UserCredits
    {
        [JsonProperty("threeD")] public CreditPool ThreeD = new CreditPool();
        [JsonProperty("ai")] public CreditPool Ai = new CreditPool();
    }

    public class ZettaUser
    {
        [JsonProperty("id")] public long Id;
        [JsonProperty("email")] public string Email;
        [JsonProperty("displayName")] public string DisplayName;
        [JsonProperty("profilePicture")] public string ProfilePicture;
        [JsonProperty("paymentPlan")] public string PaymentPlan;
        [JsonProperty("subscriptionStatus")] public string SubscriptionStatus;
        [JsonProperty("credits")] public UserCredits Credits = new UserCredits();

        public string Name => string.IsNullOrEmpty(DisplayName) ? Email : DisplayName;

        public string PlanLabel => PaymentPlan switch
        {
            "creator_pro" => "Creator Pro",
            null or "" or "none" => "Free",
            _ => char.ToUpperInvariant(PaymentPlan[0]) + PaymentPlan.Substring(1),
        };

        public bool IsFree => PaymentPlan is null or "" or "none" or "free";
    }

    public class ThreeDEntitlements
    {
        [JsonProperty("maxMultiViewImages")] public int? MaxMultiViewImages;
        [JsonProperty("allowsLowPoly")] public bool? AllowsLowPoly;
        [JsonProperty("allowsQuadMesh")] public bool? AllowsQuadMesh;
        [JsonProperty("allowsCustomFaceCount")] public bool? AllowsCustomFaceCount;
        [JsonProperty("maxFaceCount")] public int? MaxFaceCount;
        [JsonProperty("allowsZettaN25")] public bool? AllowsZettaN25;
        [JsonProperty("allowsZettaNp1")] public bool? AllowsZettaNp1;
        [JsonProperty("allowsPostProcess")] public bool? AllowsPostProcess;
        [JsonProperty("allowsPrivateGenerations")] public bool? AllowsPrivateGenerations;
        [JsonProperty("maxConcurrentJobs")] public int? MaxConcurrentJobs;
    }

    public class ImageEntitlements
    {
        [JsonProperty("allowsImageEditing")] public bool? AllowsImageEditing;
        [JsonProperty("allows4kImages")] public bool? Allows4kImages;
        [JsonProperty("allowsProImageModel")] public bool? AllowsProImageModel;
    }

    public class PriceItem
    {
        [JsonProperty("code")] public string Code;
        [JsonProperty("kind")] public string Kind;
        [JsonProperty("label")] public string Label;
        [JsonProperty("credits")] public int Credits;
        [JsonProperty("model")] public string Model;
    }

    /// <summary>The price list, with the Studio's lookup rule: an exact model row beats the null-model default.</summary>
    public class PriceList
    {
        public readonly List<PriceItem> Items;

        public PriceList(List<PriceItem> items) => Items = items ?? new List<PriceItem>();

        public int? Price(string code, string model)
        {
            var exact = Items.FirstOrDefault(i => i.Code == code && i.Model == model);
            if (exact != null) return exact.Credits;
            return Items.FirstOrDefault(i => i.Code == code && string.IsNullOrEmpty(i.Model))?.Credits;
        }

        public int? Total(string model, IEnumerable<string> codes)
        {
            var sum = 0;
            foreach (var code in codes)
            {
                var price = Price(code, model);
                if (price == null) return null;
                sum += price.Value;
            }
            return sum;
        }
    }

    public class ResultFile
    {
        [JsonProperty("type")] public string Type;
        [JsonProperty("folder")] public string Folder;
        [JsonProperty("key")] public string Key;
        [JsonProperty("url")] public string Url;
        [JsonProperty("bytes")] public long Bytes;
        /// <summary>What a file is when a job returns several: "Textured", "Vertex colours", "Collider", "UV layout".</summary>
        [JsonProperty("label")] public string Label;

        static readonly HashSet<string> PictureTypes = new HashSet<string> { "IMAGE", "WEBP", "PNG", "JPG", "JPEG", "UV_LAYOUT" };
        static readonly HashSet<string> SplatTypes = new HashSet<string> { "SPLAT", "SPZ", "PLY" };

        public bool IsPicture =>
            Folder is "thumbnail" or "gifs" or "uv" || Label == "UV layout" || PictureTypes.Contains((Type ?? "").ToUpperInvariant());

        public bool IsSplat =>
            Folder == "splat" || SplatTypes.Contains((Type ?? "").ToUpperInvariant()) || Extension is "splat" or "spz";

        public string Extension
        {
            get
            {
                var name = Key ?? Url ?? "";
                var q = name.IndexOf('?');
                if (q >= 0) name = name.Substring(0, q);
                var dot = name.LastIndexOf('.');
                return dot >= 0 ? name.Substring(dot + 1).ToLowerInvariant() : (Folder ?? "").ToLowerInvariant();
            }
        }

        public string FileName
        {
            get
            {
                var name = Key ?? "";
                var slash = name.LastIndexOf('/');
                return slash >= 0 ? name.Substring(slash + 1) : name;
            }
        }
    }

    public class ResultManifest
    {
        [JsonProperty("files")] public List<ResultFile> Files = new List<ResultFile>();
        [JsonProperty("thumbnail")] public ResultFile Thumbnail;
    }

    public class InputImage
    {
        [JsonProperty("url")] public string Url;
        [JsonProperty("viewType")] public string ViewType;
    }

    public class Generation
    {
        [JsonProperty("id")] public long Id;
        [JsonProperty("status")] public string Status;
        [JsonProperty("selectedFeature")] public string Feature;
        [JsonProperty("selectedModel")] public string Model;
        [JsonProperty("title")] public string Title;
        [JsonProperty("tags")] public List<string> Tags = new List<string>();
        [JsonProperty("availability")] public string Availability;
        [JsonProperty("inputPrompt")] public string InputPrompt;
        [JsonProperty("inputImages")] public List<InputImage> InputImages = new List<InputImage>();
        [JsonProperty("thumbnailURL")] public string ThumbnailUrl;
        [JsonProperty("faceCount")] public long? FaceCount;
        [JsonProperty("polygonType")] public string PolygonType;
        [JsonProperty("result")] public ResultManifest Result;
        [JsonProperty("errorMessage")] public string ErrorMessage;
        [JsonProperty("createdAt")] public DateTime CreatedAt;
        [JsonProperty("updatedAt")] public DateTime UpdatedAt;
        /// <summary>The 3D Pipelines run that made this row, or null (rows made by hand, and rows from before the field).</summary>
        [JsonProperty("pipelineRunId")] public long? PipelineRunId;
        /// <summary>The priced choices the row was made with: <c>{ generation: { base, modifiers[] } }</c> (what Fork reads back).</summary>
        [JsonProperty("jobSpec")] public JObject JobSpec;
        /// <summary>With <c>groupRuns</c>: this entry stands for a whole pipeline run — its models, newest first, this one included.</summary>
        [JsonProperty("runGroup")] public RunGroup RunGroup;
        /// <summary>Inside a run group: the step that made this model.</summary>
        [JsonProperty("runStep")] public RunStep RunStep;

        public bool IsPipelineMade => PipelineRunId != null;

        /// <summary>The jobSpec's base code (lowpoly / geometry_only / textured), or null.</summary>
        public string SpecBase => JobSpec?["generation"]?["base"]?.Type == JTokenType.String ? (string)JobSpec["generation"]["base"] : null;

        /// <summary>The jobSpec's modifiers (pbr, detailed_geometry, custom_face_count, …).</summary>
        public HashSet<string> SpecModifiers =>
            JobSpec?["generation"]?["modifiers"] is JArray modifiers
                ? new HashSet<string>(modifiers.Where(m => m.Type == JTokenType.String).Select(m => (string)m))
                : new HashSet<string>();

        public bool IsTerminal => Status is "success" or "failed" or "cancelled";
        public bool IsSuccess => Status == "success";
        public bool IsRunning => !IsTerminal;

        public string DisplayTitle =>
            !string.IsNullOrWhiteSpace(Title) ? Title :
            !string.IsNullOrWhiteSpace(InputPrompt) ? Shorten(InputPrompt, 48) :
            $"{ZettaCatalog.FeatureLabel(Feature)} #{Id}";

        public string StatusLabel => Status switch
        {
            "pending" => "Waiting",
            "queued" => "Queued",
            "processing" => "Generating",
            "saving" => "Saving",
            "success" => "Ready",
            "failed" => "Failed",
            "cancelled" => "Cancelled",
            _ => Status,
        };

        public IEnumerable<ResultFile> MeshFiles =>
            (Result?.Files ?? new List<ResultFile>()).Where(f => !f.IsPicture && !f.IsSplat);

        public IEnumerable<ResultFile> SplatFiles =>
            (Result?.Files ?? new List<ResultFile>()).Where(f => f.IsSplat);

        /// <summary>The Smart UV layout picture, when the job made one.</summary>
        public ResultFile UvLayout =>
            (Result?.Files ?? new List<ResultFile>()).FirstOrDefault(f => f.Label == "UV layout" || f.Folder == "uv");

        /// <summary>A Gaussian splat or world made on the web: listed, but not supported in Unity (never treated as a mesh).</summary>
        public bool IsSplat => ZettaCatalog.IsSplatFeature(Feature) || (Result?.Files?.Count > 0 && !MeshFiles.Any() && SplatFiles.Any());

        static string Shorten(string text, int max) => text.Length <= max ? text : text.Substring(0, max - 1) + "…";
    }

    /// <summary>A pipeline run's models, sent as one library entry when the list is asked with <c>groupRuns</c>.</summary>
    public class RunGroup
    {
        [JsonProperty("runId")] public long RunId;
        [JsonProperty("pipelineId")] public long? PipelineId;
        [JsonProperty("pipelineTitle")] public string PipelineTitle;
        [JsonProperty("count")] public int Count;
        /// <summary>Newest first, as the server sends them.</summary>
        [JsonProperty("members")] public List<Generation> Members = new List<Generation>();
    }

    /// <summary>The step of a pipeline run that made a model: its place in the run (1 first), its name, and the part (Isolate Parts' branches).</summary>
    public class RunStep
    {
        [JsonProperty("position")] public int Position;
        [JsonProperty("label")] public string Label;
        [JsonProperty("part")] public string Part;
    }

    public class GenerationPage
    {
        [JsonProperty("items")] public List<Generation> Items = new List<Generation>();
        [JsonProperty("total")] public int Total;
        [JsonProperty("page")] public int Page;
        [JsonProperty("pageSize")] public int PageSize;
    }

    public class QuoteLine
    {
        [JsonProperty("code")] public string Code;
        [JsonProperty("label")] public string Label;
        [JsonProperty("credits")] public int Credits;
    }

    public class Quote
    {
        [JsonProperty("credits")] public int Credits;
        [JsonProperty("lines")] public List<QuoteLine> Lines = new List<QuoteLine>();
    }

    public class CreateGenerationResult
    {
        [JsonProperty("generation")] public Generation Generation;
        [JsonProperty("quote")] public Quote Quote;
        [JsonProperty("charged")] public bool Charged;
    }

    public class UploadTicket
    {
        [JsonProperty("uploadUrl")] public string UploadUrl;
        [JsonProperty("method")] public string Method;
        [JsonProperty("headers")] public Dictionary<string, string> Headers = new Dictionary<string, string>();
        [JsonProperty("key")] public string Key;
        [JsonProperty("fileUrl")] public string FileUrl;
    }

    public class GeneratedImage
    {
        [JsonProperty("base64")] public string Base64;
        [JsonProperty("contentType")] public string ContentType;
        [JsonProperty("bytes")] public long Bytes;

        public byte[] Data => string.IsNullOrEmpty(Base64) ? Array.Empty<byte>() : Convert.FromBase64String(Base64);
    }

    public class ImageResult
    {
        [JsonProperty("postId")] public long? PostId;
        [JsonProperty("images")] public List<GeneratedImage> Images = new List<GeneratedImage>();
        [JsonProperty("text")] public string Text;
        [JsonProperty("model")] public string Model;
    }

    public class ImageModel
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("label")] public string Label;
        [JsonProperty("resolutions")] public List<string> Resolutions = new List<string>();
        [JsonProperty("locked")] public bool Locked;
        [JsonProperty("lockedReason")] public string LockedReason;
    }

    public class ImageModels
    {
        [JsonProperty("models")] public List<ImageModel> Models = new List<ImageModel>();
        [JsonProperty("default")] public string Default;
    }

    public class StoredImage
    {
        [JsonProperty("url")] public string Url;
        [JsonProperty("contentType")] public string ContentType;
        [JsonProperty("bytes")] public long Bytes;
    }

    public class ImagePost
    {
        [JsonProperty("id")] public long Id;
        [JsonProperty("feature")] public string Feature;
        [JsonProperty("prompt")] public string Prompt;
        [JsonProperty("aspectRatio")] public string AspectRatio;
        [JsonProperty("resolution")] public string Resolution;
        [JsonProperty("images")] public List<StoredImage> Images = new List<StoredImage>();
        [JsonProperty("errorMessage")] public string ErrorMessage;
        [JsonProperty("createdAt")] public DateTime CreatedAt;
    }

    public class ImagePage
    {
        [JsonProperty("items")] public List<ImagePost> Items = new List<ImagePost>();
        [JsonProperty("total")] public int Total;
    }

    /// <summary>An image going up in a request body: raw base64, no data: prefix.</summary>
    public class InlineImage
    {
        [JsonProperty("data")] public string Data;
        [JsonProperty("contentType")] public string ContentType;

        public static InlineImage From(byte[] bytes, string contentType) =>
            new InlineImage { Data = Convert.ToBase64String(bytes), ContentType = contentType };
    }

    public static class JsonExt
    {
        public static T As<T>(this JToken token, string property = null)
        {
            var node = property == null ? token : token?[property];
            return node == null || node.Type == JTokenType.Null ? default : node.ToObject<T>();
        }
    }
}
