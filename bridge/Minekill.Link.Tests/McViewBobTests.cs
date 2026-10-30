using Minekill.Link;
using Xunit;

namespace Minekill.Link.Tests
{
    public class McViewBobTests
    {
        [Fact]
        public void StandingStillDoesNotBob()
        {
            var p = McViewBob.Compute(12.3f, 12.3f, 0f, 0f, 0.5f);
            Assert.Equal(0f, p.X);
            Assert.Equal(0f, p.Y);
            Assert.Equal(0f, p.RollDeg);
            Assert.Equal(0f, p.PitchDeg);
        }

        [Fact]
        public void WalkingSwaysByMinecraftsAmounts()
        {
            // Full walking bob (0.1), a quarter step in: sin(-0.5 pi) = -1, cos = 0.
            var p = McViewBob.Compute(0.5f, 0.5f, 0.1f, 0.1f, 0f);
            Assert.Equal(-0.05f, p.X, 4);       // half a tenth of a block sideways
            Assert.Equal(0f, p.Y, 4);
            Assert.Equal(-0.3f, p.RollDeg, 4);  // 3 degrees per unit of bob
            // Each step peaks at a dip of 0.1 blocks and 0.5 degrees of pitch.
            var q = McViewBob.Compute(1f, 1f, 0.1f, 0.1f, 0f);
            Assert.Equal(-0.1f, q.Y, 4);
            Assert.InRange(q.PitchDeg, 0.48f, 0.5f);
        }

        [Fact]
        public void InterpolatesBetweenTicks()
        {
            var p = McViewBob.Compute(1.0f, 0.8f, 0.1f, 0.0f, 0.5f);
            // Bob 0.05 (lerped); the walk distance is extrapolated, a Minecraft quirk kept on
            // purpose: walkDist + (walkDist - walkDistO) * partial = 1.1.
            var q = McViewBob.Compute(1.1f, 1.1f, 0.05f, 0.05f, 0f);
            Assert.Equal(q.X, p.X, 4);
            Assert.Equal(q.Y, p.Y, 4);
        }
    }
}
