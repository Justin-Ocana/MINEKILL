using System;
using Minekill.Link;
using UnityEngine;

namespace Minekill
{
    // Minecraft's camera on V1 while Minecraft's movement is on:
    //  - field of view: Minecraft's own (its FOV setting, 70 by default, against ULTRAKILL's much wider
    //    one), +15% while sprinting, bow/spyglass zoom and potions, eased in as Minecraft does
    //    (half the way per tick);
    //  - eye height: Steve's 1.62 blocks, 1.27 sneaking;
    //  - view bobbing: GameRenderer.bobView (McViewBob) from the distance V1 really walks.
    // Applied only while ULTRAKILL's player camera renders (Camera.onPreCull) and undone right
    // after (onPostRender), so ULTRAKILL's camera code, aim and raycasts never see it.
    internal static class McCamera
    {
        private static bool hooked;
        private static Camera applied;
        private static Vector3 savedPos;
        private static Quaternion savedRot;
        private static float savedFov;
        private static float fov = -1f;

        public static string Last { get; private set; } = "off";

        private static bool Active => Plugin.MinecraftCamera.Value && McMovement.Enabled && McMovement.Controlling;

        public static void Hook()
        {
            if (hooked) return;
            hooked = true;
            Camera.onPreCull += PreCull;
            Camera.onPostRender += PostRender;
        }

        // Once per frame (Bridge.LateUpdate): ease the field of view towards Minecraft's.
        public static void Frame(float guestFov, bool guestAlive)
        {
            Restore(); // in case a render was skipped and onPostRender never came
            if (!Active)
            {
                fov = -1f;
                Last = "off";
                return;
            }
            float target = (guestAlive && guestFov >= 30f && guestFov <= 170f
                ? guestFov
                : 70f * (McMovement.Sprinting ? 1.15f : 1f)) + Plugin.MinecraftFovBonus.Value;
            target = Mathf.Clamp(target, 30f, 170f);
            fov = fov < 0f ? target : fov + (target - fov) * (1f - Mathf.Pow(0.5f, Time.deltaTime * 20f));
        }

        private static void PreCull(Camera cam)
        {
            try
            {
                if (applied != null || !Active || fov < 0f) return;
                if (!MonoSingleton<CameraController>.TryGetInstance(out CameraController cc) || cc == null || cam != cc.cam) return;
                if (!MonoSingleton<NewMovement>.TryGetInstance(out NewMovement nm) || nm == null || nm.playerCollider == null) return;

                Transform t = cam.transform;
                applied = cam;
                savedPos = t.localPosition;
                savedRot = t.localRotation;
                savedFov = cam.fieldOfView;
                float upb = Plugin.UnitsPerBlock.Value;

                cam.fieldOfView = fov;

                // Steve's eye height above V1's feet, kept inside V1's capsule.
                Bounds body = nm.playerCollider.bounds;
                float p = McMovement.Partial;
                float eye = Mathf.Lerp(McMovement.EyeO, McMovement.Eye, p) * upb;
                float y = Mathf.Clamp(body.min.y + eye, body.min.y + 0.2f, body.max.y - 0.05f);
                t.position = new Vector3(t.position.x, y, t.position.z);

                if (Plugin.MinecraftViewBob.Value)
                {
                    McViewBob.Pose b = McViewBob.Compute(McMovement.WalkDist, McMovement.WalkDistO, McMovement.Bob, McMovement.BobO, p);
                    // Minecraft moves the world by B in view space; the camera moves by B's inverse.
                    // Its view space looks down -Z, Unity's down +Z: X and Y and the roll carry over,
                    // the pitch flips.
                    Matrix4x4 bob = Matrix4x4.Translate(new Vector3(b.X, b.Y, 0f) * upb)
                        * Matrix4x4.Rotate(Quaternion.AngleAxis(b.RollDeg, Vector3.forward))
                        * Matrix4x4.Rotate(Quaternion.AngleAxis(-b.PitchDeg, Vector3.right));
                    Matrix4x4 m = Matrix4x4.TRS(t.position, t.rotation, Vector3.one) * bob.inverse;
                    t.SetPositionAndRotation(m.GetColumn(3), m.rotation);
                    Last = $"fov {fov:F1} eye {eye / upb:F2} bob {b.Y:F3}/{b.RollDeg:F2}deg";
                }
                else
                {
                    Last = $"fov {fov:F1} eye {eye / upb:F2}";
                }
            }
            catch (Exception e)
            {
                Restore();
                Plugin.Log.LogError($"Minecraft camera failed, switching it off: {e}");
                Plugin.MinecraftCamera.Value = false;
            }
        }

        private static void PostRender(Camera cam)
        {
            if (cam == applied) Restore();
        }

        private static void Restore()
        {
            if (applied == null) return;
            Camera cam = applied;
            applied = null;
            if (cam == null) return;
            cam.transform.localPosition = savedPos;
            cam.transform.localRotation = savedRot;
            cam.fieldOfView = savedFov;
        }
    }
}
