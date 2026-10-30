package dev.minekill.mc.link;

import static java.lang.foreign.ValueLayout.ADDRESS;
import static java.lang.foreign.ValueLayout.JAVA_DOUBLE_UNALIGNED;
import static java.lang.foreign.ValueLayout.JAVA_FLOAT_UNALIGNED;
import static java.lang.foreign.ValueLayout.JAVA_INT;
import static java.lang.foreign.ValueLayout.JAVA_INT_UNALIGNED;
import static java.lang.foreign.ValueLayout.JAVA_LONG;
import static java.lang.foreign.ValueLayout.JAVA_LONG_UNALIGNED;

import java.lang.foreign.Arena;
import java.lang.foreign.FunctionDescriptor;
import java.lang.foreign.Linker;
import java.lang.foreign.MemorySegment;
import java.lang.foreign.SymbolLookup;
import java.lang.invoke.MethodHandle;
import java.lang.invoke.VarHandle;
import java.nio.charset.StandardCharsets;

/**
 * Minecraft's end of the link: opens the Win32 named file mapping ULTRAKILL creates (Java's own
 * file APIs can't open a named, pagefile-backed mapping, so this calls kernel32 through the
 * Foreign Function & Memory API). Use from one thread.
 */
public final class Link implements AutoCloseable {
	private static final int FILE_MAP_ALL_ACCESS = 0xF001F;
	private static final int PAGE_READWRITE = 0x04;

	private static final MethodHandle OPEN_FILE_MAPPING, CREATE_FILE_MAPPING, MAP_VIEW_OF_FILE, UNMAP_VIEW_OF_FILE,
		CLOSE_HANDLE, GET_TICK_COUNT64, GET_CURRENT_PROCESS_ID;

	static {
		Linker linker = Linker.nativeLinker();
		SymbolLookup k32 = SymbolLookup.libraryLookup("kernel32", Arena.global());
		OPEN_FILE_MAPPING = linker.downcallHandle(k32.find("OpenFileMappingW").orElseThrow(),
			FunctionDescriptor.of(ADDRESS, JAVA_INT, JAVA_INT, ADDRESS));
		CREATE_FILE_MAPPING = linker.downcallHandle(k32.find("CreateFileMappingW").orElseThrow(),
			FunctionDescriptor.of(ADDRESS, ADDRESS, ADDRESS, JAVA_INT, JAVA_INT, JAVA_INT, ADDRESS));
		MAP_VIEW_OF_FILE = linker.downcallHandle(k32.find("MapViewOfFile").orElseThrow(),
			FunctionDescriptor.of(ADDRESS, ADDRESS, JAVA_INT, JAVA_INT, JAVA_INT, JAVA_LONG));
		UNMAP_VIEW_OF_FILE = linker.downcallHandle(k32.find("UnmapViewOfFile").orElseThrow(),
			FunctionDescriptor.of(JAVA_INT, ADDRESS));
		CLOSE_HANDLE = linker.downcallHandle(k32.find("CloseHandle").orElseThrow(), FunctionDescriptor.of(JAVA_INT, ADDRESS));
		GET_TICK_COUNT64 = linker.downcallHandle(k32.find("GetTickCount64").orElseThrow(), FunctionDescriptor.of(JAVA_LONG));
		GET_CURRENT_PROCESS_ID = linker.downcallHandle(k32.find("GetCurrentProcessId").orElseThrow(), FunctionDescriptor.of(JAVA_INT));
	}

	private final MemorySegment handle;
	private final MemorySegment view;
	private final boolean host;

	private Link(MemorySegment handle, MemorySegment view, boolean host) {
		this.handle = handle;
		this.view = view;
		this.host = host;
	}

	public static long tickCountMs() {
		try {
			return (long) GET_TICK_COUNT64.invokeExact();
		} catch (Throwable t) {
			throw new IllegalStateException(t);
		}
	}

	public static int currentPid() {
		try {
			return (int) GET_CURRENT_PROCESS_ID.invokeExact();
		} catch (Throwable t) {
			throw new IllegalStateException(t);
		}
	}

