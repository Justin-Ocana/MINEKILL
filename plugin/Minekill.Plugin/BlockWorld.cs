using System;
using System.Collections.Generic;
using System.Text;
using Minekill.Link;
using UnityEngine;
using LinkEnd = Minekill.Link.Link;

namespace Minekill
{
    // Minecraft's blocks inside ULTRAKILL's level, drawn and collided as level geometry:
    //  - each block is a cube on the Environment layer with a BoxCollider: V1 stands on it, enemies
    //    and their shots are stopped by it, ULTRAKILL's lights and fog apply (ULTRAKILL/Master with
    //    the level's keywords, the material verified in game on 2026-10-08);
    //  - textures are Minecraft's own, sent as PNGs by Minecraft's side at runtime;
    //  - lit TNT (with its white flashing) and explosions are shown too.
    internal static class BlockWorld
    {
        private sealed class TexSet
        {
            public string Name;
            public uint Flags, Tint;
            public byte[][] Pngs = new byte[3][];
            public Material[] Materials; // top, sides, bottom; built when first needed in a level
        }

        private static readonly Dictionary<uint, TexSet> texSets = new Dictionary<uint, TexSet>();
        private static readonly Dictionary<(int, int, int), (GameObject Go, uint Tex)> blocks = new Dictionary<(int, int, int), (GameObject, uint)>();
        private static readonly Dictionary<Collider, (int, int, int)> cellOf = new Dictionary<Collider, (int, int, int)>();
        private static readonly Dictionary<uint, GameObject> tnts = new Dictionary<uint, GameObject>();
        private static readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>(); // textures/materials we made
        private static GameObject root;
        private static Mesh cube;
        private static Material flash;

        public static int BlockCount => blocks.Count;
        public static int TntCount => tnts.Count;

        private static Transform Root
        {
            get
            {
                if (root == null)
                {
                    root = new GameObject("MINEKILL blocks");
                    UnityEngine.Object.DontDestroyOnLoad(root);
                }
                return root.transform;
            }
        }

        // ULTRAKILL's level geometry layers (and ours: blocks are on Environment).
        public static int LevelMask => LayerMask.GetMask("Environment", "EnvironmentBaked", "Outdoors", "OutdoorsBaked");

        public static bool TryCell(Collider c, out (int, int, int) cell) => cellOf.TryGetValue(c, out cell);

        public static void Process(LinkEnd link, Coords coords)
        {
            for (int guard = 0; guard < 64 && link.TryPopRenderMessage(out uint type, out byte[] p); guard++)
            {
                switch (type)
                {
                    case Proto.RenTexSet: ReadTexSet(p); break;
                    case Proto.RenBlocks: ApplyBlocks(p, coords); break;
                    case Proto.RenTnt: ApplyTnt(p, coords); break;
                    case Proto.RenThrow: Projectiles.Launch(p, coords); break;
                    case Proto.RenItemTex: Projectiles.ReadItemTexture(p); break;
                }
            }
            if (flash != null)
            {
                foreach (GameObject t in tnts.Values)
                {
                    // Minecraft's lit TNT flashes white every 4 ticks (5 times a second).
                    bool white = (int)(Time.time * 5f) % 2 == 0;
                    var r = t.GetComponent<MeshRenderer>();
                    if (white != (r.sharedMaterial == flash))
                    {
                        r.sharedMaterials = white ? new[] { flash, flash, flash } : Materials(TntTexSet()) ?? r.sharedMaterials;
                    }
                }
            }
        }

        // A level change: everything is rebuilt from Minecraft's next report (within 3 s).
        public static void Clear()
        {
            foreach (var b in blocks.Values) UnityEngine.Object.Destroy(b.Go);
            foreach (var t in tnts.Values) UnityEngine.Object.Destroy(t);
            blocks.Clear();
            cellOf.Clear();
            tnts.Clear();
            foreach (TexSet s in texSets.Values) s.Materials = null;
            foreach (UnityEngine.Object o in owned) if (o != null) UnityEngine.Object.Destroy(o);
            owned.Clear();
            flash = null;
        }

        private static void ReadTexSet(byte[] p)
        {
            int o = 0;
            uint id = BitConverter.ToUInt32(p, o); o += 4;
            var s = new TexSet { Flags = BitConverter.ToUInt32(p, o), Tint = BitConverter.ToUInt32(p, o + 4) };
            o += 8;
            int nameLen = BitConverter.ToInt32(p, o); o += 4;
            s.Name = Encoding.UTF8.GetString(p, o, nameLen); o += nameLen;
            for (int f = 0; f < 3; f++)
            {
                int len = BitConverter.ToInt32(p, o); o += 4;
                s.Pngs[f] = new byte[len];
                Buffer.BlockCopy(p, o, s.Pngs[f], 0, len);
                o += len;
            }
            texSets[id] = s;
            Plugin.Log.LogInfo($"blocks: type #{id} {s.Name} (flags {s.Flags})");
        }

