using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Zetta3D.Editor.Agent
{
    /// <summary>unity_project_info, and the facts the EditorSnapshot shares with it.</summary>
    static class ProjectInfo
    {
        /// <summary>Built-in | URP | HDRP | Custom, from the active pipeline asset's type name (no package reference needed).</summary>
        public static string Pipeline()
        {
            var asset = GraphicsSettings.currentRenderPipeline;
            if (asset == null) return "Built-in";
            var name = asset.GetType().FullName ?? "";
            if (name.Contains("Universal") || name.Contains("Lightweight")) return "URP";
            if (name.Contains("HighDefinition") || name.Contains("HDRenderPipeline")) return "HDRP";
            return "Custom";
        }

        /// <summary>Legacy | InputSystem | Both — PlayerSettings' activeInputHandler (0, 1, 2).</summary>
        public static string InputHandling()
        {
            try
            {
                var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault();
                if (settings != null)
                {
                    var prop = new SerializedObject(settings).FindProperty("activeInputHandler");
                    if (prop != null)
                        switch (prop.intValue)
                        {
                            case 0: return "Legacy";
                            case 1: return "InputSystem";
                            case 2: return "Both";
                        }
                }
            }
            catch (Exception)
            {
                // Fall through to the compile defines.
            }
#if ENABLE_INPUT_SYSTEM && ENABLE_LEGACY_INPUT_MANAGER
            return "Both";
#elif ENABLE_INPUT_SYSTEM
            return "InputSystem";
#else
            return "Legacy";
#endif
        }

        static NamedBuildTarget CurrentTarget() =>
            NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget));

        public static string ScriptingBackend()
        {
            try
            {
                var backend = PlayerSettings.GetScriptingBackend(CurrentTarget());
                return backend == ScriptingImplementation.IL2CPP ? "IL2CPP" : backend == ScriptingImplementation.Mono2x ? "Mono" : backend.ToString();
            }
            catch (Exception)
            {
                return "Unknown";
            }
        }

        static List<string> _packages;
        static double _packagesAt = -1000;

        /// <summary>"name@version" for registered packages (built-in modules left out), cached for 30 s.</summary>
        public static List<string> Packages()
        {
            if (_packages != null && EditorApplication.timeSinceStartup - _packagesAt < 30) return _packages;
            try
            {
                _packages = PackageInfo.GetAllRegisteredPackages()
                    .Where(p => !p.name.StartsWith("com.unity.modules."))
                    .OrderBy(p => p.isDirectDependency ? 0 : 1)
                    .ThenBy(p => p.name)
                    .Select(p => $"{p.name}@{p.version}")
                    .ToList();
            }
            catch (Exception)
            {
                _packages = new List<string>();
            }
            _packagesAt = EditorApplication.timeSinceStartup;
            return _packages;
        }

        static List<string> _scriptFolders;
        static double _scriptFoldersAt = -1000;

        /// <summary>Folders under Assets/ that hold C# scripts, cached for 30 s.</summary>
        public static List<string> ScriptFolders()
        {
            if (_scriptFolders != null && EditorApplication.timeSinceStartup - _scriptFoldersAt < 30) return _scriptFolders;
            try
            {
                _scriptFolders = AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets" })
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => p.EndsWith(".cs"))
                    .Select(p => System.IO.Path.GetDirectoryName(p)?.Replace('\\', '/'))
                    .Where(p => !string.IsNullOrEmpty(p))
                    .GroupBy(p => p)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key)
                    .Take(40)
                    .OrderBy(p => p)
                    .ToList();
            }
            catch (Exception)
            {
                _scriptFolders = new List<string>();
            }
            _scriptFoldersAt = EditorApplication.timeSinceStartup;
            return _scriptFolders;
        }

        public static ToolResult Run(JObject input)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"project: {PlayerSettings.productName} (company {PlayerSettings.companyName}) at {FileTools.ProjectRoot}");
            sb.AppendLine($"unity: {Application.unityVersion} | platform: {EditorUserBuildSettings.activeBuildTarget} | pipeline: {Pipeline()}" +
                          (GraphicsSettings.currentRenderPipeline != null ? $" ({AssetDatabase.GetAssetPath(GraphicsSettings.currentRenderPipeline)})" : ""));
            sb.AppendLine($"color space: {PlayerSettings.colorSpace} | input: {InputHandling()} | scripting backend: {ScriptingBackend()} | api: {PlayerSettings.GetApiCompatibilityLevel(CurrentTarget())}");
            sb.AppendLine($"default lit shader: {AssetTools.DefaultShaderName()} | play mode: {(EditorApplication.isPlaying ? "playing" : "stopped")} | compiling: {EditorApplication.isCompiling}");

            var packages = Packages();
            sb.AppendLine($"packages ({packages.Count}): {string.Join(", ", packages)}");

            var scenes = EditorBuildSettings.scenes;
            sb.AppendLine($"build scenes ({scenes.Length}): " + (scenes.Length == 0 ? "(none)" : string.Join(", ", scenes.Select((s, i) => $"{i}:{s.path}{(s.enabled ? "" : " (disabled)")}"))));
            sb.AppendLine($"tags: {string.Join(", ", InternalEditorUtility.tags)}");
            sb.AppendLine($"layers: {string.Join(", ", SceneTools.Layers())}");

            var folders = AssetDatabase.GetSubFolders("Assets");
            sb.AppendLine($"Assets folders: {string.Join(", ", folders.Select(f => f.Substring("Assets/".Length)))}");

            var asmdefs = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath).ToList();
            sb.AppendLine($"asmdefs ({asmdefs.Count}): " + (asmdefs.Count == 0 ? "(none — scripts compile into Assembly-CSharp)" : string.Join(", ", asmdefs.Take(50))));
            var scriptFolders = ScriptFolders();
            if (scriptFolders.Count > 0) sb.AppendLine($"script folders: {string.Join(", ", scriptFolders)}");

            var zetta = ZettaProjectSettings.Instance;
            sb.AppendLine($"zetta: models folder {zetta.ModelsFolder}, images folder {zetta.ImagesFolder}, glTF importer {(ZettaImporter.GltfImporterPresent() ? "installed" : "missing")}");
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }
    }
}
