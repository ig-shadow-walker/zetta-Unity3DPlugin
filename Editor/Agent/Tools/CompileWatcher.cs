using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Compilation;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// Keeps the errors of the most recent script compilation in SessionState, so they survive the
    /// domain reload that follows it (and so the snapshot and console_logs can report them).
    /// </summary>
    [InitializeOnLoad]
    static class CompileWatcher
    {
        const string Key = "Zetta3D.Agent.CompileErrors";

        [Serializable]
        class Entry
        {
            [JsonProperty("file")] public string File;
            [JsonProperty("line")] public int Line;
            [JsonProperty("message")] public string Message;
        }

        static List<Entry> _pending;

        static CompileWatcher()
        {
            CompilationPipeline.compilationStarted += _ => _pending = new List<Entry>();
            CompilationPipeline.assemblyCompilationFinished += OnAssembly;
            CompilationPipeline.compilationFinished += _ => Commit();
        }

        static void OnAssembly(string assembly, CompilerMessage[] messages)
        {
            _pending ??= new List<Entry>();
            foreach (var m in messages)
            {
                if (m.type != CompilerMessageType.Error) continue;
                _pending.Add(new Entry { File = (m.file ?? "").Replace('\\', '/'), Line = m.line, Message = Clean(m.message) });
            }
            // Commit as we go too: compilationFinished may not fire before a reload in every version.
            Save(_pending);
        }

        static void Commit()
        {
            Save(_pending ?? new List<Entry>());
            _pending = null;
        }

        static void Save(List<Entry> entries)
        {
            var unique = entries
                .GroupBy(e => (e.File, e.Line, e.Message))
                .Select(g => g.First())
                .Take(200)
                .ToList();
            SessionState.SetString(Key, JsonConvert.SerializeObject(unique));
        }

        static readonly Regex Prefix = new Regex(@"^.*?\(\d+,\d+\):\s*error\s+", RegexOptions.Compiled);

        /// <summary>"Assets/X.cs(12,5): error CS1002: ; expected" → "CS1002: ; expected".</summary>
        static string Clean(string message)
        {
            message = Fmt.FirstLine(message ?? "");
            var m = Prefix.Match(message);
            return m.Success ? message.Substring(m.Length) : message;
        }

        public static IReadOnlyList<(string file, int line, string message)> Errors()
        {
            try
            {
                var entries = JsonConvert.DeserializeObject<List<Entry>>(SessionState.GetString(Key, "[]")) ?? new List<Entry>();
                return entries.Select(e => (e.File, e.Line, e.Message)).ToList();
            }
            catch (Exception)
            {
                return Array.Empty<(string, int, string)>();
            }
        }
    }
}
