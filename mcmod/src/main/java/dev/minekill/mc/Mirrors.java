package dev.minekill.mc;

import dev.minekill.mc.link.Proto;
import dev.minekill.mc.link.Records;
import java.util.HashMap;
import java.util.Iterator;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentLinkedQueue;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.damagesource.DamageSource;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.entity.EntitySpawnReason;
import net.minecraft.world.entity.EntityTypes;
import net.minecraft.world.entity.LivingEntity;
import net.minecraft.world.entity.ai.attributes.Attributes;
import net.minecraft.world.entity.animal.pig.Pig;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.item.ItemStack;

/**
 * ULTRAKILL's enemies as invisible stand-ins Minecraft's player can hit (server thread).
 * A pig with no AI, gravity or sound, scaled to the enemy's height: peaceful-proof (hostile mobs are
 * removed in peaceful) and doesn't burn in daylight. Minecraft computes the hit (reach, attack
 * cooldown, crits, enchantments, strength); the damage is caught before it lands (ALLOW_DAMAGE),
 * cancelled, and queued for ULTRAKILL, which applies it with its own damage code.
 */
public final class Mirrors {
	private static final float PIG_HEIGHT = 0.9f;

	private static final Map<Integer, Integer> entityByActor = new HashMap<>();
	private static final Map<Integer, Integer> actorByEntity = new HashMap<>();
	/** Hits for ULTRAKILL, written to the link by the client thread (the event ring has one writer). */
	static final ConcurrentLinkedQueue<Records.McEvent> outbox = new ConcurrentLinkedQueue<>();

	private Mirrors() {
	}

	static void sync(MinecraftServer server, List<Records.Actor> actors) {
		ServerLevel level = server.overworld();
		Map<Integer, Records.Actor> wanted = new HashMap<>();
		if (actors != null) {
			for (Records.Actor a : actors) {
				if (!a.dead()) {
					wanted.put(a.id(), a);
				}
			}
		}
		// Gone or dead in ULTRAKILL: remove the stand-in.
		for (Iterator<Map.Entry<Integer, Integer>> it = entityByActor.entrySet().iterator(); it.hasNext(); ) {
			Map.Entry<Integer, Integer> e = it.next();
			Entity ent = level.getEntity(e.getValue());
			if (!wanted.containsKey(e.getKey()) || ent == null || ent.isRemoved()) {
				if (ent != null && !ent.isRemoved()) {
					ent.discard();
				}
				actorByEntity.remove(e.getValue());
				it.remove();
			}
		}
		for (Records.Actor a : wanted.values()) {
			Integer entityId = entityByActor.get(a.id());
			Entity ent = entityId == null ? null : level.getEntity(entityId);
			if (ent == null) {
				Pig pig = EntityTypes.PIG.create(level, EntitySpawnReason.COMMAND);
				if (pig == null) {
					continue;
				}
				pig.setNoAi(true);
				pig.setNoGravity(true);
				pig.setSilent(true);
				pig.setInvisible(true);
				var scale = pig.getAttribute(Attributes.SCALE);
				if (scale != null) {
					scale.setBaseValue(Math.max(0.25, Math.min(8.0, a.height() / PIG_HEIGHT)));
				}
				pig.snapTo(a.x(), a.y(), a.z(), a.yaw(), 0f);
				if (!level.addFreshEntity(pig)) {
					continue;
				}
				entityByActor.put(a.id(), pig.getId());
				actorByEntity.put(pig.getId(), a.id());
				MinekillClient.LOG.info("MINEKILL: stand-in #{} for ULTRAKILL enemy {} ({}x{} blocks)", pig.getId(), a.name(), a.width(), a.height());
				ent = pig;
			}
			ent.snapTo(a.x(), a.y(), a.z(), a.yaw(), 0f);
		}
	}

	/** Server thread: is this entity one of the stand-ins? */
	public static boolean isStandIn(LivingEntity entity) {
		return actorByEntity.containsKey(entity.getId());
	}

	/**
	 * A hit on a stand-in (LivingEntityMixin): it never takes the damage itself; a player's hit is
	 * passed to ULTRAKILL and reported to Minecraft as landed (true), anything else as nothing (false).
	 */
	public static boolean onHit(LivingEntity entity, DamageSource source, float amount) {
		Integer actor = actorByEntity.get(entity.getId());
		if (actor == null) {
			return false;
		}
		if (source.getEntity() instanceof Player player) {
			double dx = entity.getX() - player.getX(), dz = entity.getZ() - player.getZ();
			double len = Math.sqrt(dx * dx + dz * dz);
			float kx = len > 1e-4 ? (float) (dx / len) : 0f, kz = len > 1e-4 ? (float) (dz / len) : 0f;
			ItemStack held = player.getMainHandItem();
			boolean explosion = source.is(net.minecraft.tags.DamageTypeTags.IS_EXPLOSION);
			int weapon = explosion || held.isEmpty() ? Proto.WEAPON_UNARMED : held.getItem().toString().contains("sword") ? Proto.WEAPON_BLADE : Proto.WEAPON_BLUNT;
			outbox.add(new Records.McEvent(Proto.EV_HIT_ACTOR, actor, amount, kx, kz, 0.4f, explosion ? Proto.HIT_EXPLOSION : 0, weapon));
			MinekillClient.LOG.info("MINEKILL: {} hit ULTRAKILL enemy #{} for {} (weapon {}; attacker fallDistance={} onGround={} sprinting={} strength={})",
				player.getName().getString(), actor, amount, held, player.fallDistance, player.onGround(), player.isSprinting(), player.getAttackStrengthScale(0.5f));
			return true;
		}
		return false;
	}

	static void clear(MinecraftServer server) {
		ServerLevel level = server.overworld();
		for (int id : actorByEntity.keySet()) {
			Entity ent = level.getEntity(id);
			if (ent != null) {
				ent.discard();
			}
		}
		entityByActor.clear();
		actorByEntity.clear();
	}
}
