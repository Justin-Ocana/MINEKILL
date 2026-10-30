package dev.minekill.mc;

import dev.minekill.mc.link.GuestState;
import dev.minekill.mc.link.HostState;
import dev.minekill.mc.link.Link;
import dev.minekill.mc.link.Proto;
import dev.minekill.mc.link.Records;
import java.util.HashSet;
import java.util.Set;
import java.util.UUID;
import net.fabricmc.api.ClientModInitializer;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientTickEvents;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerTickEvents;
import net.minecraft.client.Minecraft;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.level.GameType;
import net.minecraft.world.phys.Vec3;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

/**
 * Minecraft's side of MINEKILL (split ownership): ULTRAKILL owns the body, so every server tick the
 * Minecraft player is moved to where V1 is; Minecraft owns the items, and shows its hand, held item
 * and HUD inside ULTRAKILL through the overlay.
 *  - client (render) thread: link, heartbeat, ULTRAKILL's state and input, player report, overlay;
 *  - integrated-server thread: applies V1's pose to the player, sets up survival + starting items.
 */
public final class MinekillClient implements ClientModInitializer {
	public static final Logger LOG = LoggerFactory.getLogger("minekill");
	private static final String MAPPING = System.getProperty("minekill.link", Proto.DEFAULT_MAPPING_NAME);
	private static final int SDL_SCANCODE_1 = 30; // SDL_SCANCODE_1..9 = 30..38: hotbar slots 1..9

	/** Immutable copy of ULTRAKILL's state handed from the client thread to the server thread. */
	private record HostSnapshot(boolean inGame, double x, double y, double z, float yaw, float pitch, int teleportSeq, int flags) {
	}

	private static volatile HostSnapshot host;
	private static volatile int appliedTeleportSeq;
	private static volatile java.util.List<Records.Actor> actors = java.util.List.of();
	/** Hits on V1 (Minecraft damage), applied to the player on the server thread. */
	private static final java.util.concurrent.ConcurrentLinkedQueue<Float> hurts = new java.util.concurrent.ConcurrentLinkedQueue<>();
	/** Landings V1 made under Minecraft's movement rules: fall damage for the player. */
	private static final java.util.concurrent.ConcurrentLinkedQueue<Float> falls = new java.util.concurrent.ConcurrentLinkedQueue<>();
	private boolean deathReported;
	private int respawnIn = -1;
	private static final Set<UUID> preparedPlayers = new HashSet<>();

	private static Link link;
	private static boolean peerAlive;
	/** Where V1 was last client tick (blocks), for the hand's walking bob. */
	private double lastHostX = Double.NaN, lastHostZ = Double.NaN;
	private static volatile float bobTarget;
	private boolean wasSwinging;
	private static String lastInput = "none";

	private long nextOpenAttemptMs;
	private final HostState hostState = new HostState();
	private final GuestState guestState = new GuestState();
	private long frame;
	private long nextStatusMs;
	private String lastWhyNot = "";

	/** Target of the hand's bob this tick: as in Minecraft, the ground speed up to 0.1, nothing in the air. */
	public static float bobTarget() {
		return bobTarget;
	}

	/** The open link (client thread only), or null. */
	static Link link() {
		return link;
	}

	/** Linked and ULTRAKILL is in a level: Minecraft's own world view is replaced by ULTRAKILL's. */
	public static boolean linked() {
		HostSnapshot h = host;
		return link != null && peerAlive && h != null && h.inGame();
	}

	public static void afterRender(Minecraft mc) {
		try {
			OverlayExporter.afterRender(mc);
		} catch (RuntimeException e) {
			LOG.error("MINEKILL: overlay export failed", e);
		}
	}

	@Override
	public void onInitializeClient() {
		LOG.info("MINEKILL Minecraft side loaded; link {}", MAPPING);
		ClientTickEvents.END_CLIENT_TICK.register(this::clientTick);
		ServerTickEvents.END_SERVER_TICK.register(MinekillClient::serverTick);
		Synergy.register();
		net.fabricmc.fabric.api.entity.event.v1.ServerPlayerEvents.AFTER_RESPAWN.register((oldPlayer, newPlayer, alive) -> giveKit(newPlayer));
	}

