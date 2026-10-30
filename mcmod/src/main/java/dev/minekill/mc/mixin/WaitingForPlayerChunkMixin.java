package dev.minekill.mc.mixin;

import dev.minekill.mc.MinekillClient;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * "Loading terrain" waits for the player's chunk section to be compiled for rendering, which never
 * happens while the level isn't drawn (LevelRendererMixin): the screen stayed up forever and
 * covered the overlay (2026-10-08 test, last frame was the loading screen). Don't wait while linked.
 * Same fix as SkyCraft's; private boolean isReady() checked with javap against 26.3.
 */
@Mixin(targets = "net.minecraft.client.multiplayer.LevelLoadTracker$WaitingForPlayerChunk")
public abstract class WaitingForPlayerChunkMixin {
	@Inject(method = "isReady", at = @At("HEAD"), cancellable = true)
	private void minekill$ready(CallbackInfoReturnable<Boolean> cir) {
		if (MinekillClient.linked()) {
			cir.setReturnValue(true);
		}
	}
}
