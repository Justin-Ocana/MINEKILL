namespace Minekill.Link
{
    // What ULTRAKILL tells Minecraft each frame (SkyState). Positions are Minecraft coordinates.
    public struct HostState
    {
        public uint Flags;
        public uint WorldId;
        public uint Epoch;
        public double X, Y, Z;
        public float Yaw, Pitch;
        public uint TeleportSeq;
        public uint ViewportW, ViewportH;
        public float GameHour;
    }

    // One overlay frame (Minecraft's hand + HUD), RGBA8 rows; Pixels points into the mapping.
    public struct OverlayFrame
    {
        public int Width, Height;
        public bool BottomUp;
        public long FrameId;
        public System.IntPtr Pixels;
    }

    // Host -> guest input/command (InputEvent).
    public struct InputEvent
    {
        public ushort Type, Code;
        public int A, B, C;
    }

    // An ULTRAKILL enemy near V1 (ActorRecord). Feet position in Minecraft coordinates.
    public struct ActorRecord
    {
        public uint Id, Flags;
        public float X, Y, Z, Yaw, Width, Height, HealthFrac;
        public string Name;
    }

    // Guest -> host event (McEvent).
    public struct McEvent
    {
        public uint Type, Id;
        public float A, B, C, D;
        public uint Flags, Weapon;
    }

    // What Minecraft tells ULTRAKILL (the McState fields MINEKILL uses so far).
    public struct GuestState
    {
        public uint Flags;
        public double X, Y, Z;
        public float Yaw, Pitch;
        public float EyeHeight;
        public uint TeleportAck;
        public ulong FrameCounter;
        public float Fov;
        public uint CameraMode;
    }
}
