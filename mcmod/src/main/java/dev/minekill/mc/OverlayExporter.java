package dev.minekill.mc;

import com.mojang.blaze3d.pipeline.RenderTarget;
import com.mojang.blaze3d.systems.RenderSystem;
import com.mojang.renderpearl.api.buffers.GpuBuffer;
import com.mojang.renderpearl.api.buffers.GpuBufferSlice;
import com.mojang.renderpearl.api.textures.GpuTexture;
import dev.minekill.mc.link.Link;
import dev.minekill.mc.link.Proto;
import java.lang.foreign.MemorySegment;
import net.minecraft.client.Minecraft;

/**
 * Sends Minecraft's frame (while linked: hand, held item and HUD on a transparent background,
 * because the level itself isn't drawn) to ULTRAKILL through the overlay triple buffer.
 *
 * The GPU copy is asynchronous: each frame is copied into one of three staging buffers and shipped
 * once the GPU reports the copy done, usually a frame later, so rendering never waits on readback.
 * Same technique as SkyCraft's FrameExporter (MIT); written against the 26.3 renderpearl API
 * (signatures checked with javap: GpuDevice.createBuffer, CommandEncoder.copyTextureToBuffer,
 * GpuBuffer.map, usage 9 = MAP_READ | COPY_DST).
 */
final class OverlayExporter {
	private static final int SLOTS = 3, FREE = 0, PENDING = 1, READY = 2;
	private static final int USAGE_MAP_READ_COPY_DST = GpuBuffer.USAGE_MAP_READ | GpuBuffer.USAGE_COPY_DST;

	private static final class Staging {
		GpuBuffer buffer;
		int width, height;
		volatile int state = FREE;
		long frameId;
	}

	private static final Staging[] staging = new Staging[SLOTS];
	private static long nextFrameId = 1;
	private static boolean loggedFormat;

	private OverlayExporter() {
	}

	static void afterRender(Minecraft mc) {
		Link link = MinekillClient.link();
		if (link == null || !MinekillClient.linked()) {
			return;
		}
		ship(link);
		capture(mc);
	}

	private static void capture(Minecraft mc) {
		RenderTarget target = mc.gameRenderer.mainRenderTarget();
		GpuTexture color = target.getColorTexture();
		if (color == null) {
			return;
		}
		int w = target.width, h = target.height;
		if (w <= 0 || h <= 0 || w > Proto.MAX_OVERLAY_W || h > Proto.MAX_OVERLAY_H) {
			return;
		}
		if (!loggedFormat) {
			loggedFormat = true;
			MinekillClient.LOG.info("MINEKILL: overlay capture {}x{} format {}", w, h, color.getFormat());
		}
		Staging slot = null;
		for (int i = 0; i < SLOTS && slot == null; i++) {
			if (staging[i] == null) {
				staging[i] = new Staging();
			}
			if (staging[i].state == FREE) {
				slot = staging[i];
			}
		}
		if (slot == null) {
			return; // all three still in flight: skip this frame
		}
		long bytes = (long) w * h * 4L;
		if (slot.buffer == null || slot.width != w || slot.height != h) {
			if (slot.buffer != null) {
				slot.buffer.close();
			}
			slot.buffer = RenderSystem.getDevice().createBuffer(() -> "MINEKILL overlay readback", USAGE_MAP_READ_COPY_DST, bytes);
			slot.width = w;
			slot.height = h;
		}
		final Staging s = slot;
		s.state = PENDING;
		s.frameId = nextFrameId++;
		RenderSystem.getDevice().createCommandEncoder().copyTextureToBuffer(color, s.buffer, 0L, () -> s.state = READY, 0);
	}

	private static void ship(Link link) {
		Staging newest = null;
		for (Staging s : staging) {
			if (s != null && s.state == READY && (newest == null || s.frameId > newest.frameId)) {
				newest = s;
			}
		}
		if (newest == null) {
			return;
		}
		long bytes = (long) newest.width * newest.height * 4L;
		try (GpuBufferSlice.MappedView view = newest.buffer.map(true, false)) {
			MemorySegment src = MemorySegment.ofBuffer(view.data());
			MemorySegment.copy(src, 0, link.overlayBackSlot(bytes), 0, Math.min(bytes, src.byteSize()));
		}
		link.publishOverlay(newest.width, newest.height, true, newest.frameId); // GL rows are bottom-up
		for (Staging s : staging) {
			if (s != null && s.state == READY && s.frameId <= newest.frameId) {
				s.state = FREE;
			}
		}
	}
}