	/** Opens the host's mapping; null if it doesn't exist yet or speaks another protocol version. */
	public static Link openGuest(String name, StringBuilder whyNot) {
		try (Arena arena = Arena.ofConfined()) {
			MemorySegment h = (MemorySegment) OPEN_FILE_MAPPING.invokeExact(FILE_MAP_ALL_ACCESS, 0, arena.allocateFrom(name, StandardCharsets.UTF_16LE));
			if (h.address() == 0) {
				whyNot.append("no mapping named ").append(name).append(" (is ULTRAKILL running with MINEKILL?)");
				return null;
			}
			Link link = map(h, false, whyNot);
			if (link == null) {
				return null;
			}
			int magic = link.i32Volatile(Proto.OFF_HEADER + Proto.H_MAGIC);
			int version = link.i32(Proto.OFF_HEADER + Proto.H_VERSION);
			if (magic != Proto.MAGIC || version != Proto.VERSION) {
				whyNot.append(String.format("incompatible mapping: magic 0x%08X version %d (expected 0x%08X, %d)", magic, version, Proto.MAGIC, Proto.VERSION));
				link.close();
				return null;
			}
			link.setI32(Proto.OFF_HEADER + Proto.H_GUEST_PID, currentPid());
			link.heartbeat();
			return link;
		} catch (Throwable t) {
			whyNot.append(t);
			return null;
		}
	}

	/** Host side, for tests and tools only (ULTRAKILL's plugin is the real host). */
	public static Link createHostForTest(String name) {
		try (Arena arena = Arena.ofConfined()) {
			long size = Proto.MAPPING_BYTES;
			MemorySegment h = (MemorySegment) CREATE_FILE_MAPPING.invokeExact(MemorySegment.ofAddress(-1L), MemorySegment.NULL, PAGE_READWRITE,
				(int) (size >>> 32), (int) size, arena.allocateFrom(name, StandardCharsets.UTF_16LE));
			StringBuilder why = new StringBuilder();
			Link link = map(h, true, why);
			if (link == null) {
				throw new IllegalStateException(why.toString());
			}
			link.setI32(Proto.OFF_HEADER + Proto.H_VERSION, Proto.VERSION);
			link.setI32(Proto.OFF_HEADER + Proto.H_HOST_PID, currentPid());
			link.heartbeat();
			link.setI32Volatile(Proto.OFF_HEADER + Proto.H_MAGIC, Proto.MAGIC);
			return link;
		} catch (RuntimeException e) {
			throw e;
		} catch (Throwable t) {
			throw new IllegalStateException(t);
		}
	}

	private static Link map(MemorySegment h, boolean host, StringBuilder whyNot) throws Throwable {
		if (h.address() == 0) {
			whyNot.append("CreateFileMapping/OpenFileMapping failed");
			return null;
		}
		MemorySegment v = (MemorySegment) MAP_VIEW_OF_FILE.invokeExact(h, FILE_MAP_ALL_ACCESS, 0, 0, Proto.MAPPING_BYTES);
		if (v.address() == 0) {
			whyNot.append("MapViewOfFile failed");
			int ignored = (int) CLOSE_HANDLE.invokeExact(h);
			return null;
		}
		return new Link(h, v.reinterpret(Proto.MAPPING_BYTES), host);
	}

	// ---- raw access ------------------------------------------------------------------------------
	private int i32(long off) {
		return view.get(JAVA_INT_UNALIGNED, off);
	}

	private void setI32(long off, int v) {
		view.set(JAVA_INT_UNALIGNED, off, v);
	}

	private int i32Volatile(long off) {
		VarHandle.acquireFence();
		return view.get(JAVA_INT_UNALIGNED, off);
	}

	private void setI32Volatile(long off, int v) {
		VarHandle.releaseFence();
		view.set(JAVA_INT_UNALIGNED, off, v);
		VarHandle.fullFence();
	}

	private long i64(long off) {
		return view.get(JAVA_LONG_UNALIGNED, off);
	}

	private void setI64(long off, long v) {
		view.set(JAVA_LONG_UNALIGNED, off, v);
	}

	private float f32(long off) {
		return view.get(JAVA_FLOAT_UNALIGNED, off);
	}

	private void setF32(long off, float v) {
		view.set(JAVA_FLOAT_UNALIGNED, off, v);
	}

	private double f64(long off) {
		return view.get(JAVA_DOUBLE_UNALIGNED, off);
	}

	private void setF64(long off, double v) {
		view.set(JAVA_DOUBLE_UNALIGNED, off, v);
	}

	// ---- heartbeat ------------------------------------------------------------------------------
	public void heartbeat() {
		VarHandle.releaseFence();
		setI64(Proto.OFF_HEADER + (host ? Proto.H_HOST_BEAT_MS : Proto.H_GUEST_BEAT_MS), tickCountMs());
	}

