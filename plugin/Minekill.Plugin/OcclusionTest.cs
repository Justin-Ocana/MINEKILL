using System;
using System.IO;
using UnityEngine;

namespace Minekill
{
    // In-game oracle for "Minecraft geometry is hidden behind ULTRAKILL's walls". From the main
    // camera's current view: find the wall straight ahead, then render the view three times with a
    // throwaway camera into a small render texture (no test cube / cube just behind the wall / cube
    // just in front of it) and count changed pixels. Occlusion works when the hidden cube changes
    // (almost) nothing and the visible one changes a lot. The three images are saved as PNGs.
    internal static class OcclusionTest
    {
        private const int W = 320, H = 180;

        public static string Run(Camera main, Material material, float size, string outDir)
        {
            if (main == null)
            {
                return "SKIP no main camera";
            }
            int mask = LayerMask.GetMask("Environment", "EnvironmentBaked", "Outdoors", "OutdoorsBaked");
            Vector3 eye = main.transform.position;
            Vector3 dir = main.transform.forward;
            if (!Physics.Raycast(eye, dir, out RaycastHit hit, 300f, mask, QueryTriggerInteraction.Ignore))
            {
                return "SKIP no level wall straight ahead (look at a wall)";
            }
            if (hit.distance < size * 3f)
            {
                return $"SKIP wall too close ({hit.distance:F1} units; step back)";
            }
            Vector3 behind = hit.point + dir * (size * 1.5f);
            Vector3 front = hit.point - dir * (size * 1.5f);
            // The wall must cover the whole hidden cube from here, or the test means nothing.
            float h = size * 0.5f;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = behind + new Vector3((i & 1) == 0 ? -h : h, (i & 2) == 0 ? -h : h, (i & 4) == 0 ? -h : h);
                Vector3 to = corner - eye;
                if (!Physics.Raycast(eye, to.normalized, to.magnitude, mask, QueryTriggerInteraction.Ignore))
                {
                    return "SKIP the wall ahead doesn't cover the whole test cube (face a larger wall)";
                }
            }

            GameObject cube = null, camGo = null;
            RenderTexture rt = null;
            Texture2D tex = null;
            try
            {
                cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "MINEKILL occlusion test cube";
                UnityEngine.Object.DestroyImmediate(cube.GetComponent<Collider>()); // our own component
                cube.transform.localScale = Vector3.one * size;
                cube.layer = LayerMask.NameToLayer("Environment");
                cube.GetComponent<MeshRenderer>().sharedMaterial = material;

                camGo = new GameObject("MINEKILL occlusion test camera");
                Camera cam = camGo.AddComponent<Camera>();
                cam.CopyFrom(main);
                cam.enabled = false;
                rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;
                tex = new Texture2D(W, H, TextureFormat.RGBA32, false);

                cube.SetActive(false);
                Color32[] baseline = Grab(cam, rt, tex, outDir, "0-baseline.png");
                cube.SetActive(true);
                cube.transform.position = behind;
                Color32[] hidden = Grab(cam, rt, tex, outDir, "1-cube-behind-wall.png");
                cube.transform.position = front;
                Color32[] shown = Grab(cam, rt, tex, outDir, "2-cube-in-front.png");

                int dHidden = Diff(baseline, hidden), dShown = Diff(baseline, shown);
                // A real view of a level has many colours; a failed render is one flat colour.
                bool baselineBlank = new System.Collections.Generic.HashSet<int>(
                    System.Linq.Enumerable.Select(baseline, c => (c.r << 16) | (c.g << 8) | c.b)).Count < 8;
                string verdict = baselineBlank ? "INCONCLUSIVE (test camera rendered nothing)"
                    : dShown < 50 ? "INCONCLUSIVE (the cube in front changed too few pixels)"
                    : dHidden <= Math.Max(2, dShown / 200) ? "PASS" : "FAIL";
                return $"{verdict}: wall '{hit.collider.name}' at {hit.distance:F1} units; changed pixels behind={dHidden}, in front={dShown} (of {W * H}); images in {outDir}";
            }
            finally
            {
                if (cube != null) UnityEngine.Object.Destroy(cube);
                if (camGo != null) UnityEngine.Object.Destroy(camGo);
                if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
        }

        private static Color32[] Grab(Camera cam, RenderTexture rt, Texture2D tex, string outDir, string file)
        {
            cam.Render();
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply(false);
            RenderTexture.active = prev;
            try
            {
                Directory.CreateDirectory(outDir);
                File.WriteAllBytes(Path.Combine(outDir, file), tex.EncodeToPNG());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"couldn't save {file}: {e.Message}");
            }
            return tex.GetPixels32();
        }

        private static int Diff(Color32[] a, Color32[] b)
        {
            int n = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (Math.Abs(a[i].r - b[i].r) > 8 || Math.Abs(a[i].g - b[i].g) > 8 || Math.Abs(a[i].b - b[i].b) > 8)
                {
                    n++;
                }
            }
            return n;
        }
    }
}
