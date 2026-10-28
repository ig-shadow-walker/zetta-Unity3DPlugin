using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// What the agent changed in one turn — files it wrote, created, deleted or moved, and its steps in the open
    /// scene — and how to take each back.
    ///
    /// FILES ARE COPIED JUST BEFORE THE AGENT FIRST TOUCHES THEM in a turn (with their .meta, so a restored asset keeps
    /// its GUID and every reference to it), under Library/Zetta3D/AgentChanges/&lt;turn&gt;/ — kept through a domain
    /// reload and an Editor restart, and never in the project or version control.
    ///
    /// SCENE STEPS ARE THE NAMED UNDO GROUPS its tools already make ("Zetta Agent: …"). Unity can only undo from the
    /// top of its stack, so each step remembers the records it put there; undoing the turn first checks that they are
    /// still the newest ones — never undoing something the user did after them. Not their position: Unity drops old
    /// records (an asset import can), which moves everything down.
    /// </summary>
    public static class AgentChanges
    {
        [Serializable]
        public class FileChange
        {
            public string Path;
            /// <summary>It was there before the agent touched it (edited or deleted); false = the agent created it.</summary>
            public bool Existed;
            public bool HadMeta;
            public bool Folder;
            /// <summary>Set when the agent moved or renamed it: where to.</summary>
            public string MovedTo;
            public bool Undone;
            /// <summary>What it was when it was undone (created, edited, deleted…), so the card still says so.</summary>
            public string UndoneKind;
            /// <summary>The user kept it. It can still be undone: its copy stays until the turn is pruned.</summary>
            public bool Kept;
        }

        [Serializable]
        public class SceneStep
        {
            public string Label;
            public string Scene;
            /// <summary>The records the step put on Unity's undo list: its name, then the operations in it (see <see cref="UndoNames"/>).</summary>
            public List<string> Records = new List<string>();
        }

        [Serializable]
        public class ChangeSet
        {
            public string Id;
            public List<FileChange> Files = new List<FileChange>();
            /// <summary>Folders the agent created (the topmost new one of each path), removed with their files.</summary>
            public List<string> Folders = new List<string>();
            public List<SceneStep> Steps = new List<SceneStep>();
            public bool StepsUndone;
            public bool StepsKept;
            /// <summary>Open scenes the agent saved — after undoing its steps, they are saved again to match.</summary>
            public List<string> SavedScenes = new List<string>();
        }

        /// <summary>One line of the changes card: a file, a created folder (its files folded in), or the scene steps.</summary>
        public class Row
        {
            public string Path;
            public string Kind; // created | edited | deleted | moved | folder | scene
            public string Detail;
            public bool Undone;
            public bool Kept;
            public FileChange File;

            /// <summary>Undone or kept: nothing is left to decide on this line (a kept one can still be undone).</summary>
            public bool Settled => Undone || Kept;
        }

        const string RootDir = "Library/Zetta3D/AgentChanges";
        const int KeepTurns = 40;

        /// <summary>True while the agent's tools run: what is written then is the agent's.</summary>
        public static bool Recording;

        /// <summary>A change set was recorded into, or something in one was undone.</summary>
        public static event Action Changed;

        /// <summary>
        /// Each turn's set, loaded once: every card's lines point into the same objects, so an undo or keep on one card
        /// is never saved over by another card's copy.
        /// </summary>
        static readonly Dictionary<string, ChangeSet> Sets = new Dictionary<string, ChangeSet>();

        static string ProjectRoot => System.IO.Path.GetDirectoryName(Application.dataPath);

        /// <summary>The turn being recorded (the last message sent), or null.</summary>
        public static string Turn => AgentState.instance.ChangeTurn;

        /// <summary>A new message: what its tools change from here on is one change set.</summary>
        public static void BeginTurn()
        {
            // Time first, so the folders sort oldest to newest for pruning; the suffix keeps two turns in one millisecond apart.
            AgentState.instance.ChangeTurn = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            Prune();
        }

        // ─── Recording ──────────────────────────────────────────────────────────────

        /// <summary>The agent is about to write, create or overwrite <paramref name="path"/> (a project path under Assets/).</summary>
        public static void WillWrite(string path)
        {
            if (!Recording || string.IsNullOrEmpty(Turn)) return;
            path = Normalize(path);
            if (path == null || Directory.Exists(Full(path))) return;
            Record(path, folder: false);
        }

        /// <summary>The agent is about to create <paramref name="folder"/> (and any missing parents).</summary>
        public static void WillCreateFolder(string folder)
        {
            if (!Recording || string.IsNullOrEmpty(Turn)) return;
            folder = Normalize(folder);
            var created = TopmostMissing(folder);
            if (created == null) return;
            var set = Current();
            if (set.Folders.Any(f => created == f || created.StartsWith(f + "/"))) return;
            set.Folders.Add(created);
            Save(set);
        }

        /// <summary>The agent is about to delete <paramref name="path"/> — a file, or a folder and everything in it.</summary>
        public static void WillDelete(string path)
        {
            if (!Recording || string.IsNullOrEmpty(Turn)) return;
            path = Normalize(path);
            if (path == null) return;
            var full = Full(path);
            if (!Directory.Exists(full))
            {
                Record(path, folder: false);
                return;
            }
            Record(path, folder: true);
            foreach (var file in Directory.GetFiles(full, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".meta")))
                Record(Normalize(file), folder: false);
            foreach (var dir in Directory.GetDirectories(full, "*", SearchOption.AllDirectories))
                Record(Normalize(dir), folder: true);
        }

        /// <summary>The agent is about to move or rename <paramref name="from"/> to <paramref name="to"/>.</summary>
        public static void WillMove(string from, string to)
        {
            if (!Recording || string.IsNullOrEmpty(Turn)) return;
            from = Normalize(from);
            to = Normalize(to);
            if (from == null || to == null) return;
            var set = Current();
            // Moving something it made this turn: where it ends up is still "made", nothing to move back.
            var made = set.Files.FirstOrDefault(f => f.Path == from && !f.Existed && f.MovedTo == null);
            if (made != null) made.Path = to;
            else set.Files.Add(new FileChange { Path = from, Existed = true, MovedTo = to, Folder = Directory.Exists(Full(from)) });
            Save(set);
        }

        /// <summary>The agent saved an open scene — it is saved again once its steps are undone.</summary>
        public static void SceneSaved(string path)
        {
            if (!Recording || string.IsNullOrEmpty(Turn)) return;
            var set = Current();
            if (set.SavedScenes.Contains(path)) return;
            set.SavedScenes.Add(path);
            Save(set);
        }

        /// <summary>A tool finished its undo group named <paramref name="label"/>: its records, if it made any, are a scene step.</summary>
        public static void SceneStepDone(string label)
        {
            if (!Recording || string.IsNullOrEmpty(Turn)) return;
            var names = UndoNames();
            var start = names?.LastIndexOf(label) ?? -1;
            if (start < 0) return; // nothing recorded (or the list can't be read)
            var set = Current();
            var scene = EditorSceneManager.GetActiveScene().name;
            set.Steps.Add(new SceneStep { Label = label, Scene = string.IsNullOrEmpty(scene) ? "Untitled scene" : scene, Records = names.Skip(start).ToList() });
            set.StepsUndone = false;
            Save(set);
        }

        static void Record(string path, bool folder)
        {
            if (path == null) return;
            // An open scene, however it was saved: its changes are the scene steps, not a file to copy back over it
            // while it is open (that would make Unity reload it and drop what is on screen).
            if (path.EndsWith(".unity", StringComparison.Ordinal) && IsOpenScene(path))
            {
                SceneSaved(path);
                return;
            }
            var set = Current();
            if (set.Files.Any(f => f.Path == path || f.MovedTo == path)) return;
            // Inside a folder the agent made this turn: undoing the folder takes it back.
            if (set.Folders.Any(f => path.StartsWith(f + "/"))) return;
            var full = Full(path);
            var change = new FileChange { Path = path, Folder = folder, Existed = folder ? Directory.Exists(full) : File.Exists(full) };
            var blob = Blob(set, set.Files.Count);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(blob));
            if (change.Existed && !folder) File.Copy(full, blob, true);
            if (change.Existed && File.Exists(full + ".meta"))
            {
                File.Copy(full + ".meta", blob + ".meta", true);
                change.HadMeta = true;
            }
            if (!change.Existed)
            {
                var created = TopmostMissing(System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/'));
                if (created != null && !set.Folders.Contains(created)) set.Folders.Add(created);
            }
            set.Files.Add(change);
            Save(set);
        }

        // ─── Reading ────────────────────────────────────────────────────────────────

        public static bool Has(string turn)
        {
            var set = Load(turn);
            return set != null && Rows(set).Count > 0;
        }

        public static ChangeSet Load(string turn)
        {
            if (string.IsNullOrEmpty(turn)) return null;
            if (Sets.TryGetValue(turn, out var cached)) return cached;
            var file = IndexFile(turn);
            if (!File.Exists(file)) return null;
            try
            {
                var set = JsonConvert.DeserializeObject<ChangeSet>(File.ReadAllText(file));
                if (set != null) Sets[turn] = set;
                return set;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The card's lines: what is different now from before the turn, newest state of each path.</summary>
        public static List<Row> Rows(ChangeSet set)
        {
            var rows = new List<Row>();
            if (set == null) return rows;
            foreach (var folder in set.Folders)
            {
                var full = Full(folder);
                var undone = set.Files.Any(f => f.Path == folder && f.Undone);
                var kept = set.Files.Any(f => f.Path == folder && f.Kept);
                if (!undone && !Directory.Exists(full)) continue; // gone some other way: nothing to show
                var files = undone ? 0 : Directory.GetFiles(full, "*", SearchOption.AllDirectories).Count(f => !f.EndsWith(".meta"));
                rows.Add(new Row { Path = folder + "/", Kind = "folder", Detail = undone ? null : files == 1 ? "1 file" : $"{files} files", Undone = undone, Kept = kept });
            }
            foreach (var change in set.Files)
            {
                if (set.Folders.Any(f => change.Path == f || change.Path.StartsWith(f + "/"))) continue;
                // An open scene's file is its scene steps' line (recorded as a file before that was told apart).
                if (set.Steps.Count > 0 && !change.Undone && change.Path.EndsWith(".unity", StringComparison.Ordinal) && IsOpenScene(change.Path)) continue;
                var kind = KindOf(set, change);
                if (kind == null) continue;
                // A folder the agent deleted is one line; its files are folded in.
                if (set.Files.Any(f => f.Folder && f.Existed && f.MovedTo == null && f != change && change.Path.StartsWith(f.Path + "/") && KindOf(set, f) == "deleted"))
                    continue;
                rows.Add(new Row
                {
                    Path = change.MovedTo != null ? $"{change.Path} → {change.MovedTo}" : change.Path + (change.Folder ? "/" : ""),
                    Kind = kind,
                    Undone = change.Undone,
                    Kept = change.Kept,
                    File = change,
                });
            }
            if (set.Steps.Count > 0)
            {
                var scenes = set.Steps.Select(s => s.Scene).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
                rows.Add(new Row
                {
                    Path = scenes.Count == 1 ? scenes[0] : "The open scenes",
                    Kind = "scene",
                    Detail = set.Steps.Count == 1 ? "1 change" : $"{set.Steps.Count} changes",
                    Undone = set.StepsUndone,
                    Kept = set.StepsKept,
                });
            }
            return rows;
        }

        /// <summary>created | edited | deleted | moved — or null when nothing is different any more.</summary>
        static string KindOf(ChangeSet set, FileChange change)
        {
            if (change.MovedTo != null) return "moved";
            var full = Full(change.Path);
            var exists = change.Folder ? Directory.Exists(full) : File.Exists(full);
            if (change.Undone) return change.UndoneKind ?? (change.Existed ? "edited" : "created");
            if (!change.Existed) return exists ? "created" : null;
            if (!exists) return "deleted";
            if (change.Folder) return null;
            return Same(full, BlobFor(set, change)) ? null : "edited";
        }

        static bool Same(string a, string b)
        {
            try
            {
                if (b == null || !File.Exists(b)) return false;
                var infoA = new FileInfo(a);
                var infoB = new FileInfo(b);
                if (infoA.Length != infoB.Length) return false;
                if (infoA.Length > 8 * 1024 * 1024) return false;
                return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ─── Preview ────────────────────────────────────────────────────────────────

        /// <summary>One line of a preview: a diff line (' ' kept, '+' added, '-' removed), a fold ('~'), or an item ('•').</summary>
        public class PreviewLine
        {
            public char Op;
            public string Text;
            public int Old;
            public int New;
        }

        /// <summary>What a row of the card opens to: its diff, its contents, or a note.</summary>
        public class Preview
        {
            public string Note;
            public readonly List<PreviewLine> Lines = new List<PreviewLine>();
            /// <summary>Lines left out at the end because the preview is long.</summary>
            public int More;
            public int Added;
            public int Removed;
        }

        const int PreviewLimit = 400;
        const int Context = 3;
        const long TextLimit = 1024 * 1024;

        public static Preview PreviewOf(string turn, Row row)
        {
            var preview = new Preview();
            var set = Load(turn);
            if (set == null)
            {
                preview.Note = "These changes are no longer recorded.";
                return preview;
            }
            if (row.Kind == "scene")
            {
                foreach (var step in set.Steps)
                    preview.Lines.Add(new PreviewLine { Op = '•', Text = step.Label.StartsWith("Zetta Agent: ") ? step.Label.Substring(13) : step.Label });
                if (set.StepsUndone) preview.Note = "Undone — the scene is back to how it was before this message.";
                return preview;
            }
            if (row.Kind == "folder")
            {
                var folder = row.Path.TrimEnd('/');
                if (row.Undone || !Directory.Exists(Full(folder)))
                {
                    preview.Note = "Undone — the folder and what was in it went to the trash.";
                    return preview;
                }
                var root = Full(folder);
                foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".meta")).OrderBy(f => f, StringComparer.Ordinal))
                    preview.Lines.Add(new PreviewLine { Op = '•', Text = file.Substring(root.Length).Replace('\\', '/').TrimStart('/') });
                return preview;
            }

            var change = row.File;
            if (change == null) return preview;
            if (change.Undone)
            {
                preview.Note = row.Kind switch
                {
                    "created" => "Undone — the file went to the trash.",
                    "deleted" => "Undone — the file is back.",
                    "moved" => "Undone — moved back.",
                    _ => "Undone — back to the version from before this message.",
                };
                return preview;
            }
            if (change.MovedTo != null)
            {
                preview.Note = $"Moved from {change.Path} to {change.MovedTo}.";
                return preview;
            }
            if (change.Folder)
            {
                preview.Note = "A folder — Undo puts it back with what was in it.";
                return preview;
            }

            string oldProblem = null, newProblem = null;
            var before = change.Existed ? ReadText(BlobFor(set, change), out oldProblem) : "";
            var full = Full(change.Path);
            var after = File.Exists(full) ? ReadText(full, out newProblem) : "";
            if (before == null || after == null)
            {
                preview.Note = oldProblem ?? newProblem ?? "Can't be shown here — open it to see it.";
                return preview;
            }
            Diff(before, after, preview);
            if (preview.Lines.Count == 0 && preview.Note == null) preview.Note = "No differences.";
            return preview;

            string ReadText(string path, out string problem)
            {
                problem = null;
                if (path == null || !File.Exists(path)) return "";
                var info = new FileInfo(path);
                if (info.Length > TextLimit)
                {
                    problem = "Too large to show here — open it to see it.";
                    return null;
                }
                var bytes = File.ReadAllBytes(path);
                for (var i = 0; i < Math.Min(bytes.Length, 8000); i++)
                    if (bytes[i] == 0)
                    {
                        problem = "A binary file — open it to see it.";
                        return null;
                    }
                return System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Replace("\r\n", "\n");
            }
        }

        /// <summary>
        /// A line diff of two texts as the preview shows it: changes with <see cref="Context"/> lines around them, the
        /// unchanged stretches between folded. The common start and end are trimmed first, so the longest-common-
        /// subsequence table only covers the part that changed.
        /// </summary>
        static void Diff(string before, string after, Preview preview)
        {
            var a = before.Length == 0 ? Array.Empty<string>() : before.TrimEnd('\n').Split('\n');
            var b = after.Length == 0 ? Array.Empty<string>() : after.TrimEnd('\n').Split('\n');
            var start = 0;
            while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
            var end = 0;
            while (end < a.Length - start && end < b.Length - start && a[a.Length - 1 - end] == b[b.Length - 1 - end]) end++;

            var ops = new List<(char op, int i, int j)>();
            for (var k = 0; k < start; k++) ops.Add((' ', k, k));
            int n = a.Length - start - end, m = b.Length - start - end;
            if ((long)n * m <= 4_000_000)
            {
                var table = new int[n + 1, m + 1];
                for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    table[i, j] = a[start + i] == b[start + j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
                int x = 0, y = 0;
                while (x < n || y < m)
                {
                    if (x < n && y < m && a[start + x] == b[start + y]) ops.Add((' ', start + x++, start + y++));
                    // Removed before added, as diffs read.
                    else if (x < n && (y == m || table[x + 1, y] >= table[x, y + 1])) ops.Add(('-', start + x++, -1));
                    else ops.Add(('+', -1, start + y++));
                }
            }
            else
            {
                // Too big to compare line by line: shown as replaced.
                for (var i = 0; i < n; i++) ops.Add(('-', start + i, -1));
                for (var j = 0; j < m; j++) ops.Add(('+', -1, start + j));
            }
            for (var k = 0; k < end; k++) ops.Add((' ', a.Length - end + k, b.Length - end + k));

            preview.Added = ops.Count(o => o.op == '+');
            preview.Removed = ops.Count(o => o.op == '-');
            if (preview.Added + preview.Removed == 0) return;

            // Which kept lines are near a change and stay; the rest fold into "⋯ N unchanged lines".
            var near = new bool[ops.Count];
            for (var k = 0; k < ops.Count; k++)
            {
                if (ops[k].op == ' ') continue;
                for (var c = Math.Max(0, k - Context); c <= Math.Min(ops.Count - 1, k + Context); c++) near[c] = true;
            }
            var shown = 0;
            for (var k = 0; k < ops.Count; k++)
            {
                if (!near[k])
                {
                    var skipped = 0;
                    while (k < ops.Count && !near[k])
                    {
                        skipped++;
                        k++;
                    }
                    k--;
                    preview.Lines.Add(new PreviewLine { Op = '~', Text = skipped == 1 ? "1 unchanged line" : $"{skipped} unchanged lines" });
                    continue;
                }
                if (shown == PreviewLimit)
                {
                    preview.More = ops.Skip(k).Count(o => o.op != ' ');
                    break;
                }
                var (op, i, j) = ops[k];
                preview.Lines.Add(new PreviewLine { Op = op, Text = op == '+' ? b[j] : a[i], Old = i + 1, New = j + 1 });
                shown++;
            }
        }

        // ─── Undoing ────────────────────────────────────────────────────────────────

        /// <summary>Takes back one line of the card. Returns a sentence for the user when it could not.</summary>
        public static string UndoRow(string turn, Row row)
        {
            var set = Load(turn);
            if (set == null) return "These changes are no longer recorded.";
            string problem;
            if (row.Kind == "scene") problem = UndoSteps(set);
            else if (row.Kind == "folder") problem = UndoFolder(set, row.Path.TrimEnd('/'));
            else problem = UndoFile(set, row.File);
            Save(set);
            AssetDatabase.Refresh();
            Changed?.Invoke();
            return problem;
        }

        /// <summary>
        /// Takes back the turn: the scene steps, then moves, files and folders. Lines the user kept are left alone
        /// unless <paramref name="includeKept"/> — "Undo the rest" leaves them, "Undo all" (once all are kept) does not.
        /// </summary>
        public static string UndoAll(string turn, bool includeKept = false)
        {
            var set = Load(turn);
            if (set == null) return "These changes are no longer recorded.";
            var problems = new List<string>();
            if (set.Steps.Count > 0 && !set.StepsUndone && (includeKept || !set.StepsKept)) Add(UndoSteps(set));
            // Newest first, so a file moved and then edited goes back in the order it changed.
            foreach (var change in Enumerable.Reverse(set.Files).Where(f => !f.Undone && (includeKept || !f.Kept) && KindOf(set, f) != null).ToList())
            {
                if (set.Folders.Any(f => change.Path == f || change.Path.StartsWith(f + "/"))) continue;
                Add(UndoFile(set, change));
            }
            foreach (var folder in set.Folders.Where(f => Directory.Exists(Full(f)) && (includeKept || !set.Files.Any(c => c.Path == f && c.Kept))).ToList())
                Add(UndoFolder(set, folder));
            Save(set);
            AssetDatabase.Refresh();
            Changed?.Invoke();
            return problems.Count == 0 ? null : string.Join(" ", problems.Distinct());

            void Add(string problem)
            {
                if (problem != null) problems.Add(problem);
            }
        }

        // ─── Keeping ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Keeps one line: it stays as the agent left it, marked kept on the card — and can still be undone later.
        /// Keeping is also what happens to anything never decided; this only says so.
        /// </summary>
        public static void KeepRow(string turn, Row row)
        {
            var set = Load(turn);
            if (set == null) return;
            Keep(set, row);
            Save(set);
            Changed?.Invoke();
        }

        /// <summary>Keeps every line not decided yet.</summary>
        public static void KeepAll(string turn)
        {
            var set = Load(turn);
            if (set == null) return;
            foreach (var row in Rows(set).Where(r => !r.Settled)) Keep(set, row);
            Save(set);
            Changed?.Invoke();
        }

        static void Keep(ChangeSet set, Row row)
        {
            if (row.Settled) return;
            if (row.Kind == "scene") set.StepsKept = true;
            else if (row.Kind == "folder")
            {
                var folder = row.Path.TrimEnd('/');
                var mark = set.Files.FirstOrDefault(f => f.Path == folder);
                if (mark == null) set.Files.Add(new FileChange { Path = folder, Folder = true, Kept = true });
                else mark.Kept = true;
            }
            else if (row.File != null) row.File.Kept = true;
        }

        static string UndoFile(ChangeSet set, FileChange change)
        {
            if (change == null || change.Undone) return null;
            var full = Full(change.Path);
            var kind = KindOf(set, change);
            try
            {
                if (change.MovedTo != null)
                {
                    var error = AssetDatabase.MoveAsset(change.MovedTo, change.Path);
                    if (!string.IsNullOrEmpty(error)) return $"Could not move {change.MovedTo} back: {error}";
                }
                else if (!change.Existed)
                {
                    if (File.Exists(full) && !AssetDatabase.MoveAssetToTrash(change.Path))
                    {
                        File.Delete(full);
                        if (File.Exists(full + ".meta")) File.Delete(full + ".meta");
                    }
                }
                else if (change.Folder)
                {
                    Directory.CreateDirectory(full);
                    if (change.HadMeta) File.Copy(BlobFor(set, change) + ".meta", full + ".meta", true);
                }
                else
                {
                    var blob = BlobFor(set, change);
                    if (!File.Exists(blob)) return $"The copy of {change.Path} from before the agent changed it is gone.";
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
                    if (change.HadMeta) File.Copy(blob + ".meta", full + ".meta", true);
                    File.Copy(blob, full, true);
                    AssetDatabase.ImportAsset(change.Path, ImportAssetOptions.ForceUpdate);
                }
                change.Undone = true;
                change.Kept = false;
                change.UndoneKind = kind;
                return null;
            }
            catch (Exception e)
            {
                return $"Could not undo {change.Path}: {e.Message}";
            }
        }

        static string UndoFolder(ChangeSet set, string folder)
        {
            if (Directory.Exists(Full(folder)) && !AssetDatabase.MoveAssetToTrash(folder))
                return $"Could not remove {folder}.";
            var mark = set.Files.FirstOrDefault(f => f.Path == folder);
            if (mark == null) set.Files.Add(new FileChange { Path = folder, Folder = true, Undone = true });
            else
            {
                mark.Undone = true;
                mark.Kept = false;
            }
            foreach (var change in set.Files.Where(f => f.Path.StartsWith(folder + "/"))) change.Undone = true;
            return null;
        }

        static string UndoSteps(ChangeSet set)
        {
            if (set.StepsUndone || set.Steps.Count == 0) return null;
            if (EditorApplication.isPlaying) return "Leave Play Mode first — scene changes can't be undone while it runs.";
            var names = UndoNames();
            if (names == null) return "Unity's undo list can't be read in this version — use Edit › Undo to take back the scene changes.";
            // The turn's records must be the newest on the list, in order: nothing done since would be undone with them.
            var expected = set.Steps.SelectMany(s => s.Records).ToList();
            var onTop = names.Count >= expected.Count && names.Skip(names.Count - expected.Count).SequenceEqual(expected);
            if (!onTop)
                return "The scene can't be put back from here: it was changed after these steps (by you or a later message), " +
                       "or they were already undone. Use Edit › Undo for anything newer first, then try again.";

            foreach (var _ in set.Steps) Undo.PerformUndo();
            set.StepsUndone = true;
            set.StepsKept = false;
            // Saved by the agent: save again, so the scene on disk matches what is back on screen.
            foreach (var path in set.SavedScenes)
            {
                var scene = EditorSceneManager.GetSceneByPath(path);
                if (scene.IsValid() && scene.isLoaded && scene.isDirty) EditorSceneManager.SaveScene(scene);
            }
            return null;
        }

        // ─── Unity's undo list ──────────────────────────────────────────────────────

        static readonly System.Reflection.MethodInfo GetRecords = typeof(Undo).GetMethod("GetRecords",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
            null, new[] { typeof(List<string>), typeof(List<string>) }, null);

        /// <summary>
        /// The undo stack's records, oldest first, or null when this Unity cannot list them (an internal API). A group
        /// shows as its name ("Zetta Agent: …") followed by the operations inside it.
        /// </summary>
        public static List<string> UndoNames()
        {
            if (GetRecords == null) return null;
            var undo = new List<string>();
            try
            {
                GetRecords.Invoke(null, new object[] { undo, new List<string>() });
            }
            catch (Exception)
            {
                return null;
            }
            return undo;
        }

        // ─── Storage ────────────────────────────────────────────────────────────────

        static ChangeSet Current()
        {
            var turn = Turn;
            var set = Load(turn);
            if (set != null) return set;
            set = new ChangeSet { Id = turn };
            Sets[turn] = set;
            return set;
        }

        static void Save(ChangeSet set)
        {
            try
            {
                Directory.CreateDirectory(TurnDir(set.Id));
                File.WriteAllText(IndexFile(set.Id), JsonConvert.SerializeObject(set, Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Zetta Agent] Could not record a change for undo: " + e.Message);
            }
        }

        static string TurnDir(string turn) => System.IO.Path.Combine(ProjectRoot, RootDir, turn);
        static string IndexFile(string turn) => System.IO.Path.Combine(TurnDir(turn), "changes.json");
        static string Blob(ChangeSet set, int index) => System.IO.Path.Combine(TurnDir(set.Id), "files", index.ToString(CultureInfo.InvariantCulture));

        static string BlobFor(ChangeSet set, FileChange change)
        {
            var index = set.Files.IndexOf(change);
            return index < 0 ? null : Blob(set, index);
        }

        /// <summary>Keeps the newest turns' copies; older ones (and their undo) are let go.</summary>
        static void Prune()
        {
            try
            {
                var root = System.IO.Path.Combine(ProjectRoot, RootDir);
                if (!Directory.Exists(root)) return;
                foreach (var old in Directory.GetDirectories(root).OrderByDescending(d => d, StringComparer.Ordinal).Skip(KeepTurns))
                {
                    Directory.Delete(old, true);
                    Sets.Remove(System.IO.Path.GetFileName(old));
                }
            }
            catch (Exception)
            {
                // A copy in use or locked: tried again next turn.
            }
        }

        static string Full(string path) => System.IO.Path.Combine(ProjectRoot, path);

        static bool IsOpenScene(string path)
        {
            var scene = EditorSceneManager.GetSceneByPath(path);
            return scene.IsValid() && scene.isLoaded;
        }

        /// <summary>A project path under Assets/ ("Assets/Scripts/Player.cs"), or null for anything else.</summary>
        static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            path = path.Replace('\\', '/');
            var root = ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";
            if (path.StartsWith(root, StringComparison.Ordinal)) path = path.Substring(root.Length);
            if (path.EndsWith(".meta", StringComparison.Ordinal)) path = path.Substring(0, path.Length - 5);
            path = path.TrimEnd('/');
            return path.StartsWith("Assets/", StringComparison.Ordinal) ? path : null;
        }

        /// <summary>The highest folder of <paramref name="folder"/>'s path that does not exist yet, or null.</summary>
        static string TopmostMissing(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !folder.StartsWith("Assets/", StringComparison.Ordinal)) return null;
            string missing = null;
            var current = folder;
            while (!string.IsNullOrEmpty(current) && current != "Assets" && !Directory.Exists(Full(current)))
            {
                missing = current;
                current = System.IO.Path.GetDirectoryName(current)?.Replace('\\', '/');
            }
            return missing;
        }
    }

    /// <summary>Asset saves, creations, deletions and moves made through the AssetDatabase while the agent's tools run.</summary>
    class AgentChangesWatcher : AssetModificationProcessor
    {
        static string[] OnWillSaveAssets(string[] paths)
        {
            if (!AgentChanges.Recording) return paths;
            foreach (var path in paths)
            {
                // An open scene's changes are the agent's undo steps; its file is saved again to match when they are undone.
                // An open scene goes to the scene steps either way (see Record); a scene not open is a file like any other.
                AgentChanges.WillWrite(path);
            }
            return paths;
        }

        static void OnWillCreateAsset(string path) => AgentChanges.WillWrite(path);

        static AssetDeleteResult OnWillDeleteAsset(string path, RemoveAssetOptions options)
        {
            AgentChanges.WillDelete(path);
            return AssetDeleteResult.DidNotDelete;
        }

        static AssetMoveResult OnWillMoveAsset(string from, string to)
        {
            AgentChanges.WillMove(from, to);
            return AssetMoveResult.DidNotMove;
        }
    }
}
