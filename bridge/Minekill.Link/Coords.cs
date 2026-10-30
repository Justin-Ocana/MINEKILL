using System;

namespace Minekill.Link
{
    // ULTRAKILL (Unity: left-handed, Y up, +Z forward) <-> Minecraft (right-handed, Y up, +Z south).
    // Z is negated so Minecraft geometry isn't mirrored. Scale: V1 is ~3.5 Unity units tall and the
    // Minecraft player 1.8 blocks, so ~2 units per block keeps Steve about V1's size.
    public sealed class Coords
    {
        public double UnitsPerBlock { get; }

        // Per-level origin in Minecraft, so every level gets its own area of one Minecraft world.
        public double OriginX { get; set; }
        public double OriginZ { get; set; }

        public Coords(double unitsPerBlock)
        {
            if (!(unitsPerBlock > 0))
            {
                throw new ArgumentOutOfRangeException(nameof(unitsPerBlock));
            }
            UnitsPerBlock = unitsPerBlock;
        }

        // ULTRAKILL's y = 0 sits at Minecraft y = 64, so levels from about -256 to +512 Unity units
        // fit Minecraft's build height (-64..320). Without it, deep levels put Steve below y = -128,
        // where Minecraft's void kills him.
        public const double YOffset = 64;

        public void ToMc(double ux, double uy, double uz, out double x, out double y, out double z)
        {
            x = ux / UnitsPerBlock + OriginX;
            y = uy / UnitsPerBlock + YOffset;
            z = -uz / UnitsPerBlock + OriginZ;
        }

        public void ToUnity(double x, double y, double z, out double ux, out double uy, out double uz)
        {
            ux = (x - OriginX) * UnitsPerBlock;
            uy = (y - YOffset) * UnitsPerBlock;
            uz = -(z - OriginZ) * UnitsPerBlock;
        }

        // The Minecraft block containing a Unity point.
        public void BlockAt(double ux, double uy, double uz, out int bx, out int by, out int bz)
        {
            ToMc(ux, uy, uz, out double x, out double y, out double z);
            bx = (int)Math.Floor(x);
            by = (int)Math.Floor(y);
            bz = (int)Math.Floor(z);
        }

        // Unity centre of a Minecraft block (its cube is UnitsPerBlock wide on every axis).
        public void BlockCenter(int bx, int by, int bz, out double ux, out double uy, out double uz) =>
            ToUnity(bx + 0.5, by + 0.5, bz + 0.5, out ux, out uy, out uz);

        // Unity yaw 0 faces +Z = Minecraft north = Minecraft yaw 180. Result in (-180, 180].
        public static float YawToMc(float unityYaw) => Wrap180(unityYaw + 180f);

        public static float YawToUnity(float mcYaw) => Wrap180(mcYaw - 180f);

        // ULTRAKILL's CameraController.rotationX is positive looking up; Minecraft pitch is positive
        // looking down. Clamped to Minecraft's range.
        public static float PitchToMc(float rotationX) => Math.Max(-90f, Math.Min(90f, -Wrap180(rotationX)));

        public static float Wrap180(float deg)
        {
            float d = deg % 360f;
            if (d <= -180f) d += 360f;
            if (d > 180f) d -= 360f;
            return d;
        }

        // A stable per-level origin from the scene name: areas 2048 blocks apart, within +-60000
        // (where 32-bit floats still resolve 1/128 block).
        public static void LevelOrigin(string sceneName, out double x, out double z)
        {
            uint h = 2166136261;
            foreach (char c in sceneName ?? "")
            {
                h = (h ^ c) * 16777619;
            }
            x = ((int)(h % 59) - 29) * 2048.0;
            z = ((int)(h / 59 % 59) - 29) * 2048.0;
        }
    }
}
