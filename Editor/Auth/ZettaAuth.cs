using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Zetta3D.Editor
{
    /// <summary>
    /// Sign-in through the browser, the same handoff the Blender add-on uses:
    /// the plugin listens on 127.0.0.1, opens zetta3d.ai/plugin-auth, the user presses Connect,
    /// and the page POSTs {token, state} to http://127.0.0.1:port/callback. The token is a
    /// plugin session (Bearer) — the plugin never sees a password.
    /// </summary>
    [InitializeOnLoad]
    public static class ZettaAuth
    {
        public enum State
        {
            SignedOut,
            WaitingForBrowser,
            Checking,
            SignedIn,
        }

        public static State Current { get; private set; }
        public static ZettaUser User { get; private set; }
        public static string LastError { get; private set; }

        /// <summary>Raised on the main thread whenever <see cref="Current"/> or <see cref="User"/> changes.</summary>
        public static event Action Changed;

        public static bool IsSignedIn => Current == State.SignedIn && User != null;

        static HttpListener _listener;
        static string _state;
        static CancellationTokenSource _loginCancel;

        static ZettaAuth()
        {
            ZettaHttp.Unauthorized += OnUnauthorized;
            Current = string.IsNullOrEmpty(ZettaPrefs.Token) ? State.SignedOut : State.Checking;
            if (Current == State.Checking) EditorApplication.delayCall += () => _ = RefreshAsync();
        }

        /// <summary>Re-reads the account (plan, credits). Safe to call often.</summary>
        public static async Task RefreshAsync()
        {
            if (string.IsNullOrEmpty(ZettaPrefs.Token))
            {
                Set(State.SignedOut, null);
                return;
            }
            if (TokenExpired())
            {
                SignOut("Your Zetta 3D sign-in has expired. Sign in again.");
                return;
            }
            try
            {
                var user = await ZettaApi.MeAsync();
                Set(State.SignedIn, user);
            }
            catch (ZettaApiException e) when (e.IsUnauthorized)
            {
                SignOut("Your Zetta 3D sign-in has expired. Sign in again.");
            }
            catch (ZettaApiException e)
            {
                // Offline or the API is down: keep the token, show the error, stay usable later.
                LastError = e.Message;
                Set(User != null ? State.SignedIn : State.Checking, User);
            }
        }

        public static void SignOut(string reason = null)
        {
            CancelLogin();
            ZettaPrefs.Token = null;
            ZettaPrefs.TokenExpiry = 0;
            LastError = reason;
            Set(State.SignedOut, null);
        }

        static void OnUnauthorized()
        {
            if (Current == State.SignedIn || Current == State.Checking)
                SignOut("Your Zetta 3D sign-in has expired. Sign in again.");
        }

        static void Set(State state, ZettaUser user)
        {
            Current = state;
            User = user;
            if (state == State.SignedIn) LastError = null;
            // One listener at a time: an exception in one (a page failing to build) would otherwise stop the rest
            // from hearing the sign-in, and vanish inside the fire-and-forget refresh that called this.
            foreach (var listener in Changed?.GetInvocationList() ?? Array.Empty<Delegate>())
            {
                try
                {
                    ((Action)listener)();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        // ─── Browser handoff ────────────────────────────────────────────────────────

        public static void BeginLogin()
        {
            CancelLogin();
            LastError = null;
            try
            {
                var port = FreePort();
                _state = RandomState();
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                _loginCancel = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var listener = _listener;
                var cancel = _loginCancel.Token;
                _ = Task.Run(() => Listen(listener, cancel));

                var url = $"{ZettaConfig.SiteBase}/plugin-auth?port={port}&state={_state}&client={ZettaConfig.ClientName}";
                Application.OpenURL(url);
                Set(State.WaitingForBrowser, null);
            }
            catch (Exception e)
            {
                CancelLogin();
                LastError = $"Could not start the sign-in listener: {e.Message}";
                Set(State.SignedOut, null);
            }
        }

        public static void CancelLogin()
        {
            try
            {
                _loginCancel?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            _loginCancel = null;
            try
            {
                _listener?.Stop();
                _listener?.Close();
            }
            catch (Exception)
            {
                // Already closed.
            }
            _listener = null;
            if (Current == State.WaitingForBrowser) Set(State.SignedOut, null);
        }

        static async Task Listen(HttpListener listener, CancellationToken cancel)
        {
            using var _ = cancel.Register(() =>
            {
                try
                {
                    listener.Stop();
                }
                catch (Exception)
                {
                }
            });

            while (!cancel.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception)
                {
                    break;
                }

                var done = Handle(context);
                if (done) break;
            }

            MainThread.Post(() =>
            {
                if (Current == State.WaitingForBrowser && cancel.IsCancellationRequested)
                {
                    LastError = "Sign-in timed out. Try again.";
                    CancelLogin();
                }
            });
        }

        /// <summary>Returns true once a valid token has been received.</summary>
        static bool Handle(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            var origin = request.Headers["Origin"];
            // The page is the only caller; echo its origin rather than '*' so nothing else can read the reply.
            response.AddHeader("Access-Control-Allow-Origin", string.IsNullOrEmpty(origin) ? ZettaConfig.SiteBase : origin);
            response.AddHeader("Vary", "Origin");

            try
            {
                if (request.HttpMethod == "OPTIONS")
                {
                    response.AddHeader("Access-Control-Allow-Methods", "POST, OPTIONS");
                    response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
                    // Chrome's Private Network Access: a public page calling 127.0.0.1 is preflighted.
                    response.AddHeader("Access-Control-Allow-Private-Network", "true");
                    response.StatusCode = 204;
                    return false;
                }

                if (request.HttpMethod != "POST" || request.Url.AbsolutePath.TrimEnd('/') != "/callback")
                {
                    response.StatusCode = 404;
                    return false;
                }

                string text;
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
                    text = reader.ReadToEnd();

                var body = JObject.Parse(text);
                var token = body["token"]?.ToString();
                var state = body["state"]?.ToString();
                if (string.IsNullOrEmpty(token) || state != _state)
                {
                    response.StatusCode = 400;
                    Write(response, "{\"ok\":false,\"message\":\"State mismatch.\"}");
                    return false;
                }

                response.StatusCode = 200;
                Write(response, "{\"ok\":true}");
                MainThread.Post(() => Complete(token));
                return true;
            }
            catch (Exception)
            {
                response.StatusCode = 400;
                return false;
            }
            finally
            {
                try
                {
                    response.Close();
                }
                catch (Exception)
                {
                }
            }
        }

        static void Write(HttpListenerResponse response, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        static void Complete(string token)
        {
            ZettaPrefs.Token = token;
            ZettaPrefs.TokenExpiry = ReadExpiry(token);
            _listener?.Close();
            _listener = null;
            _loginCancel = null;
            Set(State.Checking, null);
            _ = RefreshAsync();
        }

        // ─── Helpers ────────────────────────────────────────────────────────────────

        static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        static string RandomState()
        {
            var bytes = new byte[24];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        static bool TokenExpired()
        {
            var expiry = ZettaPrefs.TokenExpiry;
            return expiry > 0 && DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiry;
        }

        /// <summary>Reads the JWT's `exp` claim (no verification — the server does that).</summary>
        static long ReadExpiry(string jwt)
        {
            try
            {
                var parts = jwt.Split('.');
                if (parts.Length < 2) return 0;
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                var json = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                return json["exp"]?.Value<long>() ?? 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
