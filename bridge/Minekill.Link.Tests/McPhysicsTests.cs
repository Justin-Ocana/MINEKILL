using System;
using System.Numerics;
using Minekill.Link;
using Xunit;

namespace Minekill.Link.Tests
{
    // Minecraft's player movement (LivingEntity.travel / jumpFromGround, LocalPlayer input) on flat
    // ground, checked against the numbers Minecraft players know: walk 4.317, sprint 5.612,
    // sneak 1.295 blocks/s, jump 1.2522 blocks high, fall damage = blocks fallen - 3.
    public class McPhysicsTests
    {
        private static readonly Vector2 North = new Vector2(0, 1); // "forward" along +Z

        // Flat ground at y = 0, no walls: the body moves exactly as asked, except that it can't go
        // below the floor (that's the collision that stops a fall).
        private static (Vector3 pos, Vector3 lastMove, bool onGround) Move(Vector3 pos, Vector3 d)
        {
            Vector3 next = pos + d;
            if (next.Y <= 0)
            {
                d.Y = -pos.Y;
                next.Y = 0;
                return (next, d, true);
            }
            return (next, d, false);
        }

        private static float SteadySpeed(McInput input, int ticks = 60, float multiplier = 1f)
        {
            var p = new McPhysics { SpeedMultiplier = multiplier };
            Vector3 pos = Vector3.Zero, last = Vector3.Zero;
            bool onGround = true;
            Vector3 d = Vector3.Zero;
            for (int i = 0; i < ticks; i++)
            {
                d = p.Tick(last, onGround, input, North);
                (pos, last, onGround) = Move(pos, d);
            }
            return new Vector2(d.X, d.Z).Length() * 20f; // blocks per second
        }

        [Fact]
        public void WalkingIs4_317BlocksPerSecond() =>
            Assert.InRange(SteadySpeed(new McInput { Forward = 1 }), 4.30f, 4.33f);

        [Fact]
        public void SprintingIs5_612BlocksPerSecond() =>
            Assert.InRange(SteadySpeed(new McInput { Forward = 1, Sprint = true }), 5.59f, 5.63f);

        [Fact]
        public void SneakingIs1_295BlocksPerSecond() =>
            Assert.InRange(SteadySpeed(new McInput { Forward = 1, Sneak = true }), 1.28f, 1.31f);

        [Fact]
        public void SpeedMultiplierScalesWalkAndSprintOnly()
        {
            Assert.InRange(SteadySpeed(new McInput { Forward = 1 }, multiplier: 1.5f), 6.45f, 6.50f);
            Assert.InRange(SteadySpeed(new McInput { Forward = 1, Sprint = true }, multiplier: 1.5f), 8.39f, 8.45f);
            Assert.InRange(SteadySpeed(new McInput { Forward = 1, Sneak = true }, multiplier: 1.5f), 1.28f, 1.31f);
            var p = new McPhysics { SpeedMultiplier = 1.5f };
            Assert.Equal(McPhysics.JumpPower, p.Tick(System.Numerics.Vector3.Zero, true, new McInput { Jump = true }, North).Y);
        }

        [Fact]
        public void DiagonalIsNotFaster() =>
            Assert.InRange(SteadySpeed(new McInput { Forward = 1, Strafe = 1 }), 4.30f, 4.42f);

        [Fact]
        public void JumpPeaksAt1_2522Blocks()
        {
            var p = new McPhysics();
            Vector3 pos = Vector3.Zero, last = Vector3.Zero;
            bool onGround = true;
            float peak = 0;
            int airTicks = 0;
            for (int i = 0; i < 40; i++)
            {
                var input = new McInput { Jump = i == 0 };
                (pos, last, onGround) = Move(pos, p.Tick(last, onGround, input, North));
                peak = Math.Max(peak, pos.Y);
                if (!onGround) airTicks++;
            }
            Assert.InRange(peak, 1.24f, 1.26f);
            Assert.InRange(airTicks, 10, 13); // a jump lasts ~0.6 s
            Assert.Equal(0, p.LandingDamage);
        }

        [Fact]
        public void HoldingJumpJumpsAgainOnlyAfterLanding()
        {
            var p = new McPhysics();
            Vector3 pos = Vector3.Zero, last = Vector3.Zero;
            bool onGround = true;
            int takeOffs = 0;
            for (int i = 0; i < 60; i++)
            {
                bool wasGround = onGround;
                (pos, last, onGround) = Move(pos, p.Tick(last, onGround, new McInput { Jump = true }, North));
                if (wasGround && !onGround) takeOffs++;
            }
            Assert.InRange(takeOffs, 4, 5); // 3 s of held space: one hop per ~0.6 s, no double jumps
        }

