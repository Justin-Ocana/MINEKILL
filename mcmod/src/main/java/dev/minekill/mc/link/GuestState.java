package dev.minekill.mc.link;

/** What Minecraft publishes each tick. */
public final class GuestState {
	public int flags;
	public double x, y, z;
	public float yaw, pitch;
	public float eyeHeight;
	public int teleportAck;
	public long frameCounter;
	public float fov;
	public int cameraMode;
}
