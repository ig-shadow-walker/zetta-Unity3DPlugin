using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zetta3D.Editor.Agent
{
    /// <summary>capture_view: pictures of the Scene view, the game camera, or framed shots around an object.</summary>
    static class CaptureTools
    {
        struct View
        {
            public string Label;
            public Vector3 Position;
            public Quaternion Rotation;
            public float Fov;
            public float Aspect;
            public bool Ortho;
            public float OrthoSize;
        }

        public static ToolResult Capture(JObject input)
        {
            var view = (Args.Str(input, "view", "scene") ?? "scene").Trim().ToLowerInvariant();
            if (view != "scene" && view != "game") throw new ToolException("'view' must be \"scene\" or \"game\".");
            var width = Args.Int(input, "width", 768, 256, 1280);
            GameObject target = null;
            string note = null;
            if (Args.Present(input, "target")) target = TargetResolver.Resolve(Args.Str(input, "target"), out note);
            var angles = (Args.Str(input, "angles") ?? (target != null ? "around" : "current")).Trim().ToLowerInvariant();
            if (angles != "current" && angles != "around" && angles != "top") throw new ToolException("'angles' must be current, around or top.");

            var shots = new List<ZettaCapture.Shot>();
            var views = new List<View>();
            var notes = new List<string>();
            if (note != null) notes.Add(note);

            if (angles == "current" && view == "scene")
            {
                var sv = SceneView.lastActiveSceneView;
                var shot = sv != null ? ZettaCapture.SceneView(width) : null;
                if (shot != null)
                {
                    shots.Add(shot);
                    var cam = sv.camera;
                    views.Add(new View { Label = shot.Label, Position = cam.transform.position, Rotation = cam.transform.rotation, Fov = cam.fieldOfView, Aspect = cam.aspect, Ortho = cam.orthographic, OrthoSize = cam.orthographicSize });
                    if (target != null) notes.Add("The current Scene view is shown as the user sees it; the target is not framed (use angles:\"around\" to frame it).");
                }
                else
                {
                    notes.Add("No Scene view is open, so these are four framed shots of the whole scene instead.");
                    angles = "around";
                }
            }
            else if (angles == "current" && view == "game")
            {
                var camera = GameCamera();
                var shot = camera != null ? ZettaCapture.GameView(width) : null;
                if (shot != null)
                {
                    shots.Add(shot);
                    views.Add(new View { Label = shot.Label, Position = camera.transform.position, Rotation = camera.transform.rotation, Fov = camera.fieldOfView, Aspect = camera.aspect, Ortho = camera.orthographic, OrthoSize = camera.orthographicSize });
                    if (!EditorApplication.isPlaying) notes.Add("Rendered from the game camera in Edit mode (not playing).");
                }
                else
                {
                    notes.Add("The scene has no enabled camera, so these are four framed shots of the scene instead. Add a Camera for a Game view.");
                    angles = "around";
                }
            }

            if (angles == "around" || angles == "top")
            {
                var top = angles == "top";
                var around = ZettaCapture.Around(target, width, top);
                shots.AddRange(around);
                views.AddRange(AroundViews(target, width, top));
            }

            shots = shots.Where(s => s?.Jpeg != null && s.Jpeg.Length > 0).ToList();
            if (shots.Count == 0)
                throw new ToolException("Nothing could be rendered (no graphics device — the Editor may be running headless).");

            var sb = new StringBuilder();
            var framed = target != null ? $"{TargetResolver.Label(target)}" : "the whole scene";
            var bounds = target != null ? ZettaCapture.BoundsOf(target) : (Bounds?)null;
            sb.AppendLine($"{shots.Count} image(s), {width}px wide{(angles != "current" ? $", framing {framed}" : "")}.");
            if (bounds.HasValue) sb.AppendLine($"target bounds: center {Fmt.V(bounds.Value.center)} size {Fmt.V(bounds.Value.size)}");
            for (var i = 0; i < shots.Count; i++)
            {
                var v = i < views.Count ? views[i] : default;
                sb.Append($"image {i + 1}: {shots[i].Label}");
                if (i < views.Count)
                {
                    sb.Append($" — camera at {Fmt.V(v.Position)} looking {Fmt.V(v.Rotation * Vector3.forward)}");
                    sb.Append(v.Ortho ? $" (orthographic, size {Fmt.F(v.OrthoSize)})" : $" (fov {Fmt.F(v.Fov)})");
                    var visible = Visible(v, 8);
                    if (visible.Count > 0) sb.Append($"; in view: {string.Join(", ", visible)}");
                }
                sb.AppendLine();
            }
            if (notes.Count > 0) sb.AppendLine("Notes: " + string.Join(" ", notes));

            var result = ToolResult.Ok(sb.ToString().TrimEnd());
            result.Images = shots.Take(4).Select(s => ToolImage.Jpeg(s.Jpeg)).ToList();
            return result;
        }

        static Camera GameCamera() =>
            Camera.main != null ? Camera.main : Camera.allCameras.FirstOrDefault(c => c.enabled && c.targetTexture == null);

        /// <summary>The same camera placements ZettaCapture.Around uses, for the text description.</summary>
        static List<View> AroundViews(GameObject target, int width, bool topOnly)
        {
            var bounds = target != null ? ZettaCapture.BoundsOf(target) : SceneBounds();
            const float fov = 40f;
            var angles = topOnly
                ? new[] { ("Top", 0f, 89f) }
                : new[] { ("Front-left", -35f, 20f), ("Front-right", 35f, 20f), ("Back", 180f, 20f), ("Above", 20f, 60f) };
            var radius = Mathf.Max(0.5f, bounds.extents.magnitude);
            var distance = radius / Mathf.Sin(fov * 0.5f * Mathf.Deg2Rad) * 1.1f;
            return angles.Select(a =>
            {
                var rotation = Quaternion.Euler(a.Item3, a.Item2 + 180f, 0f);
                return new View { Label = a.Item1, Position = bounds.center - rotation * Vector3.forward * distance, Rotation = rotation, Fov = fov, Aspect = 4f / 3f };
            }).ToList();
        }

        static Bounds SceneBounds()
        {
            var renderers = TargetResolver.Components<Renderer>().Where(r => r.enabled).ToArray();
            if (renderers.Length == 0) return new Bounds(Vector3.zero, Vector3.one * 10f);
            var bounds = renderers[0].bounds;
            foreach (var r in renderers.Skip(1)) bounds.Encapsulate(r.bounds);
            return bounds;
        }

        /// <summary>Names of the nearest renderers inside the view frustum.</summary>
        static List<string> Visible(View v, int max)
        {
            try
            {
                var go = new GameObject("Zetta Frustum") { hideFlags = HideFlags.HideAndDontSave };
                try
                {
                    var cam = go.AddComponent<Camera>();
                    cam.enabled = false;
                    go.transform.SetPositionAndRotation(v.Position, v.Rotation);
                    cam.fieldOfView = v.Fov > 0 ? v.Fov : 60f;
                    cam.aspect = v.Aspect > 0 ? v.Aspect : 16f / 9f;
                    cam.orthographic = v.Ortho;
                    cam.orthographicSize = v.OrthoSize > 0 ? v.OrthoSize : 5f;
                    cam.nearClipPlane = 0.05f;
                    cam.farClipPlane = 5000f;
                    var planes = GeometryUtility.CalculateFrustumPlanes(cam);
                    return TargetResolver.Components<Renderer>()
                        .Where(r => r.enabled && r.gameObject.activeInHierarchy && GeometryUtility.TestPlanesAABB(planes, r.bounds))
                        .OrderBy(r => (r.bounds.center - v.Position).sqrMagnitude)
                        .Select(r => r.gameObject.name)
                        .Distinct()
                        .Take(max)
                        .ToList();
                }
                finally
                {
                    Object.DestroyImmediate(go);
                }
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }
    }
}
