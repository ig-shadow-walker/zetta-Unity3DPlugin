using System.Collections.Generic;
using System.Linq;

namespace Zetta3D.Editor.Studio
{
    /// <summary>
    /// Fork — start a new generation from an existing one (the web's src/lib/fork.ts): the Generate 3D or
    /// Smart 3D form filled with the settings it was made with, its reference picture(s) included, and nothing
    /// run. Only for the three ways in (text, image, multi-view to 3D) on an engine one of the two tabs offers.
    /// The settings are read back from the row's jobSpec the way the request builder writes it, so a fork lands
    /// on the same price the original paid.
    /// </summary>
    public static class StudioFork
    {
        public const string TextTool = "generate_from_text";
        public const string ImageTool = "generate_from_image";
        public const string MultiViewTool = "generate_from_multiview";

        static readonly Dictionary<string, string> ForkableFeatures = new Dictionary<string, string>
        {
            ["txt23d"] = TextTool,
            ["img23d"] = ImageTool,
            ["multiView23d"] = MultiViewTool,
        };

        /// <summary>The settings a fork puts on the form; null fields leave the form's own value.</summary>
        public class Settings
        {
            public string Model;
            public string Quality;
            public string Output;
            public bool Pbr;
            public bool SegmentParts;
            public string TextureQuality;
            public string PolygonType;
            public int? FaceCount;
            public string Prompt;
        }

        public class Picture
        {
            public string Url;
            public string Name;
            /// <summary>The multi-view slot (front / back / left / right), or null for the single picture.</summary>
            public string View;
        }

        public class Setup
        {
            public long FromId;
            public string FromTitle;
            /// <summary><see cref="StudioPage.GenerateTab"/> or <see cref="StudioPage.SmartTab"/>.</summary>
            public string Tab;
            public string Tool;
            public Settings Settings;
            public List<Picture> Pictures = new List<Picture>();
        }

        /// <summary>The tab whose engines include this model, or null for an engine neither offers.</summary>
        public static string TabFor(string model)
        {
            if (string.IsNullOrEmpty(model)) return null;
            if (ZettaCatalog.GenerateModels.Any(m => m.Id == model)) return StudioPage.GenerateTab;
            if (ZettaCatalog.SmartModels.Any(m => m.Id == model)) return StudioPage.SmartTab;
            return null;
        }

        /// <summary>Whether this row can be forked — one of the three ways in, on a Generate or Smart 3D engine.</summary>
        public static bool CanFork(Generation generation) =>
            generation != null &&
            generation.Availability != "deleted" &&
            ForkableFeatures.ContainsKey(generation.Feature ?? "") &&
            TabFor(generation.Model) != null;

        /// <summary>The settings this row was made with, as the Generate form reads them (fork.ts forkSettingsOf).</summary>
        public static Settings SettingsOf(Generation generation)
        {
            var baseCode = generation.SpecBase;
            var modifiers = generation.SpecModifiers;
            var settings = new Settings
            {
                Model = generation.Model,
                Quality = baseCode == "lowpoly" ? "low-poly" : modifiers.Contains("detailed_geometry") ? "ultra" : "high-res",
                Output = baseCode == "geometry_only" ? "geometry" : baseCode == "textured" ? "textured" : null,
                Pbr = modifiers.Contains("pbr"),
                SegmentParts = modifiers.Contains("generate_parts"),
            };
            if (baseCode == "textured")
                settings.TextureQuality = modifiers.Contains("extreme_texture") ? "extreme" : modifiers.Contains("detailed_texture") ? "detailed" : "standard";
            if (!string.IsNullOrEmpty(generation.PolygonType)) settings.PolygonType = generation.PolygonType;
            if (modifiers.Contains("custom_face_count") && generation.FaceCount is { } faces && faces > 0 && faces <= int.MaxValue)
                settings.FaceCount = (int)faces;
            if (generation.Feature == "txt23d" && !string.IsNullOrWhiteSpace(generation.InputPrompt)) settings.Prompt = generation.InputPrompt;
            return settings;
        }

        /// <summary>Builds the fork of a row, or null when it cannot be forked (fork.ts forkSetupOf).</summary>
        public static Setup Of(Generation generation)
        {
            if (!CanFork(generation)) return null;
            var tool = ForkableFeatures[generation.Feature];
            var title = string.IsNullOrWhiteSpace(generation.Title) ? $"#{generation.Id}" : generation.Title.Trim();
            var pictures = generation.InputImages ?? new List<InputImage>();
            var setup = new Setup
            {
                FromId = generation.Id,
                FromTitle = title,
                Tab = TabFor(generation.Model),
                Tool = tool,
                Settings = SettingsOf(generation),
            };
            if (tool == MultiViewTool)
            {
                foreach (var picture in pictures.Where(p => !string.IsNullOrEmpty(p?.Url)))
                {
                    var view = MultiViewInput.Slots.FirstOrDefault(s => s == picture.ViewType);
                    if (view != null) setup.Pictures.Add(new Picture { Url = picture.Url, Name = $"{title} – {view}.png", View = view });
                }
            }
            else if (tool == ImageTool)
            {
                var first = pictures.FirstOrDefault(p => !string.IsNullOrEmpty(p?.Url));
                if (first != null) setup.Pictures.Add(new Picture { Url = first.Url, Name = $"{title}.png" });
            }
            return setup;
        }

        /// <summary>The content type of downloaded picture bytes, from their header.</summary>
        public static string SniffImageType(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 12) return "image/png";
            if (bytes[0] == 0xFF && bytes[1] == 0xD8) return "image/jpeg";
            if (bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' && bytes[8] == 'W' && bytes[9] == 'E') return "image/webp";
            return "image/png";
        }
    }
}
