using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Zetta3D.Editor.Studio
{
    /// <summary>Project window right-click items: upload a .glb to the library, or build a 3D model from a picture.</summary>
    public static class ZettaAssetMenu
    {
        const string UploadItem = "Assets/Zetta 3D/Upload model to library";
        const string GenerateItem = "Assets/Zetta 3D/Generate 3D from this image";

        [MenuItem(UploadItem, true)]
        static bool CanUpload() => StudioAssets.IsGlb(AssetDatabase.GetAssetPath(Selection.activeObject));

        [MenuItem(UploadItem, false, 2000)]
        static async void Upload()
        {
            var path = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (!ZettaAuth.IsSignedIn)
            {
                EditorUtility.DisplayDialog("Zetta 3D", "Sign in to Zetta 3D first (Window › Zetta 3D › 3D Studio).", "OK");
                return;
            }
            var name = Path.GetFileName(path);
            Notify($"Uploading {name}…");
            try
            {
                await StudioAssets.UploadModelAsync(path, p => Notify(StudioAssets.UploadingText(p, $"Uploading {name}")));
                Notify($"{name} is in your library.");
                if (EditorUtility.DisplayDialog("Zetta 3D", $"{name} was added to your Zetta 3D library. Open My Generations?", "Open", "Close"))
                    ZettaStudioWindow.WithStudio(w => w.ShowTab(ZettaStudioWindow.LibraryTab));
            }
            catch (ZettaApiException e)
            {
                EditorUtility.DisplayDialog("Zetta 3D", "The upload did not go through. " + ZettaCatalog.Explain(e), "OK");
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Zetta 3D", "The upload did not go through. " + e.Message, "OK");
            }
        }

        [MenuItem(GenerateItem, true)]
        static bool CanGenerate() => Selection.activeObject is Texture2D;

        [MenuItem(GenerateItem, false, 2001)]
        static void Generate()
        {
            if (Selection.activeObject is not Texture2D texture) return;
            var path = AssetDatabase.GetAssetPath(texture);
            byte[] bytes;
            string type;
            var fileType = StudioAssets.ContentTypeOf(path);
            if (fileType is "image/png" or "image/jpeg")
            {
                bytes = File.ReadAllBytes(path);
                type = fileType;
            }
            else
            {
                bytes = ZettaCapture.EncodeReadable(texture);
                type = "image/png";
            }
            var fileName = Path.GetFileNameWithoutExtension(path) + StudioAssets.ExtensionFor(type);
            ZettaStudioWindow.WithStudio(w => w.UseImageFor3D(bytes, type, fileName));
        }

        static void Notify(string message)
        {
            var window = EditorWindow.focusedWindow ?? SceneView.lastActiveSceneView;
            window?.ShowNotification(new GUIContent(message), 2.5);
        }
    }
}