	public long peerBeatAgeMs() {
		VarHandle.acquireFence();
		long last = i64(Proto.OFF_HEADER + (host ? Proto.H_GUEST_BEAT_MS : Proto.H_HOST_BEAT_MS));
		return last == 0 ? Long.MAX_VALUE : tickCountMs() - last;
	}

	public boolean peerAlive() {
		return peerBeatAgeMs() < Proto.PEER_TIMEOUT_MS;
	}

	public int peerPid() {
		return i32(Proto.OFF_HEADER + (host ? Proto.H_GUEST_PID : Proto.H_HOST_PID));
	}

	// ---- seqlock --------------------------------------------------------------------------------
	private void beginWrite(long seqOff) {
		setI32Volatile(seqOff, i32(seqOff) | 1);
	}

	private void endWrite(long seqOff) {
		setI32Volatile(seqOff, (i32(seqOff) | 1) + 1);
	}

	/** Reads ULTRAKILL's state into {@code out}; false on a torn read (keep the previous value). */
	public boolean readHostState(HostState out) {
		long o = Proto.OFF_HOST_STATE;
		int before = i32Volatile(o + Proto.HS_SEQ);
		if ((before & 1) != 0) {
			return false;
		}
		int flags = i32(o + Proto.HS_FLAGS), worldId = i32(o + Proto.HS_WORLD_ID), epoch = i32(o + Proto.HS_EPOCH);
		double x = f64(o + Proto.HS_POS_X), y = f64(o + Proto.HS_POS_Y), z = f64(o + Proto.HS_POS_Z);
		float yaw = f32(o + Proto.HS_YAW), pitch = f32(o + Proto.HS_PITCH);
		int teleportSeq = i32(o + Proto.HS_TELEPORT_SEQ);
		if (i32Volatile(o + Proto.HS_SEQ) != before) {
			return false;
		}
		out.flags = flags;
		out.worldId = worldId;
		out.epoch = epoch;
		out.x = x;
		out.y = y;
		out.z = z;
		out.yaw = yaw;
		out.pitch = pitch;
		out.teleportSeq = teleportSeq;
		return true;
	}

	public void writeHostStateForTest(HostState s) {
		long o = Proto.OFF_HOST_STATE;
		beginWrite(o + Proto.HS_SEQ);
		setI32(o + Proto.HS_FLAGS, s.flags);
		setI32(o + Proto.HS_WORLD_ID, s.worldId);
		setI32(o + Proto.HS_EPOCH, s.epoch);
		setF64(o + Proto.HS_POS_X, s.x);
		setF64(o + Proto.HS_POS_Y, s.y);
		setF64(o + Proto.HS_POS_Z, s.z);
		setF32(o + Proto.HS_YAW, s.yaw);
		setF32(o + Proto.HS_PITCH, s.pitch);
		setI32(o + Proto.HS_TELEPORT_SEQ, s.teleportSeq);
		endWrite(o + Proto.HS_SEQ);
	}

	public void writeGuestState(GuestState s) {
		if (host) {
			throw new IllegalStateException("the host side can't write the guest state");
		}
		long o = Proto.OFF_GUEST_STATE;
		beginWrite(o + Proto.GS_SEQ);
		setI32(o + Proto.GS_FLAGS, s.flags);
		setF64(o + Proto.GS_X, s.x);
		setF64(o + Proto.GS_Y, s.y);
		setF64(o + Proto.GS_Z, s.z);
		setF32(o + Proto.GS_YAW, s.yaw);
		setF32(o + Proto.GS_PITCH, s.pitch);
		setF32(o + Proto.GS_EYE_HEIGHT, s.eyeHeight);
		setI32(o + Proto.GS_TELEPORT_ACK, s.teleportAck);
		setI64(o + Proto.GS_FRAME_COUNTER, s.frameCounter);
		setF32(o + Proto.GS_FOV, s.fov);
		setI32(o + Proto.GS_CAMERA_MODE, s.cameraMode);
		endWrite(o + Proto.GS_SEQ);
	}

	// ---- overlay triple buffer (this side writes) ------------------------------------------------
	private static final VarHandle INT_ATOMIC = JAVA_INT.varHandle();
	private static final VarHandle LONG_ATOMIC = JAVA_LONG.varHandle();
	private int overlayBack = 1; // writer's private slot; the reader's front starts at 2, middle at 0
	private int overlayFront = 2; // used only by the host side in tests

