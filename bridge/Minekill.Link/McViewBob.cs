using System;

namespace Minekill.Link
{
    // GameRenderer.bobView: how Minecraft shakes the view (and the hand) while walking, applied in
    // view space after the camera: translate (X, Y) blocks, then roll about the view axis, then pitch.
    public static class McViewBob
    {
        public struct Pose
        {
            public float X, Y;          // blocks, view space (X right, Y up)
            public float RollDeg;       // about the view axis (Minecraft's ZP)
            public float PitchDeg;      // about the right axis (Minecraft's XP)
        }

        public static Pose Compute(float walkDist, float walkDistO, float bob, float bobO, float partial)
        {
            float g = walkDist - walkDistO;
            float h = -(walkDist + g * partial);
            float i = bobO + (bob - bobO) * partial;
            double a = h * Math.PI;
            return new Pose
            {
                X = (float)(Math.Sin(a) * i * 0.5),
                Y = (float)-Math.Abs(Math.Cos(a) * i),
                RollDeg = (float)(Math.Sin(a) * i * 3.0),
                PitchDeg = (float)(Math.Abs(Math.Cos(a - 0.2f) * i) * 5.0),
            };
        }
    }
}
