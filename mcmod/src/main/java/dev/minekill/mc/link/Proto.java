package dev.minekill.mc.link;

/**
 * Byte layout of the link: SkyCraft's protocol v11 (protocol/skycraft_protocol.h, MIT), the same
 * offsets as bridge/Minekill.Link/Proto.cs on the ULTRAKILL side. Little-endian. Only what
 * MINEKILL uses so far is named here.
 */
public final class Proto {
	private Proto() {
	}

	public static final int MAGIC = 0x43594B53; // "SKYC"
	public static final int VERSION = 11;
	public static final String DEFAULT_MAPPING_NAME = "Local\\Minekill_v1";

	public static final long OFF_HEADER = 0x0;
	public static final long OFF_HOST_STATE = 0x100;
	public static final long OFF_GUEST_STATE = 0x200;
	public static final long OFF_COLLISION_RING = 0x20000;
	public static final long COLLISION_RING_BYTES = 32L << 20;
	public static final long OFF_OVERLAY_PIXELS = OFF_COLLISION_RING + COLLISION_RING_BYTES;
	public static final long OVERLAY_SLOT_BYTES = 3840L * 2160 * 4;
	public static final long OFF_RENDER_RING = OFF_OVERLAY_PIXELS + OVERLAY_SLOT_BYTES * 3;
	public static final long MAPPING_BYTES = OFF_RENDER_RING + (64L << 20);

	// Header
	public static final long H_MAGIC = 0x00, H_VERSION = 0x04, H_HOST_PID = 0x08, H_GUEST_PID = 0x0C,
		H_HOST_BEAT_MS = 0x10, H_GUEST_BEAT_MS = 0x18;

	// Host -> guest (SkyState), seqlock
	public static final long HS_SEQ = 0x00, HS_FLAGS = 0x04, HS_WORLD_ID = 0x08, HS_EPOCH = 0x0C,
		HS_POS_X = 0x10, HS_POS_Y = 0x18, HS_POS_Z = 0x20, HS_YAW = 0x28, HS_PITCH = 0x2C,
		HS_TELEPORT_SEQ = 0x30, HS_VIEWPORT_W = 0x34, HS_VIEWPORT_H = 0x38, HS_GAME_HOUR = 0x3C;
	public static final int HOST_IN_GAME = 1, HOST_MENU_OPEN = 2, HOST_LOADING = 4;

	// Guest -> host (McState), seqlock
	public static final long GS_SEQ = 0x00, GS_FLAGS = 0x04, GS_X = 0x08, GS_Y = 0x10, GS_Z = 0x18,
		GS_YAW = 0x20, GS_PITCH = 0x24, GS_EYE_HEIGHT = 0x28, GS_TELEPORT_ACK = 0x30,
		GS_FRAME_COUNTER = 0x38, GS_FOV = 0x40, GS_CAMERA_MODE = 0xC0;
	public static final int GUEST_IN_WORLD = 1, GUEST_SCREEN_OPEN = 2, GUEST_ON_GROUND = 4, GUEST_DEAD = 32;
	public static final int GUEST_BLOCKING = 1 << 8; // MINEKILL's own: Steve's shield is up

	public static final long PEER_TIMEOUT_MS = 3000;

	// Overlay triple buffer (Minecraft writes hand + HUD, ULTRAKILL reads). Writer starts with back 1.
	public static final long OFF_OVERLAY_CTL = 0x300, OC_STATE = 0x00, OC_FRAMES_PUBLISHED = 0x08;
	public static final long OFF_OVERLAY_SLOT_HDR = 0x340, SLOT_HDR_BYTES = 0x40;
	public static final long SH_WIDTH = 0x00, SH_HEIGHT = 0x04, SH_FLAGS = 0x08, SH_FRAME_ID = 0x10;
	public static final int OVERLAY_DIRTY = 1 << 2, OVERLAY_BOTTOM_UP = 1;
	public static final int MAX_OVERLAY_W = 3840, MAX_OVERLAY_H = 2160;

	// Rings: head = entries written, tail = entries consumed.
	public static final long RING_HEAD = 0x00, RING_TAIL = 0x40, RING_DATA = 0x80;

	// Input ring (ULTRAKILL -> Minecraft), 16-byte events {u16 type, u16 code, i32 a, b, c}.
	public static final long OFF_INPUT_RING = 0x1000;
	public static final int INPUT_RING_ENTRIES = 4096;
	public static final long INPUT_EVENT_BYTES = 16;
	public static final int IN_KEY = 1, IN_MOUSE_BUTTON = 2, IN_SCROLL = 3, IN_RELEASE_ALL = 6, IN_HURT = 7;

