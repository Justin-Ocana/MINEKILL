package dev.minekill.mc;

import dev.minekill.mc.link.Proto;
import java.util.concurrent.ConcurrentLinkedQueue;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.EquipmentSlot;
import net.minecraft.world.entity.item.PrimedTnt;
import net.minecraft.world.item.BlockItem;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.state.BlockState;

/**
 * Block placing, breaking and TNT lighting asked for by ULTRAKILL (which owns the aim: Minecraft
 * doesn't have ULTRAKILL's level geometry, so ULTRAKILL raycasts and names the block). Requests
 * are queued by the client thread and carried out on the integrated server's thread.
 */
final class Interactions {
	record Request(int kind, int x, int y, int z, int face, int slot) {
	}

	static final int PLACE = 1, PLACE_AGAINST = 2, IGNITE = 3, BREAK = 4;
	static final int TNT_FUSE_TICKS = 60; // Minecraft's own is 80; ULTRAKILL is faster

	static final ConcurrentLinkedQueue<Request> queue = new ConcurrentLinkedQueue<>();

	private Interactions() {
	}

	static void serverTick(MinecraftServer server) {
		for (Request r; (r = queue.poll()) != null; ) {
			for (ServerPlayer p : server.getPlayerList().getPlayers()) {
				try {
					apply(p, r);
				} catch (RuntimeException e) {
					MinekillClient.LOG.warn("MINEKILL: request {} failed", r, e);
				}
			}
		}
	}

	private static void apply(ServerPlayer p, Request r) {
		ServerLevel level = p.level();
		BlockPos pos = new BlockPos(r.x(), r.y(), r.z());
		// The hotbar slot the client had selected when it asked: the server may not have heard of a
		// slot change made in the same tick yet (a TNT placed as cobblestone, 2026-10-09 test).
		if (r.slot() >= 0 && r.slot() < 9) {
			p.getInventory().setSelectedSlot(r.slot());
		}
		ItemStack held = p.getInventory().getItem(r.slot() >= 0 && r.slot() < 9 ? r.slot() : p.getInventory().getSelectedSlot());
		switch (r.kind()) {
			case PLACE, PLACE_AGAINST -> {
				BlockPos target = r.kind() == PLACE_AGAINST ? pos.relative(Direction.from3DDataValue(r.face())) : pos;
				if (!(held.getItem() instanceof BlockItem bi)) {
					return;
				}
				BlockState there = level.getBlockState(target);
				if (!there.isAir() && !there.canBeReplaced()) {
					return;
				}
				level.setBlock(target, bi.getBlock().defaultBlockState(), 3);
				if (!p.isCreative()) {
					held.shrink(1);
				}
				MinekillClient.LOG.info("MINEKILL: placed {} at {}", bi.getBlock(), target);
			}
			case IGNITE -> {
				if (!level.getBlockState(pos).is(Blocks.TNT)) {
					return;
				}
				level.removeBlock(pos, false);
				PrimedTnt tnt = new PrimedTnt(level, pos.getX() + 0.5, pos.getY(), pos.getZ() + 0.5, p);
				tnt.setFuse(TNT_FUSE_TICKS);
				// Minecraft's world has no floor under ULTRAKILL's levels: lit TNT fell ~43 blocks and
				// blew up far below (2026-10-09 test). It stays where the block was, on ULTRAKILL's floor.
				tnt.setNoGravity(true);
				// ...and without its little lighting hop, which with no gravity never stops (it rose 7
				// blocks before exploding in the next test).
				tnt.setDeltaMovement(net.minecraft.world.phys.Vec3.ZERO);
				level.addFreshEntity(tnt);
				if (held.is(Items.FLINT_AND_STEEL)) {
					held.hurtAndBreak(1, p, EquipmentSlot.MAINHAND);
				}
				MinekillClient.LOG.info("MINEKILL: TNT lit at {}", pos);
			}
			case BREAK -> {
				BlockState s = level.getBlockState(pos);
				if (s.isAir()) {
					return;
				}
				var item = s.getBlock().asItem();
				level.destroyBlock(pos, false, p, 512);
				// The block goes straight back to the hotbar: in Minecraft's world there is no floor
				// under ULTRAKILL's levels for a dropped item to land on.
				if (!p.isCreative() && item != Items.AIR) {
					p.getInventory().add(new ItemStack(item));
				}
				MinekillClient.LOG.info("MINEKILL: broke {} at {}", s.getBlock(), pos);
			}
			default -> {
			}
		}
	}

	/** Hits on Steve while his shield is up are taken by the shield (Minecraft blocks only hits it can see coming). */
	static boolean blockedByShield(ServerPlayer p, float amount) {
		if (!p.isBlocking()) {
			return false;
		}
		ItemStack shield = p.getUseItem();
		if (!shield.isEmpty()) {
			shield.hurtAndBreak(Math.max(1, (int) Math.ceil(amount)), p, p.getUsedItemHand() == net.minecraft.world.InteractionHand.MAIN_HAND ? EquipmentSlot.MAINHAND : EquipmentSlot.OFFHAND);
		}
		MinekillClient.LOG.info("MINEKILL: shield blocked {} damage", amount);
		return true;
	}
}
