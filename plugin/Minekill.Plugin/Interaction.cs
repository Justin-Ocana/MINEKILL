using Minekill.Link;
using UnityEngine;
using UnityEngine.InputSystem;
using LinkEnd = Minekill.Link.Link;

namespace Minekill
{
    // The mouse while the hand is Minecraft's. ULTRAKILL owns the aim, so it raycasts its own level
    // (and the Minecraft blocks in it) and tells Minecraft which block is meant:
    //  - left: a Minecraft block within reach is broken; otherwise Minecraft attacks (its own target,
    //    reach and cooldown, against the enemies' stand-ins);
    //  - right: on a Minecraft block, use against that face (place, light TNT...); on ULTRAKILL's
    //    level, place into the empty block in front of the surface; else Minecraft's own "use"
    //    (raise the shield, eat a golden apple...), held until the button is released.
    internal static class Interaction
    {
        private const float ReachBlocks = 4.5f; // Minecraft's block reach
        private static bool attackHeld, useHeld;

        public static string Last { get; private set; } = "none";

        public static void Tick(LinkEnd link, Coords coords, Camera cam, Bounds v1)
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || cam == null)
            {
                return;
            }
            float reach = ReachBlocks * (float)coords.UnitsPerBlock;
            Ray ray = new Ray(cam.transform.position, cam.transform.forward);
            bool hit = Physics.Raycast(ray, out RaycastHit h, reach, BlockWorld.LevelMask, QueryTriggerInteraction.Ignore);
            (int, int, int) blockCell = default;
            bool onBlock = hit && BlockWorld.TryCell(h.collider, out blockCell);

            // Minecraft's selection box on the targeted block: a Minecraft block, or the block of
            // ULTRAKILL's floor/wall just behind the surface hit.
            if (hit)
            {
                int bx, by, bz;
                if (onBlock)
                {
                    (bx, by, bz) = blockCell;
                }
                else
                {
                    Vector3 inside = h.point - h.normal * 0.05f;
                    coords.BlockAt(inside.x, inside.y, inside.z, out bx, out by, out bz);
                }
                coords.BlockCenter(bx, by, bz, out double ox, out double oy, out double oz);
                Outline.Show(true, new Vector3((float)ox, (float)oy, (float)oz), (float)coords.UnitsPerBlock);
            }
            else
            {
                Outline.Show(false, Vector3.zero, 0f);
            }

            if (mouse.leftButton.wasPressedThisFrame)
            {
                attackHeld = true;
                if (Compat.On(Compat.Feature.Parry))
                {
                    Parrying.Swing(); // every swing is also V1's punch: projectiles in front go back, attacks get parried
                }
                if (onBlock && BlockWorld.TryCell(h.collider, out var c))
                {
                    link.PushInput(new InputEvent { Type = Proto.InBreak, A = c.Item1, B = c.Item2, C = c.Item3 });
                    Last = $"break {c}";
                }
                link.PushInput(new InputEvent { Type = Proto.InMouseButton, Code = 1, A = 1 }); // swing / attack
            }
            else if (attackHeld && mouse.leftButton.wasReleasedThisFrame)
            {
                attackHeld = false;
                link.PushInput(new InputEvent { Type = Proto.InMouseButton, Code = 1, A = 0 });
            }

            if (mouse.rightButton.wasPressedThisFrame)
            {
                useHeld = true;
                var e = new InputEvent { Type = Proto.InUse, Code = Proto.UsePressAir };
                if (onBlock && BlockWorld.TryCell(h.collider, out var c))
                {
                    e.Code = (ushort)(Proto.UsePressBlock | Face(h.normal) << 8);
                    e.A = c.Item1; e.B = c.Item2; e.C = c.Item3;
                    Last = $"use on block {c} face {Face(h.normal)}";
                }
                else if (hit)
                {
                    // The empty block just in front of the surface that was hit.
                    Vector3 p = h.point + h.normal * 0.05f;
                    coords.BlockAt(p.x, p.y, p.z, out int bx, out int by, out int bz);
                    coords.BlockCenter(bx, by, bz, out double cx, out double cy, out double cz);
                    float s = (float)coords.UnitsPerBlock;
                    var cellBounds = new Bounds(new Vector3((float)cx, (float)cy, (float)cz), Vector3.one * s * 0.98f);
                    if (cellBounds.Intersects(v1))
                    {
                        Last = "won't place a block inside V1";
                    }
                    else
                    {
                        e.Code = Proto.UsePressLevel;
                        e.A = bx; e.B = by; e.C = bz;
                        Last = $"use on level -> block ({bx}, {by}, {bz})";
                    }
                }
                else
                {
                    Last = "use (nothing in reach)";
                }
                link.PushInput(e);
            }
            else if (useHeld && mouse.rightButton.wasReleasedThisFrame)
            {
                useHeld = false;
                link.PushInput(new InputEvent { Type = Proto.InUse, Code = Proto.UseRelease });
            }
        }

        public static void ReleaseAll(LinkEnd link)
        {
            Outline.Show(false, Vector3.zero, 0f);
            if (link == null) return;
            if (attackHeld) link.PushInput(new InputEvent { Type = Proto.InMouseButton, Code = 1, A = 0 });
            if (useHeld) link.PushInput(new InputEvent { Type = Proto.InUse, Code = Proto.UseRelease });
            attackHeld = useHeld = false;
        }

        // Minecraft's Direction 3D data value of a Unity normal (Unity +Z is Minecraft's -Z = north):
        // 0 down, 1 up, 2 north, 3 south, 4 west, 5 east.
        public static int Face(Vector3 n)
        {
            float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
            if (ay >= ax && ay >= az) return n.y > 0 ? 1 : 0;
            if (az >= ax) return n.z > 0 ? 2 : 3;
            return n.x > 0 ? 5 : 4;
        }
    }
}