	private void clientTick(Minecraft mc) {
		WorldBootstrap.tick(mc);
		long now = Link.tickCountMs();
		if (link == null) {
			if (now < nextOpenAttemptMs) {
				return;
			}
			nextOpenAttemptMs = now + 1000;
			StringBuilder why = new StringBuilder();
			link = Link.openGuest(MAPPING, why);
			if (link == null) {
				if (!why.toString().equals(lastWhyNot)) {
					lastWhyNot = why.toString();
					LOG.info("MINEKILL: waiting for ULTRAKILL: {}", lastWhyNot);
				}
				return;
			}
			LOG.info("MINEKILL: linked to ULTRAKILL (pid {})", link.peerPid());
		}

		link.heartbeat();
		boolean alive = link.peerAlive();
		if (alive != peerAlive) {
			peerAlive = alive;
			LOG.info(alive ? "MINEKILL: ULTRAKILL heartbeat alive" : "MINEKILL: ULTRAKILL heartbeat stale ({} ms): paused or loading", link.peerBeatAgeMs());
		}
		if (link.readHostState(hostState)) { // a torn read keeps the last snapshot
			host = new HostSnapshot(alive && hostState.inGame(), hostState.x, hostState.y, hostState.z, hostState.yaw, hostState.pitch, hostState.teleportSeq, hostState.flags);
		}

		var fresh = link.readActors();
		if (fresh != null) { // a torn read keeps the last list
			actors = fresh;
		}

		var player = mc.player;
		trackWalk(player);
		for (Records.InputEvent e; (e = link.pollInput()) != null; ) {
			if (player != null) {
				applyInput(mc, player, e);
			}
		}
		if (player != null && player.isSwinging() != wasSwinging) {
			wasSwinging = player.isSwinging();
			if (wasSwinging) {
				LOG.info("MINEKILL: Steve swings (last input from ULTRAKILL: {})", lastInput);
			}
		}
		Screens.report(mc);
		Sounds.report(mc, now);
		try {
			BlockReporter.tick(mc, link);
			ItemTextures.tick(mc, link);
			for (byte[] t; (t = Synergy.throwsOut.poll()) != null; ) {
				link.pushRenderMessage(Proto.REN_THROW, t);
			}
			TntReporter.tick(mc, link);
		} catch (RuntimeException ex) {
			LOG.error("MINEKILL: block/TNT report failed", ex);
		}
		for (Records.McEvent ev; (ev = Mirrors.outbox.poll()) != null; ) {
			if (!link.pushEvent(ev)) {
				LOG.warn("MINEKILL: event ring full, dropped {}", ev);
			}
		}

		// Minecraft's death is V1's death; Minecraft respawns by itself (ULTRAKILL restarts from its checkpoint).
		if (player != null && player.isDeadOrDying()) {
			if (!deathReported) {
				deathReported = true;
				respawnIn = 20;
				link.pushEvent(new Records.McEvent(Proto.EV_PLAYER_DIED, 0, 0, 0, 0, 0, 0, 0));
				LOG.info("MINEKILL: Minecraft's player died; telling ULTRAKILL");
			} else if (respawnIn > 0 && --respawnIn == 0) {
				player.respawn();
				mc.gui.setScreen(null);
			}
		} else if (player != null) {
			deathReported = false;
		}

		guestState.flags = 0;
		if (player != null && mc.level != null) {
			guestState.flags |= Proto.GUEST_IN_WORLD;
			if (player.onGround()) {
				guestState.flags |= Proto.GUEST_ON_GROUND;
			}
			if (mc.gui.screen() != null) {
				guestState.flags |= Proto.GUEST_SCREEN_OPEN;
			}
			if (player.isBlocking()) {
				guestState.flags |= Proto.GUEST_BLOCKING; // ULTRAKILL's side parries incoming shots while it's up
			}
			guestState.x = player.getX();
			guestState.y = player.getY();
			guestState.z = player.getZ();
			guestState.yaw = player.getYRot();
			guestState.pitch = player.getXRot();
			guestState.eyeHeight = player.getEyeHeight();
		}
		guestState.teleportAck = appliedTeleportSeq;
		guestState.frameCounter = ++frame;
		guestState.fov = fov(mc, player);
		link.writeGuestState(guestState);

		if (now >= nextStatusMs) {
			nextStatusMs = now + 5000;
			HostSnapshot h = host;
			LOG.info("MINEKILL status: ultrakill={} host={} player=({}, {}, {}) ack={} slot={} held={} health={}",
				alive ? "alive" : "stale", h == null ? "none" : String.format("(%.2f, %.2f, %.2f) seq %d", h.x(), h.y(), h.z(), h.teleportSeq()),
				String.format("%.2f", guestState.x), String.format("%.2f", guestState.y), String.format("%.2f", guestState.z), appliedTeleportSeq,
				player == null ? "-" : player.getInventory().getSelectedSlot() + 1,
				player == null ? "-" : player.getInventory().getSelectedItem(),
				player == null ? "-" : player.getHealth());
		}
	}

