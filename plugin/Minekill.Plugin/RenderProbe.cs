using System.Linq;
using System.Text;
using UnityEngine;

namespace Minekill
{
    // Logs how ULTRAKILL renders a scene: every camera (order, mask, target), the post-process
    // handler's render textures, and which candidate shaders exist. This is the evidence for how
    // Minecraft geometry has to be drawn to land in the same colour/depth buffers as the level.
    internal static class RenderProbe
    {
        public static void Log()
        {
            var sb = new StringBuilder("render probe:\n");
            foreach (Camera c in Camera.allCameras.OrderBy(c => c.depth))
            {
                sb.AppendLine($"  camera '{c.name}' depth={c.depth} enabled={c.enabled} mask=0x{c.cullingMask:X8} clear={c.clearFlags} " +
                              $"near={c.nearClipPlane} far={c.farClipPlane} fov={c.fieldOfView:F1} depthMode={c.depthTextureMode} " +
                              $"target={Describe(c.targetTexture)} path={c.actualRenderingPath}");
            }
            if (MonoSingleton<PostProcessV2_Handler>.TryGetInstance(out PostProcessV2_Handler pp) && pp != null)
            {
                sb.AppendLine($"  PostProcessV2_Handler mainCam='{pp.mainCam?.name}' hudCam='{pp.hudCam?.name}' downscale={pp.downscaleResolution}");
                sb.AppendLine($"    mainTex={Describe(pp.mainTex)} depthBuffer={Describe(pp.depthBuffer)}");
            }
            else
            {
                sb.AppendLine("  PostProcessV2_Handler: not present in this scene");
            }
            foreach (string name in GuestMarker.ShaderCandidates)
            {
                sb.AppendLine($"  shader '{name}': {(GuestMarker.FindShader(name) != null ? "found" : "missing")}");
            }
            sb.AppendLine($"  layers: {string.Join(", ", Enumerable.Range(0, 32).Select(i => (i, n: LayerMask.LayerToName(i))).Where(x => x.n.Length > 0).Select(x => $"{x.i}={x.n}"))}");

            // How the level's own opaque surfaces are set up (keywords decide lighting and fog in
            // ULTRAKILL/Master; a material made in code starts with none).
            var surfaces = Object.FindObjectsOfType<MeshRenderer>().Where(GuestMarker.IsLevelSurface).ToList();
            sb.AppendLine($"  level surfaces (opaque ULTRAKILL/Master on level layers): {surfaces.Count} renderers, " +
                          $"{surfaces.Count(r => r.lightmapIndex >= 0 && r.lightmapIndex < 0xFFFE)} with a Unity lightmap");
            foreach (var g in surfaces.GroupBy(r => string.Join(" ", r.sharedMaterial.shaderKeywords.OrderBy(k => k))).OrderByDescending(g => g.Count()).Take(8))
            {
                Renderer ex = g.First();
                sb.AppendLine($"    {g.Count(),5} x [{g.Key}] e.g. '{ex.name}' layer={LayerMask.LayerToName(ex.gameObject.layer)} lightmapIndex={ex.lightmapIndex} " +
                              $"_VertexLighting={Get(ex.sharedMaterial, "_VertexLighting")} _Fog={Get(ex.sharedMaterial, "_Fog")} _LightMapTex={(ex.sharedMaterial.HasProperty("_LightMapTex") && ex.sharedMaterial.GetTexture("_LightMapTex") != null)}");
            }
            sb.Append($"  realtime lights: {Object.FindObjectsOfType<Light>().Count(l => l.isActiveAndEnabled)}, ambient {RenderSettings.ambientMode} {RenderSettings.ambientLight}, fog {RenderSettings.fog} {RenderSettings.fogMode} {RenderSettings.fogColor} {RenderSettings.fogStartDistance}-{RenderSettings.fogEndDistance}");
            Plugin.Log.LogInfo(sb.ToString());
        }

        private static string Get(Material m, string p) => m.HasProperty(p) ? m.GetFloat(p).ToString("0.##") : "-";

        private static string Describe(RenderTexture rt) =>
            rt == null ? "screen" : $"{rt.name}[{rt.width}x{rt.height} {rt.format} depthBits={rt.depth} aa={rt.antiAliasing}]";
    }
}
