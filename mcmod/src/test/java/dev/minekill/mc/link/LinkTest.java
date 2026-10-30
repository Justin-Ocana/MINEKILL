package dev.minekill.mc.link;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.UUID;
import java.util.concurrent.atomic.AtomicBoolean;
import org.junit.jupiter.api.Test;

class LinkTest {
	private static String uniqueName() {
		return "Local\\MinekillJavaTest_" + UUID.randomUUID().toString().replace("-", "");
	}

	// Expected values transcribed from SkyCraft's protocol/skycraft_protocol.h (v11), the same
	// numbers bridge/Minekill.Link.Tests checks on the C# side.
	@Test
	void offsetsMatchProtocolV11() {
		assertEquals(0x43594B53, Proto.MAGIC);
		assertEquals(11, Proto.VERSION);
		assertEquals(0x100, Proto.OFF_HOST_STATE);
		assertEquals(0x200, Proto.OFF_GUEST_STATE);
		assertEquals(0x10, Proto.HS_POS_X);
		assertEquals(0x28, Proto.HS_YAW);
		assertEquals(0x30, Proto.HS_TELEPORT_SEQ);
		assertEquals(0x08, Proto.GS_X);
		assertEquals(0x28, Proto.GS_EYE_HEIGHT);
		assertEquals(0x30, Proto.GS_TELEPORT_ACK);
		assertEquals(0x38, Proto.GS_FRAME_COUNTER);
		assertEquals(0x40, Proto.GS_FOV);
		assertEquals(0xC0, Proto.GS_CAMERA_MODE);
		assertEquals(0x20000L + (32L << 20) + 3840L * 2160 * 4 * 3 + (64L << 20), Proto.MAPPING_BYTES);
	}

	@Test
	void guestCannotOpenMissingMapping() {
		StringBuilder why = new StringBuilder();
		assertNull(Link.openGuest(uniqueName(), why));
		assertTrue(why.toString().contains("no mapping"), why.toString());
	}

	@Test
	void roundTripAndRawOffsets() {
		String name = uniqueName();
		try (Link host = Link.createHostForTest(name)) {
			StringBuilder why = new StringBuilder();
			Link guest = Link.openGuest(name, why);
			assertNotNull(guest, why.toString());
			try (guest) {
				assertTrue(guest.peerAlive());
				assertTrue(host.peerAlive());
				assertEquals(Link.currentPid(), host.peerPid());

				HostState hs = new HostState();
				hs.flags = Proto.HOST_IN_GAME;
				hs.x = 1.25;
				hs.y = 64.5;
				hs.z = -3.75;
				hs.yaw = 90f;
				hs.teleportSeq = 7;
				host.writeHostStateForTest(hs);
				HostState r = new HostState();
				assertTrue(guest.readHostState(r));
				assertEquals(1.25, r.x);
				assertEquals(64.5, r.y);
				assertEquals(-3.75, r.z);
				assertEquals(90f, r.yaw);
				assertEquals(7, r.teleportSeq);
				assertTrue(r.inGame());

				GuestState gs = new GuestState();
				gs.flags = Proto.GUEST_IN_WORLD;
				gs.x = 10;
				gs.y = 20;
				gs.z = 30;
				gs.teleportAck = 7;
				gs.frameCounter = 99;
				guest.writeGuestState(gs);
				// Read back through raw offsets, as the C# host does.
				assertEquals(10.0, host.debugReadF64(Proto.OFF_GUEST_STATE + Proto.GS_X));
				assertEquals(30.0, host.debugReadF64(Proto.OFF_GUEST_STATE + Proto.GS_Z));
				assertEquals(7, host.debugReadI32(Proto.OFF_GUEST_STATE + Proto.GS_TELEPORT_ACK));
				assertEquals(Proto.GUEST_IN_WORLD, host.debugReadI32(Proto.OFF_GUEST_STATE + Proto.GS_FLAGS));
				int seq = host.debugReadI32(Proto.OFF_GUEST_STATE + Proto.GS_SEQ);
				assertEquals(0, seq & 1, "seqlock left odd after a write");
				assertThrows(IllegalStateException.class, () -> host.writeGuestState(gs));
			}
		}
	}

	@Test
	void seqlockNeverReturnsTornState() throws Exception {
		String name = uniqueName();
		try (Link host = Link.createHostForTest(name); Link guest = Link.openGuest(name, new StringBuilder())) {
			AtomicBoolean stop = new AtomicBoolean();
			Thread writer = new Thread(() -> {
				HostState s = new HostState();
				double i = 0;
				while (!stop.get()) {
					i++;
					s.x = i;
					s.y = i;
					s.z = i;
					host.writeHostStateForTest(s);
				}
			});
			writer.start();
			HostState r = new HostState();
			int ok = 0;
			long end = System.currentTimeMillis() + 1500;
			while (System.currentTimeMillis() < end) {
				if (guest.readHostState(r)) {
					ok++;
					assertTrue(r.x == r.y && r.y == r.z, "torn read accepted: " + r.x + " " + r.y + " " + r.z);
				}
			}
			stop.set(true);
			writer.join();
			assertTrue(ok > 1000, "only " + ok + " successful reads");
		}
	}

	@Test
	void incompatibleVersionIsRejected() {
		String name = uniqueName();
		try (Link host = Link.createHostForTest(name)) {
			host.debugWriteI32(Proto.OFF_HEADER + Proto.H_VERSION, Proto.VERSION + 1);
			StringBuilder why = new StringBuilder();
			assertNull(Link.openGuest(name, why));
			assertTrue(why.toString().contains("incompatible"), why.toString());

			host.debugWriteI32(Proto.OFF_HEADER + Proto.H_VERSION, Proto.VERSION);
			host.debugWriteI32(Proto.OFF_HEADER + Proto.H_MAGIC, 0); // host still starting: magic not published
			StringBuilder why2 = new StringBuilder();
			assertNull(Link.openGuest(name, why2));
			assertTrue(why2.toString().contains("incompatible"), why2.toString());
		}
	}
}