	/**
	 * Walking, as Minecraft's hand and camera feel it, from how far V1 really moved this tick: the
	 * walk distance drives the bob's rhythm (Entity.move adds 0.6 per block) and the bob's size
	 * follows the ground speed (LivingEntity motion, ~0.546 of a tick's move after ground friction).
	 */
	private void trackWalk(net.minecraft.client.player.LocalPlayer player) {
		HostSnapshot h = host;
		if (h == null || !h.inGame() || player == null) {
			lastHostX = lastHostZ = Double.NaN;
			bobTarget = 0f;
			return;
		}
		double moved = Double.isNaN(lastHostX) ? 0 : Math.hypot(h.x() - lastHostX, h.z() - lastHostZ);
		lastHostX = h.x();
		lastHostZ = h.z();
		if (moved > 2.0) { // a teleport or a scene change, not a step
			moved = 0;
		}
		boolean onGround = (h.flags() & Proto.HOST_AIRBORNE) == 0;
		player.avatarState().addWalkDistance((float) moved * 0.6f);
		Sounds.walk(Minecraft.getInstance(), player, moved, onGround, (h.flags() & Proto.HOST_SNEAKING) != 0);
		bobTarget = onGround ? Math.min(0.1f, (float) moved * 0.546f) : 0f;
	}

	/**
	 * Minecraft's field of view for ULTRAKILL's camera: the FOV setting times the player's modifier
	 * (bow and spyglass zoom, Speed and Slowness), and the sprint's +15% when V1 sprints under
	 * Minecraft's movement, all scaled by "FOV Effects" as in Minecraft.
	 */
	private static float fov(Minecraft mc, net.minecraft.client.player.LocalPlayer player) {
		float base = mc.options.fov().get();
		if (player == null) {
			return base;
		}
		float effects = mc.options.fovEffectScale().get().floatValue();
		float modifier = player.getFieldOfViewModifier(true, effects);
		HostSnapshot h = host;
		if (h != null && (h.flags() & Proto.HOST_SPRINTING) != 0) {
			// Sprinting raises movement speed to 1.3x; Minecraft's modifier is (speed / walk speed + 1) / 2.
			modifier *= 1f + (1.15f - 1f) * effects;
		}
		return base * modifier;
	}

	/** ULTRAKILL's commands, by meaning rather than replayed keystrokes (ULTRAKILL owns the input). */
	private static void applyInput(Minecraft mc, net.minecraft.client.player.LocalPlayer player, Records.InputEvent e) {
		lastInput = e.type() + "/" + e.code() + "/" + e.a() + " at " + Link.tickCountMs();
		if (Screens.apply(mc, e)) { // the inventory key, and everything while a Minecraft screen is open
			return;
		}
		switch (e.type()) {
			case Proto.IN_MOUSE_BUTTON -> {
				// Left button: Minecraft's own attack (its crosshair target, reach, cooldown, swing).
				if (e.code() == 1 && e.a() == 1) {
					net.minecraft.client.KeyMapping.click(net.fabricmc.fabric.api.client.keymapping.v1.KeyMappingHelper.getBoundKeyOf(mc.options.keyAttack));
				}
			}
			case Proto.IN_HURT -> hurts.add(e.a() / 100f);
			case Proto.IN_FALL -> falls.add(e.a() / 100f);
			case Proto.IN_USE -> {
				int action = e.code() & 0xFF, face = e.code() >> 8;
				var useKey = net.fabricmc.fabric.api.client.keymapping.v1.KeyMappingHelper.getBoundKeyOf(mc.options.keyUse);
				if (action == Proto.USE_RELEASE) {
					net.minecraft.client.KeyMapping.set(useKey, false);
					return;
				}
				var held = player.getMainHandItem();
				boolean onBlock = action == Proto.USE_PRESS_BLOCK;
				boolean blockItem = held.getItem() instanceof net.minecraft.world.item.BlockItem;
				if (blockItem && (onBlock || action == Proto.USE_PRESS_LEVEL)) {
					Interactions.queue.add(new Interactions.Request(onBlock ? Interactions.PLACE_AGAINST : Interactions.PLACE, e.a(), e.b(), e.c(), face, player.getInventory().getSelectedSlot()));
				} else if (onBlock && held.is(net.minecraft.world.item.Items.FLINT_AND_STEEL)
					&& mc.level.getBlockState(new net.minecraft.core.BlockPos(e.a(), e.b(), e.c())).is(net.minecraft.world.level.block.Blocks.TNT)) {
					Interactions.queue.add(new Interactions.Request(Interactions.IGNITE, e.a(), e.b(), e.c(), face, player.getInventory().getSelectedSlot()));
				} else {
					// Anything else is Minecraft's own "use": raise the shield, eat, drink, draw a bow...
					net.minecraft.client.KeyMapping.set(useKey, true);
					net.minecraft.client.KeyMapping.click(useKey);
				}
			}
			case Proto.IN_HEAL, Proto.IN_LOOT, Proto.IN_EXPLODE -> Synergy.queue.add(new Synergy.Request(e.type(), e.code(), e.a(), e.b(), e.c()));
			case Proto.IN_BREAK -> Interactions.queue.add(new Interactions.Request(Interactions.BREAK, e.a(), e.b(), e.c(), 0, player.getInventory().getSelectedSlot()));
			case Proto.IN_KEY -> {
				int slot = e.code() - SDL_SCANCODE_1;
				if (e.a() == 1 && slot >= 0 && slot < 9) {
					player.getInventory().setSelectedSlot(slot);
				}
			}
			case Proto.IN_SCROLL -> {
				int steps = -Integer.signum(e.a()); // wheel up = previous slot, as in Minecraft
				int slot = Math.floorMod(player.getInventory().getSelectedSlot() + steps, 9);
				player.getInventory().setSelectedSlot(slot);
			}
			default -> {
			}
		}
	}

