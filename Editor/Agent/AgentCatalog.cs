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
    ///
    /// EVERY MODEL THE USER'S KEY CAN REACH: for each provider with a saved key, the provider itself is asked
    /// (through the server, with that key) which models it offers, and that list replaces the server's
    /// starting one. Until it arrives — or if it can't be fetched — the server's list stands.
    /// </summary>
    [InitializeOnLoad]
    public static class AgentCatalog
    {
        const string CacheKey = "Zetta3D.Agent.Catalog";
        const string LiveKey = "Zetta3D.Agent.LiveModels";
        static List<AgentProviderInfo> _providers;
        static Dictionary<string, List<AgentModelInfo>> _live;
        static readonly HashSet<string> Fetching = new HashSet<string>();
        static bool _loading;

        static AgentCatalog()
        {
            // A removed key takes its provider's list with it; a new one is fetched when it's checked.
            ZettaKeychain.Changed += () =>
            {
                var gone = Live.Keys.Where(provider => !ZettaKeychain.Has(provider)).ToList();
                if (gone.Count == 0) return;
                foreach (var provider in gone) Live.Remove(provider);
                SaveLive();
            };
        }

        /// <summary>Each provider's own list for the user's key, by provider.</summary>
        static Dictionary<string, List<AgentModelInfo>> Live
        {
            get
            {
                if (_live != null) return _live;
                var cached = SessionState.GetString(LiveKey, "");
                try
                {
                    _live = cached.Length > 0
                        ? JsonConvert.DeserializeObject<Dictionary<string, List<AgentModelInfo>>>(cached)
                        : null;
                }
                catch (JsonException)
                {
                    _live = null;
                }
                return _live ??= new Dictionary<string, List<AgentModelInfo>>();
            }
        }

        static void SaveLive() => SessionState.SetString(LiveKey, JsonConvert.SerializeObject(Live));

        /// <summary>The providers with each one's own list, where it has been fetched, in place of the server's.</summary>
        static List<AgentProviderInfo> WithLive(List<AgentProviderInfo> providers)
        {
            if (providers == null) return null;
            foreach (var provider in providers)
                if (Live.TryGetValue(provider.Key, out var models) && models != null && models.Count > 0)
                    provider.Models = models;
            return providers;
        }

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
                            _providers = WithLive(JsonConvert.DeserializeObject<List<AgentProviderInfo>>(cached));
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
                SessionState.SetString(CacheKey, providers == null ? "" : JsonConvert.SerializeObject(providers));
                _providers = WithLive(providers);
                Changed?.Invoke();
                RefreshKeyedModels();
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

        /// <summary>Asks every provider with a saved key for its models — see <see cref="RefreshModelsAsync"/>.</summary>
        public static void RefreshKeyedModels()
        {
            foreach (var provider in KeyedProviders) _ = RefreshModelsAsync(provider.Key);
        }

        /// <summary>
        /// The provider's own list of the models the user's key can reach, through the server (the key goes in a
        /// header and is kept nowhere). It replaces the server's starting list; on any failure that list stays.
        /// </summary>
        public static async Task RefreshModelsAsync(string providerKey)
        {
            if (!ZettaAuth.IsSignedIn || Provider(providerKey) == null || !Fetching.Add(providerKey)) return;
            try
            {
                var key = ZettaKeychain.Get(providerKey);
                if (string.IsNullOrEmpty(key)) return;
                var answer = await ZettaHttp.PostAsync("/unityAgent/provider-models", new JObject { ["provider"] = providerKey },
                    new Dictionary<string, string> { ["X-Zetta-Provider-Key"] = key }, 30) as JObject;
                if (answer?["ok"]?.Value<bool>() != true || !(answer["models"] is JArray list)) return;
                var models = list.ToObject<List<AgentModelInfo>>();
                if (models == null || models.Count == 0) return;
                Live[providerKey] = models;
                SaveLive();
                var provider = Provider(providerKey);
                if (provider != null) provider.Models = models;
                Changed?.Invoke();
            }
            catch (Exception)
            {
                // Offline, an older server, or the provider said no: the server's list still works.
            }
            finally
            {
                Fetching.Remove(providerKey);
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
