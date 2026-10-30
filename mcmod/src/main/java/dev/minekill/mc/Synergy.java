package dev.minekill.mc;

import dev.minekill.mc.link.Proto;
import dev.minekill.mc.link.Records;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.concurrent.ConcurrentLinkedQueue;
import net.minecraft.core.BlockPos;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.item.PrimedTnt;
import net.minecraft.world.entity.projectile.Projectile;
import net.minecraft.world.entity.projectile.arrow.AbstractArrow;
import net.minecraft.world.entity.projectile.throwableitemprojectile.Snowball;
import net.minecraft.world.entity.projectile.throwableitemprojectile.ThrownEgg;
import net.minecraft.world.entity.projectile.throwableitemprojectile.ThrownEnderpearl;
import net.minecraft.world.item.Item;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.phys.Vec3;

/**
 * Where the two games' rules meet (integrated-server thread; requests come from the client thread):
 *  - Bloodcraft: ULTRAKILL's blood heals V1 and, now, Steve;
 *  - loot: Minecraft weapons' kills in ULTRAKILL drop Minecraft loot and experience into the hotbar;
 *  - ULTRAKILL explosions break Minecraft blocks and set Minecraft TNT off (chain reactions);
 *  - jump crits: V1 coming down makes Steve's swings critical hits, as falling does in Minecraft;
 *  - shield parry: a shield raised just before the hit is ULTRAKILL's parry;
 *  - projectiles: what Steve throws or shoots (arrows, snowballs, eggs, ender pearls) leaves
 *    Minecraft with Minecraft's own launch speed and flies on in ULTRAKILL's level instead.
 */
final class Synergy {
	record Request(int type, int code, int a, int b, int c) {
	}

	static final ConcurrentLinkedQueue<Request> queue = new ConcurrentLinkedQueue<>();
	/** Thrown/shot projectiles for ULTRAKILL (RenThrow payloads), written by the client thread. */
	static final ConcurrentLinkedQueue<byte[]> throwsOut = new ConcurrentLinkedQueue<>();

	private static final int PARRY_WINDOW_TICKS = 10; // the shield went up at most half a second ago

	private Synergy() {
	}

	private static volatile boolean v1FallingNow;

	/**
	 * Minecraft decides a crit inside Player.attack from the attacker's fall distance, which the
	 * per-tick teleport and reset keep at 0 (measured: fallDistance=0.0 at the moment of the hit,
	 * 2026-10-09). So it is set right before the attack is handled, on the server, while V1 falls.
	 */
	static void register() {
		net.fabricmc.fabric.api.event.player.AttackEntityCallback.EVENT.register((player, level, hand, target, hit) -> {
			if (!level.isClientSide() && v1FallingNow) {
				player.fallDistance = Math.max(player.fallDistance, 0.5);
			}
			return net.minecraft.world.InteractionResult.PASS;
		});
	}

	static void serverTick(MinecraftServer server, boolean v1Falling) {
		v1FallingNow = v1Falling;
		for (Request r; (r = queue.poll()) != null; ) {
			for (ServerPlayer p : server.getPlayerList().getPlayers()) {
				try {
					apply(p, r);
				} catch (RuntimeException e) {
					MinekillClient.LOG.warn("MINEKILL: synergy request {} failed", r, e);
				}
			}
		}
		for (ServerPlayer p : server.getPlayerList().getPlayers()) {
			captureProjectiles(p);
		}
	}

	private static void apply(ServerPlayer p, Request r) {
		switch (r.type()) {
			case Proto.IN_HEAL -> p.heal(r.a() / 100f);
			case Proto.IN_LOOT -> {
				Item item = lootItem(r.code());
				if (item != null && r.a() > 0) {
					p.getInventory().add(new ItemStack(item, r.a()));
				}
				if (r.b() > 0) {
					p.giveExperiencePoints(r.b());
				}
				MinekillClient.LOG.info("MINEKILL: loot {} x{} and {} xp", item, r.a(), r.b());
			}
			case Proto.IN_EXPLODE -> explodeBlocks(p.level(), r.a() / 100.0, r.b() / 100.0, r.c() / 100.0, r.code() / 10.0);
			default -> {
			}
		}
	}

	static Item lootItem(int id) {
		return switch (id) {
			case Proto.LOOT_ARROW -> Items.ARROW;
			case Proto.LOOT_TNT -> Items.TNT;
			case Proto.LOOT_GUNPOWDER -> Items.GUNPOWDER;
			case Proto.LOOT_ENDER_PEARL -> Items.ENDER_PEARL;
			case Proto.LOOT_SNOWBALL -> Items.SNOWBALL;
			case Proto.LOOT_ROTTEN_FLESH -> Items.ROTTEN_FLESH;
			case Proto.LOOT_IRON_INGOT -> Items.IRON_INGOT;
			case Proto.LOOT_DIAMOND -> Items.DIAMOND;
			case Proto.LOOT_GOLDEN_APPLE -> Items.GOLDEN_APPLE;
			case Proto.LOOT_ENCHANTED_GOLDEN_APPLE -> Items.ENCHANTED_GOLDEN_APPLE;
			case Proto.LOOT_COOKED_BEEF -> Items.COOKED_BEEF;
			default -> null;
		};
	}

