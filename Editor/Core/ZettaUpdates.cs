using System;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Zetta3D.Editor
{
    /// <summary>What the newest release is, and whether this copy may keep running (a "required" update).</summary>
    public sealed class ZettaRelease
    {
        public string Latest;
        public string Minimum;
        public string Message;
        public string NotesUrl;

        /// <summary>A newer release than the one installed is out.</summary>
        public bool Available => ZettaUpdates.Compare(Latest, ZettaConfig.Version) > 0;

        /// <summary>The installed version is older than the oldest that still works — and there is one to update to.</summary>
        public bool Required => Available && ZettaUpdates.Compare(ZettaConfig.Version, Minimum) < 0;

        /// <summary>Reads the server's answer, or the repository's files put together the same way. Null without a latest.</summary>
        public static ZettaRelease From(JObject json)
        {
            string Text(string key) => json?[key]?.Type == JTokenType.String && json[key].ToString().Trim() != "" ? json[key].ToString().Trim() : null;
            var latest = ZettaUpdates.Normalize(Text("latest"));
            if (latest == null) return null;
            return new ZettaRelease
            {
                Latest = latest,
                Minimum = ZettaUpdates.Normalize(Text("minimum")),
                Message = Text("message"),
                NotesUrl = Text("notesUrl") ?? ZettaUpdates.ReleasesUrl + "/tag/v" + latest,
            };
        }

        public JObject ToJson() => new JObject { ["latest"] = Latest, ["minimum"] = Minimum, ["message"] = Message, ["notesUrl"] = NotesUrl };
    }

    /// <summary>
    /// Tells people when a newer Zetta 3D is out, and installs it. Checked every time Unity starts, and again when the
    /// Zetta 3D window is focused six hours later — from <c>GET /unity-plugin/version</c>, or straight from the repository
    /// when the server can't be reached. What it found is kept, so the banner shows at once, offline too.
    ///
    /// Releasing needs nothing here: the latest is main's package.json. To make an update required, set
    /// <c>minimum</c> (and a <c>message</c>) in Release~/update.json in the release commit — see Release~/README.md.
    /// </summary>
    [InitializeOnLoad]
    public static class ZettaUpdates
    {
        public const string RepositoryUrl = "https://github.com/ig-shadow-walker/zetta-Unity3DPlugin";
        public const string ReleasesUrl = RepositoryUrl + "/releases";
        const string RawUrl = "https://raw.githubusercontent.com/ig-shadow-walker/zetta-Unity3DPlugin/main";
        const string HowToUpdateUrl = RepositoryUrl + "#install";

        const string KnownKey = "Zetta3D.Updates.Known";
        const string CheckedKey = "Zetta3D.Updates.CheckedAt";
        const string InstallingKey = "Zetta3D.Updates.Installing";
        /// <summary>Set once Unity has started and checked: a script reload re-runs the static constructor, a restart doesn't keep it.</summary>
        const string StartedKey = "Zetta3D.Updates.CheckedThisSession";

        static readonly TimeSpan Every = TimeSpan.FromHours(6);
        static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

        static Task<ZettaRelease> _pending;
        static ZettaRelease _known;
        static bool _read;

        /// <summary>Raised (on the main thread) when what is known, or the state of an update, changes.</summary>
        public static event Action Changed;

        /// <summary>Installing the newest release through the Package Manager.</summary>
        public static bool Installing { get; private set; }

        /// <summary>Why the last update didn't happen, for people — null when it did, or none was tried.</summary>
        public static string Problem { get; private set; }

        static ZettaUpdates()
        {
            if (Application.isBatchMode) return;
            EditorApplication.delayCall += () =>
            {
                var installed = SessionState.GetString(InstallingKey, "");
                if (installed != "")
                {
                    SessionState.EraseString(InstallingKey);
                    if (installed == ZettaConfig.Version) Debug.Log($"Zetta 3D updated to {installed}.");
                }
                // Every time Unity starts, so a release shows the next time it is opened; after that, every six hours.
                if (!SessionState.GetBool(StartedKey, false))
                {
                    SessionState.SetBool(StartedKey, true);
                    _ = CheckAsync();
                }
                else CheckIfDue();
            };
        }

        /// <summary>The newest release last heard of, or null before the first check.</summary>
        public static ZettaRelease Known
        {
            get
            {
                if (_read) return _known;
                _read = true;
                try
                {
                    var saved = EditorPrefs.GetString(KnownKey, "");
                    _known = saved == "" ? null : ZettaRelease.From(JObject.Parse(saved));
                }
                catch (Exception)
                {
                    _known = null;
                }
                return _known;
            }
        }

        /// <summary>Whether the window shows the update banner: whenever a newer version is out, until it is installed. It can't be closed.</summary>
        public static bool ShowBanner => Known is { Available: true };

        /// <summary>Checks when the last check is six hours old (an hour, when it failed).</summary>
        public static void CheckIfDue()
        {
            if (DateTime.UtcNow < LastChecked() + Every) return;
            _ = CheckAsync();
        }

        /// <summary>Asks for the newest release now. Never throws: null when neither the server nor the repository answered.</summary>
        public static Task<ZettaRelease> CheckAsync()
        {
            return _pending ??= Run();

            async Task<ZettaRelease> Run()
            {
                try
                {
                    var release = await FetchAsync();
                    if (release == null)
                    {
                        Checked(DateTime.UtcNow - Every + RetryAfterFailure);
                        return null;
                    }
                    Remember(release);
                    Checked(DateTime.UtcNow);
                    return release;
                }
                finally
                {
                    _pending = null;
                }
            }
        }

        /// <summary>Keeps what a check found and tells the window.</summary>
        public static void Remember(ZettaRelease release)
        {
            if (release == null) return;
            _known = release;
            _read = true;
            EditorPrefs.SetString(KnownKey, release.ToJson().ToString(Newtonsoft.Json.Formatting.None));
            Changed?.Invoke();
        }

        static DateTime LastChecked() =>
            long.TryParse(EditorPrefs.GetString(CheckedKey, ""), out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : DateTime.MinValue;

        static void Checked(DateTime at) => EditorPrefs.SetString(CheckedKey, new DateTimeOffset(at).ToUnixTimeSeconds().ToString());

        static async Task<ZettaRelease> FetchAsync()
        {
            try
            {
                var answer = await ZettaHttp.SendAsync(HttpMethod.Get, "/unity-plugin/version?current=" + Uri.EscapeDataString(ZettaConfig.Version),
                    auth: false, timeoutSeconds: 10) as JObject;
                if (ZettaRelease.From(answer) is { } release) return release;
            }
            catch (ZettaApiException)
            {
            }

            // Straight from the repository: the server is down, or a developer's own server doesn't serve this.
            try
            {
                var manifest = await ZettaHttp.SendAsync(HttpMethod.Get, RawUrl + "/package.json", auth: false, timeoutSeconds: 10) as JObject;
                JObject update = null;
                try
                {
                    update = await ZettaHttp.SendAsync(HttpMethod.Get, RawUrl + "/Release~/update.json", auth: false, timeoutSeconds: 10) as JObject;
                }
                catch (ZettaApiException)
                {
                    // No release file: nothing is required.
                }
                return ZettaRelease.From(new JObject
                {
                    ["latest"] = manifest?["version"],
                    ["minimum"] = update?["minimum"],
                    ["message"] = update?["message"],
                });
            }
            catch (ZettaApiException)
            {
                return null;
            }
        }

        // ─── Installing ────────────────────────────────────────────────────────────

        /// <summary>
        /// Updates through the Package Manager. A copy that follows the Git URL is added again from it (main is always
        /// the latest release); one pinned to a release tag has its tag moved to the newest. Unity reloads when done.
        /// </summary>
        public static void Install()
        {
            var known = Known;
            if (known == null || !known.Available || Installing) return;
            var package = PackageInfo.FindForAssembly(typeof(ZettaUpdates).Assembly);
            var target = Target(package, known.Latest);
            if (target == null)
            {
                Fail(package?.source is PackageSource.Embedded or PackageSource.Local or PackageSource.LocalTarball
                    ? "This copy of Zetta 3D is a folder on your computer, not installed from Git — update that folder (git pull), or install from the Git URL."
                    : "This copy of Zetta 3D can't be updated from here.");
                return;
            }

            Installing = true;
            Problem = null;
            SessionState.SetString(InstallingKey, known.Latest);
            Changed?.Invoke();
            var request = Client.Add(target);
            EditorApplication.update += Wait;

            void Wait()
            {
                if (!request.IsCompleted) return;
                EditorApplication.update -= Wait;
                // Unity reloads the scripts now, with the new version: "Updating…" stays until it does.
                if (request.Status == StatusCode.Success) return;
                Installing = false;
                SessionState.EraseString(InstallingKey);
                Fail($"The update didn't finish: {request.Error?.message ?? "the Package Manager gave no reason"}");
            }
        }

        static void Fail(string problem)
        {
            Problem = problem;
            Changed?.Invoke();
        }

        /// <summary>What to hand <see cref="Client.Add"/> to reach <paramref name="latest"/>, or null when it can't.</summary>
        public static string Target(PackageInfo package, string latest)
        {
            if (package == null || latest == null) return null;
            switch (package.source)
            {
                case PackageSource.Git:
                {
                    var at = package.packageId.IndexOf('@');
                    if (at < 0) return null;
                    return MoveTag(package.packageId.Substring(at + 1), latest);
                }
                case PackageSource.Registry:
                    return package.name + "@" + latest;
                default:
                    return null;
            }
        }

        /// <summary>A Git URL that reaches <paramref name="latest"/>: a release tag moved to it, anything else kept (main, a branch).</summary>
        public static string MoveTag(string url, string latest)
        {
            var hash = url.IndexOf('#');
            if (hash < 0 || Normalize(url.Substring(hash + 1)) == null) return url;
            return url.Substring(0, hash) + "#v" + latest;
        }

        /// <summary>Opens how to update by hand.</summary>
        public static void OpenHowTo() => Application.OpenURL(HowToUpdateUrl);

        // ─── Versions ──────────────────────────────────────────────────────────────

        /// <summary>"0.7.1" from "0.7.1" or "v0.7.1"; null for anything that isn't major.minor.patch.</summary>
        public static string Normalize(string value)
        {
            value = value?.Trim();
            if (string.IsNullOrEmpty(value)) return null;
            if (value[0] == 'v' || value[0] == 'V') value = value.Substring(1);
            var parts = value.Split('.');
            if (parts.Length != 3) return null;
            foreach (var part in parts)
                if (part.Length == 0 || part.Length > 4 || !int.TryParse(part, System.Globalization.NumberStyles.None, null, out _)) return null;
            return value;
        }

        /// <summary>Orders two versions numerically (0.10.0 is after 0.9.9); one that isn't a version is the lowest.</summary>
        public static int Compare(string a, string b)
        {
            a = Normalize(a);
            b = Normalize(b);
            if (a == null || b == null) return a == null ? (b == null ? 0 : -1) : 1;
            var x = Array.ConvertAll(a.Split('.'), int.Parse);
            var y = Array.ConvertAll(b.Split('.'), int.Parse);
            for (var i = 0; i < 3; i++)
                if (x[i] != y[i]) return x[i].CompareTo(y[i]);
            return 0;
        }

        // ─── Menu ──────────────────────────────────────────────────────────────────

        [MenuItem("Window/Zetta 3D/Check for Updates…", priority = 40)]
        static async void MenuCheck()
        {
            var release = await CheckAsync();
            if (release == null)
            {
                if (EditorUtility.DisplayDialog("Zetta 3D", "Couldn't check for updates — Zetta 3D and GitHub didn't answer. Check your connection and try again.",
                        "Open releases", "OK"))
                    Application.OpenURL(ReleasesUrl);
                return;
            }
            if (!release.Available)
            {
                EditorUtility.DisplayDialog("Zetta 3D", $"You have the latest version, {ZettaConfig.Version}.", "OK");
                return;
            }
            var text = release.Required
                ? $"Zetta 3D {release.Latest} is out, and this version ({ZettaConfig.Version}) no longer works with Zetta 3D — update to keep using it."
                : $"Zetta 3D {release.Latest} is out — you have {ZettaConfig.Version}.";
            if (release.Message != null) text += "\n\n" + release.Message;
            text += "\n\nUnity reloads its scripts when the update is installed.";
            switch (EditorUtility.DisplayDialogComplex("Zetta 3D update", text, "Update", "Later", "What's new"))
            {
                case 0:
                    Install();
                    ZettaWindow.Open();
                    break;
                case 2:
                    Application.OpenURL(release.NotesUrl);
                    break;
            }
        }
    }
}
