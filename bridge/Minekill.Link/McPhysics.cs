using System;
using System.Numerics;

namespace Minekill.Link
{
    // What the player presses this tick. Forward/Strafe are -1..1 (W - S, D - A).
    public struct McInput
    {
        public float Forward, Strafe;
        public bool Jump, Sprint, Sneak;
    }

    // Minecraft's player movement rules, one 20 Hz tick at a time, in blocks and blocks/tick
    // (Unity's axes: X right, Y up, Z forward). The caller owns the collisions: it hands back how far
    // the body really moved last tick (walls and floors cut that short) and whether it stands on
    // something, and gets the displacement Minecraft would ask for this tick.
    // Numbers are Minecraft's own (LivingEntity.travel, jumpFromGround, LocalPlayer.aiStep):
    // gravity 0.08 and drag 0.98 per tick, ground friction 0.6 * 0.91, air 0.91, walk speed 0.1,
    // sprint x1.3, sneak x0.3, air acceleration 0.02 (0.026 sprinting), jump 0.42 (+0.2 sprinting),
    // jump delay 10 ticks, fall damage = ceil(distance - 3).
    public sealed class McPhysics
    {
        public const float Gravity = 0.08f, VerticalDrag = 0.98f;
        public const float BlockFriction = 0.6f, AirFriction = 0.91f;
        public const float WalkSpeed = 0.1f, SprintFactor = 1.3f, SneakFactor = 0.3f;
        public const float AirAccel = 0.02f, AirAccelSprinting = 0.026f;
        public const float JumpPower = 0.42f, SprintJumpBoost = 0.2f;
        public const int JumpDelayTicks = 10;
        public const double SafeFallDistance = 3.0;

        private float lastFriction = 1f; // nothing to slow down yet: momentum from outside carries over once
        private int jumpDelay;
        private Vector2 lastRequested;

        // MINEKILL's own: scales walking and sprinting (ground and air acceleration, the sprint-jump
        // push) for a pace between Minecraft's and ULTRAKILL's. 1 = Minecraft. Sneaking and the
        // jump's height stay Minecraft's.
        public float SpeedMultiplier { get; set; } = 1f;

        public bool Sprinting { get; private set; }
        public double FallDistance { get; private set; }
        // Minecraft damage (health points) taken by the landing seen at the start of the last tick.
        public int LandingDamage { get; private set; }

        public void Reset()
        {
            lastFriction = 1f;
            jumpDelay = 0;
            lastRequested = Vector2.Zero;
            Sprinting = false;
            FallDistance = 0;
            LandingDamage = 0;
        }

        // lastMove: how far the body moved during the previous tick (blocks); onGround: standing now;
        // forward: the camera's horizontal look as a unit (x, z) vector; edgeGuard: while sneaking on
        // the ground, cuts a horizontal move short where it would step off a ledge.
        public Vector3 Tick(Vector3 lastMove, bool onGround, McInput input, Vector2 forward, Func<Vector3, Vector3> edgeGuard = null)
        {
            // Falling: distance accumulates on the way down, and the landing hurts beyond 3 blocks.
            LandingDamage = 0;
            if (onGround)
            {
                if (FallDistance > SafeFallDistance + 1e-4)
                {
                    LandingDamage = (int)Math.Ceiling(FallDistance - SafeFallDistance - 1e-4);
                }
                FallDistance = 0;
            }
            else if (lastMove.Y < 0)
            {
                FallDistance -= lastMove.Y;
            }

            // Running into a wall stops a sprint (most of the asked-for move didn't happen).
            Vector2 moved = new Vector2(lastMove.X, lastMove.Z);
            bool bumped = lastRequested.LengthSquared() > 0.05f * 0.05f && moved.Length() < 0.5f * lastRequested.Length();

            // Input as LocalPlayer and LivingEntity.aiStep shape it.
            float fwd = Clamp(input.Forward), strafe = Clamp(input.Strafe);
            if (input.Sneak)
            {
                fwd *= SneakFactor;
                strafe *= SneakFactor;
            }
            fwd *= 0.98f;
            strafe *= 0.98f;
            if (input.Sneak || input.Forward <= 0 || bumped)
            {
                Sprinting = false;
            }
            else if (input.Sprint)
            {
                Sprinting = true;
            }

            // The motion Minecraft carries between ticks: what really moved, after last tick's friction/gravity.
            // (Standing still that is -0.0784 a tick: pressed into the floor, as in Minecraft.)
            var v = new Vector3(lastMove.X * lastFriction, (lastMove.Y - Gravity) * VerticalDrag, lastMove.Z * lastFriction);

            if (jumpDelay > 0) jumpDelay--;
            if (!input.Jump)
            {
                jumpDelay = 0;
            }
            else if (onGround && jumpDelay == 0)
            {
                v.Y = JumpPower;
                if (Sprinting)
                {
                    v.X += forward.X * SprintJumpBoost * SpeedMultiplier;
                    v.Z += forward.Y * SprintJumpBoost * SpeedMultiplier;
                }
                jumpDelay = JumpDelayTicks;
            }

            // moveRelative: the input (at most length 1) times this tick's acceleration, turned to the look.
            float speed = WalkSpeed * (Sprinting ? SprintFactor : 1f);
            float accel = onGround ? speed * (0.21600002f / (BlockFriction * BlockFriction * BlockFriction)) : (Sprinting ? AirAccelSprinting : AirAccel);
            if (!input.Sneak)
            {
                accel *= SpeedMultiplier;
            }
            var wish = new Vector2(strafe, fwd);
            if (wish.LengthSquared() > 1f)
            {
                wish = Vector2.Normalize(wish);
            }
            wish *= accel;
            Vector2 right = new Vector2(forward.Y, -forward.X);
            v.X += forward.X * wish.Y + right.X * wish.X;
            v.Z += forward.Y * wish.Y + right.Y * wish.X;

            if (input.Sneak && onGround && edgeGuard != null)
            {
                v = edgeGuard(v);
            }

            lastFriction = onGround ? BlockFriction * AirFriction : AirFriction;
            lastRequested = new Vector2(v.X, v.Z);
            return v;
        }

        private static float Clamp(float x) => x < -1f ? -1f : x > 1f ? 1f : x;
    }
}
