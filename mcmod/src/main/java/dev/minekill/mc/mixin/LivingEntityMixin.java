package dev.minekill.mc.mixin;

import dev.minekill.mc.Mirrors;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.entity.LivingEntity;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * Hits on the stand-ins of ULTRAKILL's enemies. They never lose health (the damage goes to the
 * ULTRAKILL enemy), but a player's hit reports success, so Minecraft carries on as with a real hit:
 * its strong/crit/sweep/knockback sounds, sweeping, the weapon's durability, hunger, stats. Before,
 * the hit was cancelled and Minecraft played its "missed" whoosh instead (seen 2026-10-09).
 * hurtServer(ServerLevel, DamageSource, float) checked with javap.
 */
@Mixin(LivingEntity.class)
public abstract class LivingEntityMixin {
	@Inject(method = "hurtServer", at = @At("HEAD"), cancellable = true)
	private void minekill$standInHit(ServerLevel level, DamageSource source, float amount, CallbackInfoReturnable<Boolean> cir) {
		LivingEntity self = (LivingEntity) (Object) this;
		if (Mirrors.isStandIn(self)) {
			cir.setReturnValue(Mirrors.onHit(self, source, amount));
		}
	}
}
