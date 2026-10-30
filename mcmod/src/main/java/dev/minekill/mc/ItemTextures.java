package dev.minekill.mc;

import dev.minekill.mc.link.Link;
import dev.minekill.mc.link.Proto;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import net.minecraft.client.Minecraft;
import net.minecraft.resources.Identifier;

/**
 * The item textures ULTRAKILL needs to draw projectiles (arrow, snowball, egg, ender pearl),
 * read from Minecraft's own resources at runtime and sent once per link.
 */
final class ItemTextures {
	private static final String[] NAMES = {"arrow", "snowball", "egg", "ender_pearl"};
	private static Link sentTo;

	private ItemTextures() {
	}

	static void tick(Minecraft mc, Link link) {
		if (link == sentTo) {
			return;
		}
		for (String name : NAMES) {
			var r = mc.getResourceManager().getResource(Identifier.withDefaultNamespace("textures/item/" + name + ".png"));
			if (r.isEmpty()) {
				MinekillClient.LOG.warn("MINEKILL: no item texture for {}", name);
				continue;
			}
			try (InputStream in = r.get().open(); ByteArrayOutputStream out = new ByteArrayOutputStream()) {
				in.transferTo(out);
				byte[] png = out.toByteArray(), n = name.getBytes(StandardCharsets.UTF_8);
				ByteBuffer buf = ByteBuffer.allocate(8 + n.length + png.length).order(ByteOrder.LITTLE_ENDIAN);
				buf.putInt(n.length).put(n).putInt(png.length).put(png);
				if (!link.pushRenderMessage(Proto.REN_ITEMTEX, buf.array())) {
					return; // ring full: retry next tick
				}
			} catch (IOException e) {
				MinekillClient.LOG.warn("MINEKILL: couldn't read item texture {}", name, e);
			}
		}
		sentTo = link;
		MinekillClient.LOG.info("MINEKILL: item textures sent to ULTRAKILL");
	}
}
