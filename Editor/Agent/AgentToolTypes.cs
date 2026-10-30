using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Zetta3D.Editor.Agent
{
    /// <summary>A picture returned with a tool result (capture_view): raw base64, no data: prefix.</summary>
    public class ToolImage
    {
        [JsonProperty("data")] public string Data;
        [JsonProperty("mediaType")] public string MediaType = "image/jpeg";

        public static ToolImage Jpeg(byte[] bytes) => new ToolImage { Data = Convert.ToBase64String(bytes), MediaType = "image/jpeg" };
    }

    /// <summary>What a client tool hands back to the model. See the Unity agent protocol.</summary>
    public class ToolResult
    {
        public const int MaxContent = 100_000;

        [JsonProperty("toolUseId")] public string ToolUseId;
        [JsonProperty("content")] public string Content = "";
        [JsonProperty("isError")] public bool IsError;
        [JsonProperty("images", NullValueHandling = NullValueHandling.Ignore)] public List<ToolImage> Images;

        /// <summary>Not sent: set when the call wrote or edited a script, so the runtime waits for compilation.</summary>
        [JsonIgnore] public bool WroteScripts;

        /// <summary>Not sent: a picture the call made (a 2D library id and its link), drawn in the chat like a generate_image result.</summary>
        [JsonIgnore] public long ShowImageId;
        [JsonIgnore] public string ShowImageUrl;

        public static ToolResult Ok(string content) => new ToolResult { Content = Truncate(content) };
        public static ToolResult Error(string message) => new ToolResult { Content = Truncate(message), IsError = true };

        public static string Truncate(string text)
        {
            text ??= "";
            return text.Length <= MaxContent ? text : text.Substring(0, MaxContent - 80) + $"\n… [truncated: {text.Length - MaxContent + 80} more characters]";
        }
    }

    /// <summary>A client tool call streamed by the server (`client_tool` event).</summary>
    public class ClientToolCall
    {
        [JsonProperty("toolUseId")] public string ToolUseId;
        [JsonProperty("name")] public string Name;
        [JsonProperty("input")] public JObject Input = new JObject();
        /// <summary>read | write | destructive | generate</summary>
        [JsonProperty("effect")] public string Effect = "read";
        [JsonProperty("review")] public ToolReview Review;
    }

    public class ToolReview
    {
        [JsonProperty("confirm")] public bool Confirm;
        [JsonProperty("reasons")] public List<string> Reasons = new List<string>();
    }

    /// <summary>Something the user must approve before a tool runs (writes, deletes, credit spends).</summary>
    public class ApprovalRequest
    {
        public string Title;
        public string Detail;
        /// <summary>write | destructive | generate</summary>
        public string Kind;
        /// <summary>Credits the action spends, when it spends any.</summary>
        public int? Credits;
        public string ConfirmLabel = "Allow";
        public List<string> Reasons = new List<string>();
    }

    /// <summary>
    /// What a tool may ask of the agent window while it runs. The window supplies these; tools must
    /// not reach into UI themselves.
    /// </summary>
    public class ToolRunContext
    {
        /// <summary>The chat session the call belongs to (for job tracking).</summary>
        public long SessionId;

        /// <summary>Shows an approval card in the chat and resolves with the user's choice.</summary>
        public Func<ApprovalRequest, Task<bool>> Confirm = _ => Task.FromResult(false);

        /// <summary>Progress line under the running step ("Downloading…").</summary>
        public Action<string> Status = _ => { };

        /// <summary>Images the server made this session (imageId → signed URL), from `tool_result` events.</summary>
        public Func<long, string> ImageUrl = _ => null;

        /// <summary>The call being run (a card shown for it is found again by this id after a reload).</summary>
        public string ToolUseId;

        /// <summary>Shows an ask_user card and resolves with the answers, or null when the user skipped it.</summary>
        public Func<AskRequest, Task<List<AskAnswer>>> AskUser;
    }
}
