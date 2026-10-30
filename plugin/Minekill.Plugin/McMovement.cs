using System;
using HarmonyLib;
using Minekill.Link;
using UnityEngine;
using UnityEngine.InputSystem;
using LinkEnd = Minekill.Link.Link;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;

namespace Minekill
{
    // Minecraft's movement for V1 (toggled with a key): walk 4.3, sprint 5.6 (Ctrl or double-tap W),
    // sneak 1.3 blocks/s without falling off edges (Shift), Minecraft's jump (Space), gravity, air
    // control and fall damage (sent to Minecraft's health). The rules are McPhysics (unit-tested);
    // ULTRAKILL's physics engine still does the collisions against its level and Minecraft's blocks.
    // While this runs, NewMovement's Update/FixedUpdate are skipped (no dash, slide, slam, wall
    // jump), and come back exactly as they were when it's switched off. It also holds V1 still
    // (no keys) while a Minecraft screen is open. Outside events still move V1: an explosion's push
    // is kept and slowed by Minecraft's drag.
    [DefaultExecutionOrder(-30000)] // decide before NewMovement's Update whether it runs this frame
    internal sealed class McMovement : MonoBehaviour
    {
        public static bool Enabled { get; private set; }
        public static bool Controlling { get; private set; }
        public static string Last { get; private set; } = "off";
        public static int FallDamageSent { get; private set; }

        private const float TickSeconds = 0.05f; // Minecraft's 20 Hz
        public const float EyeStanding = 1.62f, EyeSneaking = 1.27f; // blocks above the feet
        private static readonly McPhysics physics = new McPhysics();
        private static float tickClock;
        private bool savedUseGravity;
        private float savedDrag;
        private PhysicMaterial savedMaterial;
        private static PhysicMaterial frictionless;

        // What Minecraft's camera does with the walk (GameRenderer.bobView, Player eye height), per
        // tick; McCamera interpolates them with Partial like Minecraft does between ticks.
        public static bool Sprinting => Controlling && physics.Sprinting;
        public static bool Sneaking { get; private set; }
        public static float WalkDist, WalkDistO, Bob, BobO;
        public static float Eye = EyeStanding, EyeO = EyeStanding;
        private static float lastTickAt;
        public static float Partial => Mathf.Clamp01((Time.time - lastTickAt) / TickSeconds);
        private float speedLogAt;
        private float askedSpeed;
        private float lastForwardTap = -10f;
        private bool tapSprint;
        private int failures;

