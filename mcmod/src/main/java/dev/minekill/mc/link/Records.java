package dev.minekill.mc.link;

/** Small value types carried by the link's rings and tables. */
public final class Records {
	private Records() {
	}

	/** ULTRAKILL -> Minecraft input/command. */
	public record InputEvent(int type, int code, int a, int b, int c) {
	}

	/** An ULTRAKILL enemy near V1; feet position in Minecraft coordinates. */
	public record Actor(int id, int flags, float x, float y, float z, float yaw, float width, float height, float healthFrac, String name) {
		public boolean dead() {
			return (flags & Proto.ACTOR_DEAD) != 0;
		}
	}

	/** Minecraft -> ULTRAKILL event. */
	public record McEvent(int type, int id, float a, float b, float c, float d, int flags, int weapon) {
	}
}