        private static TexSet TntTexSet()
        {
            foreach (TexSet s in texSets.Values) if (s.Name == "minecraft:tnt") return s;
            return null;
        }

        private static Material[] Materials(TexSet s)
        {
            if (s == null) return null;
            if (s.Materials != null) return s.Materials;
            Shader shader = GuestMarker.FindShader("ULTRAKILL/Master");
            if (shader == null) return null; // not in a level yet
            s.Materials = new Material[3];
            for (int f = 0; f < 3; f++)
            {
                var m = new Material(shader) { name = $"MINEKILL {s.Name} {f}" };
                GuestMarker.EnableLevelKeywords(m);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                if (s.Pngs[f].Length == 0 || !ImageConversion.LoadImage(tex, s.Pngs[f]))
                {
                    tex = Texture2D.whiteTexture;
                }
                else
                {
                    tex.filterMode = FilterMode.Point;
                    owned.Add(tex);
                }
                m.mainTexture = tex;
                bool tinted = (s.Flags & Proto.TexTintAll) != 0 || (f == 0 && (s.Flags & Proto.TexTintTop) != 0);
                m.color = tinted ? new Color(((s.Tint >> 16) & 255) / 255f, ((s.Tint >> 8) & 255) / 255f, (s.Tint & 255) / 255f) : Color.white;
                if ((s.Flags & Proto.TexCutout) != 0)
                {
                    m.EnableKeyword("ALPHA_TEST"); // as the level's own cut-out surfaces have it
                }
                owned.Add(m);
                s.Materials[f] = m;
            }
            return s.Materials;
        }

        private static void ApplyBlocks(byte[] p, Coords coords)
        {
            int n = BitConverter.ToInt32(p, 0);
            var seen = new HashSet<(int, int, int)>();
            for (int i = 0; i < n; i++)
            {
                int o = 4 + i * 16;
                var cell = (BitConverter.ToInt32(p, o), BitConverter.ToInt32(p, o + 4), BitConverter.ToInt32(p, o + 8));
                uint tex = BitConverter.ToUInt32(p, o + 12);
                seen.Add(cell);
                if (blocks.TryGetValue(cell, out var b))
                {
                    if (b.Tex == tex) continue;
                    Remove(cell);
                }
                Material[] mats = Materials(texSets.TryGetValue(tex, out TexSet s) ? s : null);
                if (mats == null) continue; // shader not loaded (menu) or type unknown yet: next report
                GameObject go = MakeCube($"block {cell}", mats, coords, cell.Item1 + 0.5, cell.Item2 + 0.5, cell.Item3 + 0.5, (float)coords.UnitsPerBlock);
                var box = go.AddComponent<BoxCollider>();
                box.size = Vector3.one; // the cube is scaled to a block
                blocks[cell] = (go, tex);
                cellOf[box] = cell;
            }
            var gone = new List<(int, int, int)>();
            foreach (var cell in blocks.Keys) if (!seen.Contains(cell)) gone.Add(cell);
            foreach (var cell in gone) Remove(cell);
        }

        private static void Remove((int, int, int) cell)
        {
            if (!blocks.TryGetValue(cell, out var b)) return;
            foreach (Collider c in b.Go.GetComponents<Collider>()) cellOf.Remove(c);
            UnityEngine.Object.Destroy(b.Go);
            blocks.Remove(cell);
        }

        private static void ApplyTnt(byte[] p, Coords coords)
        {
            int n = BitConverter.ToInt32(p, 0);
            var seen = new HashSet<uint>();
            for (int i = 0; i < n; i++)
            {
                int o = 4 + i * 20;
                uint id = BitConverter.ToUInt32(p, o);
                float x = BitConverter.ToSingle(p, o + 4), y = BitConverter.ToSingle(p, o + 8), z = BitConverter.ToSingle(p, o + 12);
                int fuse = BitConverter.ToInt32(p, o + 16);
                seen.Add(id);
                if (!tnts.TryGetValue(id, out GameObject go))
                {
                    Material[] mats = Materials(TntTexSet());
                    if (mats == null) continue;
                    EnsureFlash();
                    go = MakeCube($"lit TNT {id}", mats, coords, x, y + 0.5, z, (float)coords.UnitsPerBlock);
                    tnts[id] = go;
                }
                coords.ToUnity(x, y + 0.5, z, out double ux, out double uy, out double uz);
                go.transform.position = new Vector3((float)ux, (float)uy, (float)uz);
                // Minecraft's lit TNT swells in its last half second.
                float swell = fuse < 10 ? 1f + (10 - fuse) / 10f * 0.3f : 1f;
                go.transform.localScale = Vector3.one * (float)coords.UnitsPerBlock * swell;
            }
            var gone = new List<uint>();
            foreach (uint id in tnts.Keys) if (!seen.Contains(id)) gone.Add(id);
            foreach (uint id in gone)
            {
                UnityEngine.Object.Destroy(tnts[id]);
                tnts.Remove(id);
            }
        }

