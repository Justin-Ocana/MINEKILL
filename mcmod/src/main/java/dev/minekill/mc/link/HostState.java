package dev.minekill.mc.link;

/** What ULTRAKILL publishes each frame. Positions are Minecraft coordinates (V1's feet). */
public final class HostState {
	public int flags;
	public int worldId;
	public int epoch;
	public double x, y, z;
	public float yaw, pitch;
	public int teleportSeq;

	public boolean inGame() {
		return (flags & Proto.HOST_IN_GAME) != 0;
	}
}
