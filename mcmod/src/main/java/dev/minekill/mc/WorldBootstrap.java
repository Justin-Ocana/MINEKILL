package dev.minekill.mc;

import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.screens.TitleScreen;
import net.minecraft.core.registries.Registries;
import net.minecraft.world.Difficulty;
import net.minecraft.world.level.GameType;
import net.minecraft.world.level.LevelSettings;
import net.minecraft.world.level.WorldDataConfiguration;
import net.minecraft.world.level.levelgen.WorldOptions;
import net.minecraft.world.level.levelgen.presets.WorldPresets;

/**
 * Opens (or, the first time, creates) MINEKILL's own world as soon as the title screen is up, so
 * Minecraft can run unattended next to ULTRAKILL. A flat, peaceful, creative world: the player is
 * a puppet that follows V1, so no fall damage or mobs interfere yet. Pattern from SkyCraft's
 * MirrorWorld (MIT), with Minecraft's own flat preset instead of a custom one.
 */
final class WorldBootstrap {
	static final String WORLD_NAME = "MINEKILL";
	private static boolean attempted;

	private WorldBootstrap() {
	}

	static void tick(Minecraft mc) {
		if (attempted || mc.level != null || mc.gui.overlay() != null) {
			return;
		}
		if (!(mc.gui.screen() instanceof TitleScreen title)) {
			// First launch shows an onboarding/accessibility screen before the title screen.
			if (mc.gui.screen() == null || mc.gui.screen().getClass().getName().contains("Onboarding")) {
				mc.gui.setScreen(new TitleScreen());
			}
			return;
		}
		attempted = true;
		if (mc.getLevelSource().levelExists(WORLD_NAME)) {
			MinekillClient.LOG.info("MINEKILL: opening world '{}'", WORLD_NAME);
			mc.createWorldOpenFlows().openWorld(WORLD_NAME, () -> mc.gui.setScreen(title));
			return;
		}
		MinekillClient.LOG.info("MINEKILL: creating world '{}' (flat, creative, peaceful)", WORLD_NAME);
		LevelSettings settings = new LevelSettings(
			WORLD_NAME,
			GameType.CREATIVE,
			new LevelSettings.DifficultySettings(Difficulty.PEACEFUL, false, false),
			true,
			WorldDataConfiguration.DEFAULT);
		mc.createWorldOpenFlows().createFreshLevel(
			WORLD_NAME,
			settings,
			new WorldOptions(0L, false, false),
			registries -> registries.lookupOrThrow(Registries.WORLD_PRESET).getOrThrow(WorldPresets.FLAT).value().createWorldDimensions(),
			title);
	}
}
