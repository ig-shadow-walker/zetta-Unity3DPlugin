using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Zetta3D.Editor.Agent
{
    /// <summary>A PDF attached to a message (protocol v1.4 `documents`).</summary>
    public class AgentDocument
    {
        public string Data;
        public string MediaType = "application/pdf";
        public string Name;
    }

    /// <summary>
    /// A file the user attached to a message, sorted into what the agent can take: a picture, a PDF, text (inlined),
    /// or anything else — copied into the project and tagged as an asset, so the agent can import and use it.
    /// </summary>
    public class AgentAttachment
    {
        /// <summary>image | document | text | asset</summary>
        public string Kind;
        public string Label;
        public ToolImage Image;
        public AgentDocument Document;
        public string Text;
        public AgentMention Mention;
        /// <summary>A picture's file as it was on disk (Image may be scaled down), kept for the picture tools.</summary>
        public byte[] Original;
        public string OriginalName;

        public const int MaxTextBytes = 200 * 1024;
        public const int MaxPdfBytes = 6 * 1024 * 1024;
        public const string AttachmentsFolder = "Assets/Zetta3D/Attachments";

        static readonly HashSet<string> ImageExtensions = new HashSet<string> { ".png", ".jpg", ".jpeg", ".webp", ".gif" };

        static readonly HashSet<string> TextExtensions = new HashSet<string>
        {
            ".cs", ".txt", ".md", ".json", ".xml", ".yaml", ".yml", ".csv", ".tsv", ".shader", ".hlsl", ".cginc", ".compute",
            ".uss", ".uxml", ".asmdef", ".asmref", ".ini", ".cfg", ".toml", ".log", ".js", ".ts", ".py", ".lua", ".html",
            ".css", ".sql", ".glsl", ".vert", ".frag", ".rsp", ".editorconfig", ".gitignore", ".properties", ".rtf",
        };

        /// <summary>Sorts a file from disk (or the project) into an attachment. Throws with a plain reason when it can't be used.</summary>
        public static AgentAttachment FromPath(string path)
        {
            var full = System.IO.Path.GetFullPath(path);
            if (!File.Exists(full)) throw new InvalidOperationException($"“{System.IO.Path.GetFileName(path)}” is not a file.");
            var name = System.IO.Path.GetFileName(full);
            var ext = System.IO.Path.GetExtension(full).ToLowerInvariant();

            // Already in the project: tag it where it is — nothing to copy.
            var projectPath = ProjectRelative(full);
            if (projectPath != null && (projectPath.StartsWith("Assets/") || projectPath.StartsWith("Packages/")))
                return new AgentAttachment { Kind = "asset", Label = projectPath, Mention = AgentMention.Asset(projectPath) };

            var bytes = File.ReadAllBytes(full);
            if (ImageExtensions.Contains(ext))
                return new AgentAttachment { Kind = "image", Label = "picture " + name, Image = Picture(bytes, ext), Original = bytes, OriginalName = name };

            if (ext == ".pdf")
            {
                if (bytes.Length > MaxPdfBytes)
                    throw new InvalidOperationException($"“{name}” is {bytes.Length / 1024f / 1024f:0.#} MB — PDFs up to 6 MB can be attached.");
                return new AgentAttachment
                {
                    Kind = "document",
                    Label = "PDF " + name,
                    Document = new AgentDocument { Data = Convert.ToBase64String(bytes), Name = name },
                };
            }

            if ((TextExtensions.Contains(ext) || LooksLikeText(bytes)) && bytes.Length <= MaxTextBytes)
                return new AgentAttachment { Kind = "text", Label = name, Text = Encoding.UTF8.GetString(bytes) };

            // Anything else — a model, a sound, a font, an archive — goes into the project, where the agent can use it.
            ZettaImporter.EnsureFolder(AttachmentsFolder);
            var target = AssetDatabase.GenerateUniqueAssetPath($"{AttachmentsFolder}/{name}");
            File.WriteAllBytes(target, bytes);
            AssetDatabase.ImportAsset(target, ImportAssetOptions.ForceSynchronousImport);
            var mention = AgentMention.Asset(target);
            mention.Note = "the user attached this file; it was copied into the project";
            return new AgentAttachment { Kind = "asset", Label = target, Mention = mention };
        }

        /// <summary>The block a text attachment adds to the message.</summary>
        public string TextBlock() => $"\n\n<attached_file name=\"{Label}\">\n{Text}\n</attached_file>";

        static ToolImage Picture(byte[] bytes, string ext)
        {
            // A large photo is scaled to 1600 px and re-encoded: the model gains nothing from more, and the request stays small.
            if (bytes.Length <= 2_500_000 && ext != ".gif")
                return new ToolImage { Data = Convert.ToBase64String(bytes), MediaType = ext == ".png" ? "image/png" : ext == ".webp" ? "image/webp" : "image/jpeg" };
            var texture = new Texture2D(2, 2);
            if (!texture.LoadImage(bytes))
            {
                UnityEngine.Object.DestroyImmediate(texture);
                throw new InvalidOperationException("That picture could not be read.");
            }
            var scale = Mathf.Min(1f, 1600f / Mathf.Max(texture.width, texture.height));
            var w = Mathf.Max(1, Mathf.RoundToInt(texture.width * scale));
            var h = Mathf.Max(1, Mathf.RoundToInt(texture.height * scale));
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(texture, rt);
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var small = new Texture2D(w, h, TextureFormat.RGB24, false);
            small.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            small.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            var jpeg = small.EncodeToJPG(88);
            UnityEngine.Object.DestroyImmediate(texture);
            UnityEngine.Object.DestroyImmediate(small);
            return ToolImage.Jpeg(jpeg);
        }

        /// <summary>No NUL bytes in the first 8 KB, and valid UTF-8: treat it as text.</summary>
        static bool LooksLikeText(byte[] bytes)
        {
            var n = Math.Min(bytes.Length, 8192);
            for (var i = 0; i < n; i++) if (bytes[i] == 0) return false;
            try
            {
                new UTF8Encoding(false, true).GetString(bytes, 0, n);
                return n > 0;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        static string ProjectRelative(string full)
        {
            var root = System.IO.Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/').TrimEnd('/') + "/";
            var f = full.Replace('\\', '/');
            return f.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? f.Substring(root.Length) : null;
        }
    }

    /// <summary>
    /// Pictures the user attached to a message — files from outside the project, Scene and Game captures — kept in
    /// Library/Zetta3D/Attached (not imported, not in the project) so the picture tools can edit or compose them by
    /// "attached:&lt;name&gt;", the name the message gives them. Kept a week.
    /// </summary>
    public static class AttachedPictures
    {
        public const string Prefix = "attached:";
        const string Folder = "Library/Zetta3D/Attached";
        static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

        /// <summary>Keeps a picture and returns its "attached:name" — the same bytes under the same name come back as they were.</summary>
        public static string Keep(byte[] bytes, string name)
        {
            Directory.CreateDirectory(Folder);
            Sweep();
            var safe = Safe(name);
            var stem = Path.GetFileNameWithoutExtension(safe);
            var ext = Path.GetExtension(safe);
            for (var n = 1; ; n++)
            {
                var candidate = n == 1 ? safe : $"{stem}-{n}{ext}";
                var file = Path.Combine(Folder, candidate);
                if (File.Exists(file) && !File.ReadAllBytes(file).SequenceEqual(bytes)) continue;
                File.WriteAllBytes(file, bytes);
                return Prefix + candidate;
            }
        }

        /// <summary>The file behind an "attached:name", or null when it is not one (or is gone).</summary>
        public static string FileOf(string key)
        {
            if (key == null || !key.StartsWith(Prefix, StringComparison.Ordinal)) return null;
            var name = key.Substring(Prefix.Length).Trim();
            if (name.Length == 0 || name != Safe(name)) return null;
            var file = Path.Combine(Folder, name);
            return File.Exists(file) ? file : null;
        }

        /// <summary>Letters, digits, dot, dash and underscore only — no folders, so a name can't reach outside the store.</summary>
        static string Safe(string name)
        {
            var file = Path.GetFileName(name ?? "") ?? "";
            var clean = new string(file.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray()).Trim('.', '-');
            if (clean.Length == 0) clean = "picture";
            if (clean.Length > 80) clean = clean.Substring(clean.Length - 80);
            return Path.HasExtension(clean) ? clean : clean + ".png";
        }

        static void Sweep()
        {
            try
            {
                foreach (var file in Directory.GetFiles(Folder))
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > KeepFor) File.Delete(file);
            }
            catch (IOException)
            {
                // Best effort: an old picture left behind costs a little disk.
            }
        }
    }
}
