using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Zetta3D.Editor.Agent
{
    /// <summary>
    /// Drives the Unity Agent conversation (see the Unity agent protocol): sends a turn,
    /// streams the reply, runs the client tools the server hands back — with the user's approval
    /// where it is needed — waits out compilation and the domain reload that follows a new script,
    /// and resumes the turn with the results. Everything it needs to resume lives in
    /// <see cref="AgentState"/>, so a reload in the middle is a pause, not a loss.
    /// </summary>
    [InitializeOnLoad]
    public static class AgentRuntime
    {
        const string ChatPath = "/unityAgent/chat";

        public static AgentState State => AgentState.instance;
        public static bool Busy => State.Phase != "idle";

        /// <summary>Raised on the main thread whenever the transcript or phase changes.</summary>
        public static event Action Changed;

        static CancellationTokenSource _stream;
        static int _reloadLocks;
        // Set once this domain starts work of its own: a phase seen after that is live, not left over from a reload.
        static bool _activeThisDomain;
        static readonly Dictionary<ChatItem, TaskCompletionSource<bool>> Approvals = new Dictionary<ChatItem, TaskCompletionSource<bool>>();
        static readonly Dictionary<ChatItem, TaskCompletionSource<List<AskAnswer>>> Questions = new Dictionary<ChatItem, TaskCompletionSource<List<AskAnswer>>>();
        // Bumped when a batch of tool calls starts or is cancelled: a call that finishes for a batch that is over sends nothing.
        static int _batch;

        static AgentRuntime()
        {
            ZettaJobs.Finished += OnJobFinished;
            Pipelines.RunFollower.RunFinished += OnPipelineRunFinished;
            Pipelines.RunFollower.RunNeedsUser += run => OnPipelineRunFinished(run, null);
            EditorApplication.delayCall += ResumeAfterReload;
        }

        static void Notify() => Changed?.Invoke();

        // ─── Public API (the window calls these) ────────────────────────────────────

        public static void NewChat()
        {
            Stop(false);
            State.Reset();
            Notify();
        }

        public static void Send(string message, List<ToolImage> images = null, List<AgentDocument> documents = null)
        {
            message = (message ?? "").Trim();
            if (message.Length == 0 || !ZettaAuth.IsSignedIn) return;

            // Typing while the agent waits on tools: the server closes the pending calls itself.
            if (State.Phase is "tools" or "compiling") CancelPendingApprovals();
            if (State.Phase is "streaming" or "sending") return;
            State.Halted = false;

            // The bubble shows what the user typed; the blocks the agent reads (tags, inlined files) become a summary line.
            var (shown, extras) = ForDisplay(message);
            var summary = AttachedSummary(images, documents);
            State.Items.Add(new ChatItem
            {
                Kind = "user",
                Text = shown,
                Detail = string.Join(" · ", new[] { extras, summary }.Where(x => !string.IsNullOrEmpty(x))),
            });
            ClearPending();
            var body = Base();
            body["message"] = message;
            if (images is { Count: > 0 }) body["images"] = JArray.FromObject(images.Take(4));
            if (documents is { Count: > 0 })
                body["documents"] = new JArray(documents.Take(4).Select(d => new JObject { ["data"] = d.Data, ["mediaType"] = d.MediaType, ["name"] = d.Name }));
            _ = Stream(body);
        }

        static readonly System.Text.RegularExpressions.Regex MentionsBlock =
            new System.Text.RegularExpressions.Regex(@"\s*<mentions>(.*?)</mentions>", System.Text.RegularExpressions.RegexOptions.Singleline);
        static readonly System.Text.RegularExpressions.Regex FileBlock =
            new System.Text.RegularExpressions.Regex(@"\s*<attached_file name=""([^""]*)"">.*?</attached_file>", System.Text.RegularExpressions.RegexOptions.Singleline);
        static readonly System.Text.RegularExpressions.Regex PicturesNote =
            new System.Text.RegularExpressions.Regex(@"\s*\(Attached: [^)]*\)\s*$");

        /// <summary>A user message as the bubble shows it: the typed words, and a summary of tags and inlined files.</summary>
        public static (string text, string extras) ForDisplay(string message)
        {
            if (string.IsNullOrEmpty(message)) return (message, null);
            var extras = new List<string>();
            var text = MentionsBlock.Replace(message, m =>
            {
                // Each tag by its name: "@MainMenuController.cs", "@Player/Camera" → "@Camera".
                foreach (var line in m.Groups[1].Value.Split('\n'))
                {
                    var tag = System.Text.RegularExpressions.Regex.Match(line, "^\\s*-\\s*(?:scene object|asset)\\s+\"([^\"]+)\"");
                    if (!tag.Success) continue;
                    var path = tag.Groups[1].Value;
                    extras.Add("@" + (path.Contains('/') ? path.Substring(path.LastIndexOf('/') + 1) : path));
                }
                return "";
            });
            text = FileBlock.Replace(text, m =>
            {
                extras.Add(m.Groups[1].Value);
                return "";
            });
            text = PicturesNote.Replace(text, "");
            return (text.Trim(), extras.Count == 0 ? null : string.Join(" · ", extras));
        }

        static string AttachedSummary(List<ToolImage> images, List<AgentDocument> documents)
        {
            var parts = new List<string>();
            if (images is { Count: > 0 }) parts.Add($"{images.Count} image{(images.Count > 1 ? "s" : "")}");
            if (documents is { Count: > 0 }) parts.Add($"{documents.Count} PDF{(documents.Count > 1 ? "s" : "")}");
            return parts.Count == 0 ? null : string.Join(" and ", parts) + " attached";
        }

        /// <summary>Answers a question card with the chosen option.</summary>
        public static void Answer(ChatItem question, string option)
        {
            question.Resolved = true;
            question.Detail = option;
            Send(option);
        }

        /// <summary>Answers an ask_user card (OK), or skips it when <paramref name="answers"/> is null.</summary>
        public static void SubmitAnswers(ChatItem card, List<AskAnswer> answers)
        {
            if (card.Resolved) return;
            card.Resolved = true;
            card.Status = answers == null ? "skipped" : "answered";
            card.AnswersJson = answers == null ? null : JsonConvert.SerializeObject(answers);
            if (Questions.TryGetValue(card, out var tcs))
            {
                Questions.Remove(card);
                tcs.TrySetResult(answers);
            }
            Notify();
        }

        public static void Resolve(ChatItem approval, bool allowed)
        {
            approval.Resolved = true;
            approval.Status = allowed ? "done" : "declined";
            if (Approvals.TryGetValue(approval, out var tcs))
            {
                Approvals.Remove(approval);
                tcs.TrySetResult(allowed);
            }
            Notify();
        }

        /// <summary>
        /// Stops the agent in whatever it is doing — replying, waiting on an approval, running tools or
        /// waiting for a compile — tells the server to end the turn, and holds everything until the user
        /// types again. <paramref name="byUser"/> false is a quiet cancel (switching chats).
        /// </summary>
        public static void Stop(bool byUser = true)
        {
            var wasBusy = Busy;
            var sessionId = State.SessionId;

            // Settle the state before anything that was waiting gets to run again: a batch released from an
            // approval card must find the turn halted and idle, not carry on and send a "declined" result.
            if (wasBusy && byUser)
            {
                State.Halted = true;
                State.QueuedEvents.Clear();
            }
            State.Phase = "idle";
            ClearPending();
            try
            {
                _stream?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            CancelPendingApprovals(byUser ? "stopped" : "declined");

            foreach (var item in State.Items)
                if (item.Kind == "step" && item.Status is "running" or "waiting")
                    item.Status = "stopped";

            if (wasBusy && byUser)
            {
                var lastUser = State.Items.FindLastIndex(i => i.Kind == "user");
                var replied = State.Items.Skip(lastUser + 1).Any(i => i.Kind == "assistant" && !string.IsNullOrWhiteSpace(i.Text));
                State.Items.Add(new ChatItem { Kind = "notice", Text = replied ? "Stopped." : "Stopped before it replied." });
            }
            FinishOpenText();
            ReleaseReloadLocks();
            Notify();

            // The server ends the model's turn and closes any calls it is waiting on, so the next message starts clean.
            if (wasBusy && sessionId > 0)
                _ = StopOnServer(sessionId);
        }

        static async Task StopOnServer(long sessionId)
        {
            try
            {
                await ZettaHttp.PostAsync($"/unityAgent/sessions/{sessionId}/stop");
            }
            catch (ZettaApiException)
            {
                // An older server without the route, or nothing running: the next message closes pending calls anyway.
            }
        }

        /// <summary>Loads a stored conversation (and resumes it if the server is waiting on tools).</summary>
        public static async Task OpenSession(long id)
        {
            Stop(false);
            try
            {
                var json = await ZettaHttp.GetAsync($"/unityAgent/sessions/{id}");
                State.Reset();
                State.SessionId = id;
                State.SessionTitle = json["session"]?["title"]?.ToString();
                foreach (var item in json["transcript"] as JArray ?? new JArray())
                {
                    var role = item["role"]?.ToString();
                    if (role == "tool")
                        State.Items.Add(new ChatItem
                        {
                            Kind = "step",
                            ToolName = item["name"]?.ToString(),
                            ToolUseId = item["toolUseId"]?.ToString(),
                            Client = item["side"]?.ToString() == "client",
                            Status = item["isError"]?.Value<bool>() == true ? "error" : "done",
                            Text = item["summary"]?.ToString() ?? AgentLabels.ServerTool(item["name"]?.ToString()),
                        });
                    else
                    {
                        var text = item["text"]?.ToString();
                        string extras = null;
                        if (role == "user") (text, extras) = ForDisplay(text);
                        State.Items.Add(new ChatItem { Kind = role == "event" ? "event" : role, Text = text, Detail = string.IsNullOrEmpty(extras) ? null : extras });
                    }
                }
                var awaiting = json["awaiting"]?.ToObject<List<ClientToolCall>>() ?? new List<ClientToolCall>();
                if (awaiting.Count > 0)
                {
                    State.Items.Add(new ChatItem { Kind = "notice", Text = "This conversation was waiting on the Editor. Carrying on…" });
                    State.PendingCallsJson = JsonConvert.SerializeObject(awaiting);
                    Notify();
                    await RunPendingTools();
                    return;
                }
            }
            catch (ZettaApiException e)
            {
                AddError(e.Message);
            }
            Notify();
        }

        // ─── Streaming ──────────────────────────────────────────────────────────────

        static JObject Base()
        {
            var project = ZettaProjectSettings.Instance;
            var body = new JObject
            {
                ["projectId"] = project.ProjectId,
                ["projectName"] = ZettaProjectSettings.ProjectName,
                ["tier"] = ZettaPrefs.AgentTier,
                ["adaptive"] = ZettaPrefs.AgentAdaptive,
                ["mode"] = ZettaPrefs.AgentMode,
                ["approval"] = ZettaPrefs.AgentFullAutonomy ? "auto" : "confirm_destructive",
            };
            // Model and thinking are each Auto on their own: "auto" here lets the server decide the thinking.
            body["thinking"] = ZettaPrefs.AgentThinking;
            if (State.SessionId > 0) body["sessionId"] = State.SessionId;
            try
            {
                var snapshot = UnityToolbox.Snapshot();
                if (snapshot != null && snapshot.ToString(Formatting.None).Length <= 60_000) body["editor"] = snapshot;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Zetta Agent] Could not describe the Editor: " + e.Message);
            }
            return body;
        }

        static async Task Stream(JObject body)
        {
            _activeThisDomain = true;
            State.Phase = body["toolResults"] != null ? "sending" : "streaming";
            // A new message is routed afresh: the last turn's model is not shown while this one's is decided.
            if (body["toolResults"] == null)
            {
                State.RouteLabel = State.RouteThinking = State.RouteReason = State.RouteSource = null;
                State.RouteThinkingAsked = body["thinking"]?.ToString();
            }
            Notify();
            _stream = new CancellationTokenSource();
            var cancel = _stream.Token;
            var awaiting = new List<string>();
            var calls = new List<ClientToolCall>();
            LockReload();
            try
            {
                await ZettaHttp.StreamAsync(ChatPath, body, evt => OnEvent(evt, calls, awaiting), cancel);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ZettaApiException e)
            {
                AddError(e.IsUnauthorized ? "Sign in to Zetta 3D to carry on." : e.Message);
            }
            catch (Exception e)
            {
                AddError("The agent stopped unexpectedly: " + e.Message);
            }
            finally
            {
                ReleaseReloadLocks();
                FinishOpenText();
                if (State.Phase is "streaming" or "sending") State.Phase = "idle";
                Notify();
            }

            if (cancel.IsCancellationRequested) return;
            if (awaiting.Count > 0)
            {
                // Keep the server's order; a call we somehow did not see still gets answered (as not run).
                var ordered = awaiting.Select(id => calls.FirstOrDefault(c => c.ToolUseId == id)
                                                   ?? new ClientToolCall { ToolUseId = id, Name = "unknown" }).ToList();
                State.PendingCallsJson = JsonConvert.SerializeObject(ordered);
                await RunPendingTools();
                return;
            }

            DrainQueuedEvents();
        }

        static void OnEvent(JObject evt, List<ClientToolCall> calls, List<string> awaiting)
        {
            var type = evt["type"]?.ToString();
            switch (type)
            {
                case "session":
                    State.SessionId = evt["sessionId"]?.Value<long>() ?? State.SessionId;
                    break;
                case "route":
                    // The model and thinking the server chose — Auto's pick, or the hand-picked one after the plan's
                    // ceiling. Shown beside the working indicator, not as a line in the conversation.
                    var route = evt["route"] as JObject;
                    State.RouteLabel = route?["label"]?.ToString();
                    State.RouteThinking = route?["thinking"]?.ToString();
                    State.RouteReason = route?["reason"]?.ToString();
                    State.RouteSource = route?["source"]?.ToString();
                    break;
                case "text":
                    AppendText(evt["text"]?.ToString() ?? "", evt["start"]?.Value<bool>() == true);
                    break;
                case "tool":
                    FinishOpenText();
                    var client = evt["side"]?.ToString() == "client";
                    State.Items.Add(new ChatItem
                    {
                        Kind = "step",
                        ToolName = evt["name"]?.ToString(),
                        ToolUseId = evt["toolUseId"]?.ToString(),
                        Client = client,
                        Status = client ? "waiting" : "running",
                        Text = client ? AgentLabels.Pending(evt["name"]?.ToString()) : AgentLabels.ServerTool(evt["name"]?.ToString()),
                    });
                    break;
                case "tool_result":
                    var step = Step(evt["toolUseId"]?.ToString());
                    var isError = evt["isError"]?.Value<bool>() == true;
                    if (step != null)
                    {
                        step.Status = isError ? "error" : "done";
                        if (isError) step.Detail = Shorten(evt["content"]?.ToString(), 240);
                    }
                    var imageUrl = evt["imageUrl"]?.ToString();
                    if (!string.IsNullOrEmpty(imageUrl))
                    {
                        var imageId = evt["imageId"]?.Value<long>() ?? 0;
                        if (imageId > 0) State.RememberImage(imageId, imageUrl);
                        State.Items.Add(new ChatItem { Kind = "image", ImageUrl = imageUrl, ImageId = imageId, Text = imageId > 0 ? $"Image #{imageId}" : "Image" });
                    }
                    break;
                case "client_tool":
                    var call = evt.ToObject<ClientToolCall>();
                    if (call != null)
                    {
                        call.Input ??= new JObject();
                        calls.Add(call);
                        var existing = Step(call.ToolUseId);
                        if (existing == null)
                        {
                            existing = new ChatItem { Kind = "step", ToolName = call.Name, ToolUseId = call.ToolUseId, Client = true };
                            State.Items.Add(existing);
                        }
                        existing.Status = "waiting";
                        existing.Text = SafeDescribe(call);
                    }
                    break;
                case "memory":
                    var memory = evt["memory"];
                    State.Items.Add(new ChatItem
                    {
                        Kind = "memory",
                        Text = memory?["text"]?.ToString(),
                        Detail = memory?["action"]?.ToString() == "forgotten" ? "Forgot" : memory?["scope"]?.ToString() == "global" ? "Remembered (all projects)" : "Remembered (this project)",
                        ImageId = memory?["id"]?.Value<long>() ?? 0,
                    });
                    break;
                case "brief":
                    State.Items.Add(new ChatItem { Kind = "event", Text = "Updated the project brief." });
                    break;
                case "question":
                    var q = evt["question"];
                    State.Items.Add(new ChatItem
                    {
                        Kind = "question",
                        Text = q?["question"]?.ToString(),
                        Options = (q?["options"] as JArray ?? new JArray()).Select(o => o["label"]?.ToString()).Where(s => s != null).ToList(),
                    });
                    break;
                case "done":
                    State.TokensThisSession += evt["tokens"]?.Value<int>() ?? 0;
                    if (evt["stopped"]?.Value<bool>() == true) State.Halted = true;
                    foreach (var id in evt["awaiting"] as JArray ?? new JArray()) awaiting.Add(id.ToString());
                    break;
                case "error":
                    AddError(evt["message"]?.ToString() ?? "The agent could not finish.");
                    break;
            }
            Notify();
        }

        static ChatItem _openText;

        static void AppendText(string text, bool start)
        {
            if (_openText == null || !State.Items.Contains(_openText))
            {
                _openText = new ChatItem { Kind = "assistant", Text = "" };
                State.Items.Add(_openText);
            }
            else if (start && _openText.Text.Length > 0) _openText.Text += "\n\n";
            _openText.Text += text;
        }

        static void FinishOpenText() => _openText = null;

        static ChatItem Step(string toolUseId) =>
            string.IsNullOrEmpty(toolUseId) ? null : State.Items.LastOrDefault(i => i.Kind == "step" && i.ToolUseId == toolUseId);

        // ─── Client tools ───────────────────────────────────────────────────────────

        static async Task RunPendingTools()
        {
            _activeThisDomain = true;
            State.Phase = "tools";
            Notify();
            var batch = ++_batch;
            var calls = JsonConvert.DeserializeObject<List<ClientToolCall>>(State.PendingCallsJson) ?? new List<ClientToolCall>();
            var results = JsonConvert.DeserializeObject<List<ToolResult>>(State.PendingResultsJson) ?? new List<ToolResult>();
            var context = new ToolRunContext
            {
                SessionId = State.SessionId,
                Confirm = Ask,
                Status = line =>
                {
                    var running = State.Items.LastOrDefault(i => i.Kind == "step" && i.Status == "running");
                    if (running != null) running.Detail = line;
                    Notify();
                },
                ImageUrl = State.ImageUrl,
            };
            context.AskUser = request => AskUser(request, context.ToolUseId);

            LockReload();
            try
            {
                for (var i = results.Count; i < calls.Count; i++)
                {
                    if (State.Phase != "tools") return; // the user stopped or sent a new message
                    var call = calls[i];
                    var step = Step(call.ToolUseId) ?? AddStep(call);
                    ToolResult result;

                    if (NeedsApproval(call))
                    {
                        step.Status = "waiting";
                        Notify();
                        var allowed = await Ask(new ApprovalRequest
                        {
                            Kind = call.Effect,
                            Title = SafeDescribe(call),
                            Detail = SafeSummarize(call),
                            Reasons = call.Review?.Reasons ?? new List<string>(),
                            ConfirmLabel = call.Effect == "destructive" ? "Delete" : "Allow",
                        });
                        if (State.Phase != "tools") return;
                        if (!allowed)
                        {
                            step.Status = "declined";
                            results.Add(new ToolResult { ToolUseId = call.ToolUseId, IsError = true, Content = "The user declined this action. Ask what they would like instead, or continue without it." });
                            Save(results);
                            continue;
                        }
                    }

                    step.Status = call.Name == "ask_user" ? "waiting" : "running";
                    context.ToolUseId = call.ToolUseId;
                    Notify();
                    try
                    {
                        result = UnityToolbox.Has(call.Name)
                            ? await UnityToolbox.RunAsync(call, context)
                            : ToolResult.Error($"This version of the Zetta 3D package does not have the tool '{call.Name}'. Update the package, or do it another way.");
                    }
                    catch (Exception e)
                    {
                        result = ToolResult.Error($"{call.Name} failed: {e.Message}");
                    }
                    // Stopped, or a new message took over, while the tool waited (on the user, a download…).
                    if (batch != _batch || State.Phase != "tools") return;
                    result.ToolUseId = call.ToolUseId;
                    step.Status = result.IsError ? "error" : "done";
                    step.Detail = result.IsError ? Shorten(result.Content, 240) : step.Detail;
                    if (result.WroteScripts) State.LastScriptResult = results.Count;
                    results.Add(result);
                    Save(results);
                    Notify();
                }
            }
            finally
            {
                ReleaseReloadLocks();
            }

            // Stopped (or a new message took over) while the batch ran: nothing is sent on.
            if (State.Phase != "tools" || State.Halted) return;

            if (State.LastScriptResult >= 0)
            {
                State.Phase = "compiling";
                State.Items.Add(new ChatItem { Kind = "notice", Text = "Compiling scripts…" });
                Notify();
                AssetDatabase.Refresh();
                // If the new scripts compile, Unity reloads the domain here; ResumeAfterReload picks it up.
                await WaitForCompilation();
                if (State.Phase == "compiling") await FinishCompileAndSend();
                return;
            }

            await SendResults();
        }

        static ChatItem AddStep(ClientToolCall call)
        {
            var step = new ChatItem { Kind = "step", ToolName = call.Name, ToolUseId = call.ToolUseId, Client = true, Text = SafeDescribe(call) };
            State.Items.Add(step);
            return step;
        }

        static bool NeedsApproval(ClientToolCall call)
        {
            // Full autonomy: nothing waits on the user — not even a call the pre-flight check flagged.
            if (ZettaPrefs.AgentFullAutonomy) return false;
            var approval = ZettaPrefs.AgentApproval == "confirm_all" ? "confirm_destructive" : ZettaPrefs.AgentApproval;
            var flagged = call.Review?.Confirm == true;

            // These manage_pipeline_run actions show their own card (credits or cancel); asking here too would ask twice.
            if (call.Name == "manage_pipeline_run" && !flagged &&
                call.Input?["action"]?.ToString() is "cancel" or "run_waiting" or "run_step" or "retry_step" or "set_mode")
                return false;

            switch (call.Effect)
            {
                case "generate":
                    return false; // the tool shows its own credit card through ctx.Confirm
                case "destructive":
                    return approval != "auto" || flagged;
                case "write":
                    return approval == "confirm_all" || flagged;
                default:
                    return flagged;
            }
        }

        static Task<bool> Ask(ApprovalRequest request)
        {
            if (ZettaPrefs.AgentFullAutonomy)
            {
                // The user chose full autonomy: approve it for them, and leave a line saying so (and what it cost).
                State.Items.Add(new ChatItem
                {
                    Kind = "event",
                    Text = "Approved automatically · " + request.Title + (request.Credits is > 0 ? $" · {request.Credits} credits" : ""),
                });
                Notify();
                return Task.FromResult(true);
            }

            var item = new ChatItem
            {
                Kind = "approval",
                Text = request.Title,
                Detail = request.Detail,
                ApprovalKind = request.Kind,
                Credits = request.Credits,
                Options = new List<string> { request.ConfirmLabel ?? "Allow" },
            };
            if (request.Reasons is { Count: > 0 })
                item.Detail = "Why this needs a look: " + string.Join("; ", request.Reasons) + (string.IsNullOrEmpty(item.Detail) ? "" : "\n\n" + item.Detail);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Approvals[item] = tcs;
            State.Items.Add(item);
            Notify();
            AgentWindow.Reveal();
            return tcs.Task;
        }

        static Task<List<AskAnswer>> AskUser(AskRequest request, string toolUseId)
        {
            // After a reload the same call asks again: its card is still in the chat, so it is used, not doubled.
            var card = string.IsNullOrEmpty(toolUseId) ? null : State.Items.LastOrDefault(i => i.Kind == "ask" && !i.Resolved && i.ToolUseId == toolUseId);
            if (card == null)
            {
                card = new ChatItem
                {
                    Kind = "ask",
                    ToolUseId = toolUseId,
                    Text = request.Title,
                    QuestionsJson = JsonConvert.SerializeObject(request.Questions),
                };
                State.Items.Add(card);
            }
            var tcs = new TaskCompletionSource<List<AskAnswer>>(TaskCreationOptions.RunContinuationsAsynchronously);
            Questions[card] = tcs;
            Notify();
            AgentWindow.Reveal();
            return tcs.Task;
        }

        /// <summary>
        /// Closes every open approval card as not allowed. The phase leaves "tools" FIRST, so the batch that
        /// was waiting on a card sees it has been superseded and sends nothing on.
        /// </summary>
        static void CancelPendingApprovals(string status = "declined")
        {
            if (State.Phase is "tools" or "compiling") State.Phase = "idle";
            _batch++;
            var asked = Questions.ToList();
            Questions.Clear();
            foreach (var pair in asked)
            {
                pair.Key.Resolved = true;
                pair.Key.Status = status == "stopped" ? "stopped" : "superseded";
                pair.Value.TrySetResult(null);
            }
            var open = Approvals.ToList();
            Approvals.Clear();
            foreach (var pair in open)
            {
                pair.Key.Resolved = true;
                pair.Key.Status = status;
                pair.Value.TrySetResult(false);
            }
        }

        static void Save(List<ToolResult> results) => State.PendingResultsJson = JsonConvert.SerializeObject(results);

        static void ClearPending()
        {
            State.PendingCallsJson = "[]";
            State.PendingResultsJson = "[]";
            State.LastScriptResult = -1;
        }

        static async Task WaitForCompilation()
        {
            // Give the refresh a moment to start a compilation, then wait for it (and the import) to finish.
            var started = DateTime.UtcNow;
            while ((DateTime.UtcNow - started).TotalSeconds < 1.5 && !EditorApplication.isCompiling)
                await Task.Delay(100);
            var settled = DateTime.MinValue;
            while (true)
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) settled = DateTime.MinValue;
                else if (settled == DateTime.MinValue) settled = DateTime.UtcNow;
                else if ((DateTime.UtcNow - settled).TotalSeconds > 0.6) return;
                await Task.Delay(100);
            }
        }

        static async void ResumeAfterReload()
        {
            if (_activeThisDomain || State.Halted) return;
            if (State.Phase == "compiling")
            {
                await WaitForCompilation();
                if (State.Phase == "compiling") await FinishCompileAndSend();
            }
            else if (State.Phase is "streaming" or "sending")
            {
                // A reload cut a stream (e.g. the user entered Play Mode with reload on). The server kept the turn.
                State.Phase = "idle";
                State.Items.Add(new ChatItem { Kind = "notice", Text = "The Editor reloaded while the agent was replying. Reopen this chat from the history to see where it got to." });
                Notify();
            }
            else if (State.Phase == "tools")
            {
                // Tools were interrupted by a reload we did not expect; carry on from the last finished one.
                await RunPendingTools();
            }
        }

        static async Task FinishCompileAndSend()
        {
            var results = JsonConvert.DeserializeObject<List<ToolResult>>(State.PendingResultsJson) ?? new List<ToolResult>();
            var errors = UnityToolbox.LastCompileErrors();
            var report = errors.Count == 0
                ? "Compiled OK."
                : "Compile errors:\n" + string.Join("\n", errors.Take(20).Select(e => $"{e.file}({e.line}): {e.message}"));
            var index = State.LastScriptResult;
            if (index >= 0 && index < results.Count)
                results[index].Content = ToolResult.Truncate(results[index].Content + "\n\n" + report);
            Save(results);
            var notice = State.Items.LastOrDefault(i => i.Kind == "notice" && i.Text == "Compiling scripts…");
            if (notice != null) notice.Text = errors.Count == 0 ? "Scripts compiled." : $"Scripts have {errors.Count} compile error{(errors.Count > 1 ? "s" : "")} — the agent will fix them.";
            State.LastScriptResult = -1;
            await SendResults();
        }

        static async Task SendResults()
        {
            var calls = JsonConvert.DeserializeObject<List<ClientToolCall>>(State.PendingCallsJson) ?? new List<ClientToolCall>();
            var results = JsonConvert.DeserializeObject<List<ToolResult>>(State.PendingResultsJson) ?? new List<ToolResult>();
            if (calls.Count == 0) return;
            var body = Base();
            body["toolResults"] = JArray.FromObject(results, JsonSerializer.Create(ZettaHttp.Json));
            ClearPending();
            await Stream(body);
        }

        // ─── App events ─────────────────────────────────────────────────────────────

        static void OnJobFinished(ZettaJobs.Job job)
        {
            if (job.Source != "agent" || job.AgentSessionId != State.SessionId || State.SessionId == 0) return;
            var evt = new JObject { ["kind"] = "job_finished", ["generationId"] = job.Id, ["status"] = job.Status };
            if (!string.IsNullOrEmpty(job.ImportedPath)) evt["importedPath"] = job.ImportedPath;
            State.Items.Add(new ChatItem
            {
                Kind = "event",
                Text = job.Status == "success"
                    ? $"“{job.Label}” is ready" + (job.ImportedPath != null ? $" — imported to {job.ImportedPath}" : "")
                    : $"“{job.Label}” {job.Status}" + (job.Error != null ? $": {job.Error}" : ""),
            });
            // After Stop the agent does not pick the job up on its own; the line above still tells the user.
            if (!State.Halted) State.QueuedEvents.Add(evt.ToString(Formatting.None));
            Notify();
            if (!Busy) DrainQueuedEvents();
        }

        /// <summary>
        /// A pipeline run the agent started (run_pipeline / manage_pipeline_run) finished, or stopped to wait for the
        /// user (a manual step, an isolate-parts review). The server re-reads the run and describes it to the model.
        /// </summary>
        static void OnPipelineRunFinished(Pipelines.PipelineRun run, IReadOnlyList<string> importedPaths)
        {
            if (run == null || State.SessionId == 0) return;
            if (Pipelines.RunFollower.AgentSessionIdOf(run.Id) != State.SessionId) return;
            var status = run.Status switch
            {
                Pipelines.RunStatuses.Done => "done",
                Pipelines.RunStatuses.Failed => "failed",
                Pipelines.RunStatuses.Cancelled => "cancelled",
                Pipelines.RunStatuses.Awaiting => "awaiting",
                _ => null,
            };
            if (status == null) return; // the protocol carries only these four; anything else is not an event

            var evt = new JObject { ["kind"] = "pipeline_run_finished", ["runId"] = run.Id, ["status"] = status };
            var paths = importedPaths?.Where(p => !string.IsNullOrEmpty(p)).Take(50).ToList();
            if (paths is { Count: > 0 }) evt["importedPaths"] = new JArray(paths);
            State.Items.Add(new ChatItem
            {
                Kind = "event",
                Text = status switch
                {
                    "done" => $"Pipeline run #{run.Id} finished" + (paths is { Count: > 0 } ? $" — {paths.Count} model(s) imported" : ""),
                    "awaiting" => $"Pipeline run #{run.Id} is waiting for you",
                    _ => $"Pipeline run #{run.Id} {status}",
                },
            });
            if (!State.Halted) State.QueuedEvents.Add(evt.ToString(Formatting.None));
            Notify();
            if (!Busy) DrainQueuedEvents();
        }

        static void DrainQueuedEvents()
        {
            if (Busy || State.Halted || State.QueuedEvents.Count == 0) return;
            var evt = JObject.Parse(State.QueuedEvents[0]);
            State.QueuedEvents.RemoveAt(0);
            var body = Base();
            body["event"] = evt;
            _ = Stream(body);
        }

        // ─── Helpers ────────────────────────────────────────────────────────────────

        static void LockReload()
        {
            EditorApplication.LockReloadAssemblies();
            _reloadLocks++;
        }

        static void ReleaseReloadLocks()
        {
            while (_reloadLocks > 0)
            {
                EditorApplication.UnlockReloadAssemblies();
                _reloadLocks--;
            }
        }

        static void AddError(string message)
        {
            FinishOpenText();
            State.Items.Add(new ChatItem { Kind = "error", Text = message });
            Notify();
        }

        static string SafeDescribe(ClientToolCall call)
        {
            try
            {
                return UnityToolbox.Describe(call);
            }
            catch (Exception)
            {
                return AgentLabels.Pending(call.Name);
            }
        }

        static string SafeSummarize(ClientToolCall call)
        {
            try
            {
                return UnityToolbox.Summarize(call);
            }
            catch (Exception)
            {
                return call.Input?.ToString(Formatting.Indented);
            }
        }

        static string Shorten(string text, int max) =>
            string.IsNullOrEmpty(text) || text.Length <= max ? text : text.Substring(0, max - 1) + "…";
    }

    /// <summary>Plain-language labels for tool steps.</summary>
    public static class AgentLabels
    {
        public static string ServerTool(string name) => name switch
        {
            "remember" => "Remembering that",
            "forget" => "Forgetting that",
            "update_project_brief" => "Updating the project brief",
            "generate_image" => "Drawing a picture",
            "ask_options" => "Asking you to choose",
            _ => name,
        };

        public static string Pending(string name) => name switch
        {
            "capture_view" => "Looking at the scene",
            "scene_hierarchy" => "Reading the hierarchy",
            "write_file" => "Writing a file",
            "edit_file" => "Editing a file",
            "read_file" => "Reading a file",
            "generate_3d_model" => "Setting up a 3D model",
            "process_model" => "Setting up a model tool",
            "import_generation" => "Importing a model",
            "list_generations" => "Looking through your models",
            "unity_project_info" => "Reading the project",
            "inspect_gameobject" => "Inspecting an object",
            "console_logs" => "Reading the console",
            "ask_user" => "Asking you",
            _ => name?.Replace('_', ' ') ?? "Working",
        };
    }
}
