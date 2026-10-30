package dev.minekill.mc.mixin;

import dev.minekill.mc.MinekillClient;
import net.minecraft.client.renderer.LevelRenderer;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * ULTRAKILL draws the world. While linked, Minecraft skips its own level (sky, terrain, entities)
 * so its frame is just hand + HUD on a transparent background. Descriptor checked with javap
 * against 26.3 (same as SkyCraft's).
 */
@Mixin(LevelRenderer.class)
public abstract class LevelRendererMixin {
	@Inject(
		method = "render(Lcom/mojang/blaze3d/resource/GraphicsResourceAllocator;ZLnet/minecraft/client/renderer/state/level/CameraRenderState;Lcom/mojang/renderpearl/api/buffers/GpuBufferSlice;Lorg/joml/Vector4f;ZZ)V",
		at = @At("HEAD"),
		cancellable = true)
	private void minekill$skipLevel(CallbackInfo ci) {
		if (MinekillClient.linked()) {
			ci.cancel();
		}
	}
}
