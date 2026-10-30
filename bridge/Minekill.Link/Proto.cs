namespace Minekill.Link
{
    // Byte layout of SkyCraft's shared memory, protocol v11 (SkyCraft protocol/skycraft_protocol.h,
    // MIT). Only the regions MINEKILL uses so far are named here; the full mapping size is kept so
    // SkyCraft's mod, which maps the whole thing, can open ours. Little-endian throughout.
    // "Host" is SkyCraft's "Skyrim" side (here ULTRAKILL); "guest" is Minecraft.
    public static class Proto
    {
        public const uint Magic = 0x43594B53; // "SKYC"
        public const uint Version = 11;

        // Not SkyCraft's default name, so MINEKILL never collides with SkyCraft or Killcraft running
        // at the same time. SkyCraft's mod is pointed here with -Dskycraft.link=Local\Minekill_v1.
        public const string DefaultMappingName = "Local\\Minekill_v1";

        public const long OffHeader = 0x0;
        public const long OffHostState = 0x100;
        public const long OffGuestState = 0x200;
        public const long OffCollisionRing = 0x20000;
        public const long CollisionRingBytes = 32L << 20;
        public const long OffOverlayPixels = OffCollisionRing + CollisionRingBytes;
        public const long OverlaySlotBytes = 3840L * 2160 * 4;
        public const int OverlaySlots = 3;
        public const long OffRenderRing = OffOverlayPixels + OverlaySlotBytes * OverlaySlots;
        public const long RenderRingBytes = 64L << 20;
        public const long MappingBytes = OffRenderRing + RenderRingBytes;

        // Header (0x20 bytes)
        public const long HMagic = 0x00, HVersion = 0x04, HHostPid = 0x08, HGuestPid = 0x0C,
            HHostBeatMs = 0x10, HGuestBeatMs = 0x18;
        public const long HeaderBytes = 0x20;

        // Host -> guest state (SkyState, 0x40 bytes, seqlock: seq odd while writing)
        public const long HsSeq = 0x00, HsFlags = 0x04, HsWorldId = 0x08, HsEpoch = 0x0C,
            HsPosX = 0x10, HsPosY = 0x18, HsPosZ = 0x20, HsYaw = 0x28, HsPitch = 0x2C,
            HsTeleportSeq = 0x30, HsViewportW = 0x34, HsViewportH = 0x38, HsGameHour = 0x3C;
        public const long HostStateBytes = 0x40;
        public const uint HostInGame = 1, HostMenuOpen = 2, HostLoading = 4;

        // Guest -> host state (McState, 0xC8 bytes, seqlock). Only the fields MINEKILL reads.
        public const long GsSeq = 0x00, GsFlags = 0x04, GsX = 0x08, GsY = 0x10, GsZ = 0x18,
            GsYaw = 0x20, GsPitch = 0x24, GsEyeHeight = 0x28, GsTeleportAck = 0x30,
            GsFrameCounter = 0x38, GsFov = 0x40, GsTickQpc = 0x68, GsCameraMode = 0xC0;
        public const long GuestStateBytes = 0xC8;
        public const uint GuestInWorld = 1, GuestScreenOpen = 2, GuestOnGround = 4, GuestDead = 32;
        public const uint GuestBlocking = 1 << 8; // MINEKILL's own: Steve's shield is up

        // A peer whose heartbeat is older than this is treated as gone.
        public const long PeerTimeoutMs = 3000;

        // Overlay triple buffer (guest -> host): Minecraft's hand + HUD, RGBA8.
        // state bits 0-1 = middle slot, bit 2 = middle holds an unread frame. Writer: xchg(state,
        // back | Dirty), keep the returned index as its new back. Reader: only if Dirty, xchg(state,
        // front), keep the returned index as its new front.
        public const long OffOverlayCtl = 0x300, OcState = 0x00, OcFramesPublished = 0x08;
        public const long OffOverlaySlotHdr = 0x340, SlotHdrBytes = 0x40;
        public const long ShWidth = 0x00, ShHeight = 0x04, ShFlags = 0x08, ShFrameId = 0x10;
        public const uint OverlayDirty = 1u << 2, OverlayBottomUp = 1;
        public const int MaxOverlayW = 3840, MaxOverlayH = 2160;

        // Input ring (host -> guest): 16-byte events {u16 type, u16 code, i32 a, b, c}.
        public const long OffInputRing = 0x1000;
        public const int InputRingEntries = 4096;
        public const long InputEventBytes = 16;
        public const ushort InKey = 1, InMouseButton = 2, InScroll = 3, InReleaseAll = 6, InHurt = 7;
        public const ushort HurtMelee = 0, HurtProjectile = 1, HurtMagic = 2, HurtOther = 3;

        // Ring header shared by the input and event rings.
        public const long RingHead = 0x00, RingTail = 0x40, RingData = 0x80;

        // Actor table (host -> guest, seqlock): enemies near V1, mirrored in Minecraft.
        public const long OffActorTable = 0x12000, AtSeq = 0x00, AtCount = 0x04, AtRecords = 0x40, ActorRecordBytes = 64;
        public const int MaxActors = 256;
        public const long ArId = 0, ArFlags = 4, ArX = 8, ArY = 12, ArZ = 16, ArYaw = 20, ArWidth = 24, ArHeight = 28,
            ArHealthFrac = 32, ArLevel = 36, ArName = 40, ArNameBytes = 24;
        public const uint ActorHostile = 1, ActorDead = 2;

        // Render ring (guest -> host): a byte ring at OffRenderRing; head/tail = total bytes written/
        // consumed. Messages start 8-byte aligned with {u32 type, u32 payloadBytes}; type 0 (pad)
        // means "skip to the start of the ring". SkyCraft's own types are 1..11; MINEKILL's are 100+.
        public const long RenRingDataBytes = RenderRingBytes - RingData;
        public const uint RenPad = 0;
        public const uint RenBlocks = 100;   // u32 count, then count x {i32 x, y, z, u32 texSet}
        public const uint RenTexSet = 101;   // u32 id, u32 flags, u32 tint RGB, u32 nameLen, name, 3 x {u32 len, PNG} (top, side, bottom)
        public const uint RenTnt = 102;      // u32 count, then count x {u32 id, f32 x, y, z, i32 fuseTicks}
        public const uint TexTintTop = 1, TexTintSides = 2, TexTintAll = 4, TexCutout = 8;

        // Input commands beyond SkyCraft's (MINEKILL's own, 100+).
        // InUse: code = action | face << 8; a, b, c = Minecraft block. Actions: 0 release, 1 press
        // with nothing targeted, 2 press on ULTRAKILL geometry (a, b, c = the empty block to fill),
        // 3 press on a Minecraft block (a, b, c = that block, face = the face hit).
        public const ushort InUse = 100, InBreak = 101;
        // InHeal: a = Minecraft health * 100 (ULTRAKILL's blood heals Steve too).
        // InLoot: code = Loot* item, a = count, b = experience points.
        // InExplode: a, b, c = Minecraft centre * 100, code = radius in blocks * 10.
        public const ushort InHeal = 102, InLoot = 103, InExplode = 104;
        public const ushort LootArrow = 1, LootTnt = 2, LootGunpowder = 3, LootEnderPearl = 4, LootSnowball = 5, LootRottenFlesh = 6,
            LootIronIngot = 7, LootDiamond = 8, LootGoldenApple = 9, LootEnchantedGoldenApple = 10, LootCookedBeef = 11;
        public const uint HostAirborne = 8, HostFalling = 16, HostSprinting = 32, HostSneaking = 64; // HostSprinting: V1 sprints under Minecraft's movement (FOV) // HostState flags: V1 off the ground / coming down (Minecraft's crits)

        // More render-ring messages: thrown projectiles and item textures.
        public const uint RenThrow = 103;   // u32 kind, f32 x, y, z, vx, vy, vz (blocks, blocks/tick), f32 damage, u32 flags
        public const uint RenItemTex = 104; // u32 nameLen, name, u32 pngLen, PNG
        public const uint ThrowArrow = 1, ThrowSnowball = 2, ThrowEgg = 3, ThrowEnderPearl = 4;
        public const uint ThrowCrit = 1;
        public const ushort UseRelease = 0, UsePressAir = 1, UsePressLevel = 2, UsePressBlock = 3;

        // Minecraft's screens (inventory...) driven from ULTRAKILL. InKey carries an SDL scancode
        // (Minecraft 26.3's key codes: E = 8, Esc = 41, 1-9 = 30-38), a = 1 press / 0 release, b = SDL
        // modifiers. InCursor: a, b = cursor over the overlay in 1/10000ths of its width/height,
        // origin top-left. InScreenButton: code = SDL mouse button (1 left, 2 middle, 3 right),
        // a = 1 press / 0 release, b = modifiers. InFall: a = Minecraft fall damage * 100.
        public const ushort InCursor = 105, InScreenButton = 106, InFall = 107;
        public const ushort ScanE = 8, ScanF = 9, ScanQ = 20, ScanEscape = 41, Scan1 = 30;
        public const int CursorScale = 10000;
        public const int ModShift = 0x0003, ModCtrl = 0x00C0;

        // Event ring (guest -> host): 32-byte events {u32 type, u32 id, f32 a, b, c, d, u32 flags, u32 weapon}.
        public const long OffEventRing = 0x17000;
        public const int EventRingEntries = 512;
        public const long EventBytes = 32;
        public const uint EvHitActor = 1, EvPlayerDied = 2, EvExplosion = 3; // explosion: a, b, c = centre (MC), d = power
        public const uint EvParry = 110; // Steve's shield went up just in time: ULTRAKILL's parry
        public const uint HitCritical = 1, HitProjectile = 2, HitSweep = 4, HitFire = 8, HitExplosion = 1 << 8; // 1 << 8: MINEKILL's own
        public const uint WeaponUnarmed = 0, WeaponBlade = 1, WeaponAxe = 2, WeaponBlunt = 3, WeaponPierce = 4, WeaponArrow = 5;
    }
}
