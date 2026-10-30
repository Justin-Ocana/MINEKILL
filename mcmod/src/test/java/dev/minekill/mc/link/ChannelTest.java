package dev.minekill.mc.link;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.List;
import java.util.UUID;
import org.junit.jupiter.api.Test;

class ChannelTest {
	private static String uniqueName() {
		return "Local\\MinekillJavaTest_" + UUID.randomUUID().toString().replace("-", "");
	}

	@Test
	void offsetsMatchCSharpSide() {
		assertEquals(0x300, Proto.OFF_OVERLAY_CTL);
		assertEquals(0x340, Proto.OFF_OVERLAY_SLOT_HDR);
		assertEquals(0x1000, Proto.OFF_INPUT_RING);
		assertEquals(0x12000, Proto.OFF_ACTOR_TABLE);
		assertEquals(0x17000, Proto.OFF_EVENT_RING);
		assertEquals(40, Proto.AR_NAME);
	}

	// Same literals as Minekill.Link.Tests ChannelTests.MinekillNumbersMatchJava.
	@Test
	void minekillNumbersMatchCSharp() {
		assertEquals(100, Proto.IN_USE);
		assertEquals(101, Proto.IN_BREAK);
		assertEquals(102, Proto.IN_HEAL);
		assertEquals(103, Proto.IN_LOOT);
		assertEquals(104, Proto.IN_EXPLODE);
		assertEquals(100, Proto.REN_BLOCKS);
		assertEquals(101, Proto.REN_TEXSET);
		assertEquals(102, Proto.REN_TNT);
		assertEquals(103, Proto.REN_THROW);
		assertEquals(104, Proto.REN_ITEMTEX);
		assertEquals(110, Proto.EV_PARRY);
		assertEquals(256, Proto.HIT_EXPLOSION);
		assertEquals(8, Proto.HOST_AIRBORNE);
		assertEquals(16, Proto.HOST_FALLING);
		assertEquals(32, Proto.HOST_SPRINTING);
		assertEquals(64, Proto.HOST_SNEAKING);
		assertEquals(1, Proto.THROW_ARROW);
		assertEquals(4, Proto.THROW_ENDER_PEARL);
		assertEquals(11, Proto.LOOT_COOKED_BEEF);
		assertEquals(256, Proto.GUEST_BLOCKING);
		assertEquals(105, Proto.IN_CURSOR);
		assertEquals(106, Proto.IN_SCREEN_BUTTON);
		assertEquals(107, Proto.IN_FALL);
		assertEquals(10000, Proto.CURSOR_SCALE);
	}

	// The key, button and modifier codes ULTRAKILL sends are Minecraft 26.3's own (SDL) ones.
	@Test
	void inputCodesAreMinecrafts() {
		assertEquals(com.mojang.blaze3d.platform.InputConstants.KEY_E, Proto.SCAN_E);
		assertEquals(com.mojang.blaze3d.platform.InputConstants.KEY_F, Proto.SCAN_F);
		assertEquals(com.mojang.blaze3d.platform.InputConstants.KEY_Q, Proto.SCAN_Q);
		assertEquals(com.mojang.blaze3d.platform.InputConstants.KEY_ESCAPE, Proto.SCAN_ESCAPE);
		assertEquals(com.mojang.blaze3d.platform.InputConstants.KEY_1, Proto.SCAN_1);
		assertEquals(com.mojang.blaze3d.platform.InputConstants.MOD_SHIFT, Proto.MOD_SHIFT);
		assertEquals(com.mojang.blaze3d.platform.InputConstants.MOD_CONTROL, Proto.MOD_CTRL);
		assertEquals(com.mojang.blaze3d.platform.InputConstants.MOUSE_BUTTON_LEFT, 1);
		assertEquals(com.mojang.blaze3d.platform.InputConstants.MOUSE_BUTTON_RIGHT, 3);
	}

