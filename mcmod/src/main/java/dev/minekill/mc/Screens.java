package dev.minekill.mc;

import dev.minekill.mc.link.Proto;
import dev.minekill.mc.link.Records;
import net.minecraft.client.Minecraft;
import net.minecraft.client.input.KeyEvent;
import net.minecraft.client.input.MouseButtonInfo;
import org.lwjgl.sdl.SDLKeyboard;

/**
 * Minecraft's screens (the inventory first) used from inside ULTRAKILL. ULTRAKILL still owns the
 * keyboard and mouse; while a Minecraft screen is open it frees its cursor and sends here the keys,
 * the cursor position over the overlay and the clicks, which go into Minecraft's own keyboard and
 * mouse handlers, so every screen behaves as in Minecraft (E and Esc close, 1-9 and F swap the
 * hovered slot, shift-click moves a stack, Q drops...). Client thread only.
 */
final class Screens {
	private static double lastX = -1, lastY = -1;
	private static String lastScreen = "";
	private static String lastCarried = "";

	private Screens() {
	}

	/** True if the event was a screen/keyboard event and has been handled here. */
	static boolean apply(Minecraft mc, Records.InputEvent e) {
		long handle = mc.getWindow().handle();
		switch (e.type()) {
			case Proto.IN_KEY -> {
				int scancode = e.code();
				boolean open = mc.gui.screen() != null;
				// With no screen open, only the inventory key goes in (Esc would open the pause menu,
				// the rest are ULTRAKILL's); hotbar keys are handled as slot commands by the caller.
				if (!open && scancode != Proto.SCAN_E) {
					return false;
				}
				int mods = e.b();
				int keycode = SDLKeyboard.SDL_GetKeyFromScancode(scancode, (short) mods, true);
				mc.keyboardHandler.keyPress(handle, e.a() != 0 ? 1 : 0, new KeyEvent(scancode, keycode, mods));
				return true;
			}
			case Proto.IN_CURSOR -> {
				var w = mc.getWindow();
				double x = e.a() / (double) Proto.CURSOR_SCALE * w.getScreenWidth();
				double y = e.b() / (double) Proto.CURSOR_SCALE * w.getScreenHeight();
				double dx = lastX < 0 ? 0 : x - lastX, dy = lastY < 0 ? 0 : y - lastY;
				lastX = x;
				lastY = y;
				mc.mouseHandler.onMove(handle, x, y, dx, dy);
				return true;
			}
			case Proto.IN_SCREEN_BUTTON -> {
				if (mc.gui.screen() != null) {
					mc.mouseHandler.onButton(handle, new MouseButtonInfo(e.code(), e.b()), e.a() != 0 ? 1 : 0);
				}
				return true;
			}
			default -> {
				return false;
			}
		}
	}

	/** Logs screen changes and what the cursor carries (the evidence the inventory really works). */
	static void report(Minecraft mc) {
		var screen = mc.gui.screen();
		String name = screen == null ? "none" : screen.getClass().getSimpleName();
		if (!name.equals(lastScreen)) {
			lastScreen = name;
			MinekillClient.LOG.info("MINEKILL: Minecraft screen -> {}", name);
		}
		var player = mc.player;
		String carried = player == null || player.containerMenu == null || player.containerMenu.getCarried().isEmpty() ? "" : player.containerMenu.getCarried().toString();
		if (!carried.equals(lastCarried)) {
			lastCarried = carried;
			MinekillClient.LOG.info("MINEKILL: cursor carries {}", carried.isEmpty() ? "nothing" : carried);
		}
	}
}