	/** An ULTRAKILL explosion: Minecraft blocks inside it break, Minecraft TNT inside it goes off. */
	private static void explodeBlocks(ServerLevel level, double cx, double cy, double cz, double radius) {
		int r = (int) Math.ceil(radius);
		int broken = 0, lit = 0;
		BlockPos.MutableBlockPos pos = new BlockPos.MutableBlockPos();
		for (int x = (int) Math.floor(cx) - r; x <= (int) Math.floor(cx) + r; x++) {
			for (int y = (int) Math.floor(cy) - r; y <= (int) Math.floor(cy) + r; y++) {
				for (int z = (int) Math.floor(cz) - r; z <= (int) Math.floor(cz) + r; z++) {
					double dx = x + 0.5 - cx, dy = y + 0.5 - cy, dz = z + 0.5 - cz;
					if (dx * dx + dy * dy + dz * dz > radius * radius) {
						continue;
					}
					BlockState s = level.getBlockState(pos.set(x, y, z));
					if (s.isAir() || y <= -61) {
						continue; // nothing there, or the flat world's floor
					}
					if (s.is(Blocks.TNT)) {
						level.removeBlock(pos, false);
						PrimedTnt tnt = new PrimedTnt(level, x + 0.5, y, z + 0.5, null);
						tnt.setFuse(10 + level.getRandom().nextInt(10)); // a chain, not all at once
						tnt.setNoGravity(true);
						tnt.setDeltaMovement(Vec3.ZERO);
						level.addFreshEntity(tnt);
						lit++;
					} else if (s.getBlock().getExplosionResistance() < 600f) { // obsidian and up survive
						level.destroyBlock(pos.immutable(), false);
						broken++;
					}
				}
			}
		}
		if (broken + lit > 0) {
			MinekillClient.LOG.info("MINEKILL: ULTRAKILL explosion (r={}) broke {} blocks, lit {} TNT", radius, broken, lit);
		}
	}

	/** Hits Steve takes while his shield is up: a parry if it went up at most half a second ago. */
	static boolean parried(ServerPlayer p) {
		if (p.isBlocking() && p.getTicksUsingItem() <= PARRY_WINDOW_TICKS) {
			p.heal(4f); // a parry heals, as in ULTRAKILL
			Mirrors.outbox.add(new Records.McEvent(Proto.EV_PARRY, 0, 0, 0, 0, 0, 0, 0));
			MinekillClient.LOG.info("MINEKILL: shield parry");
			return true;
		}
		return false;
	}

	/** Steve's fresh projectiles leave Minecraft (whose world has no level to fly through) for ULTRAKILL. */
	private static void captureProjectiles(ServerPlayer p) {
		ServerLevel level = p.level();
		var box = p.getBoundingBox().inflate(6.0);
		for (Entity e : level.getEntities((Entity) null, box, e -> e instanceof Projectile pr && pr.getOwner() == p)) {
			int kind;
			float damage = 0f;
			int flags = 0;
			Vec3 v = e.getDeltaMovement();
			if (e instanceof AbstractArrow arrow) {
				if (arrow.tickCount > 1) {
					continue; // already handled (or stuck from before)
				}
				kind = Proto.THROW_ARROW;
				// Minecraft's arrow damage: speed (blocks/tick) x base 2, +up to half again on a crit.
				damage = (float) Math.ceil(v.length() * 2.0);
				if (arrow.isCritArrow()) {
					flags |= Proto.THROW_CRIT;
					damage += level.getRandom().nextInt((int) damage / 2 + 2);
				}
			} else if (e instanceof ThrownEnderpearl) {
				kind = Proto.THROW_ENDER_PEARL;
			} else if (e instanceof Snowball) {
				kind = Proto.THROW_SNOWBALL;
			} else if (e instanceof ThrownEgg) {
				kind = Proto.THROW_EGG;
			} else {
				continue;
			}
			ByteBuffer buf = ByteBuffer.allocate(36).order(ByteOrder.LITTLE_ENDIAN);
			buf.putInt(kind).putFloat((float) e.getX()).putFloat((float) e.getY()).putFloat((float) e.getZ())
				.putFloat((float) v.x).putFloat((float) v.y).putFloat((float) v.z).putFloat(damage).putInt(flags);
			throwsOut.add(buf.array());
			e.discard();
			MinekillClient.LOG.info("MINEKILL: {} launched into ULTRAKILL (speed {} blocks/tick, damage {})", e.getType(), String.format("%.2f", v.length()), damage);
		}
	}
}
