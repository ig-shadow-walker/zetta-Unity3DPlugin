using UnityEditor;

namespace Zetta3D.Editor
{
    /// <summary>
    /// Where the plugin talks to. Production by default; a developer can point it at a local
    /// backend from Preferences > Zetta 3D without touching code.
    /// </summary>
    public static class ZettaConfig
    {
        public const string Version = "0.6.0";
        public const string ClientName = "unity";

        public const string ProductionApi = "https://api.zetta3d.ai/api";
        public const string ProductionSite = "https://zetta3d.ai";

        const string ApiKey = "Zetta3D.ApiBase";
        const string SiteKey = "Zetta3D.SiteBase";

        public static string ApiBase
        {
            get => Trim(EditorPrefs.GetString(ApiKey, ProductionApi), ProductionApi);
            set => EditorPrefs.SetString(ApiKey, value ?? ProductionApi);
        }

        public static string SiteBase
        {
            get => Trim(EditorPrefs.GetString(SiteKey, ProductionSite), ProductionSite);
            set => EditorPrefs.SetString(SiteKey, value ?? ProductionSite);
        }

        public static bool IsProduction => ApiBase == ProductionApi;

        public static void ResetToProduction()
        {
            EditorPrefs.DeleteKey(ApiKey);
            EditorPrefs.DeleteKey(SiteKey);
        }

        static string Trim(string value, string fallback)
        {
            value = (value ?? "").Trim().TrimEnd('/');
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
    }
}