	private static void giveKit(ServerPlayer p) {
		// Hotbar 1-9, then the backpack (the bow's arrows, spare blocks).
		ItemStack[] kit = {
			new ItemStack(Items.DIAMOND_SWORD),
			new ItemStack(Items.BOW),
			new ItemStack(Items.COBBLESTONE, 64),
			new ItemStack(Items.OAK_PLANKS, 64),
			new ItemStack(Items.TNT, 16),
			new ItemStack(Items.FLINT_AND_STEEL),
			new ItemStack(Items.ENDER_PEARL, 16),
			new ItemStack(Items.SNOWBALL, 16),
			new ItemStack(Items.GOLDEN_APPLE, 8),
			new ItemStack(Items.ARROW, 64),
			new ItemStack(Items.GLASS, 64),
			new ItemStack(Items.BRICKS, 64),
			new ItemStack(Items.EGG, 16),
			new ItemStack(Items.COOKED_BEEF, 16),
		};
		// Kit v2 (with the bow): an inventory from the first kit gets the new layout.
		boolean upgrade = !p.getInventory().contains(new ItemStack(Items.BOW));
		for (int i = 0; i < kit.length; i++) {
			if (upgrade || p.getInventory().getItem(i).isEmpty()) {
				p.getInventory().setItem(i, kit[i]);
			}
		}
		if (p.getInventory().getItem(net.minecraft.world.entity.player.Inventory.SLOT_OFFHAND).isEmpty()) {
			p.getInventory().setItem(net.minecraft.world.entity.player.Inventory.SLOT_OFFHAND, new ItemStack(Items.SHIELD));
		}
		LOG.info("MINEKILL: player {} in survival with the starting kit", p.getName().getString());
	}

	private static void serverTick(MinecraftServer server) {
		for (ServerPlayer p : server.getPlayerList().getPlayers()) {
			if (preparedPlayers.add(p.getUUID())) {
				// Survival so Minecraft's health, armour and hunger apply; a sword to start with.
				p.setGameMode(GameType.SURVIVAL);
				giveKit(p);
			}
			for (Float amount; (amount = hurts.poll()) != null; ) {
				if (!Synergy.parried(p) && !Interactions.blockedByShield(p, amount)) {
					p.hurtServer(p.level(), p.damageSources().generic(), amount);
				}
			}
		}
		Interactions.serverTick(server);
		HostSnapshot hs = host;
		Synergy.serverTick(server, hs != null && hs.inGame() && (hs.flags() & Proto.HOST_FALLING) != 0);
		// The body is V1's puppet: Minecraft's gravity and fall damage never apply to it, linked or
		// not. Without this, Steve fell ~125 blocks and died (dropping his items) 3 s after every
		// time ULTRAKILL stopped answering (2026-10-08: "Steve fell from a high place").
		for (ServerPlayer p : server.getPlayerList().getPlayers()) {
			p.setNoGravity(true);
			p.resetFallDistance();
		}
		HostSnapshot h = host;
		Mirrors.sync(server, h != null && h.inGame() ? actors : null);
		if (h == null || !h.inGame()) {
			// ULTRAKILL isn't in a level (or stopped answering): hold the player still where it is.
			for (ServerPlayer p : server.getPlayerList().getPlayers()) {
				p.setDeltaMovement(Vec3.ZERO);
			}
			return;
		}
		for (ServerPlayer p : server.getPlayerList().getPlayers()) {
			// Keep the yaw continuous (the nearest turn to the current one): ULTRAKILL's wraps at
			// +-180, and a 360-degree jump made the hand's turning lag sweep it across the screen.
			float yaw = p.getYRot() + net.minecraft.util.Mth.wrapDegrees(h.yaw() - p.getYRot());
			p.teleportTo(p.level(), h.x(), h.y(), h.z(), Set.of(), yaw, h.pitch(), false);
		}
		if (appliedTeleportSeq != h.teleportSeq()) {
			appliedTeleportSeq = h.teleportSeq();
			LOG.info("MINEKILL: applied teleport request {} -> ({}, {}, {})", h.teleportSeq(), h.x(), h.y(), h.z());
		}
	}
}