        public static void Toggle()
        {
            Enabled = !Enabled;
            Plugin.Log.LogInfo($"Minecraft movement {(Enabled ? "ON (walk, sprint, sneak, jump and fall as in Minecraft)" : "OFF (ULTRAKILL's movement back)")}");
        }

        private static bool TryGetV1(out NewMovement nm)
        {
            return MonoSingleton<NewMovement>.TryGetInstance(out nm) && nm != null && nm.isActiveAndEnabled
                && nm.rb != null && nm.gc != null && nm.playerCollider != null;
        }

        private void Update()
        {
            try
            {
                MeasureUltrakillSpeed();
                NewMovement v1 = null;
                bool want = Compat.On(Compat.Feature.McMovement) && (Enabled || McScreen.Open)
                    && TryGetV1(out v1)
                    && v1.activated && !v1.dead && !v1.levelOver; // cutscenes, death and the level's end stay ULTRAKILL's
                if (want && !Controlling) Begin(v1);
                else if (!want && Controlling) End();
                if (Controlling)
                {
                    // NewMovement.Update counts down the hit invincibility; it's skipped now, so do it here.
                    if (v1.hurtInvincibility > 0f) v1.hurtInvincibility = Mathf.Max(0f, v1.hurtInvincibility - Time.deltaTime);
                    Keyboard kb = Keyboard.current;
                    if (kb != null)
                    {
                        if (kb.wKey.wasPressedThisFrame)
                        {
                            tapSprint = Time.unscaledTime - lastForwardTap < 0.35f; // Minecraft's double-tap (7 ticks)
                            lastForwardTap = Time.unscaledTime;
                        }
                        if (!kb.wKey.isPressed) tapSprint = false;
                    }
                }
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        // For balancing MinecraftSpeed: V1's top ground speed under ULTRAKILL's own movement, logged every 5 s.
        private float ukTop, ukLogAt;

        private void MeasureUltrakillSpeed()
        {
            if (Controlling || !TryGetV1(out NewMovement nm)) return;
            if (nm.gc.onGround)
            {
                Vector3 v = nm.rb.velocity;
                ukTop = Mathf.Max(ukTop, new Vector2(v.x, v.z).magnitude / Plugin.UnitsPerBlock.Value);
            }
            if (Time.unscaledTime >= ukLogAt)
            {
                ukLogAt = Time.unscaledTime + 5f;
                if (ukTop > 1f) Plugin.Log.LogInfo($"ULTRAKILL movement: top ground speed {ukTop:F2} blocks/s in the last 5 s");
                ukTop = 0f;
            }
        }

        private void FixedUpdate()
        {
            if (!Controlling)
            {
                return;
            }
            try
            {
                if (!TryGetV1(out NewMovement nm))
                {
                    End();
                    return;
                }
                tickClock += Time.fixedDeltaTime;
                // At most a few ticks per step, so a hitch doesn't turn into a burst.
                for (int n = 0; tickClock >= TickSeconds && n < 4; n++)
                {
                    tickClock -= TickSeconds;
                    Tick(nm);
                }
                if (tickClock > TickSeconds) tickClock = 0f;
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private void Tick(NewMovement nm)
        {
            lastTickAt = Time.fixedTime;
            float upb = Plugin.UnitsPerBlock.Value;
            float toBlocks = 1f / (upb / TickSeconds); // units/s -> blocks/tick
            Vector3 vel = nm.rb.velocity;
            var lastMove = new NVector3(vel.x * toBlocks, vel.y * toBlocks, vel.z * toBlocks);
            bool onGround = nm.gc.onGround;

            McInput input = default;
            Keyboard kb = Keyboard.current;
            if (kb != null && Enabled && !McScreen.Open)
            {
                input.Forward = (kb.wKey.isPressed ? 1 : 0) - (kb.sKey.isPressed ? 1 : 0);
                input.Strafe = (kb.dKey.isPressed ? 1 : 0) - (kb.aKey.isPressed ? 1 : 0);
                input.Jump = kb.spaceKey.isPressed;
                input.Sneak = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
                input.Sprint = kb.leftCtrlKey.isPressed || tapSprint;
            }

            Vector3 look = nm.transform.forward;
            if (MonoSingleton<CameraController>.TryGetInstance(out CameraController cc) && cc != null && cc.cam != null)
            {
                look = cc.cam.transform.forward;
            }
            var flat = new NVector2(look.x, look.z);
            flat = flat.LengthSquared() > 1e-6f ? NVector2.Normalize(flat) : new NVector2(0, 1);

            Sneaking = input.Sneak;
            physics.SpeedMultiplier = Plugin.MinecraftSpeed.Value;
            Bounds body = nm.playerCollider.bounds;
            NVector3 d = physics.Tick(lastMove, onGround, input, flat, move => KeepOnEdge(body, move, upb));

            if (physics.LandingDamage > 0)
            {
                LinkEnd link = Bridge.CurrentLink;
                if (link != null && link.PeerAlive)
                {
                    link.PushInput(new InputEvent { Type = Proto.InFall, A = physics.LandingDamage * 100 });
                    FallDamageSent++;
                }
                Plugin.Log.LogInfo($"Minecraft fall damage {physics.LandingDamage}");
            }
            nm.rb.velocity = new Vector3(d.X, d.Y, d.Z) * (upb / TickSeconds);

            // The camera's walk, as Minecraft computes it from what really moved (Entity.move adds
            // 0.6 per block to the walk distance; Player bobs towards its ground speed, max 0.1).
            float moved = new Vector2(lastMove.X, lastMove.Z).magnitude;
            WalkDistO = WalkDist;
            WalkDist += moved * 0.6f;
            BobO = Bob;
            Bob += ((onGround ? Mathf.Min(0.1f, moved * BlockFrictionOnGround) : 0f) - Bob) * 0.4f;
            EyeO = Eye;
            Eye += ((input.Sneak ? EyeSneaking : EyeStanding) - Eye) * 0.5f;

            // Diagnostics: the speed Minecraft's rules asked for against the one ULTRAKILL's physics delivered.
            float real = moved * 20f;
            if (askedSpeed > 0.5f && Time.unscaledTime >= speedLogAt)
            {
                speedLogAt = Time.unscaledTime + 2f;
                Plugin.Log.LogInfo($"Minecraft movement: asked {askedSpeed:F2} b/s, real {real:F2} b/s ({(onGround ? "ground" : "air")}, {(physics.Sprinting ? "sprint" : input.Sneak ? "sneak" : "walk")})");
            }
            askedSpeed = new Vector2(d.X, d.Z).magnitude * 20f;
            Last = $"{(onGround ? "ground" : "air")} {(physics.Sprinting ? "sprint" : input.Sneak ? "sneak" : "walk")} asked {askedSpeed:F2} real {real:F2} b/s fall {physics.FallDistance:F1}";
        }

        // Player.maybeBackOffFromEdge: while sneaking on the ground, shorten each horizontal step
        // (0.05 blocks at a time) until the body would still have floor within 0.6 blocks under it.
        private static NVector3 KeepOnEdge(Bounds body, NVector3 move, float upb)
        {
            const float Step = 0.05f;
            float dx = move.X, dz = move.Z;
            while (dx != 0f && !Supported(body, dx, 0f, upb)) dx = Mathf.Abs(dx) < Step ? 0f : dx - Mathf.Sign(dx) * Step;
            while (dz != 0f && !Supported(body, 0f, dz, upb)) dz = Mathf.Abs(dz) < Step ? 0f : dz - Mathf.Sign(dz) * Step;
            while (dx != 0f && dz != 0f && !Supported(body, dx, dz, upb))
            {
                dx = Mathf.Abs(dx) < Step ? 0f : dx - Mathf.Sign(dx) * Step;
                dz = Mathf.Abs(dz) < Step ? 0f : dz - Mathf.Sign(dz) * Step;
            }
            return new NVector3(dx, move.Y, dz);
        }

        private static bool Supported(Bounds body, float dxBlocks, float dzBlocks, float upb)
        {
            float drop = 0.6f * upb; // Minecraft's step height
            var feet = new Vector3(body.center.x + dxBlocks * upb, body.min.y, body.center.z + dzBlocks * upb);
            var half = new Vector3(body.extents.x * 0.95f, drop * 0.5f, body.extents.z * 0.95f);
            return Physics.CheckBox(feet + Vector3.down * (drop * 0.5f), half, Quaternion.identity, BlockWorld.LevelMask, QueryTriggerInteraction.Ignore);
        }

        private void Begin(NewMovement nm)
        {
            Controlling = true;
            physics.Reset();
            tickClock = TickSeconds; // first tick right away
            savedUseGravity = nm.rb.useGravity;
            nm.rb.useGravity = false; // Minecraft's gravity instead
            // Minecraft's friction and drag are in McPhysics; Unity's would slow V1 down a second time.
            savedDrag = nm.rb.drag;
            nm.rb.drag = 0f;
            savedMaterial = nm.playerCollider.sharedMaterial;
            if (frictionless == null)
            {
                frictionless = new PhysicMaterial("MINEKILL frictionless")
                {
                    dynamicFriction = 0f,
                    staticFriction = 0f,
                    bounciness = 0f,
                    frictionCombine = PhysicMaterialCombine.Minimum,
                    bounceCombine = PhysicMaterialCombine.Minimum,
                };
            }
            nm.playerCollider.sharedMaterial = frictionless;
            if (nm.sliding) nm.StopSlide();
            WalkDist = WalkDistO = Bob = BobO = 0f;
            Eye = EyeO = EyeStanding;
            float upb = Plugin.UnitsPerBlock.Value;
            string eye = "?";
            if (MonoSingleton<CameraController>.TryGetInstance(out CameraController cc) && cc != null && cc.cam != null)
            {
                eye = $"{(cc.cam.transform.position.y - nm.playerCollider.bounds.min.y) / upb:F2} blocks, fov {cc.cam.fieldOfView:F0}";
            }
            Plugin.Log.LogInfo($"Minecraft movement takes V1 (ULTRAKILL's NewMovement paused; rb.useGravity was {savedUseGravity}, drag {savedDrag}, " +
                $"material '{(savedMaterial != null ? $"{savedMaterial.name}' friction {savedMaterial.dynamicFriction}/{savedMaterial.staticFriction} {savedMaterial.frictionCombine}" : "none'")}, " +
                $"V1 {nm.playerCollider.bounds.size.y / upb:F2} blocks tall, eye {eye}, fixedDeltaTime {Time.fixedDeltaTime})");
        }

        private const float BlockFrictionOnGround = McPhysics.BlockFriction * McPhysics.AirFriction;

        private void End()
        {
            Controlling = false;
            if (TryGetV1(out NewMovement nm))
            {
                nm.rb.useGravity = savedUseGravity;
                nm.rb.drag = savedDrag;
                if (nm.playerCollider.sharedMaterial == frictionless) nm.playerCollider.sharedMaterial = savedMaterial;
            }
            Last = "off";
            Plugin.Log.LogInfo("ULTRAKILL's movement takes V1 back");
        }

        private void Fail(Exception e)
        {
            if (++failures <= 3) Plugin.Log.LogError($"Minecraft movement failed, switching it off: {e}");
            Compat.Disable(Compat.Feature.McMovement);
            Enabled = false;
            if (Controlling)
            {
                try { End(); } catch (Exception) { Controlling = false; }
            }
        }
    }

    // NewMovement.Update/FixedUpdate (both checked by Compat) don't run while Minecraft's rules move V1.
    [HarmonyPatch(typeof(NewMovement), "Update")]
    internal static class NewMovementUpdatePatch
    {
        private static bool Prefix() => !McMovement.Controlling;
    }

    [HarmonyPatch(typeof(NewMovement), "FixedUpdate")]
    internal static class NewMovementFixedUpdatePatch
    {
        private static bool Prefix() => !McMovement.Controlling;
    }
}
