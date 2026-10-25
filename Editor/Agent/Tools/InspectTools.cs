using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Zetta3D.Editor.Agent
{
    /// <summary>inspect_gameobject: what the Inspector shows, as compact text.</summary>
    static class InspectTools
    {
        /// <summary>Renderer / lighting fields that are rarely what the agent needs; shown when the component is asked for by name.</summary>
        static readonly System.Text.RegularExpressions.Regex Noise = new System.Text.RegularExpressions.Regex(
            "^m_(RayTrac|StaticShadowCaster|DynamicOccludee|SmallMeshCulling|ForceMeshLod|MeshLodSelectionBias|RendererPriority|ScaleInLightmap|" +
            "ReceiveGI|PreserveUVs|IgnoreNormalsForChartDetection|ImportantGI|StitchLightmapSeams|MinimumChartSize|AutoUVMax|LightmapParameters|" +
            "SelectedEditorRenderState|AdditionalVertexStreams|EnlightenVertexStream|LightProbeVolumeOverride|ProbeAnchor|RenderingLayerMask|" +
            "LightProbeUsage|ReflectionProbeUsage|MaskInteraction|LayerOverridePriority|ProvidesContacts|" +
            "IncludeLayers|ExcludeLayers|ImplicitCom|ImplicitTensor|InertiaTensor|InertiaRotation|BakingOutput|Lightmapping|BounceIntensity|" +
            "ColorTemperature|UseColorTemperature|UseBoundingSphereOverride|BoundingSphereOverride|UseViewFrustumForShadowCasterCull|" +
            "ShadowRadius|ShadowAngle|LightShadowCasterMode|LightUnit|LuxAtDistance|EnableSpotReflector|AreaSize|CullingMatrixOverride|" +
            "ForceVisible|ShadowUsesCustomResolution|CookieSize|Cookie$|DrawHalo|Flare|FocalLength|SensorSize|LensShift|GateFitMode|" +
            "Iso|ShutterSpeed|Aperture|FocusDistance|BladeCount|Curvature|BarrelClipping|Anamorphism|TargetEye|HDR|AllowMSAA|AllowDynamicResolution|" +
            "ForceIntoRT|OcclusionCulling|StereoConvergence|StereoSeparation)");

        public static ToolResult Inspect(JObject input)
        {
            var go = TargetResolver.Resolve(Args.Required(input, "target"), out var note);
            var depth = Args.Int(input, "depth", 1, 1, 3);
            var filter = Args.StrList(input, "components")?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
            if (filter != null && filter.Count == 0) filter = null;

            var sb = new StringBuilder();
            if (note != null) sb.AppendLine("Note: " + note);
            sb.AppendLine($"{go.name} #{Ids.Of(go)}");
            sb.AppendLine($"path: {TargetResolver.PathOf(go)}" + (go.scene.IsValid() ? $"  (scene {TargetResolver.SceneLabel(go.scene)})" : ""));
            sb.AppendLine($"active: {Bool(go.activeSelf)}{(go.activeSelf && !go.activeInHierarchy ? " (a parent is inactive)" : "")} | tag: {go.tag} | layer: {go.layer} {LayerMask.LayerToName(go.layer)} | static: {Static(go)}");
            sb.AppendLine("prefab: " + PrefabStatus(go));

            var t = go.transform;
            sb.AppendLine($"transform: local pos {Fmt.V(t.localPosition)} rot {Fmt.V(t.localEulerAngles)} scale {Fmt.V(t.localScale)}");
            sb.AppendLine($"           world pos {Fmt.V(t.position)} rot {Fmt.V(t.eulerAngles)} lossyScale {Fmt.V(t.lossyScale)}");
            if (t is RectTransform rt)
                sb.AppendLine($"rect: anchorMin {Fmt.V(rt.anchorMin)} anchorMax {Fmt.V(rt.anchorMax)} pivot {Fmt.V(rt.pivot)} anchoredPos {Fmt.V(rt.anchoredPosition)} size {Fmt.V(rt.sizeDelta)}");
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length > 0)
            {
                var b = ZettaCapture.BoundsOf(go);
                sb.AppendLine($"bounds (renderers incl. children): center {Fmt.V(b.center)} size {Fmt.V(b.size)}");
            }
            sb.AppendLine($"parent: {(t.parent != null ? TargetResolver.PathOf(t.parent.gameObject) : "(scene root)")} | children ({t.childCount}): " +
                          string.Join(", ", t.Cast<Transform>().Take(20).Select(c => c.name)) + (t.childCount > 20 ? ", …" : ""));

            var components = go.GetComponents<Component>();
            sb.AppendLine($"components ({components.Length}):");
            var shown = 0;
            for (var i = 0; i < components.Length; i++)
            {
                var c = components[i];
                if (c == null)
                {
                    sb.AppendLine($"[{i}] Missing Script (the script was deleted or failed to compile)");
                    continue;
                }
                var type = c.GetType();
                if (filter != null && !filter.Any(f => TargetResolver.TypeMatches(type, f))) continue;
                shown++;

                var header = new StringBuilder($"[{i}] {type.Name}");
                if (type.Namespace != null && !type.Namespace.StartsWith("UnityEngine")) header.Append($" ({type.FullName})");
                if (c is MonoBehaviour mb)
                {
                    var script = MonoScript.FromMonoBehaviour(mb);
                    var scriptPath = script != null ? AssetDatabase.GetAssetPath(script) : null;
                    if (!string.IsNullOrEmpty(scriptPath)) header.Append($" script {scriptPath}");
                }
                if (c is Behaviour be && !be.enabled) header.Append(" (disabled)");
                else if (c is Renderer r && !r.enabled) header.Append(" (disabled)");
                else if (c is Collider col && !col.enabled) header.Append(" (disabled)");
                sb.AppendLine(header.ToString());

                if (c is Transform && filter == null) continue; // shown above
                var maxLines = filter != null ? 200 : 40;
                foreach (var line in PropertyWriter.Lines(c, depth, maxLines, filter == null ? Noise : null)) sb.Append("  ").AppendLine(line);
                if (c is Renderer renderer) AppendMaterials(sb, renderer);
            }
            if (filter != null && shown == 0)
                sb.AppendLine($"(no component matches {string.Join(", ", filter)})");
            return ToolResult.Ok(sb.ToString().TrimEnd());
        }

        static void AppendMaterials(StringBuilder sb, Renderer renderer)
        {
            var mats = renderer.sharedMaterials;
            for (var i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null)
                {
                    sb.AppendLine($"  material[{i}]: None");
                    continue;
                }
                var color = m.HasProperty("_BaseColor") ? Fmt.Color(m.GetColor("_BaseColor")) : m.HasProperty("_Color") ? Fmt.Color(m.GetColor("_Color")) : null;
                sb.AppendLine($"  material[{i}]: {PropertyWriter.RefLabel(m)} shader {m.shader?.name}{(color != null ? " color " + color : "")}");
            }
        }

        static string Bool(bool b) => b ? "true" : "false";

        static string Static(GameObject go)
        {
            var flags = GameObjectUtility.GetStaticEditorFlags(go);
            if (flags == 0) return "false";
            return go.isStatic ? "true" : flags.ToString();
        }

        public static string PrefabStatus(GameObject go)
        {
            if (!PrefabUtility.IsPartOfPrefabInstance(go)) return "none";
            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            var path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            var kind = PrefabUtility.GetPrefabAssetType(go).ToString();
            var status = PrefabUtility.GetPrefabInstanceStatus(go);
            var where = root == go ? "instance root" : $"inside instance {root.name}";
            var overrides = root == go && PrefabUtility.HasPrefabInstanceAnyOverrides(go, false) ? ", has overrides" : "";
            return $"{where} of {path} ({kind}{(status != PrefabInstanceStatus.Connected ? ", " + status : "")}{overrides})";
        }
    }
}