	/** Where to write the next overlay frame (RGBA8, width*height*4 bytes). */
	public MemorySegment overlayBackSlot(long bytes) {
		return view.asSlice(Proto.OFF_OVERLAY_PIXELS + overlayBack * Proto.OVERLAY_SLOT_BYTES, bytes);
	}

	/** Publishes the frame just written into {@link #overlayBackSlot}. */
	public void publishOverlay(int width, int height, boolean bottomUp, long frameId) {
		long hdr = Proto.OFF_OVERLAY_SLOT_HDR + overlayBack * Proto.SLOT_HDR_BYTES;
		setI32(hdr + Proto.SH_WIDTH, width);
		setI32(hdr + Proto.SH_HEIGHT, height);
		setI32(hdr + Proto.SH_FLAGS, bottomUp ? Proto.OVERLAY_BOTTOM_UP : 0);
		setI64(hdr + Proto.SH_FRAME_ID, frameId);
		int old = (int) INT_ATOMIC.getAndSet(view, Proto.OFF_OVERLAY_CTL + Proto.OC_STATE, overlayBack | Proto.OVERLAY_DIRTY);
		overlayBack = old & 3;
		long ignored = (long) LONG_ATOMIC.getAndAdd(view, Proto.OFF_OVERLAY_CTL + Proto.OC_FRAMES_PUBLISHED, 1L);
	}

	/** Host-side read for tests: {width, height, flags, frameId} of the newest frame, or null. */
	public long[] acquireOverlayForTest() {
		int state = (int) INT_ATOMIC.getVolatile(view, Proto.OFF_OVERLAY_CTL + Proto.OC_STATE);
		if ((state & Proto.OVERLAY_DIRTY) == 0) {
			return null;
		}
		int old = (int) INT_ATOMIC.getAndSet(view, Proto.OFF_OVERLAY_CTL + Proto.OC_STATE, overlayFront);
		overlayFront = old & 3;
		long hdr = Proto.OFF_OVERLAY_SLOT_HDR + overlayFront * Proto.SLOT_HDR_BYTES;
		return new long[] {i32(hdr + Proto.SH_WIDTH), i32(hdr + Proto.SH_HEIGHT), i32(hdr + Proto.SH_FLAGS), i64(hdr + Proto.SH_FRAME_ID),
			view.get(java.lang.foreign.ValueLayout.JAVA_BYTE, Proto.OFF_OVERLAY_PIXELS + overlayFront * Proto.OVERLAY_SLOT_BYTES)};
	}

	// ---- rings ----------------------------------------------------------------------------------
	private long ringSlot(long ring, int entries, long entryBytes, long index) {
		return ring + Proto.RING_DATA + (index & (entries - 1)) * entryBytes;
	}

	/** Next input event from ULTRAKILL, or null. */
	public Records.InputEvent pollInput() {
		long ring = Proto.OFF_INPUT_RING;
		long tail = i64(ring + Proto.RING_TAIL);
		VarHandle.acquireFence();
		long head = i64(ring + Proto.RING_HEAD);
		if (tail >= head) {
			return null;
		}
		if (head - tail > Proto.INPUT_RING_ENTRIES) {
			tail = head - Proto.INPUT_RING_ENTRIES;
		}
		long at = ringSlot(ring, Proto.INPUT_RING_ENTRIES, Proto.INPUT_EVENT_BYTES, tail);
		var e = new Records.InputEvent(Short.toUnsignedInt(view.get(java.lang.foreign.ValueLayout.JAVA_SHORT_UNALIGNED, at)),
			Short.toUnsignedInt(view.get(java.lang.foreign.ValueLayout.JAVA_SHORT_UNALIGNED, at + 2)), i32(at + 4), i32(at + 8), i32(at + 12));
		VarHandle.releaseFence();
		setI64(ring + Proto.RING_TAIL, tail + 1);
		return e;
	}

