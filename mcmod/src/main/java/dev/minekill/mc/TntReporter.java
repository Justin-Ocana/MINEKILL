package dev.minekill.mc;

import dev.minekill.mc.link.Link;
import dev.minekill.mc.link.Proto;
import dev.minekill.mc.link.Records;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import net.minecraft.client.Minecraft;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.item.PrimedTnt;

/**
 * Lit TNT for ULTRAKILL to draw (position and fuse, every tick while any exists), and its explosion:
 * a primed TNT that vanishes with its fuse nearly out has exploded (Minecraft has already broken
 * the blocks and hurt the entities around it, ULTRAKILL's enemies' stand-ins included, by its own
 * rules); ULTRAKILL is told where, to show it. Client thread.
 */
final class TntReporter {
	private record Seen(double x, double y, double z, int fuse) {
	}

	private static Map<Integer, Seen> last = new HashMap<>();

	private TntReporter() {
	}

	static void tick(Minecraft mc, Link link) {
		if (mc.level == null) {
			last.clear();
			return;
		}
		Map<Integer, Seen> now = new HashMap<>();
		List<PrimedTnt> lit = new ArrayList<>();
		for (Entity e : mc.level.entitiesForRendering()) {
			if (e instanceof PrimedTnt t && !t.isRemoved()) {
				lit.add(t);
				now.put(t.getId(), new Seen(t.getX(), t.getY(), t.getZ(), t.getFuse()));
			}
		}
		for (var e : last.entrySet()) {
			if (!now.containsKey(e.getKey()) && e.getValue().fuse() <= 3) {
				Seen s = e.getValue();
				link.pushEvent(new Records.McEvent(Proto.EV_EXPLOSION, e.getKey(), (float) s.x(), (float) s.y() + 0.5f, (float) s.z(), 4f, 0, 0));
				MinekillClient.LOG.info("MINEKILL: TNT #{} exploded at ({}, {}, {})", e.getKey(), s.x(), s.y(), s.z());
			}
		}
		if (!lit.isEmpty() || !last.isEmpty()) {
			ByteBuffer buf = ByteBuffer.allocate(4 + lit.size() * 20).order(ByteOrder.LITTLE_ENDIAN);
			buf.putInt(lit.size());
			for (PrimedTnt t : lit) {
				buf.putInt(t.getId()).putFloat((float) t.getX()).putFloat((float) t.getY()).putFloat((float) t.getZ()).putInt(t.getFuse());
			}
			link.pushRenderMessage(Proto.REN_TNT, buf.array());
		}
		last = now;
	}
}
