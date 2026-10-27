using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Zetta3D.Editor.Agent
{
    /// <summary>A provider the agent can run on with the user's own key, and its models.</summary>
    public class AgentProviderInfo
    {
        [JsonProperty("key")] public string Key;
        [JsonProperty("label")] public string Label;
        [JsonProperty("keyUrl")] public string KeyUrl;
        [JsonProperty("models")] public List<AgentModelInfo> Models = new List<AgentModelInfo>();
    }

    public class AgentModelInfo
    {
        [JsonProperty("key")] public string Key;
        [JsonProperty("label")] public string Label;
        [JsonProperty("blurb")] public string Blurb;
        [JsonProperty("vision")] public bool Vision = true;
        [JsonProperty("documents")] public bool Documents = true;
        /// <summary>The thinking levels this model has, in order ("off", "low", …). Auto is always offered too.</summary>
        [JsonProperty("thinking")] public List<string> Thinking = new List<string>();
    }

    /// <summary>
    /// What the agent can run on, from the server's options — the providers and models for the user's own
    /// keys. Nothing here is hardcoded: the server's list is the truth, so a new or retired model needs no
    /// plugin update. Kept across domain reloads in SessionState.
    /// </summary>
    public static class AgentCatalog
    {
        const string CacheKey = "Zetta3D.Agent.Catalog";
        static List<AgentProviderInfo> _providers;
        static bool _loading;

        public static event Action Changed;

        /// <summary>
        /// The own-key providers the server offers, or null when it offers none (the feature is off there,
        /// or the options have not loaded yet) — the Editor then runs on the Zetta account only.
        /// </summary>
        public static List<AgentProviderInfo> Providers
        {
            get
            {
                if (_providers == null)
                {
                    var cached = SessionState.GetString(CacheKey, "");
                    if (cached.Length > 0)
                    {
                        try
                        {
                            _providers = JsonConvert.DeserializeObject<List<AgentProviderInfo>>(cached);
                        }
                        catch (JsonException)
                        {
                            _providers = null;
                        }
                    }
                }
                return _providers;
            }
        }

        public static async Task RefreshAsync()
        {
            if (_loading || !ZettaAuth.IsSignedIn) return;
            _loading = true;
            try
            {
                var options = await ZettaHttp.GetAsync("/unityAgent/options") as JObject;
                var providers = options?["providers"] is JArray list ? list.ToObject<List<AgentProviderInfo>>() : null;
                _providers = providers;
                SessionState.SetString(CacheKey, providers == null ? "" : JsonConvert.SerializeObject(providers));
                Changed?.Invoke();
            }
            catch (Exception)
            {
                // Offline or an older server: keep what we had; the Zetta account still works.
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>Tells every open menu that the account, provider or model changed (from Preferences, say).</summary>
        public static void NotifyChanged() => Changed?.Invoke();

        public static AgentProviderInfo Provider(string key) => Providers?.FirstOrDefault(p => p.Key == key);

        /// <summary>The user chose their own keys, and this server offers them.</summary>
        public static bool OwnKeys => ZettaPrefs.AgentAccount == "own" && Providers != null;

        /// <summary>Providers with a saved key, in the server's order — what the chat box's provider menu lists.</summary>
        public static List<AgentProviderInfo> KeyedProviders =>
            Providers?.Where(p => ZettaKeychain.Has(p.Key)).ToList() ?? new List<AgentProviderInfo>();

        /// <summary>The provider a message goes to: the one picked, if it still has a key; otherwise the first that does.</summary>
        public static AgentProviderInfo ActiveProvider
        {
            get
            {
                if (!OwnKeys) return null;
                var keyed = KeyedProviders;
                return keyed.FirstOrDefault(p => p.Key == ZettaPrefs.AgentProvider) ?? keyed.FirstOrDefault();
            }
        }

        /// <summary>The model a message goes to on the active provider: the one last used with it, or its first.</summary>
        public static AgentModelInfo ActiveModel
        {
            get
            {
                var provider = ActiveProvider;
                if (provider == null || provider.Models.Count == 0) return null;
                var last = ZettaPrefs.AgentModelFor(provider.Key);
                return provider.Models.FirstOrDefault(m => m.Key == last) ?? provider.Models[0];
            }
        }

        static readonly List<string> Order = new List<string> { "off", "low", "medium", "high", "xhigh", "max" };

        /// <summary>
        /// The thinking a message asks for on a model: Auto, or the chosen level — or, when the model lacks it,
        /// the nearest it has (deeper on a tie), the same rule the server runs by.
        /// </summary>
        public static string ThinkingFor(AgentModelInfo model, string wanted)
        {
            if (wanted == "auto" || model == null || model.Thinking.Count == 0 || model.Thinking.Contains(wanted)) return wanted;
            var at = Order.IndexOf(wanted);
            return model.Thinking
                .OrderBy(level => Math.Abs(Order.IndexOf(level) - at))
                .ThenByDescending(level => Order.IndexOf(level))
                .First();
        }
    }
}
