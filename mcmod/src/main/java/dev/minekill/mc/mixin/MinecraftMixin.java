package dev.minekill.mc.mixin;

import dev.minekill.mc.MinekillClient;
import net.minecraft.client.Minecraft;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** Captures each frame right after GameRenderer.render() (renderFrame(boolean) checked with javap). */
@Mixin(Minecraft.class)
public abstract class MinecraftMixin {
	@Inject(
		method = "renderFrame",
		at = @At(value = "INVOKE", target = "Lnet/minecraft/client/renderer/GameRenderer;render()V", shift = At.Shift.AFTER))
	private void minekill$afterRender(boolean advanceGameTime, CallbackInfo ci) {
		MinekillClient.afterRender((Minecraft) (Object) this);
	}
}
