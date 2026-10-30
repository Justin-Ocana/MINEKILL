using UnityEngine;

namespace Minekill
{
    // Minecraft's block selection box: thin black lines at 40% opacity around the block the
    // crosshair is on (a Minecraft block, or the block of ULTRAKILL's floor/wall it would be),
    // within reach, depth-tested like the rest of the level, slightly larger than the block.
    internal static class Outline
    {
        private static GameObject go;
        private static Material mat;

        public static void Show(bool visible, Vector3 centre, float size)
        {
            if (go == null)
            {
                if (!visible) return;
                Create();
            }
            if (go.activeSelf != visible) go.SetActive(visible);
            if (visible)
            {
                go.transform.position = centre;
                go.transform.localScale = Vector3.one * size * 1.004f; // Minecraft inflates it by 0.002
            }
        }

        private static void Create()
        {
            go = new GameObject("MINEKILL block outline");
            Object.DontDestroyOnLoad(go);
            int env = LayerMask.NameToLayer("Environment");
            if (env >= 0) go.layer = env;
            var mesh = new Mesh { name = "MINEKILL outline" };
            var v = new Vector3[8];
            for (int i = 0; i < 8; i++) v[i] = new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f);
            mesh.vertices = v;
            // The 12 edges: pairs of corners that differ in one coordinate bit.
            var idx = new System.Collections.Generic.List<int>();
            for (int a = 0; a < 8; a++)
                for (int bit = 1; bit <= 4; bit <<= 1)
                    if ((a & bit) == 0) { idx.Add(a); idx.Add(a | bit); }
            mesh.SetIndices(idx.ToArray(), MeshTopology.Lines, 0);
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            // Unity's built-in line shader (always in builds): colour with alpha, depth-tested, no depth write.
            Shader s = Shader.Find("Hidden/Internal-Colored") ?? GuestMarker.FindShader("Sprites/Default");
            mat = new Material(s) { name = "MINEKILL outline", color = new Color(0f, 0f, 0f, 0.4f) };
            if (mat.HasProperty("_SrcBlend")) mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend")) mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_ZWrite")) mat.SetInt("_ZWrite", 0);
            if (mat.HasProperty("_ZTest")) mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.LessEqual);
            if (mat.HasProperty("_Cull")) mat.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            Plugin.Log.LogInfo($"block outline using shader '{s?.name}'");
        }
    }
}