	/** Host-side write for tests. */
	public boolean pushInputForTest(Records.InputEvent e) {
		long ring = Proto.OFF_INPUT_RING;
		long head = i64(ring + Proto.RING_HEAD);
		if (head - i64(ring + Proto.RING_TAIL) >= Proto.INPUT_RING_ENTRIES) {
			return false;
		}
		long at = ringSlot(ring, Proto.INPUT_RING_ENTRIES, Proto.INPUT_EVENT_BYTES, head);
		view.set(java.lang.foreign.ValueLayout.JAVA_SHORT_UNALIGNED, at, (short) e.type());
		view.set(java.lang.foreign.ValueLayout.JAVA_SHORT_UNALIGNED, at + 2, (short) e.code());
		setI32(at + 4, e.a());
		setI32(at + 8, e.b());
		setI32(at + 12, e.c());
		VarHandle.releaseFence();
		setI64(ring + Proto.RING_HEAD, head + 1);
		return true;
	}

	/** Sends an event to ULTRAKILL; false if its ring is full (ULTRAKILL not reading). */
	public boolean pushEvent(Records.McEvent e) {
		long ring = Proto.OFF_EVENT_RING;
		long head = i64(ring + Proto.RING_HEAD);
		VarHandle.acquireFence();
		if (head - i64(ring + Proto.RING_TAIL) >= Proto.EVENT_RING_ENTRIES) {
			return false;
		}
		long at = ringSlot(ring, Proto.EVENT_RING_ENTRIES, Proto.EVENT_BYTES, head);
		setI32(at, e.type());
		setI32(at + 4, e.id());
		setF32(at + 8, e.a());
		setF32(at + 12, e.b());
		setF32(at + 16, e.c());
		setF32(at + 20, e.d());
		setI32(at + 24, e.flags());
		setI32(at + 28, e.weapon());
		VarHandle.releaseFence();
		setI64(ring + Proto.RING_HEAD, head + 1);
		return true;
	}

	/** Host-side read for tests. */
	public Records.McEvent popEventForTest() {
		long ring = Proto.OFF_EVENT_RING;
		long tail = i64(ring + Proto.RING_TAIL);
		VarHandle.acquireFence();
		if (tail >= i64(ring + Proto.RING_HEAD)) {
			return null;
		}
		long at = ringSlot(ring, Proto.EVENT_RING_ENTRIES, Proto.EVENT_BYTES, tail);
		var e = new Records.McEvent(i32(at), i32(at + 4), f32(at + 8), f32(at + 12), f32(at + 16), f32(at + 20), i32(at + 24), i32(at + 28));
		VarHandle.releaseFence();
		setI64(ring + Proto.RING_TAIL, tail + 1);
		return e;
	}

	// ---- render ring (this side writes) -------------------------------------------------------
	private static long align8(long n) {
		return (n + 7) & ~7L;
	}

	/** Queues a message for ULTRAKILL; false if the ring is full (ULTRAKILL not reading). */
	public boolean pushRenderMessage(int type, byte[] payload) {
		long ring = Proto.OFF_RENDER_RING, cap = Proto.REN_RING_DATA_BYTES;
		long msg = 8 + align8(payload.length);
		if (msg > cap / 2) {
			throw new IllegalArgumentException("render message too large: " + payload.length);
		}
		long head = i64(ring + Proto.RING_HEAD);
		VarHandle.acquireFence();
		long tail = i64(ring + Proto.RING_TAIL);
		long pos = head % cap;
		long pad = pos + msg > cap ? cap - pos : 0;
		if (head + pad + msg - tail > cap) {
			return false;
		}
		if (pad > 0) {
			setI32(ring + Proto.RING_DATA + pos, Proto.REN_PAD);
			setI32(ring + Proto.RING_DATA + pos + 4, (int) (pad - 8));
			head += pad;
			pos = 0;
		}
		setI32(ring + Proto.RING_DATA + pos, type);
		setI32(ring + Proto.RING_DATA + pos + 4, payload.length);
		MemorySegment.copy(payload, 0, view, java.lang.foreign.ValueLayout.JAVA_BYTE, ring + Proto.RING_DATA + pos + 8, payload.length);
		VarHandle.releaseFence();
		setI64(ring + Proto.RING_HEAD, head + msg);
		return true;
	}

	/** Host-side read for tests: {type, payload} or null. */
	public Object[] popRenderMessageForTest() {
		long ring = Proto.OFF_RENDER_RING, cap = Proto.REN_RING_DATA_BYTES;
		while (true) {
			long tail = i64(ring + Proto.RING_TAIL);
			VarHandle.acquireFence();
			if (tail >= i64(ring + Proto.RING_HEAD)) {
				return null;
			}
			long pos = tail % cap;
			int type = i32(ring + Proto.RING_DATA + pos), len = i32(ring + Proto.RING_DATA + pos + 4);
			if (type == Proto.REN_PAD) {
				setI64(ring + Proto.RING_TAIL, tail + (cap - pos));
				continue;
			}
			byte[] p = new byte[len];
			MemorySegment.copy(view, java.lang.foreign.ValueLayout.JAVA_BYTE, ring + Proto.RING_DATA + pos + 8, p, 0, len);
			setI64(ring + Proto.RING_TAIL, tail + 8 + align8(len));
			return new Object[] {type, p};
		}
	}