	// Actor table (ULTRAKILL -> Minecraft, seqlock).
	public static final long OFF_ACTOR_TABLE = 0x12000, AT_SEQ = 0x00, AT_COUNT = 0x04, AT_RECORDS = 0x40, ACTOR_RECORD_BYTES = 64;
	public static final int MAX_ACTORS = 256;
	public static final long AR_ID = 0, AR_FLAGS = 4, AR_X = 8, AR_Y = 12, AR_Z = 16, AR_YAW = 20, AR_WIDTH = 24, AR_HEIGHT = 28,
		AR_HEALTH_FRAC = 32, AR_NAME = 40, AR_NAME_BYTES = 24;
	public static final int ACTOR_HOSTILE = 1, ACTOR_DEAD = 2;

	// Render ring (Minecraft -> ULTRAKILL): byte ring at OFF_RENDER_RING, head/tail = total bytes;
	// 8-aligned messages {u32 type, u32 payloadBytes}; type 0 = pad to the ring's start.
	public static final long REN_RING_DATA_BYTES = (64L << 20) - RING_DATA;
	public static final int REN_PAD = 0, REN_BLOCKS = 100, REN_TEXSET = 101, REN_TNT = 102;
	public static final int TEX_TINT_TOP = 1, TEX_TINT_SIDES = 2, TEX_TINT_ALL = 4, TEX_CUTOUT = 8;

	// MINEKILL's own input commands (see Proto.cs): use (right button) and break.
	public static final int IN_USE = 100, IN_BREAK = 101;
	public static final int IN_HEAL = 102, IN_LOOT = 103, IN_EXPLODE = 104;
	/** Minecraft's screens driven from ULTRAKILL; see Proto.cs. Keys are SDL scancodes (InputConstants.KEY_*). */
	public static final int IN_CURSOR = 105, IN_SCREEN_BUTTON = 106, IN_FALL = 107;
	public static final int SCAN_E = 8, SCAN_F = 9, SCAN_Q = 20, SCAN_ESCAPE = 41, SCAN_1 = 30;
	public static final int CURSOR_SCALE = 10000;
	public static final int MOD_SHIFT = 0x0003, MOD_CTRL = 0x00C0;
	public static final int LOOT_ARROW = 1, LOOT_TNT = 2, LOOT_GUNPOWDER = 3, LOOT_ENDER_PEARL = 4, LOOT_SNOWBALL = 5, LOOT_ROTTEN_FLESH = 6,
		LOOT_IRON_INGOT = 7, LOOT_DIAMOND = 8, LOOT_GOLDEN_APPLE = 9, LOOT_ENCHANTED_GOLDEN_APPLE = 10, LOOT_COOKED_BEEF = 11;
	public static final int HOST_AIRBORNE = 8, HOST_FALLING = 16, HOST_SPRINTING = 32, HOST_SNEAKING = 64;
	public static final int REN_THROW = 103, REN_ITEMTEX = 104;
	public static final int THROW_ARROW = 1, THROW_SNOWBALL = 2, THROW_EGG = 3, THROW_ENDER_PEARL = 4;
	public static final int THROW_CRIT = 1;
	public static final int EV_PARRY = 110;
	public static final int USE_RELEASE = 0, USE_PRESS_AIR = 1, USE_PRESS_LEVEL = 2, USE_PRESS_BLOCK = 3;

	// Event ring (Minecraft -> ULTRAKILL), 32-byte events {u32 type, u32 id, f32 a, b, c, d, u32 flags, u32 weapon}.
	public static final long OFF_EVENT_RING = 0x17000;
	public static final int EVENT_RING_ENTRIES = 512;
	public static final long EVENT_BYTES = 32;
	public static final int EV_HIT_ACTOR = 1, EV_PLAYER_DIED = 2, EV_EXPLOSION = 3;
	public static final int HIT_CRITICAL = 1, HIT_SWEEP = 4, HIT_EXPLOSION = 1 << 8; // 1 << 8: MINEKILL's own
	public static final int WEAPON_UNARMED = 0, WEAPON_BLADE = 1, WEAPON_AXE = 2, WEAPON_BLUNT = 3, WEAPON_PIERCE = 4;
}
