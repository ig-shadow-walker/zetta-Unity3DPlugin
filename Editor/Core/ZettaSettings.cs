using System;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace Zetta3D.Editor
{
    /// <summary>
    /// Per-machine preferences (EditorPrefs) — the sign-in token lives here, never in the project,
    /// so it is not committed with the game.
    /// </summary>
    public static class ZettaPrefs
    {
        const string TokenKey = "Zetta3D.Token";
        const string TokenExpiryKey = "Zetta3D.TokenExpiry";

        public static string Token
        {
            get => EditorPrefs.GetString(TokenKey, "");
            set
            {
                if (string.IsNullOrEmpty(value)) EditorPrefs.DeleteKey(TokenKey);
                else EditorPrefs.SetString(TokenKey, value);
            }
        }

        /// <summary>Unix seconds; 0 when unknown.</summary>
        public static long TokenExpiry
        {
            get => long.TryParse(EditorPrefs.GetString(TokenExpiryKey, "0"), out var v) ? v : 0;
            set => EditorPrefs.SetString(TokenExpiryKey, value.ToString());
        }

        public static string AgentTier
        {
            get => EditorPrefs.GetString("Zetta3D.Agent.Tier", "auto");
            set => EditorPrefs.SetString("Zetta3D.Agent.Tier", value);
        }

        public static string AgentThinking
        {
            get => EditorPrefs.GetString("Zetta3D.Agent.Thinking", "auto");
            set => EditorPrefs.SetString("Zetta3D.Agent.Thinking", value);
        }

        public static bool AgentAdaptive
        {
            get => EditorPrefs.GetBool("Zetta3D.Agent.Adaptive", true);
            set => EditorPrefs.SetBool("Zetta3D.Agent.Adaptive", value);
        }

        /// <summary>confirm_all | confirm_destructive | auto</summary>
        public static string AgentApproval
        {
            get => EditorPrefs.GetString("Zetta3D.Agent.Approval", "confirm_destructive");
            set => EditorPrefs.SetString("Zetta3D.Agent.Approval", value);
        }

        /// <summary>
        /// The agent's autonomy, in two levels: "ask" asks before deleting anything and before spending credits;
        /// "full" never asks — every call runs and the turn goes on to the end. Stored as the approval mode the
        /// server is told (confirm_destructive / auto); an older "confirm_all" reads as "ask".
        /// </summary>
        public static string AgentAutonomy
        {
            get => AgentApproval == "auto" ? "full" : "ask";
            set => AgentApproval = value == "full" ? "auto" : "confirm_destructive";
        }

        public static bool AgentFullAutonomy => AgentAutonomy == "full";

        public static string AgentMode
        {
            get => EditorPrefs.GetString("Zetta3D.Agent.Mode", "agent");
            set => EditorPrefs.SetString("Zetta3D.Agent.Mode", value);
        }
    }

    /// <summary>
    /// Per-project settings, stored in ProjectSettings/Zetta3DSettings.json so a team shares them
    /// (import folders, the agent's project id — which keys the project's memory on the server).
    /// </summary>
    [Serializable]
    public class ZettaProjectSettings
    {
        const string FilePath = "ProjectSettings/Zetta3DSettings.json";

        [JsonProperty("projectId")] public string ProjectId;
        [JsonProperty("modelsFolder")] public string ModelsFolder = "Assets/Zetta3D/Models";
        [JsonProperty("imagesFolder")] public string ImagesFolder = "Assets/Zetta3D/Images";
        [JsonProperty("placeImportedInScene")] public bool PlaceImportedInScene = true;
        [JsonProperty("preferredFormat")] public string PreferredFormat = "glb";

        static ZettaProjectSettings _instance;

        public static ZettaProjectSettings Instance
        {
            get
            {
                if (_instance != null) return _instance;
                _instance = Load();
                return _instance;
            }
        }

        public static string ProjectName => new DirectoryInfo(Path.GetDirectoryName(Application.dataPath) ?? ".").Name;

        static ZettaProjectSettings Load()
        {
            ZettaProjectSettings settings = null;
            try
            {
                if (File.Exists(FilePath))
                    settings = JsonConvert.DeserializeObject<ZettaProjectSettings>(File.ReadAllText(FilePath));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Zetta 3D] Could not read {FilePath}: {e.Message}");
            }

            settings ??= new ZettaProjectSettings();
            if (string.IsNullOrEmpty(settings.ProjectId))
            {
                settings.ProjectId = Guid.NewGuid().ToString("N");
                settings.Save();
            }
            return settings;
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Zetta 3D] Could not write {FilePath}: {e.Message}");
            }
        }
    }
}
