using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Zetta3D.Editor
{
    /// <summary>
    /// An error the API answered with. Branch on <see cref="Code"/> (e.g. INSUFFICIENT_CREDITS,
    /// JOB_LIMIT_REACHED, PLAN_CAPABILITY_REQUIRED) — <see cref="Message"/> is for people.
    /// </summary>
    public class ZettaApiException : Exception
    {
        public readonly int Status;
        public readonly string Code;
        public readonly JObject Body;

        public ZettaApiException(int status, string message, string code, JObject body)
            : base(message)
        {
            Status = status;
            Code = code;
            Body = body;
        }

        public bool IsUnauthorized => Status == 401;
    }

    /// <summary>
    /// The one HTTP client for the plugin. Awaited from the main thread, continuations come back
    /// on the main thread (Unity's synchronization context), so callers can touch the Editor API.
    /// </summary>
    public static class ZettaHttp
    {
        static readonly HttpClient Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        public static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Include,
        };

        /// <summary>Raised (on the main thread) whenever the API says the token is no good.</summary>
        public static event Action Unauthorized;

        static string Url(string path) =>
            path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : ZettaConfig.ApiBase + path;

        static HttpRequestMessage Build(HttpMethod method, string path, object body, IDictionary<string, string> headers, bool auth)
        {
            var request = new HttpRequestMessage(method, Url(path));
            request.Headers.UserAgent.ParseAdd($"Zetta3D-Unity/{ZettaConfig.Version}");
            request.Headers.TryAddWithoutValidation("X-Zetta-Client", ZettaConfig.ClientName);
            if (auth && !string.IsNullOrEmpty(ZettaPrefs.Token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ZettaPrefs.Token);
            if (headers != null)
                foreach (var pair in headers)
                    request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            // A bodyless POST with a JSON content type is a 400 on Fastify — only send it with a body.
            if (body != null)
            {
                var json = body is JToken token ? token.ToString(Formatting.None) : JsonConvert.SerializeObject(body, Json);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
            return request;
        }

        public static async Task<JToken> SendAsync(
            HttpMethod method,
            string path,
            object body = null,
            IDictionary<string, string> headers = null,
            bool auth = true,
            int timeoutSeconds = 60,
            CancellationToken cancel = default)
        {
            using var request = Build(method, path, body, headers, auth);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            HttpResponseMessage response;
            try
            {
                response = await Client.SendAsync(request, timeout.Token);
            }
            catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new ZettaApiException(0, "Zetta 3D did not answer in time. Check your connection and try again.", "TIMEOUT", null);
            }
            catch (HttpRequestException e)
            {
                throw new ZettaApiException(0, $"Could not reach Zetta 3D ({e.Message}).", "UNREACHABLE", null);
            }

            using (response)
            {
                var text = response.Content == null ? "" : await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) throw Failure((int)response.StatusCode, text);
                if (string.IsNullOrWhiteSpace(text)) return JValue.CreateNull();
                try
                {
                    return JToken.Parse(text);
                }
                catch (JsonException)
                {
                    return new JValue(text);
                }
            }
        }

        public static Task<JToken> GetAsync(string path, CancellationToken cancel = default) =>
            SendAsync(HttpMethod.Get, path, cancel: cancel);

        public static Task<JToken> PostAsync(string path, object body = null, IDictionary<string, string> headers = null, int timeoutSeconds = 60, CancellationToken cancel = default) =>
            SendAsync(HttpMethod.Post, path, body, headers, timeoutSeconds: timeoutSeconds, cancel: cancel);

        public static Task<JToken> PatchAsync(string path, object body) =>
            SendAsync(new HttpMethod("PATCH"), path, body);

        public static Task<JToken> PutAsync(string path, object body) =>
            SendAsync(HttpMethod.Put, path, body);

        public static Task<JToken> DeleteAsync(string path) =>
            SendAsync(HttpMethod.Delete, path);

        /// <summary>Above this, an upload goes up in pieces: the edge in front of the API ends any request after 100s.</summary>
        public const int UploadPieceBytes = 8 * 1024 * 1024;

        static readonly int[] RetryDelaysMs = { 600, 1800 };

        /// <summary>
        /// Uploads bytes to the URL <c>POST /3d/uploads</c> (or result-upload) returned — the API's upload relay
        /// (<c>/api/3d/uploads/relay?ticket=…</c>). The ticket is the authorisation: no Bearer header is sent.
        /// Up to 8 MB is one PUT; anything larger uses the relay's multipart protocol (start → 8 MB parts →
        /// complete, aborted on failure). Network drops and 5xx are retried twice. A 410 (ticket expired)
        /// surfaces as code <c>UPLOAD_TICKET_EXPIRED</c> so the caller can ask for a new ticket.
        /// </summary>
        public static async Task PutBytesAsync(string url, byte[] bytes, IDictionary<string, string> headers,
            CancellationToken cancel = default, Action<float> progress = null)
        {
            var contentType = headers != null && headers.TryGetValue("Content-Type", out var ct) ? ct : "application/octet-stream";
            var ticket = QueryValue(url, "ticket");
            if (bytes.Length <= UploadPieceBytes || ticket == null)
            {
                await WithRetry(() => Upload(HttpMethod.Put, url, bytes, 0, bytes.Length, contentType, headers, cancel), cancel);
                progress?.Invoke(1f);
                return;
            }

            var baseUrl = url.Substring(0, url.IndexOf('?'));
            var t = Uri.EscapeDataString(ticket);
            var start = await WithRetry(() => Send(HttpMethod.Post, $"{baseUrl}/multipart?ticket={t}", cancel), cancel);
            var uploadId = Uri.EscapeDataString(JObject.Parse(start)["uploadId"]?.ToString() ?? "");
            try
            {
                var parts = (bytes.Length + UploadPieceBytes - 1) / UploadPieceBytes;
                for (var part = 1; part <= parts; part++)
                {
                    var offset = (part - 1) * UploadPieceBytes;
                    var length = Math.Min(UploadPieceBytes, bytes.Length - offset);
                    var partUrl = $"{baseUrl}/multipart/part?ticket={t}&uploadId={uploadId}&part={part}";
                    await WithRetry(() => Upload(HttpMethod.Put, partUrl, bytes, offset, length, contentType, null, cancel), cancel);
                    progress?.Invoke((float)part / parts);
                }
                await WithRetry(() => Send(HttpMethod.Post,
                    $"{baseUrl}/multipart/complete?ticket={t}&uploadId={uploadId}&parts={parts}&bytes={bytes.Length}", cancel), cancel);
            }
            catch
            {
                try
                {
                    await Send(HttpMethod.Delete, $"{baseUrl}/multipart?ticket={t}&uploadId={uploadId}", CancellationToken.None);
                }
                catch (Exception)
                {
                    // Best effort: an unfinished upload expires with its ticket anyway.
                }
                throw;
            }
        }

        static async Task<string> Upload(HttpMethod method, string url, byte[] bytes, int offset, int length, string contentType,
            IDictionary<string, string> headers, CancellationToken cancel)
        {
            using var request = new HttpRequestMessage(method, url) { Content = new ByteArrayContent(bytes, offset, length) };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            if (headers != null)
                foreach (var pair in headers)
                    if (!pair.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) &&
                        !request.Content.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
                        request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            return await Execute(request, cancel);
        }

        static async Task<string> Send(HttpMethod method, string url, CancellationToken cancel)
        {
            using var request = new HttpRequestMessage(method, url);
            return await Execute(request, cancel);
        }

        static async Task<string> Execute(HttpRequestMessage request, CancellationToken cancel)
        {
            HttpResponseMessage response;
            try
            {
                response = await Client.SendAsync(request, cancel);
            }
            catch (HttpRequestException e)
            {
                throw new ZettaApiException(0, $"The upload could not reach Zetta 3D ({e.Message}).", "UPLOAD_FAILED", null);
            }
            using (response)
            {
                var text = response.Content == null ? "" : await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode) return text;
                var status = (int)response.StatusCode;
                string message = null;
                try
                {
                    message = JObject.Parse(text)["message"]?.ToString();
                }
                catch (JsonException)
                {
                }
                var code = status switch
                {
                    410 => "UPLOAD_TICKET_EXPIRED",
                    413 => "UPLOAD_TOO_LARGE",
                    415 => "UPLOAD_WRONG_TYPE",
                    _ => "UPLOAD_FAILED",
                };
                if (status == 413) message ??= "That file is too large to upload (pictures up to 32 MB, models up to 512 MB).";
                throw new ZettaApiException(status, message ?? $"Upload failed ({status}).", code, null);
            }
        }

        static async Task<T> WithRetry<T>(Func<Task<T>> attempt, CancellationToken cancel)
        {
            for (var i = 0; ; i++)
            {
                try
                {
                    return await attempt();
                }
                catch (ZettaApiException e) when ((e.Status == 0 || e.Status >= 500) && i < RetryDelaysMs.Length)
                {
                    await Task.Delay(RetryDelaysMs[i], cancel);
                }
            }
        }

        static string QueryValue(string url, string name)
        {
            var q = url.IndexOf('?');
            if (q < 0) return null;
            foreach (var pair in url.Substring(q + 1).Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0 && pair.Substring(0, eq) == name) return Uri.UnescapeDataString(pair.Substring(eq + 1));
            }
            return null;
        }

        /// <summary>GET a file — signed storage URLs carry their own auth, so none is sent.</summary>
        public static async Task<byte[]> DownloadAsync(string url, CancellationToken cancel = default, Action<float> progress = null)
        {
            // Model files come through the API's download relay (/api/files), which honours byte ranges. Fetching
            // in 8 MB pieces keeps every request well under the edge's 100-second limit on a slow connection,
            // lets a dropped piece be retried on its own, and gives a real progress figure.
            var first = await WithRetry(() => DownloadRange(url, 0, UploadPieceBytes - 1, cancel), cancel);
            if (!first.partial)
            {
                // The server sent the whole file in one response (no range support): that was the download.
                progress?.Invoke(1f);
                return first.bytes;
            }

            var total = first.total;
            if (total <= first.bytes.Length)
            {
                progress?.Invoke(1f);
                return first.bytes;
            }

            var buffer = new byte[total];
            Buffer.BlockCopy(first.bytes, 0, buffer, 0, first.bytes.Length);
            long offset = first.bytes.Length;
            progress?.Invoke((float)offset / total);
            while (offset < total)
            {
                var end = Math.Min(offset + UploadPieceBytes, total) - 1;
                var start = offset;
                var piece = await WithRetry(() => DownloadRange(url, start, end, cancel), cancel);
                if (piece.bytes.Length == 0)
                    throw new ZettaApiException(0, "The download stopped early. Try again.", "DOWNLOAD_FAILED", null);
                Buffer.BlockCopy(piece.bytes, 0, buffer, (int)offset, piece.bytes.Length);
                offset += piece.bytes.Length;
                progress?.Invoke((float)offset / total);
            }
            return buffer;
        }

        static async Task<(byte[] bytes, bool partial, long total)> DownloadRange(string url, long start, long end, CancellationToken cancel)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(start, end);
            HttpResponseMessage response;
            try
            {
                response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            }
            catch (HttpRequestException e)
            {
                throw new ZettaApiException(0, $"The download could not reach Zetta 3D ({e.Message}).", "DOWNLOAD_FAILED", null);
            }
            using (response)
            {
                var status = (int)response.StatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    var message = status == 410
                        ? "The download link expired. Try the import again."
                        : $"Download failed ({status}).";
                    throw new ZettaApiException(status, message, status == 410 ? "DOWNLOAD_EXPIRED" : "DOWNLOAD_FAILED", null);
                }
                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (status != 206) return (bytes, false, bytes.Length);
                var total = response.Content.Headers.ContentRange?.Length ?? (start + bytes.Length);
                return (bytes, true, total);
            }
        }

        /// <summary>
        /// POST and read a server-sent-event stream (`data: {json}\n\n` frames). Events are delivered
        /// on the caller's context as they arrive. Not EventSource: the API streams the body of a POST.
        /// </summary>
        public static async Task StreamAsync(string path, object body, Action<JObject> onEvent, CancellationToken cancel)
        {
            using var request = Build(HttpMethod.Post, path, body, null, true);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            HttpResponseMessage response;
            try
            {
                response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            }
            catch (HttpRequestException e)
            {
                throw new ZettaApiException(0, $"The agent could not be reached ({e.Message}).", "UNREACHABLE", null);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw Failure((int)response.StatusCode, await response.Content.ReadAsStringAsync());

                using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var data = new StringBuilder();
                while (!cancel.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break;
                    if (line.Length == 0)
                    {
                        if (data.Length > 0)
                        {
                            Dispatch(data.ToString(), onEvent);
                            data.Clear();
                        }
                        continue;
                    }
                    if (line.StartsWith("data:"))
                    {
                        if (data.Length > 0) data.Append('\n');
                        data.Append(line.Substring(5).TrimStart());
                    }
                }
                if (data.Length > 0) Dispatch(data.ToString(), onEvent);
            }
        }

        static void Dispatch(string json, Action<JObject> onEvent)
        {
            JObject evt;
            try
            {
                evt = JObject.Parse(json);
            }
            catch (JsonException)
            {
                return;
            }
            onEvent(evt);
        }

        static ZettaApiException Failure(int status, string text)
        {
            JObject body = null;
            try
            {
                body = JObject.Parse(text);
            }
            catch (JsonException)
            {
            }

            var message = body?["message"] switch
            {
                JArray list => string.Join(" ", list),
                JValue value => value.ToString(),
                _ => null,
            };
            var code = body?["code"]?.ToString();
            if (string.IsNullOrEmpty(message))
                message = status switch
                {
                    401 => "Sign in to Zetta 3D to carry on.",
                    403 => "Your plan does not include this.",
                    404 => "Not found.",
                    413 => "That is too large to send.",
                    429 => "Too many requests — wait a moment and try again.",
                    >= 500 => "Zetta 3D had a problem. Try again in a moment.",
                    _ => $"Request failed ({status}).",
                };
            if (status == 401) Unauthorized?.Invoke();
            return new ZettaApiException(status, message, code, body);
        }
    }
}
