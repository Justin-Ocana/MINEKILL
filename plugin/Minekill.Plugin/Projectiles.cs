using System;
using System.Collections.Generic;
using System.Text;
using Minekill.Link;
using UnityEngine;
using LinkEnd = Minekill.Link.Link;

namespace Minekill
{
    // What Steve shoots or throws flies here, in ULTRAKILL's level: Minecraft decides the launch
    // (bow draw, speed, crit, ammo used) and hands the projectile over (RenThrow); it then moves with
    // Minecraft's own gravity and drag, collides with ULTRAKILL's geometry and enemies, and does:
    //  arrow: Minecraft's arrow damage to the enemy it hits, sticks in walls;
    //  snowball / egg: a knock and a puff; ender pearl: V1 teleports where it lands (5 damage to
    //  Steve, as in Minecraft).
    internal static class Projectiles
    {
        private sealed class Flying
        {
            public uint Kind, Flags;
            public Vector3 Pos, Vel; // Unity units, units/s
            public float Damage, Age;
            public GameObject Go;
            public bool Stuck;
        }

        private static readonly List<Flying> flying = new List<Flying>();
        private static readonly Dictionary<string, Texture2D> itemTex = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<uint, Material> mats = new Dictionary<uint, Material>();
        private static Mesh quad;

        public static int Active => flying.Count;
        public static string Last { get; private set; } = "none";

        public static void ReadItemTexture(byte[] p)
        {
            int nameLen = BitConverter.ToInt32(p, 0);
            string name = Encoding.UTF8.GetString(p, 4, nameLen);
            int pngLen = BitConverter.ToInt32(p, 4 + nameLen);
            var png = new byte[pngLen];
            Buffer.BlockCopy(p, 8 + nameLen, png, 0, pngLen);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (ImageConversion.LoadImage(tex, png))
            {
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
                if (itemTex.TryGetValue(name, out Texture2D old)) UnityEngine.Object.Destroy(old);
                itemTex[name] = tex;
                mats.Clear(); // rebuilt with the new texture
            }
        }

        public static void Launch(byte[] p, Coords coords)
        {
            uint kind = BitConverter.ToUInt32(p, 0);
            float x = BitConverter.ToSingle(p, 4), y = BitConverter.ToSingle(p, 8), z = BitConverter.ToSingle(p, 12);
            float vx = BitConverter.ToSingle(p, 16), vy = BitConverter.ToSingle(p, 20), vz = BitConverter.ToSingle(p, 24);
            float u = (float)coords.UnitsPerBlock;
            coords.ToUnity(x, y, z, out double ux, out double uy, out double uz);
            var f = new Flying
            {
                Kind = kind,
                Pos = new Vector3((float)ux, (float)uy, (float)uz),
                Vel = new Vector3(vx, vy, -vz) * 20f * u, // blocks/tick -> units/s (Unity Z is Minecraft's -Z)
                Damage = BitConverter.ToSingle(p, 28),
                Flags = BitConverter.ToUInt32(p, 32),
            };
            f.Go = MakeSprite(kind, u);
            if (f.Go != null) f.Go.transform.position = f.Pos;
            flying.Add(f);
            Last = $"launched {Name(kind)} ({f.Vel.magnitude / u:F1} blocks/s, damage {f.Damage})";
        }

        // Minecraft per-tick gravity (blocks/tick^2): arrows 0.05, thrown items 0.03; drag 0.99/tick.
        private static float Gravity(uint kind) => kind == Proto.ThrowArrow ? 0.05f : 0.03f;