	// ---- actor table (seqlock) ------------------------------------------------------------------
	/** ULTRAKILL's enemies near V1; null on a torn read (keep the previous list). */
	public java.util.List<Records.Actor> readActors() {
		long t = Proto.OFF_ACTOR_TABLE;
		int before = i32Volatile(t + Proto.AT_SEQ);
		if ((before & 1) != 0) {
			return null;
		}
		int n = Math.min(Math.max(i32(t + Proto.AT_COUNT), 0), Proto.MAX_ACTORS);
		var list = new java.util.ArrayList<Records.Actor>(n);
		byte[] name = new byte[(int) Proto.AR_NAME_BYTES];
		for (int i = 0; i < n; i++) {
			long r = t + Proto.AT_RECORDS + i * Proto.ACTOR_RECORD_BYTES;
			MemorySegment.copy(view, java.lang.foreign.ValueLayout.JAVA_BYTE, r + Proto.AR_NAME, name, 0, name.length);
			int len = 0;
			while (len < name.length && name[len] != 0) {
				len++;
			}
			list.add(new Records.Actor(i32(r + Proto.AR_ID), i32(r + Proto.AR_FLAGS), f32(r + Proto.AR_X), f32(r + Proto.AR_Y), f32(r + Proto.AR_Z),
				f32(r + Proto.AR_YAW), f32(r + Proto.AR_WIDTH), f32(r + Proto.AR_HEIGHT), f32(r + Proto.AR_HEALTH_FRAC),
				new String(name, 0, len, StandardCharsets.UTF_8)));
		}
		if (i32Volatile(t + Proto.AT_SEQ) != before) {
			return null;
		}
		return list;
	}

	/** Host-side write for tests. */
	public void writeActorsForTest(java.util.List<Records.Actor> actors) {
		long t = Proto.OFF_ACTOR_TABLE;
		beginWrite(t + Proto.AT_SEQ);
		setI32(t + Proto.AT_COUNT, actors.size());
		for (int i = 0; i < actors.size(); i++) {
			long r = t + Proto.AT_RECORDS + i * Proto.ACTOR_RECORD_BYTES;
			var a = actors.get(i);
			setI32(r + Proto.AR_ID, a.id());
			setI32(r + Proto.AR_FLAGS, a.flags());
			setF32(r + Proto.AR_X, a.x());
			setF32(r + Proto.AR_Y, a.y());
			setF32(r + Proto.AR_Z, a.z());
			setF32(r + Proto.AR_YAW, a.yaw());
			setF32(r + Proto.AR_WIDTH, a.width());
			setF32(r + Proto.AR_HEIGHT, a.height());
			setF32(r + Proto.AR_HEALTH_FRAC, a.healthFrac());
			byte[] n = a.name().getBytes(StandardCharsets.UTF_8);
			view.asSlice(r + Proto.AR_NAME, Proto.AR_NAME_BYTES).fill((byte) 0);
			MemorySegment.copy(n, 0, view, java.lang.foreign.ValueLayout.JAVA_BYTE, r + Proto.AR_NAME, Math.min(n.length, (int) Proto.AR_NAME_BYTES - 1));
		}
		endWrite(t + Proto.AT_SEQ);
	}

	/** Raw read used by tests to check what was written where. */
	public double debugReadF64(long absoluteOffset) {
		return f64(absoluteOffset);
	}

	public int debugReadI32(long absoluteOffset) {
		return i32(absoluteOffset);
	}

	/** Test hook: corrupt the header to check the guest's compatibility check. */
	public void debugWriteI32(long absoluteOffset, int v) {
		setI32Volatile(absoluteOffset, v);
	}

	@Override
	public void close() {
		try {
			int a = (int) UNMAP_VIEW_OF_FILE.invokeExact(view);
			int b = (int) CLOSE_HANDLE.invokeExact(handle);
		} catch (Throwable ignored) {
		}
	}
}
