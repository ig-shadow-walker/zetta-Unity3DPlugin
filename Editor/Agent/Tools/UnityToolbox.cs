using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// The Editor side of the Unity agent's client tools (the Unity agent protocol):
    /// a registry, a never-throwing dispatcher, chat labels, approval-card summaries, and the
    /// EditorSnapshot sent with every request.
    /// </summary>
    public static class UnityToolbox
    {
        class Tool
        {
            public Func<JObject, ToolRunContext, Task<ToolResult>> Run;
            /// <summary>Grouped into one named Undo step (synchronous scene edits only).</summary>
            public bool Undoable;
        }

        static Tool Sync(Func<JObject, ToolResult> run, bool undoable = false) =>
            new Tool { Run = (input, _) => Task.FromResult(run(input)), Undoable = undoable };

        static Tool Async(Func<JObject, ToolRunContext, Task<ToolResult>> run) => new Tool { Run = run };

        static readonly Dictionary<string, Tool> Tools = new Dictionary<string, Tool>
        {
            // Read
            ["unity_project_info"] = Sync(ProjectInfo.Run),
            ["scene_hierarchy"] = Sync(SceneTools.Hierarchy),
            ["inspect_gameobject"] = Sync(InspectTools.Inspect),
            ["find_gameobjects"] = Sync(SceneTools.Find),
            ["find_assets"] = Sync(AssetTools.Find),
            ["read_file"] = Sync(FileTools.Read),
            ["search_files"] = Sync(FileTools.Search),
            ["console_logs"] = Sync(ConsoleBuffer.Run),
            ["capture_view"] = Sync(CaptureTools.Capture),
            ["get_selection"] = Sync(SceneTools.Selection_),
            ["ask_user"] = Async(AskTools.Run),
            ["list_generations"] = Async(ZettaTools.List),
            ["list_pipelines"] = Async(ZettaTools.ListPipelines),
            ["get_pipeline"] = Async(ZettaTools.GetPipelineTool),
            ["pipeline_run_status"] = Async(ZettaTools.PipelineRunStatus),
            // Write
            ["write_file"] = Sync(FileTools.Write),
            ["edit_file"] = Sync(FileTools.Edit),
            ["create_gameobject"] = Sync(SceneTools.Create, true),
            ["modify_gameobject"] = Sync(SceneTools.Modify, true),
            ["add_component"] = Sync(ComponentTools.AddComponent, true),
            ["set_properties"] = Sync(ComponentTools.SetProperties, true),
            ["create_prefab"] = Sync(AssetTools.CreatePrefab, true),
            ["create_material"] = Sync(AssetTools.CreateMaterial, true),
            ["assign_material"] = Sync(AssetTools.AssignMaterial, true),
            ["scene_operation"] = Sync(SceneTools.SceneOperation),
            ["move_asset"] = Sync(AssetTools.Move),
            ["save_image_asset"] = Async(ZettaTools.SaveImage),
            ["import_generation"] = Async(ZettaTools.Import),
            ["play_mode"] = Sync(SceneTools.PlayMode),
            ["execute_menu_item"] = Sync(SceneTools.MenuItem, true),
            ["undo"] = Sync(SceneTools.Undo_),
            ["build_pipeline"] = Async(ZettaTools.BuildPipeline),
            ["edit_pipeline"] = Async(ZettaTools.EditPipeline),
            ["manage_pipeline_run"] = Async(ZettaTools.ManagePipelineRun),
            ["import_pipeline_results"] = Async(ZettaTools.ImportPipelineResults),
            // Destructive
            ["delete_gameobject"] = Sync(SceneTools.Delete, true),
            ["remove_component"] = Sync(ComponentTools.RemoveComponent, true),
            ["delete_asset"] = Sync(AssetTools.Delete),
            // Generate
            ["generate_3d_model"] = Async(ZettaTools.Generate),
            ["process_model"] = Async(ZettaTools.ProcessModel),
            ["run_pipeline"] = Async(ZettaTools.RunPipeline),
        };

        public static bool Has(string name) => !string.IsNullOrEmpty(name) && Tools.ContainsKey(name);

        /// <summary>Runs a client tool. Never throws; errors come back as isError results.</summary>
        public static async Task<ToolResult> RunAsync(ClientToolCall call, ToolRunContext ctx)
        {
            ToolResult result;
            var name = call?.Name ?? "";
            ctx ??= new ToolRunContext();
            ctx.Status ??= _ => { };
            ctx.Confirm ??= _ => Task.FromResult(false);
            ctx.ImageUrl ??= _ => null;

            try
            {
                if (call == null) result = ToolResult.Error("No tool call.");
                else if (!Tools.TryGetValue(name, out var tool)) result = ToolResult.Error($"Unknown client tool '{name}'. This editor supports: {string.Join(", ", Tools.Keys)}.");
                else
                {
                    var input = call.Input ?? new JObject();
                    var group = -1;
                    var label = "Zetta Agent: " + Describe(call);
                    if (tool.Undoable)
                    {
                        Undo.IncrementCurrentGroup();
                        group = Undo.GetCurrentGroup();
                        Undo.SetCurrentGroupName(label);
                    }
                    try
                    {
                        result = await tool.Run(input, ctx);
                    }
                    finally
                    {
                        if (group >= 0)
                        {
                            Undo.SetCurrentGroupName(label);
                            Undo.CollapseUndoOperations(group);
                            Undo.IncrementCurrentGroup();
                            // What this step put on Unity's undo stack, so the turn can be undone from the top.
                            AgentChanges.SceneStepDone(label);
                        }
                    }
                }
            }
            catch (ToolException e)
            {
                result = ToolResult.Error(e.Message);
            }
            catch (ZettaApiException e)
            {
                result = ToolResult.Error(e.IsUnauthorized ? "Not signed in to Zetta 3D (the session expired). Ask the user to sign in again." : ZettaCatalog.Explain(e));
            }
            catch (Exception e)
            {
                var inner = e is AggregateException { InnerException: { } ie } ? ie : e;
                result = ToolResult.Error($"{name} failed: {inner.GetType().Name}: {Fmt.FirstLine(inner.Message)}");
            }

            result ??= ToolResult.Error($"{name} returned nothing.");
            result.ToolUseId = call?.ToolUseId;
            result.Content = ToolResult.Truncate(result.Content);
            if (result.Images != null && result.Images.Count > 4) result.Images = result.Images.Take(4).ToList();
            return result;
        }

        // ─── Labels ──────────────────────────────────────────────────────────────

        static string S(JObject input, string key) => Args.Str(input, key) ?? "";

        static string Target(JObject input, string key = "target") => TargetResolver.ShortName(S(input, key));

        /// <summary>A short present-tense label for the chat step. Never throws.</summary>
        public static string Describe(ClientToolCall call)
        {
            try
            {
                var i = call?.Input ?? new JObject();
                switch (call?.Name)
                {
                    case "unity_project_info": return "Reading the project settings";
                    case "scene_hierarchy": return Args.Present(i, "root") ? $"Reading the hierarchy under {Target(i, "root")}" : "Reading the scene hierarchy";
                    case "inspect_gameobject": return $"Inspecting {Target(i)}";
                    case "find_gameobjects":
                    {
                        var parts = new List<string>();
                        if (Args.Present(i, "name")) parts.Add($"named '{S(i, "name")}'");
                        if (Args.Present(i, "component")) parts.Add($"with {S(i, "component")}");
                        if (Args.Present(i, "tag")) parts.Add($"tagged {S(i, "tag")}");
                        if (Args.Present(i, "layer")) parts.Add($"on layer {S(i, "layer")}");
                        return "Finding objects " + (parts.Count > 0 ? string.Join(" ", parts) : "in the scene");
                    }
                    case "find_assets": return $"Searching assets for \"{Fmt.Short(S(i, "query"), 60)}\"";
                    case "read_file": return $"Reading {S(i, "path")}";
                    case "search_files": return $"Searching {S(i, "glob").DefaultIfBlank("scripts")} for /{Fmt.Short(S(i, "pattern"), 50)}/";
                    case "console_logs": return "Reading the console";
                    case "capture_view":
                    {
                        var angles = S(i, "angles");
                        var target = Args.Present(i, "target") ? Target(i) : null;
                        if (angles == "top") return target != null ? $"Looking at {target} from above" : "Looking at the scene from above";
                        if (angles == "around" || (target != null && angles != "current")) return target != null ? $"Looking around {target}" : "Looking around the scene";
                        return S(i, "view") == "game" ? "Looking at the Game view" : "Looking at the Scene view";
                    }
                    case "get_selection": return "Checking the selection";
                    case "ask_user": return AskTools.Describe(i);
                    case "list_generations": return Args.Present(i, "search") ? $"Searching your Zetta 3D library for \"{S(i, "search")}\"" : "Browsing your Zetta 3D library";
                    case "write_file": return $"Writing {S(i, "path")}";
                    case "edit_file": return $"Editing {S(i, "path")}";
                    case "create_gameobject": return $"Creating {S(i, "name").DefaultIfBlank("a GameObject")}";
                    case "modify_gameobject": return $"Updating {Target(i)}";
                    case "add_component": return $"Adding {S(i, "type")} to {Target(i)}";
                    case "set_properties": return $"Setting {S(i, "component")} properties on {Target(i)}";
                    case "create_prefab": return $"Saving prefab {S(i, "path")}";
                    case "create_material": return $"Creating material {S(i, "path")}";
                    case "assign_material": return $"Assigning {System.IO.Path.GetFileName(S(i, "material"))} to {Target(i)}";
                    case "scene_operation":
                        switch (S(i, "action"))
                        {
                            case "new": return Args.Bool(i, "additive", false) ? "Adding a new scene" : "Creating a new scene";
                            case "open": return $"Opening {System.IO.Path.GetFileName(S(i, "path"))}";
                            case "save": return "Saving the scene";
                            case "save_as": return $"Saving the scene as {S(i, "path")}";
                            default: return "Changing scenes";
                        }
                    case "move_asset": return $"Moving {S(i, "from")} to {S(i, "to")}";
                    case "save_image_asset": return $"Saving image #{S(i, "imageId")} to {S(i, "path")}";
                    case "import_generation": return $"Importing model #{S(i, "generationId")}";
                    case "play_mode":
                        switch (S(i, "action"))
                        {
                            case "enter": return "Entering Play mode";
                            case "exit": return "Exiting Play mode";
                            default: return "Pausing Play mode";
                        }
                    case "execute_menu_item": return $"Running menu {S(i, "path")}";
                    case "undo":
                        var steps = Args.Int(i, "steps", 1, 1, 20);
                        return steps == 1 ? "Undoing the last step" : $"Undoing {steps} steps";
                    case "delete_gameobject": return $"Deleting {Target(i)}";
                    case "remove_component": return $"Removing {S(i, "component")} from {Target(i)}";
                    case "delete_asset": return $"Deleting {S(i, "path")}";
                    case "generate_3d_model":
                        var title = S(i, "title");
                        if (title.Length == 0) title = Fmt.Short(S(i, "prompt"), 50);
                        return title.Length > 0 ? $"Generating a 3D model: {title}" : "Generating a 3D model";
                    case "process_model":
                    {
                        var id = S(i, "generationId");
                        string op;
                        try
                        {
                            op = AgentRequestBuilders.NormalizeOperation(S(i, "operation"));
                        }
                        catch (ToolException)
                        {
                            op = "";
                        }
                        switch (op)
                        {
                            case "retopologize": return $"Retopologizing #{id}";
                            case "smart_uv": return $"Laying out new UVs for #{id}";
                            case "texture": return $"Texturing #{id}";
                            case "rig": return $"Rigging #{id}";
                            case "segment": return $"Segmenting #{id}";
                            default: return $"Processing #{id}";
                        }
                    }
                    default:
                        return ZettaTools.DescribePipelineTool(call?.Name, i) ?? call?.Name ?? "Running a tool";
                }
            }
            catch (Exception)
            {
                return call?.Name ?? "Running a tool";
            }
        }

        static string DefaultIfBlank(this string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s;

        /// <summary>Detail text for an approval card. Never throws.</summary>
        public static string Summarize(ClientToolCall call)
        {
            try
            {
                var i = call?.Input ?? new JObject();
                switch (call?.Name)
                {
                    case "write_file": return FileTools.SummarizeWrite(i);
                    case "edit_file": return FileTools.SummarizeEdit(i);
                    case "delete_asset": return AssetTools.SummarizeDelete(i);
                    case "delete_gameobject":
                    {
                        var go = TargetResolver.TryResolve(S(i, "target"), out _, out var error);
                        if (go == null) return $"{S(i, "target")} ({error})";
                        var children = go.GetComponentsInChildren<Transform>(true).Skip(1).Select(t => t.name).ToList();
                        var text = $"Deletes {TargetResolver.Label(go)}";
                        if (children.Count > 0)
                            text += $" and its {Fmt.Plural(children.Count, "child", "children")}: {string.Join(", ", children.Take(12))}{(children.Count > 12 ? ", …" : "")}";
                        return text + ". Undo can restore it.";
                    }
                    case "remove_component":
                    {
                        var go = TargetResolver.TryResolve(S(i, "target"), out _, out var error);
                        var where = go != null ? TargetResolver.Label(go) : $"{S(i, "target")} ({error})";
                        var index = Args.IntN(i, "index");
                        return $"Removes {S(i, "component")}{(index > 0 ? $" (#{index})" : "")} from {where}.";
                    }
                    case "move_asset": return $"{S(i, "from")}\n→ {S(i, "to")}";
                    case "scene_operation":
                    {
                        var action = S(i, "action");
                        var text = $"{action} {S(i, "path")}".Trim();
                        if (Args.Bool(i, "additive", false)) text += " (additive)";
                        var dirty = TargetResolver.LoadedScenes().Where(s => s.isDirty).Select(TargetResolver.SceneLabel).ToList();
                        if (dirty.Count > 0 && action is "new" or "open") text += $"\nUnsaved changes in: {string.Join(", ", dirty)}";
                        return text;
                    }
                    case "create_gameobject":
                    {
                        var sb = new StringBuilder($"Create \"{S(i, "name")}\"");
                        if (Args.Present(i, "prefab")) sb.Append($" from {S(i, "prefab")}");
                        else if (Args.Present(i, "primitive")) sb.Append($" ({S(i, "primitive")})");
                        if (Args.Present(i, "parent")) sb.Append($" under {S(i, "parent")}");
                        if (Args.Present(i, "position")) sb.Append($" at {S(i, "position")}");
                        if (Args.Get(i, "components") is JArray comps && comps.Count > 0)
                            sb.Append("\nComponents: ").Append(string.Join(", ", comps.Select(c => c.Type == JTokenType.String ? c.ToString() : c["type"]?.ToString())));
                        return sb.ToString();
                    }
                    case "set_properties":
                        return $"{S(i, "component")} on {S(i, "target")}:\n{Compact(Args.Get(i, "properties"))}";
                    case "generate_3d_model": return ZettaTools.SummarizeGenerate(i);
                    case "process_model": return ZettaTools.SummarizeProcess(i);
                    case "execute_menu_item": return $"Runs the menu item \"{S(i, "path")}\".";
                    case "play_mode": return $"Play mode: {S(i, "action")}";
                    case "undo": return $"Undo {Args.Int(i, "steps", 1, 1, 20)} step(s).";
                    default:
                        return ZettaTools.SummarizePipelineTool(call?.Name, i) ?? Compact(i);
                }
            }
            catch (Exception)
            {
                try
                {
                    return Compact(call?.Input);
                }
                catch (Exception)
                {
                    return call?.Name ?? "";
                }
            }
        }

        static string Compact(JToken token)
        {
            if (token == null) return "";
            var s = token.ToString(Formatting.Indented);
            return s.Length > 1500 ? s.Substring(0, 1500) + "\n…" : s;
        }

        // ─── Snapshot ────────────────────────────────────────────────────────────

        /// <summary>The EditorSnapshot sent with every chat request (all protocol fields).</summary>
        public static JObject Snapshot()
        {
            var snapshot = new JObject();
            void Try(string key, Func<JToken> value)
            {
                try
                {
                    var v = value();
                    if (v != null) snapshot[key] = v;
                }
                catch (Exception)
                {
                    // A missing fact beats a failed request.
                }
            }

            Try("unityVersion", () => Application.unityVersion);
            Try("platform", () => EditorUserBuildSettings.activeBuildTarget.ToString());
            Try("renderPipeline", () => ProjectInfo.Pipeline());
            Try("colorSpace", () => PlayerSettings.colorSpace.ToString());
            Try("inputSystem", () => ProjectInfo.InputHandling());
            Try("scriptingBackend", () => ProjectInfo.ScriptingBackend());
            Try("isPlaying", () => EditorApplication.isPlaying);
            Try("isCompiling", () => EditorApplication.isCompiling);
            Try("activeScene", () =>
            {
                var s = SceneManager.GetActiveScene();
                return new JObject
                {
                    ["path"] = s.path ?? "", ["name"] = string.IsNullOrEmpty(s.name) ? "Untitled (never saved)" : s.name,
                    ["dirty"] = s.isDirty, ["rootCount"] = s.isLoaded ? s.rootCount : 0,
                };
            });
            Try("openScenes", () => new JArray(TargetResolver.LoadedScenes().Select(s => string.IsNullOrEmpty(s.path) ? "(untitled)" : s.path)));
            Try("selection", () => new JArray(Selection.objects.Where(o => o != null).Take(20).Select(o =>
            {
                var go = o as GameObject;
                var inScene = go != null && !EditorUtility.IsPersistent(go);
                return new JObject
                {
                    ["path"] = inScene ? TargetResolver.PathOf(go) : AssetDatabase.GetAssetPath(o),
                    ["type"] = inScene ? "GameObject" : o.GetType().Name,
                    ["instanceId"] = Ids.Of(o),
                };
            })));
            Try("compileErrors", () => new JArray(LastCompileErrors().Take(20).Select(e => new JObject { ["file"] = e.file, ["line"] = e.line, ["message"] = Fmt.Short(e.message, 400) })));
            Try("packages", () => new JArray(ProjectInfo.Packages().Take(80)));
            Try("hierarchy", () => SceneTools.SnapshotOutline(3, 20_000));
            Try("scriptFolders", () => new JArray(ProjectInfo.ScriptFolders()));
            return snapshot;
        }

        /// <summary>Errors from the most recent compilation, for the runtime's compile report.</summary>
        public static IReadOnlyList<(string file, int line, string message)> LastCompileErrors() => CompileWatcher.Errors();
    }
}
