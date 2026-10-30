package dev.minekill.mc.mixin;

import dev.minekill.mc.MinekillClient;
import net.minecraft.client.Minecraft;
import net.minecraft.client.player.AbstractClientPlayer;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * The hand's walking bob. Minecraft's own updateBob() reads the player's motion, which is always
 * zero here (ULTRAKILL moves the body by teleport), so the hand never swayed. While linked, the bob
 * follows how fast V1 really moves instead (updateBob() is protected, checked with javap).
 */
@Mixin(AbstractClientPlayer.class)
public abstract class AbstractClientPlayerMixin {
	@Inject(method = "updateBob", at = @At("HEAD"), cancellable = true)
	private void minekill$bobFromV1(CallbackInfo ci) {
		AbstractClientPlayer self = (AbstractClientPlayer) (Object) this;
		if (MinekillClient.linked() && self == Minecraft.getInstance().player) {
			self.avatarState().updateBob(MinekillClient.bobTarget());
			ci.cancel();
		}
	}
}
