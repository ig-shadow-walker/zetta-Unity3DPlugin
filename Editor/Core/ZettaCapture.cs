using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Zetta3D.Editor
{
    /// <summary>
    /// Pictures of the Editor for the agent's eyes and for AI tagging: the Scene view as the user
    /// sees it, the Game view's camera, or four framed shots around an object.
    /// </summary>
    public static class ZettaCapture
    {
        public class Shot
        {
            public string Label;
            public byte[] Jpeg;
        }

        /// <summary>PNG bytes of any texture, readable or not (blit through a RenderTexture).</summary>
        public static byte[] EncodeReadable(Texture texture)
        {
            var rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(texture, rt);
            var copy = Read(rt);
            RenderTexture.ReleaseTemporary(rt);
            var png = copy.EncodeToPNG();
            Object.DestroyImmediate(copy);
            return png;
        }

        static Texture2D Read(RenderTexture rt)
        {
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var texture = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            texture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            texture.Apply();
            RenderTexture.active = previous;
            return texture;
        }

        static byte[] Render(Camera camera, int width, int height)
        {
            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.antiAliasing = 4;
            var target = camera.targetTexture;
            camera.targetTexture = rt;
            camera.Render();
            camera.targetTexture = target;
            var texture = Read(rt);
            RenderTexture.ReleaseTemporary(rt);
            var jpeg = texture.EncodeToJPG(82);
            Object.DestroyImmediate(texture);
            return jpeg;
        }

        static (int w, int h) Size(Camera camera, int width)
        {
            var aspect = camera != null && camera.pixelHeight > 0 ? (float)camera.pixelWidth / camera.pixelHeight : 16f / 9f;
            aspect = Mathf.Clamp(aspect, 0.5f, 2.5f);
            return (width, Mathf.Max(64, Mathf.RoundToInt(width / aspect)));
        }

        /// <summary>The Scene view from where the user is looking.</summary>
        public static Shot SceneView(int width = 768)
        {
            var view = UnityEditor.SceneView.lastActiveSceneView;
            if (view == null || view.camera == null) return null;
            var (w, h) = Size(view.camera, width);
            return new Shot { Label = "Scene view (current)", Jpeg = Render(view.camera, w, h) };
        }

        /// <summary>The main camera (or the first enabled camera) — what the player would see.</summary>
        public static Shot GameView(int width = 768)
        {
            var camera = Camera.main ?? ZettaScene.All<Camera>().FirstOrDefault(c => c.enabled && c.gameObject.activeInHierarchy && c.targetTexture == null);
            if (camera == null) return null;
            var (w, h) = Size(camera, width);
            return new Shot { Label = $"Game view ({camera.name})", Jpeg = Render(camera, w, h) };
        }

        public static Bounds BoundsOf(GameObject target)
        {
            var renderers = target.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return new Bounds(target.transform.position, Vector3.one);
            var bounds = renderers[0].bounds;
            foreach (var r in renderers.Skip(1)) bounds.Encapsulate(r.bounds);
            return bounds;
        }

        /// <summary>Four framed shots around the target (or the whole scene), plus an optional top view.</summary>
        public static List<Shot> Around(GameObject target, int width = 640, bool topOnly = false)
        {
            var bounds = target != null ? BoundsOf(target) : SceneBounds();
            var go = new GameObject("Zetta Capture Camera") { hideFlags = HideFlags.HideAndDontSave };
            var camera = go.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.fieldOfView = 40f;
            camera.nearClipPlane = Mathf.Max(0.01f, bounds.extents.magnitude * 0.01f);
            camera.farClipPlane = Mathf.Max(1000f, bounds.extents.magnitude * 20f);

            var angles = topOnly
                ? new[] { ("Top", 0f, 89f) }
                : new[] { ("Front-left", -35f, 20f), ("Front-right", 35f, 20f), ("Back", 180f, 20f), ("Above", 20f, 60f) };

            var shots = new List<Shot>();
            var radius = Mathf.Max(0.5f, bounds.extents.magnitude);
            var distance = radius / Mathf.Sin(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.1f;
            foreach (var (label, yaw, pitch) in angles)
            {
                var rotation = Quaternion.Euler(pitch, yaw + 180f, 0f);
                go.transform.position = bounds.center - rotation * Vector3.forward * distance;
                go.transform.rotation = rotation;
                var height = Mathf.RoundToInt(width * 0.75f);
                shots.Add(new Shot { Label = label, Jpeg = Render(camera, width, height) });
            }

            Object.DestroyImmediate(go);
            return shots;
        }

        static Bounds SceneBounds()
        {
            var renderers = ZettaScene.All<Renderer>().Where(r => r.enabled && r.gameObject.activeInHierarchy).ToArray();
            if (renderers.Length == 0) return new Bounds(Vector3.zero, Vector3.one * 10f);
            var bounds = renderers[0].bounds;
            foreach (var r in renderers.Skip(1)) bounds.Encapsulate(r.bounds);
            return bounds;
        }

        /// <summary>Framed shots of a model asset (no scene needed) via a preview scene — for tagging and rig checks.</summary>
        public static List<byte[]> AssetShots(GameObject prefab, int width = 512)
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            try
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                var lightGo = new GameObject("Light");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(lightGo, scene);
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.2f;
                lightGo.transform.rotation = Quaternion.Euler(50, -30, 0);

                var camGo = new GameObject("Camera");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(camGo, scene);
                var camera = camGo.AddComponent<Camera>();
                camera.scene = scene;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.16f, 0.15f, 0.13f);
                camera.fieldOfView = 35f;

                var bounds = BoundsOf(instance);
                var radius = Mathf.Max(0.1f, bounds.extents.magnitude);
                var distance = radius / Mathf.Sin(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.05f;
                camera.nearClipPlane = distance * 0.01f;
                camera.farClipPlane = distance * 10f;

                var result = new List<byte[]>();
                foreach (var yaw in new[] { 0f, 90f, 180f, 270f })
                {
                    var rotation = Quaternion.Euler(15f, yaw + 180f, 0f);
                    camGo.transform.position = bounds.center - rotation * Vector3.forward * distance;
                    camGo.transform.rotation = rotation;
                    result.Add(Render(camera, width, width));
                }
                return result;
            }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
            }
        }
    }
}
