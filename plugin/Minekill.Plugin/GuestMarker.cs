using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Minekill
{
    // A one-block cube drawn as an ordinary scene object, so it goes through ULTRAKILL's own
    // cameras, depth and fog (no overlay). Test: it must be hidden behind level walls and lit and
    // fogged like the level. The material is switchable at runtime (A/B test) and every choice is
    // logged with its shader properties and keywords:
    //   0  a new material on ULTRAKILL/Master
    //   1  a copy of the nearest level material that uses ULTRAKILL/Master (keeps its keywords)
    //   2  Unity's Standard shader
    internal static class GuestMarker
    {
        public static readonly string[] ShaderCandidates = { "ULTRAKILL/Master", "psx/vertexlit/vertexlit", "Standard", "Unlit/Color" };
        public static readonly string[] ModeNames = { "new ULTRAKILL/Master", "copy of nearest level material", "Unity Standard", "new ULTRAKILL/Master + level keywords" };

        // Keywords every opaque level surface in 0-1 had (RenderProbe, 2026-10-08 21:36). Unity only
        // derives keywords from the toggle properties inside the editor, so a material made in code
        // must enable them itself.
        private static readonly string[] LevelKeywords = { "_FOG_ON", "_VERTEXLIGHTING_ON", "VERTEX_LIGHTING", "_VERTEXCOLORS_ON", "_ZWRITE_ON" };

        private static readonly Color Green = new Color(0.45f, 0.75f, 0.3f);

        public static void EnableLevelKeywords(Material m)
        {
            foreach (string k in LevelKeywords)
            {
                m.EnableKeyword(k);
            }
        }

        public static Shader FindShader(string name)
        {
            Shader s = Shader.Find(name);
            if (s != null)
            {
                return s;
            }
            // Shaders that ship in asset bundles aren't visible to Shader.Find until loaded.
            foreach (Shader loaded in Resources.FindObjectsOfTypeAll<Shader>())
            {
                if (loaded.name == name)
                {
                    return loaded;
                }
            }
            return null;
        }

        public static GameObject Create(float size, int mode, Vector3 at)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "MINEKILL guest marker";
            Object.Destroy(go.GetComponent<Collider>()); // visual only for now
            go.transform.localScale = Vector3.one * size;
            go.transform.position = at;
            Object.DontDestroyOnLoad(go);
            // Same layer as the level geometry, which ULTRAKILL's main camera renders.
            int env = LayerMask.NameToLayer("Environment");
            if (env >= 0)
            {
                go.layer = env;
            }
            ApplyMaterial(go, mode);
            return go;
        }

        // Materials MINEKILL created. Only these may ever be destroyed: a new primitive's renderer
        // starts with Unity's shared built-in Default-Material, and destroying that crashed the game
        // natively (2026-10-08, Main Menu, right after the marker was created).
        private static readonly HashSet<Material> owned = new HashSet<Material>();

        public static void Release(GameObject go)
        {
            Material m = go.GetComponent<MeshRenderer>()?.sharedMaterial;
            if (m != null && owned.Remove(m))
            {
                Object.Destroy(m);
            }
            Object.Destroy(go);
        }

        // A standalone material for other tests (the caller destroys it).
        public static Material BuildMaterial(int mode, Vector3 near) => Build(mode, near, out _);

        public static void ApplyMaterial(GameObject go, int mode)
        {
            var r = go.GetComponent<MeshRenderer>();
            Material old = r.sharedMaterial;
            Material m = Build(mode, go.transform.position, out string source);
            owned.Add(m);
            r.sharedMaterial = m;
            if (old != null && old != m && owned.Remove(old))
            {
                Object.Destroy(old);
            }
            Plugin.Log.LogInfo($"guest marker material mode {mode} ({ModeNames[mode]}): {source}, layer {go.layer} ({LayerMask.LayerToName(go.layer)})\n{Describe(m)}");
        }

        private static Material Build(int mode, Vector3 near, out string source)
        {
            Material m = null;
            source = "fallback";
            if (mode == 1)
            {
                // Only visible, opaque level geometry is a valid template. The first try picked
                // 'Cube (1)' on layer 16 (Invisible), a transparent trigger volume.
                MeshRenderer best = Object.FindObjectsOfType<MeshRenderer>()
                    .Where(x => x.gameObject.name != "MINEKILL guest marker" && IsLevelSurface(x))
                    .OrderBy(x => (x.bounds.ClosestPoint(near) - near).sqrMagnitude)
                    .FirstOrDefault();
                if (best != null)
                {
                    m = new Material(best.sharedMaterial);
                    source = $"copied from '{best.name}' (layer {best.gameObject.layer}, lightmapIndex {best.lightmapIndex}, {Vector3.Distance(best.bounds.ClosestPoint(near), near):F1} units away)";
                }
            }
            else
            {
                Shader s = FindShader(mode == 2 ? "Standard" : "ULTRAKILL/Master");
                if (s != null)
                {
                    m = new Material(s);
                    source = $"new material on '{s.name}'";
                    if (mode == 3)
                    {
                        foreach (string k in LevelKeywords)
                        {
                            m.EnableKeyword(k);
                        }
                        source += $" + keywords [{string.Join(" ", LevelKeywords)}]";
                    }
                }
            }
            if (m == null)
            {
                m = new Material(FindShader("Standard") ?? Shader.Find("Hidden/InternalErrorShader"));
                source += " (requested shader/material not found)";
            }
            m.name = "MINEKILL marker";
            if (m.HasProperty("_MainTex"))
            {
                m.mainTexture = Texture2D.whiteTexture;
            }
            if (m.HasProperty("_Color"))
            {
                m.color = Green;
            }
            return m;
        }

        private static readonly string[] LevelLayers = { "Environment", "EnvironmentBaked", "Outdoors", "OutdoorsBaked" };

        // Opaque, rendered ULTRAKILL/Master surface on one of the level-geometry layers.
        public static bool IsLevelSurface(Renderer r)
        {
            Material m = r.sharedMaterial;
            return r.enabled && r.gameObject.activeInHierarchy && m != null && m.shader != null &&
                   m.shader.name == "ULTRAKILL/Master" && m.renderQueue < 2500 &&
                   !m.shaderKeywords.Contains("TRANSPARENCY") &&
                   LevelLayers.Contains(LayerMask.LayerToName(r.gameObject.layer));
        }

        private static string Describe(Material m)
        {
            var sb = new StringBuilder();
            Shader s = m.shader;
            sb.Append($"  shader '{s.name}' keywords=[{string.Join(" ", m.shaderKeywords)}] renderQueue={m.renderQueue}\n  properties:");
            for (int i = 0; i < s.GetPropertyCount(); i++)
            {
                string n = s.GetPropertyName(i);
                var t = s.GetPropertyType(i);
                string v = t == UnityEngine.Rendering.ShaderPropertyType.Float || t == UnityEngine.Rendering.ShaderPropertyType.Range ? m.GetFloat(n).ToString("0.###")
                    : t == UnityEngine.Rendering.ShaderPropertyType.Color ? m.GetColor(n).ToString()
                    : t == UnityEngine.Rendering.ShaderPropertyType.Texture ? (m.GetTexture(n) != null ? m.GetTexture(n).name : "null")
                    : t == UnityEngine.Rendering.ShaderPropertyType.Vector ? m.GetVector(n).ToString() : "?";
                sb.Append($" {n}={v}");
            }
            return sb.ToString();
        }
    }
}