        public static void Tick(LinkEnd link, Coords coords, Camera cam, Transform v1)
        {
            float dt = Time.deltaTime, u = (float)coords.UnitsPerBlock;
            for (int i = flying.Count - 1; i >= 0; i--)
            {
                Flying f = flying[i];
                f.Age += dt;
                if (f.Stuck)
                {
                    if (f.Age > 10f) Remove(i);
                    continue;
                }
                if (f.Age > 8f)
                {
                    Remove(i); // flew off into nothing
                    continue;
                }
                f.Vel.y -= Gravity(f.Kind) * 400f * u * dt;          // 20 ticks/s squared
                f.Vel *= Mathf.Pow(0.99f, 20f * dt);
                Vector3 next = f.Pos + f.Vel * dt;
                if (TryHit(f.Pos, next, v1, out RaycastHit hit, out EnemyIdentifier eid))
                {
                    Impact(link, coords, f, hit, eid, v1);
                    if (!f.Stuck) { Remove(i); continue; }
                }
                else
                {
                    f.Pos = next;
                }
                if (f.Go != null)
                {
                    f.Go.transform.position = f.Pos;
                    if (cam != null) f.Go.transform.rotation = cam.transform.rotation; // a sprite facing the camera
                }
            }
        }

        // The first thing along the path that isn't V1: an enemy (also its trigger hitboxes) or solid geometry.
        private static bool TryHit(Vector3 from, Vector3 to, Transform v1, out RaycastHit best, out EnemyIdentifier eid)
        {
            best = default;
            eid = null;
            Vector3 d = to - from;
            float len = d.magnitude;
            if (len < 1e-5f) return false;
            RaycastHit[] hits = Physics.RaycastAll(from, d / len, len, ~0, QueryTriggerInteraction.Collide);
            float bestDist = float.MaxValue;
            foreach (RaycastHit h in hits)
            {
                if (h.distance >= bestDist || h.collider == null) continue;
                Transform t = h.collider.transform;
                if (v1 != null && (t == v1 || t.IsChildOf(v1))) continue;
                if (t.name.StartsWith("MINEKILL") && !BlockWorld.TryCell(h.collider, out _)) continue;
                EnemyIdentifier e = EnemyOf(h.collider);
                if (e == null && h.collider.isTrigger) continue; // other triggers (zones, pickups)
                if (e != null && e.dead) continue;
                best = h;
                bestDist = h.distance;
                eid = e;
            }
            return bestDist < float.MaxValue;
        }

        private static EnemyIdentifier EnemyOf(Collider c)
        {
            var eii = c.GetComponent<EnemyIdentifierIdentifier>();
            if (eii != null && eii.eid != null) return eii.eid;
            return c.GetComponentInParent<EnemyIdentifier>();
        }

        private static void Impact(LinkEnd link, Coords coords, Flying f, RaycastHit hit, EnemyIdentifier eid, Transform v1)
        {
            float u = (float)coords.UnitsPerBlock;
            switch (f.Kind)
            {
                case Proto.ThrowArrow:
                    if (eid != null)
                    {
                        float damage = f.Damage * Plugin.DamageToUltrakill.Value;
                        eid.hitter = "minecraft arrow";
                        eid.DeliverDamage(hit.collider.gameObject, f.Vel.normalized * 2000f, hit.point, damage, false, 0f, null, false, false);
                        Last = $"arrow hit {eid.enemyType} for {f.Damage} MC{((f.Flags & Proto.ThrowCrit) != 0 ? " (crit)" : "")}";
                        if (eid.dead)
                        {
                            Style.Add("minekill.arrow", "ARROWED", 90, eid);
                            Synergy.Loot(link, eid);
                        }
                    }
                    else
                    {
                        // Sticks in the wall a moment, as in Minecraft.
                        f.Stuck = true;
                        f.Age = 0f;
                        f.Pos = hit.point - f.Vel.normalized * 0.2f * u;
                        if (f.Go != null) f.Go.transform.position = f.Pos;
                        Last = "arrow stuck in a wall";
                    }
                    break;
                case Proto.ThrowSnowball:
                case Proto.ThrowEgg:
                    if (eid != null)
                    {
                        eid.hitter = "minecraft snowball";
                        eid.DeliverDamage(hit.collider.gameObject, f.Vel.normalized * 4000f, hit.point, 0.01f, false, 0f, null, false, false);
                        Style.Add("minekill.snowball", "SNOWBALLED", 30, eid);
                        Last = $"{Name(f.Kind)} knocked {eid.enemyType}";
                    }
                    else
                    {
                        Last = $"{Name(f.Kind)} splat";
                    }
                    break;
                case Proto.ThrowEnderPearl:
                    Teleport(link, hit, v1, u);
                    break;
            }
        }

