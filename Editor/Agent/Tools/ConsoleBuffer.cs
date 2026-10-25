using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// The last 500 console messages, captured from <see cref="Application.logMessageReceivedThreaded"/>.
    /// Carried across domain reloads through SessionState (the last 200).
    /// </summary>
    [InitializeOnLoad]
    static class ConsoleBuffer
    {
        const int Capacity = 500;
        const string Key = "Zetta3D.Agent.Console";

        [Serializable]
        class Entry
        {
            [JsonProperty("t")] public DateTime Time;
            [JsonProperty("k")] public string Kind;
            [JsonProperty("m")] public string Message;
            [JsonProperty("f")] public string Frame;
            [JsonProperty("n")] public int Count = 1;
        }

        static readonly object Lock = new object();
        static readonly LinkedList<Entry> Entries = new LinkedList<Entry>();

        static ConsoleBuffer()
        {
            try
            {
                var saved = JsonConvert.DeserializeObject<List<Entry>>(SessionState.GetString(Key, "[]"));
                if (saved != null)
                    foreach (var e in saved)
                        Entries.AddLast(e);
            }
            catch (Exception)
            {
                // A corrupt carry-over is not worth failing over.
            }
            Application.logMessageReceivedThreaded += OnLog;
            AssemblyReloadEvents.beforeAssemblyReload += Persist;
        }

        static void Persist()
        {
            List<Entry> copy;
            lock (Lock) copy = Entries.Skip(Math.Max(0, Entries.Count - 200)).ToList();
            SessionState.SetString(Key, JsonConvert.SerializeObject(copy));
        }

        static void OnLog(string message, string stackTrace, LogType type)
        {
            var kind = type switch
            {
                LogType.Warning => "warning",
                LogType.Log => "log",
                _ => "error",
            };
            if (message != null && message.Length > 2000) message = message.Substring(0, 2000) + "…";
            var frame = TopFrame(stackTrace);
            lock (Lock)
            {
                var last = Entries.Last?.Value;
                if (last != null && last.Kind == kind && last.Message == message && last.Frame == frame)
                {
                    last.Count++;
                    last.Time = DateTime.Now;
                    return;
                }
                Entries.AddLast(new Entry { Time = DateTime.Now, Kind = kind, Message = message ?? "", Frame = frame });
                while (Entries.Count > Capacity) Entries.RemoveFirst();
            }
        }

        /// <summary>The first stack frame outside Unity's and .NET's own code.</summary>
        static string TopFrame(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace)) return null;
            string first = null;
            foreach (var raw in stackTrace.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                first ??= line;
                if (line.StartsWith("UnityEngine.") || line.StartsWith("UnityEditor.") || line.StartsWith("System.") ||
                    line.StartsWith("Unity.") || line.StartsWith("Zetta3D.Editor.Agent.ConsoleBuffer"))
                    continue;
                return line.Length > 300 ? line.Substring(0, 300) : line;
            }
            return first != null && first.Length > 300 ? first.Substring(0, 300) : first;
        }

        public static ToolResult Run(JObject input)
        {
            var types = Args.StrList(input, "types")?.Select(t => t.Trim().ToLowerInvariant()).ToHashSet();
            if (types != null && types.Count == 0) types = null;
            var limit = Args.Int(input, "limit", 50, 1, 200);

            var sb = new StringBuilder();
            var compile = CompileWatcher.Errors();
            if ((types == null || types.Contains("error")) && compile.Count > 0)
            {
                sb.AppendLine($"Compile errors from the last compilation ({compile.Count}):");
                foreach (var (file, line, message) in compile.Take(20))
                    sb.AppendLine($"  {file}({line}): {message}");
                sb.AppendLine();
            }
            else if (EditorUtility.scriptCompilationFailed && (types == null || types.Contains("error")))
            {
                sb.AppendLine("Scripts currently have compile errors (details were not captured this session; see the Console).");
                sb.AppendLine();
            }

            List<Entry> picked;
            int total;
            lock (Lock)
            {
                var filtered = Entries.Reverse().Where(e => types == null || types.Contains(e.Kind)).ToList();
                total = filtered.Count;
                picked = filtered.Take(limit).ToList();
            }

            if (picked.Count == 0)
            {
                sb.AppendLine(total == 0 ? "No console messages captured since the last domain reload." : "No messages of those types.");
                return ToolResult.Ok(sb.ToString().TrimEnd());
            }

            sb.AppendLine($"{picked.Count} of {total} messages, most recent first:");
            foreach (var e in picked)
            {
                var count = e.Count > 1 ? $" (×{e.Count})" : "";
                sb.Append($"[{e.Time:HH:mm:ss}] {e.Kind}{count}: ").AppendLine(Fmt.Short(e.Message, 600));
                if (!string.IsNullOrEmpty(e.Frame)) sb.Append("    at ").AppendLine(e.Frame);
            }
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }
    }
}
