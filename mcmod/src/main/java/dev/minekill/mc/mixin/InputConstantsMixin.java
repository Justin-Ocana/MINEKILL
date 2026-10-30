package dev.minekill.mc.mixin;

import com.mojang.blaze3d.platform.InputConstants;
import com.mojang.blaze3d.platform.Window;
import dev.minekill.mc.MinekillClient;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * While linked the mouse is ULTRAKILL's: Minecraft must never capture (and hide or warp) the system
 * cursor, e.g. when a screen it opened from ULTRAKILL closes while its own window has the focus.
 * grabMouse(Window, double, double) is public static (checked with javap).
 */
@Mixin(InputConstants.class)
public abstract class InputConstantsMixin {
	@Inject(method = "grabMouse", at = @At("HEAD"), cancellable = true)
	private static void minekill$keepCursorFree(Window window, double x, double y, CallbackInfo ci) {
		if (MinekillClient.linked()) {
			ci.cancel();
		}
	}
}