        private static void EnsureFlash()
        {
            if (flash != null) return;
            Shader shader = GuestMarker.FindShader("ULTRAKILL/Master");
            if (shader == null) return;
            flash = new Material(shader) { name = "MINEKILL TNT flash", color = Color.white, mainTexture = Texture2D.whiteTexture };
            owned.Add(flash); // no lighting keywords: flat bright white, like Minecraft's flash
        }

        private static GameObject MakeCube(string name, Material[] mats, Coords coords, double x, double y, double z, float size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(Root, false);
            coords.ToUnity(x, y, z, out double ux, out double uy, out double uz);
            go.transform.position = new Vector3((float)ux, (float)uy, (float)uz);
            go.transform.localScale = Vector3.one * size;
            go.layer = LayerMask.NameToLayer("Environment");
            go.AddComponent<MeshFilter>().sharedMesh = Cube();
            go.AddComponent<MeshRenderer>().sharedMaterials = mats;
            return go;
        }

        // The unit cube from CubeGeometry (unit-tested: every triangle faces outward, side textures
        // upright), as a Unity mesh with submeshes 0 top, 1 sides, 2 bottom.
        private static Mesh Cube()
        {
            if (cube != null) return cube;
            CubeGeometry.Data d = CubeGeometry.Build();
            cube = new Mesh { name = "MINEKILL block" };
            cube.SetVertices(d.Positions.ConvertAll(p => new Vector3(p.X, p.Y, p.Z)));
            cube.SetNormals(d.Normals.ConvertAll(n => new Vector3(n.X, n.Y, n.Z)));
            cube.SetUVs(0, d.Uvs.ConvertAll(u => new Vector2(u.X, u.Y)));
            cube.subMeshCount = 3;
            for (int s = 0; s < 3; s++) cube.SetTriangles(d.Triangles[s], s);
            cube.RecalculateBounds();
            return cube;
        }
        // ---- explosion ------------------------------------------------------------------------
        public static void Explode(Coords coords, float x, float y, float z, float power)
        {
            coords.ToUnity(x, y, z, out double ux, out double uy, out double uz);
            var at = new Vector3((float)ux, (float)uy, (float)uz);
            var go = new GameObject("MINEKILL explosion");
            go.transform.position = at;
            go.AddComponent<ExplosionFx>().Init(power * (float)coords.UnitsPerBlock);
            try
            {
                if (MonoSingleton<CameraController>.TryGetInstance(out CameraController cc) && cc != null)
                {
                    float dist = Vector3.Distance(cc.transform.position, at);
                    cc.CameraShake(Mathf.Clamp(3f - dist / 20f, 0.3f, 3f));
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"camera shake failed: {e.Message}");
            }
            Plugin.Log.LogInfo($"blocks: TNT exploded at {at}");
        }
    }

    // A quick fireball: a bright sphere and a point light that swell and fade in half a second.
    internal sealed class ExplosionFx : MonoBehaviour
    {
        private float radius, t;
        private GameObject ball;
        private Light glow;
        private Material mat;

        public void Init(float r)
        {
            radius = r;
            ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(ball.GetComponent<Collider>()); // our own primitive's component
            ball.transform.SetParent(transform, false);
            Shader s = GuestMarker.FindShader("ULTRAKILL/Master") ?? Shader.Find("Standard");
            mat = new Material(s) { color = new Color(1f, 0.6f, 0.15f), mainTexture = Texture2D.whiteTexture };
            ball.GetComponent<MeshRenderer>().sharedMaterial = mat;
            glow = gameObject.AddComponent<Light>();
            glow.type = LightType.Point;
            glow.color = new Color(1f, 0.55f, 0.2f);
            glow.range = r * 3f;
        }

        private void Update()
        {
            t += Time.deltaTime;
            float k = t / 0.5f;
            if (k >= 1f)
            {
                Destroy(mat);
                Destroy(gameObject);
                return;
            }
            float size = radius * 2f * (k < 0.3f ? k / 0.3f : 1f - (k - 0.3f) / 0.7f * 0.8f);
            ball.transform.localScale = Vector3.one * size;
            glow.intensity = 8f * (1f - k);
        }
    }
}