	@Test
	void overlayPublishDeliversNewest() {
		String name = uniqueName();
		try (Link host = Link.createHostForTest(name); Link guest = Link.openGuest(name, new StringBuilder())) {
			assertNull(host.acquireOverlayForTest());
			for (int f = 1; f <= 2; f++) {
				guest.overlayBackSlot(4 * 2 * 4).fill((byte) (f * 10));
				guest.publishOverlay(4, 2, true, f);
			}
			long[] got = host.acquireOverlayForTest();
			assertNotNull(got);
			assertEquals(4, got[0]);
			assertEquals(2, got[1]);
			assertEquals(Proto.OVERLAY_BOTTOM_UP, got[2]);
			assertEquals(2, got[3]);
			assertEquals(20, got[4]); // first pixel byte of the newest frame
			assertNull(host.acquireOverlayForTest());
		}
	}

	@Test
	void inputRingFifo() {
		String name = uniqueName();
		try (Link host = Link.createHostForTest(name); Link guest = Link.openGuest(name, new StringBuilder())) {
			assertNull(guest.pollInput());
			for (int i = 0; i < 5; i++) {
				assertTrue(host.pushInputForTest(new Records.InputEvent(Proto.IN_KEY, 30 + i, 1, -i, i * 3)));
			}
			for (int i = 0; i < 5; i++) {
				var e = guest.pollInput();
				assertNotNull(e);
				assertEquals(30 + i, e.code());
				assertEquals(-i, e.b());
			}
			assertNull(guest.pollInput());
			// 0xFFFF codes survive (u16, not sign-extended).
			host.pushInputForTest(new Records.InputEvent(Proto.IN_KEY, 0xFFFF, 0, 0, 0));
			assertEquals(0xFFFF, guest.pollInput().code());
		}
	}

	@Test
	void renderRingRoundTripAndWrap() {
		String name = uniqueName();
		try (Link host = Link.createHostForTest(name); Link guest = Link.openGuest(name, new StringBuilder())) {
			assertNull(host.popRenderMessageForTest());
			assertTrue(guest.pushRenderMessage(Proto.REN_BLOCKS, new byte[] {1, 2, 3}));
			Object[] m = host.popRenderMessageForTest();
			assertEquals(Proto.REN_BLOCKS, m[0]);
			assertEquals(3, ((byte[]) m[1]).length);
			assertEquals(3, ((byte[]) m[1])[2]);
			// ~200 MB through the 64 MB ring in 1 MB messages: every wrap must come out intact.
			byte[] big = new byte[(1 << 20) + 13];
			int sent = 0, got = 0;
			while (got < 200) {
				while (sent - got < 40) {
					big[0] = (byte) sent;
					if (!guest.pushRenderMessage(Proto.REN_TEXSET, big)) {
						break;
					}
					sent++;
				}
				Object[] r = host.popRenderMessageForTest();
				assertNotNull(r);
				assertEquals((byte) got, ((byte[]) r[1])[0]);
				assertEquals(big.length, ((byte[]) r[1]).length);
				got++;
			}
		}
	}

	@Test
	void eventRingAndActorTable() {
		String name = uniqueName();
		try (Link host = Link.createHostForTest(name); Link guest = Link.openGuest(name, new StringBuilder())) {
			assertTrue(guest.pushEvent(new Records.McEvent(Proto.EV_HIT_ACTOR, 42, 7f, 0.5f, -0.5f, 0.4f, Proto.HIT_CRITICAL, Proto.WEAPON_BLADE)));
			var e = host.popEventForTest();
			assertNotNull(e);
			assertEquals(42, e.id());
			assertEquals(7f, e.a());
			assertEquals(Proto.WEAPON_BLADE, e.weapon());
			assertNull(host.popEventForTest());

			host.writeActorsForTest(List.of(new Records.Actor(7, Proto.ACTOR_HOSTILE, 1, 2, 3, 90, 0.8f, 1.9f, 0.5f, "Filth")));
			var actors = guest.readActors();
			assertNotNull(actors);
			assertEquals(1, actors.size());
			assertEquals("Filth", actors.get(0).name());
			assertEquals(1.9f, actors.get(0).height());
			assertFalse(actors.get(0).dead());
		}
	}
}
