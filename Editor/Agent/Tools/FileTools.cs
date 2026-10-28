using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Zetta3D.Editor.Agent
{
    /// <summary>read_file, search_files, write_file, edit_file — confined to the project.</summary>
    static class FileTools
    {
        static readonly string[] ReadRoots = { "Assets", "Packages", "ProjectSettings" };
        static readonly HashSet<string> ScriptExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".asmdef", ".asmref", ".rsp" };
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        const int MaxLinesPerRead = 2000;

        public static string ProjectRoot => Path.GetDirectoryName(Application.dataPath)?.Replace('\\', '/');

        public static bool IsScript(string path) => ScriptExtensions.Contains(Path.GetExtension(path ?? ""));

        /// <summary>Project-relative, forward slashes; rejects absolute paths and anything that escapes the project.</summary>
        public static string Normalize(string path)
        {
            path = (path ?? "").Trim().Trim('"').Replace('\\', '/');
            if (path.Length == 0) throw new ToolException("A project-relative path is required (e.g. \"Assets/Scripts/Player.cs\").");
            if (path.IndexOf('\0') >= 0) throw new ToolException("Invalid path.");
            var root = ProjectRoot;
            if (root != null && path.StartsWith(root + "/", StringComparison.Ordinal)) path = path.Substring(root.Length + 1);
            if (path.StartsWith("/") || path.StartsWith("~") || Regex.IsMatch(path, "^[A-Za-z]:"))
                throw new ToolException($"'{path}' is absolute; use a project-relative path like \"Assets/…\".");
            while (path.StartsWith("./")) path = path.Substring(2);
            path = Regex.Replace(path, "/{2,}", "/").TrimEnd('/');
            if (path.Split('/').Any(s => s == ".." || s == "."))
                throw new ToolException($"'{path}' may not contain '.' or '..' segments.");
            return path;
        }

        static bool Under(string path, string root) => path == root || path.StartsWith(root + "/", StringComparison.Ordinal);

        public static string ForWrite(string path)
        {
            path = Normalize(path);
            if (!Under(path, "Assets") || path == "Assets")
                throw new ToolException($"'{path}' is outside Assets/. The agent may only write under Assets/.");
            return path;
        }

        public static string ForRead(string path)
        {
            path = Normalize(path);
            if (!ReadRoots.Any(r => Under(path, r)))
                throw new ToolException($"'{path}' is outside Assets/, Packages/ and ProjectSettings/.");
            return path;
        }

        /// <summary>The file on disk for a project path (Packages/… may live in Library/PackageCache or elsewhere).</summary>
        public static string Physical(string path)
        {
            if (Under(path, "Packages") && path.Count(c => c == '/') >= 1)
            {
                try
                {
                    var physical = FileUtil.GetPhysicalPath(path);
                    if (!string.IsNullOrEmpty(physical)) return Path.GetFullPath(physical).Replace('\\', '/');
                }
                catch (Exception)
                {
                    // Fall back to the literal path.
                }
            }
            var full = Path.GetFullPath(Path.Combine(ProjectRoot, path)).Replace('\\', '/');
            if (!full.StartsWith(ProjectRoot + "/", StringComparison.Ordinal) && full != ProjectRoot)
                throw new ToolException($"'{path}' resolves outside the project.");
            return full;
        }

        static bool LooksBinary(string file)
        {
            using var stream = File.OpenRead(file);
            var buffer = new byte[8000];
            var n = stream.Read(buffer, 0, buffer.Length);
            for (var i = 0; i < n; i++)
                if (buffer[i] == 0) return true;
            return false;
        }

        static int CountLines(string text) => string.IsNullOrEmpty(text) ? 0 : text.Count(c => c == '\n') + (text.EndsWith("\n") ? 0 : 1);

        // ─── read_file ───────────────────────────────────────────────────────────

        public static ToolResult Read(JObject input)
        {
            var path = ForRead(Args.Required(input, "path"));
            var full = Physical(path);

            if (Directory.Exists(full))
            {
                var entries = Directory.GetDirectories(full).Select(d => Path.GetFileName(d) + "/")
                    .Concat(Directory.GetFiles(full).Where(f => !f.EndsWith(".meta")).Select(Path.GetFileName))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                var shown = entries.Take(300).ToList();
                return ToolResult.Ok($"{path}/ is a folder ({entries.Count} entries):\n" + string.Join("\n", shown) +
                                     (entries.Count > shown.Count ? $"\n… {entries.Count - shown.Count} more" : ""));
            }
            if (!File.Exists(full)) throw new ToolException($"No file at '{path}'. Use find_assets or search_files to locate it.");
            if (new FileInfo(full).Length > 8 * 1024 * 1024) throw new ToolException($"{path} is larger than 8 MB; read a smaller file.");
            if (LooksBinary(full)) throw new ToolException($"{path} is a binary file ({Path.GetExtension(path)}); it cannot be shown as text.");

            var lines = File.ReadAllText(full).Replace("\r\n", "\n").Split('\n');
            var count = lines.Length > 0 && lines[lines.Length - 1] == "" ? lines.Length - 1 : lines.Length;
            var start = Math.Max(1, Args.Int(input, "startLine", 1));
            var end = Math.Min(count, Args.Int(input, "endLine", start + MaxLinesPerRead - 1));
            if (end - start + 1 > MaxLinesPerRead) end = start + MaxLinesPerRead - 1;
            if (count == 0) return ToolResult.Ok($"{path} is empty.");
            if (start > count) throw new ToolException($"{path} has only {count} lines.");

            var width = end.ToString().Length;
            var sb = new StringBuilder($"{path} (lines {start}–{end} of {count})\n");
            for (var i = start; i <= end; i++)
            {
                var line = lines[i - 1];
                if (line.Length > 2000) line = line.Substring(0, 2000) + "…";
                sb.Append(i.ToString().PadLeft(width)).Append(": ").Append(line).Append('\n');
            }
            if (end < count) sb.Append($"… {count - end} more lines (read again with startLine={end + 1}).");
            return ToolResult.Ok(sb.ToString().TrimEnd('\n'));
        }

        // ─── search_files ────────────────────────────────────────────────────────

        static Regex GlobToRegex(string glob)
        {
            var sb = new StringBuilder("^");
            for (var i = 0; i < glob.Length; i++)
            {
                var c = glob[i];
                if (c == '*')
                {
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < glob.Length && glob[i + 1] == '/')
                        {
                            i++;
                            sb.Append("(?:.*/)?");
                        }
                        else sb.Append(".*");
                    }
                    else sb.Append("[^/]*");
                }
                else if (c == '?') sb.Append("[^/]");
                else if (c == '{')
                {
                    var close = glob.IndexOf('}', i);
                    if (close < 0)
                    {
                        sb.Append("\\{");
                        continue;
                    }
                    var options = glob.Substring(i + 1, close - i - 1).Split(',').Select(Regex.Escape);
                    sb.Append("(?:").Append(string.Join("|", options)).Append(')');
                    i = close;
                }
                else sb.Append(Regex.Escape(c.ToString()));
            }
            return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase);
        }

        public static ToolResult Search(JObject input)
        {
            var pattern = Args.Required(input, "pattern");
            var glob = Normalize(Args.Str(input, "glob", "Assets/**/*.cs"));
            if (!glob.Contains("/") && !ReadRoots.Contains(glob.Split('/')[0])) glob = "Assets/**/" + glob;
            var limit = Args.Int(input, "limit", 50, 1, 200);

            Regex regex;
            try
            {
                regex = new Regex(pattern, RegexOptions.Multiline, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException e)
            {
                throw new ToolException($"Invalid regex: {e.Message}");
            }

            // The literal folder before the first wildcard is where the walk starts.
            var segments = glob.Split('/');
            var baseSegments = segments.TakeWhile(s => s.IndexOfAny(new[] { '*', '?', '{' }) < 0).ToList();
            if (baseSegments.Count == segments.Length) baseSegments.RemoveAt(baseSegments.Count - 1);
            var baseDir = string.Join("/", baseSegments);
            if (baseDir.Length == 0) throw new ToolException("The glob must start with Assets/, Packages/ or ProjectSettings/.");
            ForRead(baseDir);
            var physicalBase = Physical(baseDir);
            if (!Directory.Exists(physicalBase)) throw new ToolException($"No folder '{baseDir}'.");

            var globRegex = GlobToRegex(glob);
            var includeMeta = glob.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
            var results = new List<string>();
            var files = 0;
            var matchedFiles = 0;
            var truncated = false;
            foreach (var file in Directory.EnumerateFiles(physicalBase, "*", SearchOption.AllDirectories))
            {
                var rel = baseDir + file.Replace('\\', '/').Substring(physicalBase.Length);
                if (!includeMeta && rel.EndsWith(".meta")) continue;
                if (rel.Contains("/.") ) continue;
                if (!globRegex.IsMatch(rel)) continue;
                if (++files > 20000)
                {
                    truncated = true;
                    break;
                }
                FileInfo info;
                try
                {
                    info = new FileInfo(file);
                    if (info.Length > 2 * 1024 * 1024 || LooksBinary(file)) continue;
                }
                catch (IOException)
                {
                    continue;
                }

                string[] lines;
                try
                {
                    lines = File.ReadAllText(file).Replace("\r\n", "\n").Split('\n');
                }
                catch (IOException)
                {
                    continue;
                }
                var any = false;
                for (var i = 0; i < lines.Length; i++)
                {
                    bool hit;
                    try
                    {
                        hit = regex.IsMatch(lines[i]);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        throw new ToolException("The regex is too slow; simplify it.");
                    }
                    if (!hit) continue;
                    any = true;
                    if (results.Count < limit) results.Add($"{rel}:{i + 1}: {Fmt.Short(lines[i].Trim(), 240)}");
                    else truncated = true;
                }
                if (any) matchedFiles++;
            }

            if (results.Count == 0) return ToolResult.Ok($"No matches for /{pattern}/ in {files} file(s) matching {glob}.");
            var sb = new StringBuilder();
            foreach (var r in results) sb.AppendLine(r);
            sb.Append($"— {results.Count} match(es) in {matchedFiles} file(s) of {files} searched");
            sb.Append(truncated ? $"; more exist (raise limit or narrow the glob)." : ".");
            return ToolResult.Ok(sb.ToString());
        }

        // ─── write_file ──────────────────────────────────────────────────────────

        /// <summary>Makes sure the folder exists without refreshing the AssetDatabase (which would compile scripts mid-batch).</summary>
        public static void EnsureFolder(string folder, bool import = true)
        {
            if (string.IsNullOrEmpty(folder) || folder == "Assets") return;
            AgentChanges.WillCreateFolder(folder);
            if (import)
            {
                if (AssetDatabase.IsValidFolder(folder)) return;
                var parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
                EnsureFolder(parent, true);
                if (Directory.Exists(Physical(folder))) AssetDatabase.ImportAsset(folder);
                else AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
            }
            else Directory.CreateDirectory(Physical(folder));
        }

        static string LinePreview(string text, int maxLines)
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            var shown = lines.Take(maxLines).Select(l => l.Length > 200 ? l.Substring(0, 200) + "…" : l);
            var s = string.Join("\n", shown);
            if (lines.Length > maxLines) s += $"\n… (+{lines.Length - maxLines} more lines)";
            return s;
        }

        public static ToolResult Write(JObject input)
        {
            var path = ForWrite(Args.Required(input, "path"));
            if (!Args.Present(input, "content")) throw new ToolException("'content' is required.");
            var content = Args.Str(input, "content") ?? "";
            var overwrite = Args.Bool(input, "overwrite", false);
            var full = Physical(path);

            if (Directory.Exists(full)) throw new ToolException($"'{path}' is a folder.");
            var existed = File.Exists(full);
            var oldLines = 0;
            if (existed)
            {
                if (!overwrite)
                    throw new ToolException($"{path} already exists ({CountLines(File.ReadAllText(full))} lines). Pass overwrite:true to replace it, or use edit_file.");
                oldLines = CountLines(File.ReadAllText(full));
            }

            var script = IsScript(path);
            content = content.Replace("\r\n", "\n");
            if (script && content.Length > 0 && !content.EndsWith("\n")) content += "\n";
            var folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
            AgentChanges.WillWrite(path);
            EnsureFolder(folder, !script);
            File.WriteAllText(full, content, Utf8);

            var result = new ToolResult();
            var lines = CountLines(content);
            var what = existed ? $"overwrote {oldLines} lines with {lines}" : $"new file, {lines} lines";
            if (script)
            {
                result.WroteScripts = true;
                result.Content = $"Wrote {path} ({what}). It compiles after this step; the result will report errors.";
            }
            else
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                result.Content = $"Wrote {path} ({what}) and imported it.";
            }
            return result;
        }

        public static string SummarizeWrite(JObject input)
        {
            var raw = Args.Str(input, "path") ?? "?";
            var content = Args.Str(input, "content") ?? "";
            string status;
            try
            {
                var path = ForWrite(raw);
                var full = Physical(path);
                if (File.Exists(full))
                {
                    var n = CountLines(File.ReadAllText(full));
                    status = Args.Bool(input, "overwrite", false)
                        ? $"Overwrites {path} ({n} lines → {CountLines(content)})"
                        : $"{path} already exists ({n} lines) — this will fail without overwrite";
                }
                else status = $"New file {path} ({CountLines(content)} lines)";
            }
            catch (ToolException e)
            {
                status = $"{raw}: {e.Message}";
            }
            return status + "\n\n" + LinePreview(content, 40);
        }

        // ─── edit_file ───────────────────────────────────────────────────────────

        static int CountOccurrences(string text, string find)
        {
            var n = 0;
            for (var i = text.IndexOf(find, StringComparison.Ordinal); i >= 0; i = text.IndexOf(find, i + find.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        static int LineAt(string text, int index)
        {
            var line = 1;
            for (var i = 0; i < index && i < text.Length; i++)
                if (text[i] == '\n') line++;
            return line;
        }

        public static ToolResult Edit(JObject input)
        {
            var path = ForWrite(Args.Required(input, "path"));
            var full = Physical(path);
            if (!File.Exists(full)) throw new ToolException($"No file at '{path}'. Use write_file to create it.");
            if (!(Args.Get(input, "edits") is JArray edits) || edits.Count == 0)
                throw new ToolException("'edits' must be a list of { find, replace, all? } (1–30).");
            if (edits.Count > 30) throw new ToolException($"At most 30 edits per call ({edits.Count} given).");

            var bytes = File.ReadAllBytes(full);
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var text = File.ReadAllText(full);
            var crlf = text.Contains("\r\n");
            var working = text;
            var report = new List<string>();

            for (var k = 0; k < edits.Count; k++)
            {
                var edit = edits[k] as JObject;
                var find = edit?["find"]?.Type == JTokenType.String ? (string)edit["find"] : null;
                var replace = edit?["replace"] == null || edit["replace"].Type == JTokenType.Null ? null : edit["replace"].ToString();
                var all = Args.ToBool(edit?["all"]) ?? false;
                if (string.IsNullOrEmpty(find)) throw new ToolException($"Edit {k + 1}: 'find' must be a non-empty string. No changes were made.");
                if (replace == null) throw new ToolException($"Edit {k + 1}: 'replace' is required (use \"\" to delete). No changes were made.");

                if (crlf && !find.Contains("\r\n"))
                {
                    find = find.Replace("\n", "\r\n");
                    replace = replace.Replace("\r\n", "\n").Replace("\n", "\r\n");
                }

                var count = CountOccurrences(working, find);
                if (count == 0)
                {
                    var hint = CountOccurrences(working.Replace("\r\n", "\n"), find.Replace("\r\n", "\n").Trim()) > 0 ? " (it matches after trimming — check leading/trailing whitespace)" : "";
                    throw new ToolException($"Edit {k + 1}: {Fmt.Quote(Fmt.Short(find, 80))} was not found{hint}. No changes were made; read_file the current text and retry.");
                }
                if (count > 1 && !all)
                    throw new ToolException($"Edit {k + 1}: {Fmt.Quote(Fmt.Short(find, 80))} matches {count} times. Add surrounding context to make it unique, or set all:true. No changes were made.");

                var lines = new List<int>();
                var sb = new StringBuilder();
                var pos = 0;
                for (var i = working.IndexOf(find, StringComparison.Ordinal); i >= 0; i = working.IndexOf(find, i + find.Length, StringComparison.Ordinal))
                {
                    sb.Append(working, pos, i - pos);
                    lines.Add(LineAt(sb.ToString(), sb.Length));
                    sb.Append(replace);
                    pos = i + find.Length;
                    if (!all) break;
                }
                sb.Append(working, pos, working.Length - pos);
                working = sb.ToString();

                var span = CountLines(replace.Replace("\r\n", "\n")) - 1;
                var where = string.Join(", ", lines.Take(10).Select(l => span > 0 ? $"{l}–{l + span}" : l.ToString()));
                if (lines.Count > 10) where += ", …";
                report.Add($"edit {k + 1}: line{(lines.Count > 1 || span > 0 ? "s" : "")} {where}{(lines.Count > 1 ? $" ({lines.Count} replacements)" : "")}");
            }

            if (working == text) return ToolResult.Ok($"{path}: the edits change nothing (replacements equal the originals).");

            var encoding = bom ? new UTF8Encoding(true) : Utf8;
            AgentChanges.WillWrite(path);
            File.WriteAllText(full, working, encoding);

            var result = new ToolResult();
            var total = CountLines(working.Replace("\r\n", "\n"));
            var summary = $"Edited {path} ({total} lines now): {string.Join("; ", report)}.";
            if (IsScript(path))
            {
                result.WroteScripts = true;
                result.Content = summary + " It compiles after this step.";
            }
            else
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                result.Content = summary + " Re-imported.";
            }
            return result;
        }

        public static string SummarizeEdit(JObject input)
        {
            var sb = new StringBuilder($"Edit {Args.Str(input, "path") ?? "?"}\n");
            if (Args.Get(input, "edits") is JArray edits)
            {
                var k = 0;
                foreach (var e in edits.Take(30))
                {
                    k++;
                    var find = e?["find"]?.ToString() ?? "";
                    var replace = e?["replace"]?.ToString() ?? "";
                    var all = Args.ToBool(e?["all"]) == true ? " (every match)" : "";
                    sb.Append($"\n#{k}{all}\n");
                    sb.Append(Prefix(LinePreview(find.Trim('\n', '\r'), 8), "- ")).Append('\n');
                    sb.Append(replace.Length == 0 ? "+ (delete)" : Prefix(LinePreview(replace.Trim('\n', '\r'), 8), "+ ")).Append('\n');
                }
            }
            return sb.ToString().TrimEnd();
        }

        static string Prefix(string text, string prefix) => string.Join("\n", text.Split('\n').Select(l => prefix + l));
    }
}
