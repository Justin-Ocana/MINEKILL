package dev.minekill.mc;

import dev.minekill.mc.link.Link;
import dev.minekill.mc.link.Proto;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import net.minecraft.client.Minecraft;
import net.minecraft.core.BlockPos;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.resources.Identifier;
import net.minecraft.server.packs.resources.Resource;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.state.BlockState;

/**
 * Tells ULTRAKILL which Minecraft blocks are around the player (client thread, owner of the link).
 * Every half second: the non-air blocks within RADIUS of the player, as {x, y, z, texSet}, sent only
 * when the set changed (and every few seconds anyway, in case ULTRAKILL restarted). Each block type
 * is described once (RenTexSet) with its top/side/bottom textures, read from Minecraft's own
 * resources at runtime (nothing of Minecraft's is shipped). Flat-world floor layers are skipped.
 */
final class BlockReporter {
	private static final int RADIUS = 16;
	private static final int FLOOR_TOP_Y = -61; // the flat world's bedrock/dirt/grass layers
	private static final int GRASS_RGB = 0x91BD59, FOLIAGE_RGB = 0x77AB2F;

	private static final Map<Block, Integer> texSets = new HashMap<>();
	private static int ticks;
	private static long lastHash;
	private static long lastSentMs;
	private static Link sentTo;

	private BlockReporter() {
	}

	static void tick(Minecraft mc, Link link) {
		if (link != sentTo) {
			// A new link (ULTRAKILL restarted): it knows no textures yet.
			sentTo = link;
			texSets.clear();
			lastHash = 0;
		}
		if (mc.level == null || mc.player == null || ++ticks % 10 != 0) {
			return;
		}
		BlockPos c = mc.player.blockPosition();
		BlockPos.MutableBlockPos p = new BlockPos.MutableBlockPos();
		List<int[]> found = new ArrayList<>();
		for (int y = Math.max(FLOOR_TOP_Y + 1, c.getY() - RADIUS); y <= c.getY() + RADIUS; y++) {
			for (int x = c.getX() - RADIUS; x <= c.getX() + RADIUS; x++) {
				for (int z = c.getZ() - RADIUS; z <= c.getZ() + RADIUS; z++) {
					BlockState s = mc.level.getBlockState(p.set(x, y, z));
					if (s.isAir()) {
						continue;
					}
					Integer id = texSets.get(s.getBlock());
					if (id == null) {
						id = texSets.size() + 1;
						if (!sendTexSet(mc, link, id, s.getBlock())) {
							return; // ring full: try again next time
						}
						texSets.put(s.getBlock(), id);
					}
					found.add(new int[] {x, y, z, id});
				}
			}
		}
		long hash = 1469598103934665603L;
		for (int[] b : found) {
			hash = (hash ^ Arrays.hashCode(b)) * 1099511628211L;
		}
		long now = Link.tickCountMs();
		if (hash == lastHash && now - lastSentMs < 3000) {
			return;
		}
		ByteBuffer buf = ByteBuffer.allocate(4 + found.size() * 16).order(ByteOrder.LITTLE_ENDIAN);
		buf.putInt(found.size());
		for (int[] b : found) {
			buf.putInt(b[0]).putInt(b[1]).putInt(b[2]).putInt(b[3]);
		}
		if (link.pushRenderMessage(Proto.REN_BLOCKS, buf.array())) {
			if (hash != lastHash) {
				MinekillClient.LOG.info("MINEKILL: {} blocks around the player sent to ULTRAKILL ({} block types)", found.size(), texSets.size());
			}
			lastHash = hash;
			lastSentMs = now;
		}
	}

	private static boolean sendTexSet(Minecraft mc, Link link, int id, Block block) {
		Identifier key = BuiltInRegistries.BLOCK.getKey(block);
		String ns = key.getNamespace(), path = key.getPath();
		byte[] top = texture(mc, ns, path + "_top", path + "_end", path);
		byte[] side = texture(mc, ns, path + "_side", path, path + "_front");
		byte[] bottom = texture(mc, ns, path + "_bottom", path + "_top", path + "_end", path);
		int flags = 0, tint = 0xFFFFFF;
		if (path.equals("grass_block")) {
			flags |= Proto.TEX_TINT_TOP;
			tint = GRASS_RGB;
		} else if (path.endsWith("_leaves")) {
			flags |= Proto.TEX_TINT_ALL | Proto.TEX_CUTOUT;
			tint = FOLIAGE_RGB;
		}
		if (path.contains("glass") || path.endsWith("_leaves") || path.contains("ice")) {
			flags |= Proto.TEX_CUTOUT;
		}
		byte[] name = key.toString().getBytes(StandardCharsets.UTF_8);
		ByteBuffer buf = ByteBuffer.allocate(16 + name.length + 12 + top.length + side.length + bottom.length).order(ByteOrder.LITTLE_ENDIAN);
		buf.putInt(id).putInt(flags).putInt(tint).putInt(name.length).put(name);
		for (byte[] png : new byte[][] {top, side, bottom}) {
			buf.putInt(png.length).put(png);
		}
		boolean ok = link.pushRenderMessage(Proto.REN_TEXSET, buf.array());
		if (ok) {
			MinekillClient.LOG.info("MINEKILL: block type #{} {} described (textures {}/{}/{} bytes)", id, key, top.length, side.length, bottom.length);
		}
		return ok;
	}

	/** The first existing texture among block/<candidate>.png, as PNG bytes (empty if none). */
	private static byte[] texture(Minecraft mc, String ns, String... candidates) {
		for (String c : candidates) {
			Optional<Resource> r = mc.getResourceManager().getResource(Identifier.fromNamespaceAndPath(ns, "textures/block/" + c + ".png"));
			if (r.isPresent()) {
				try (InputStream in = r.get().open(); ByteArrayOutputStream out = new ByteArrayOutputStream()) {
					in.transferTo(out);
					return out.toByteArray();
				} catch (IOException e) {
					MinekillClient.LOG.warn("MINEKILL: couldn't read texture {}:{}", ns, c, e);
				}
			}
		}
		return new byte[0];
	}
}