        // V1 lands where the pearl did: on top of a floor, or just off a wall or an enemy.
        private static void Teleport(LinkEnd link, RaycastHit hit, Transform v1, float u)
        {
            if (v1 == null || !MonoSingleton<NewMovement>.TryGetInstance(out NewMovement nm) || nm == null || nm.dead)
            {
                return;
            }
            Bounds b = nm.playerCollider.bounds;
            float feetBelowRoot = v1.position.y - b.min.y;
            Vector3 target = hit.normal.y > 0.6f
                ? hit.point + Vector3.up * (feetBelowRoot + 0.05f)
                : hit.point + hit.normal * (b.extents.x + 0.3f) + Vector3.up * (feetBelowRoot - b.extents.y * 0.5f);
            nm.rb.position = target;
            v1.position = target;
            nm.rb.velocity = Vector3.zero;
            // Minecraft's ender pearl costs 5 health on landing.
            link?.PushInput(new InputEvent { Type = Proto.InHurt, Code = Proto.HurtOther, A = 500 });
            Style.Add("minekill.pearl", "ENDERED", 60);
            Last = $"ender pearl: V1 teleported to {target}";
            Plugin.Log.LogInfo($"synergy: {Last}");
        }

        private static void Remove(int i)
        {
            if (flying[i].Go != null) UnityEngine.Object.Destroy(flying[i].Go);
            flying.RemoveAt(i);
        }

        public static void Clear()
        {
            for (int i = flying.Count - 1; i >= 0; i--) Remove(i);
            foreach (Material m in mats.Values) UnityEngine.Object.Destroy(m);
            mats.Clear();
        }

        private static string Name(uint kind) => kind switch
        {
            Proto.ThrowArrow => "arrow",
            Proto.ThrowSnowball => "snowball",
            Proto.ThrowEgg => "egg",
            Proto.ThrowEnderPearl => "ender_pearl",
            _ => "projectile",
        };

        private static GameObject MakeSprite(uint kind, float u)
        {
            if (!mats.TryGetValue(kind, out Material m))
            {
                Shader shader = GuestMarker.FindShader("ULTRAKILL/Master");
                if (shader == null) return null;
                m = new Material(shader) { name = $"MINEKILL {Name(kind)}" };
                GuestMarker.EnableLevelKeywords(m);
                m.EnableKeyword("ALPHA_TEST");
                m.mainTexture = itemTex.TryGetValue(Name(kind), out Texture2D t) ? t : Texture2D.whiteTexture;
                m.SetInt("_CullMode", 0); // both sides
                mats[kind] = m;
            }
            var go = new GameObject($"MINEKILL {Name(kind)}");
            go.transform.localScale = Vector3.one * u * 0.5f; // Minecraft draws thrown items half a block wide
            go.AddComponent<MeshFilter>().sharedMesh = Quad();
            go.AddComponent<MeshRenderer>().sharedMaterial = m;
            return go;
        }

        private static Mesh Quad()
        {
            if (quad != null) return quad;
            quad = new Mesh { name = "MINEKILL sprite" };
            quad.SetVertices(new List<Vector3> { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) });
            quad.SetUVs(0, new List<Vector2> { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) });
            quad.SetNormals(new List<Vector3> { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
            quad.SetTriangles(new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 }, 0); // both windings: visible from either side
            quad.RecalculateBounds();
            return quad;
        }
    }
}
