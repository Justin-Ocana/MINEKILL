package dev.minekill.mc;

import java.util.Map;
import java.util.TreeMap;
import net.minecraft.client.Minecraft;
import net.minecraft.client.resources.sounds.SimpleSoundInstance;
import net.minecraft.client.resources.sounds.SoundInstance;
import net.minecraft.client.player.LocalPlayer;
import net.minecraft.core.BlockPos;
import net.minecraft.sounds.SoundSource;
import net.minecraft.world.level.block.SoundType;
import net.minecraft.world.level.block.state.BlockState;

/**
 * Minecraft's sounds next to ULTRAKILL's. Minecraft plays its own (swings, hits, blocks, TNT, bow,
 * eating, shield, Steve getting hurt...) through its own audio as usual. What it can't play by
 * itself are the player's footsteps: they come from Entity.move, and here the body moves by
 * teleport. So steps and landings are played from how far V1 really walks: one step every
 * 1/0.6 blocks as in Entity.move, none while sneaking (Player.getMovementEmission), with the sound
 * of the Minecraft block underfoot, or stone on ULTRAKILL's own floors. Client thread only.
 */
final class Sounds {
	private static float moveDist, nextStep = 1f;
	private static int airTicks;
	private static boolean listening;
	private static final Map<String, Integer> played = new TreeMap<>();
	private static long reportAt;

	private Sounds() {
	}

	static void walk(Minecraft mc, LocalPlayer player, double moved, boolean onGround, boolean sneaking) {
		listen(mc);
		if (!onGround) {
			airTicks++;
			return;
		}
		if (airTicks > 5) {
			step(mc, player, true); // landing after a jump or a fall
			nextStep = moveDist + 1f;
		}
		airTicks = 0;
		if (sneaking) {
			return;
		}
		moveDist += (float) moved * 0.6f;
		if (moveDist > nextStep) {
			nextStep = moveDist + 1f;
			step(mc, player, false);
		}
	}

	private static void step(Minecraft mc, LocalPlayer player, boolean landing) {
		if (mc.level == null) {
			return;
		}
		BlockState under = mc.level.getBlockState(BlockPos.containing(player.getX(), player.getY() - 0.2, player.getZ()));
		SoundType type = under.isAir() ? SoundType.STONE : under.getSoundType();
		var event = landing ? type.getFallSound() : type.getStepSound();
		// At the listener, unattenuated: the player's own steps.
		mc.getSoundManager().play(new SimpleSoundInstance(event.location(), SoundSource.PLAYERS,
			type.getVolume() * (landing ? 0.5f : 0.15f), type.getPitch() * (landing ? 0.75f : 1f), player.getRandom(),
			false, 0, SoundInstance.Attenuation.NONE, 0, 0, 0, true));
	}

	/** Counts what Minecraft plays, for the log (evidence that the sounds really go out). */
	private static void listen(Minecraft mc) {
		if (listening) {
			return;
		}
		listening = true;
		mc.getSoundManager().addListener((sound, events, range) -> played.merge(sound.getIdentifier().getPath(), 1, Integer::sum));
	}

	static void report(Minecraft mc, long nowMs) {
		if (nowMs < reportAt) {
			return;
		}
		reportAt = nowMs + 5000;
		if (!played.isEmpty()) {
			var cam = mc.gameRenderer.mainCamera().position();
			var p = mc.player;
			MinekillClient.LOG.info("MINEKILL sounds (5 s): {} | listener ({}, {}, {}) player ({}, {}, {})", played,
				String.format("%.1f", cam.x), String.format("%.1f", cam.y), String.format("%.1f", cam.z),
				p == null ? "-" : String.format("%.1f", p.getX()), p == null ? "-" : String.format("%.1f", p.getY()), p == null ? "-" : String.format("%.1f", p.getZ()));
			played.clear();
		}
	}
}
