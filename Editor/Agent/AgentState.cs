using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Zetta3D.Editor.Agent
{
    /// <summary>One line of the conversation as the window draws it.</summary>
    [Serializable]
    public class ChatItem
    {
        /// <summary>user | assistant | event | step | route | memory | question | ask | approval | image | error | notice | changes (Detail = the turn)</summary>
        public string Kind;
        public string Text;
        public string Detail;
        public string ToolUseId;
        public string ToolName;
        /// <summary>For steps: running | done | error | waiting | declined. For ask cards: answered | skipped | stopped | superseded</summary>
        public string Status;
        public string ImageUrl;
        public long ImageId;
        public bool Client;
        /// <summary>question: the option labels; approval: [confirm label]</summary>
        public List<string> Options = new List<string>();
        public int? Credits;
        public bool Resolved;
        public string ApprovalKind;
        /// <summary>ask: the card's questions (AskQuestion[]) and, once answered, the answers (AskAnswer[]), as JSON.</summary>
        public string QuestionsJson;
        public string AnswersJson;
    }

    /// <summary>
    /// The agent's state, kept across domain reloads. A ScriptableSingleton lives in memory and is
    /// serialized through a reload — which is exactly the moment the agent needs it most: it has just
    /// written a script, Unity recompiles, and the turn must carry on with the compile result.
    /// </summary>
    public class AgentState : ScriptableSingleton<AgentState>
    {
        public long SessionId;
        public string SessionTitle;
        public List<ChatItem> Items = new List<ChatItem>();

        /// <summary>idle | streaming | tools | compiling | sending</summary>
        public string Phase = "idle";

        /// <summary>The server's `awaiting` client calls for this pause, as JSON (ClientToolCall[]).</summary>
        public string PendingCallsJson = "[]";

        /// <summary>Results gathered so far for the pending batch, as JSON (ToolResult[]).</summary>
        public string PendingResultsJson = "[]";

        /// <summary>The index in the pending batch of the last call that wrote a script (-1 = none).</summary>
        public int LastScriptResult = -1;

        /// <summary>App events (job finished) that arrived while a turn was running, as JSON objects.</summary>
        public List<string> QueuedEvents = new List<string>();

        /// <summary>imageId → signed URL for pictures the server made in this session.</summary>
        public List<long> ImageIds = new List<long>();
        public List<string> ImageUrls = new List<string>();

        public int TokensThisSession;

        /// <summary>
        /// The model tier and thinking the server is running this turn on (its `route` event): "Capable", "low",
        /// and why — Auto's reason, or "chosen by hand". Shown beside the working indicator; empty until it arrives.
        /// </summary>
        public string RouteLabel;
        public string RouteThinking;
        public string RouteReason;
        public string RouteSource;
        /// <summary>The thinking this turn's message asked for — "auto" or a level — to say who picked it.</summary>
        public string RouteThinkingAsked;

        /// <summary>The change set the agent's tools record into: one per message (see <see cref="AgentChanges"/>).</summary>
        public string ChangeTurn;

        /// <summary>
        /// The user pressed Stop: nothing resumes on its own — no tool results, compile reports or queued job
        /// events — until they send a new message. Survives domain reloads.
        /// </summary>
        public bool Halted;

        public void RememberImage(long id, string url)
        {
            var index = ImageIds.IndexOf(id);
            if (index >= 0) ImageUrls[index] = url;
            else
            {
                ImageIds.Add(id);
                ImageUrls.Add(url);
            }
        }

        public string ImageUrl(long id)
        {
            var index = ImageIds.IndexOf(id);
            return index >= 0 ? ImageUrls[index] : null;
        }

        public void Reset()
        {
            SessionId = 0;
            SessionTitle = null;
            Items.Clear();
            Phase = "idle";
            PendingCallsJson = "[]";
            PendingResultsJson = "[]";
            LastScriptResult = -1;
            QueuedEvents.Clear();
            ImageIds.Clear();
            ImageUrls.Clear();
            TokensThisSession = 0;
            Halted = false;
        }
    }
}