        [Fact]
        public void SprintJumpGetsMinecraftsBoost()
        {
            var p = new McPhysics();
            Vector3 last = Vector3.Zero, pos = Vector3.Zero;
            bool onGround = true;
            for (int i = 0; i < 40; i++) (pos, last, onGround) = Move(pos, p.Tick(last, onGround, new McInput { Forward = 1, Sprint = true }, North));
            float before = last.Z;
            Vector3 d = p.Tick(last, onGround, new McInput { Forward = 1, Sprint = true, Jump = true }, North);
            Assert.Equal(0.42f, d.Y, 3);
            Assert.InRange(d.Z - before, 0.19f, 0.21f);
        }

        [Fact]
        public void FallOf10_5BlocksDoes7DamageAnd3BlocksNone()
        {
            // As in Entity.checkFallDamage, the move that ends on the floor isn't added to the
            // fall distance: 10.5 blocks count as ~9.6 here, so 7 damage, not 8.
            Assert.Equal(7, Fall(10.5f));
            Assert.Equal(0, Fall(3f));
            Assert.Equal(1, Fall(3.5f));
        }

        private static int Fall(float height)
        {
            var p = new McPhysics();
            Vector3 pos = new Vector3(0, height, 0), last = Vector3.Zero;
            bool onGround = false;
            int damage = 0;
            for (int i = 0; i < 200; i++)
            {
                Vector3 d = p.Tick(last, onGround, default, North);
                damage += p.LandingDamage;
                (pos, last, onGround) = Move(pos, d);
            }
            return damage;
        }

        [Fact]
        public void SprintStopsWhenForwardIsReleased()
        {
            var p = new McPhysics();
            p.Tick(Vector3.Zero, true, new McInput { Forward = 1, Sprint = true }, North);
            Assert.True(p.Sprinting);
            p.Tick(Vector3.Zero, true, new McInput { Forward = 0, Sprint = true }, North);
            Assert.False(p.Sprinting);
        }

        [Fact]
        public void SprintStopsAgainstAWall()
        {
            var p = new McPhysics();
            Vector3 last = Vector3.Zero;
            for (int i = 0; i < 5; i++)
            {
                p.Tick(last, true, new McInput { Forward = 1, Sprint = true }, North);
                last = Vector3.Zero; // the wall: nothing moved
            }
            Assert.False(p.Sprinting);
        }

        [Fact]
        public void SneakingKeepsYouOnTheEdge()
        {
            // The ground ends at z = 0.5: the edge test says "no floor" for anything past it.
            var p = new McPhysics();
            Vector3 pos = Vector3.Zero, last = Vector3.Zero;
            for (int i = 0; i < 100; i++)
            {
                Vector3 at = pos;
                Vector3 d = p.Tick(last, true, new McInput { Forward = 1, Sneak = true }, North,
                    move => at.Z + move.Z > 0.5f ? new Vector3(move.X, move.Y, Math.Max(0, 0.5f - at.Z)) : move);
                d.Y = 0;
                pos += d;
                last = d;
            }
            Assert.InRange(pos.Z, 0.45f, 0.5f);
        }

        [Fact]
        public void TurningFollowsTheCamera()
        {
            var p = new McPhysics();
            Vector3 d = p.Tick(Vector3.Zero, true, new McInput { Forward = 1 }, new Vector2(1, 0));
            Assert.True(d.X > 0 && Math.Abs(d.Z) < 1e-6);
            d = new McPhysics().Tick(Vector3.Zero, true, new McInput { Strafe = 1 }, North);
            Assert.True(d.X > 0 && Math.Abs(d.Z) < 1e-6); // strafe right with forward = +Z is +X (Unity's frame)
        }

        [Fact]
        public void MomentumFromOutsideCarriesOverAndDecays()
        {
            // An ULTRAKILL explosion launched V1 at 3 blocks/tick sideways: Minecraft's air drag eats it.
            var p = new McPhysics();
            Vector3 d = p.Tick(new Vector3(3, 0, 0), false, default, North);
            Assert.InRange(d.X, 2.6f, 3.0f);
        }
    }
}
