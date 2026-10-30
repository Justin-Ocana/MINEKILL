using System;
using System.Text;
using Minekill.Link;
using UnityEngine;
using UnityEngine.InputSystem;
using LinkEnd = Minekill.Link.Link;

namespace Minekill
{
    // ULTRAKILL's end of the link, once per frame after the game's own scripts (the camera has
    // already turned this frame):
    //  - publishes V1's feet and look in Minecraft coordinates (HostState);
    //  - reads Minecraft's player back (GuestState) and measures how far it is from V1;
    //  - shows a debug panel and, optionally, a marker cube where Minecraft's player is.
    // Every game access is in a small method behind a Compat feature and a try/catch, so a changed
    // game build turns a feature off instead of breaking the game.
    [DefaultExecutionOrder(30000)]
    internal sealed class Bridge : MonoBehaviour
    {
        private LinkEnd link;
        internal static LinkEnd CurrentLink { get; private set; }
        internal static Coords CurrentCoords { get; private set; }
        internal static uint GuestFlags { get; private set; }
        private Transform v1Transform;
        private bool v1Airborne, v1Falling;
        private string linkError;
        private Coords coords;

        private string scene = "";
        private uint worldId;
        private uint epoch;
        private uint teleportSeq = 1;

        private bool havePose;
        private Vector3 feet;
        private Bounds v1Bounds;
        private Camera playerCam;
        private float camYaw, camPitch;
        private bool dead;

        private bool peerWasAlive;
        private bool haveGuest;
        private GuestState guest;
        private Vector3 guestUnity;
        private ulong lastGuestFrame;
        private float guestFps;
        private float guestFpsTimer;

        private bool panel = true;
        private float statusTimer;
        private int poseFailures;

        private void Start()
        {
            McCamera.Hook();
            coords = new Coords(Plugin.UnitsPerBlock.Value);
            CurrentCoords = coords;
            link = LinkEnd.CreateHost(Plugin.MappingName.Value, out linkError);
            CurrentLink = link;
            if (link == null)
            {
                Plugin.Log.LogError($"link disabled: {linkError}");
            }
            else
            {
                Plugin.Log.LogInfo($"link {link.MappingName} ready ({(link.ReusedMapping ? "reused" : "created")}, {Proto.MappingBytes >> 20} MB)");
            }
        }

        private void OnDestroy()
        {
            link?.Dispose();
            link = null;
        }

        private void Update()
        {
            try
            {
                Keyboard kb = Keyboard.current;
                if (kb != null && (kb[Plugin.DebugKey.Value] as UnityEngine.InputSystem.Controls.KeyControl)?.wasPressedThisFrame == true)
                {
                    panel = !panel;
                }
                if (kb != null && Compat.On(Compat.Feature.MinecraftHand) &&
                    (kb[Plugin.HandToggleKey.Value] as UnityEngine.InputSystem.Controls.KeyControl)?.wasPressedThisFrame == true)
                {
                    HandMode.Toggle();
                }
                if (kb != null && Compat.On(Compat.Feature.McMovement) &&
                    (kb[Plugin.MovementToggleKey.Value] as UnityEngine.InputSystem.Controls.KeyControl)?.wasPressedThisFrame == true)
                {
                    McMovement.Toggle();
                }
                if (kb != null && (kb[Plugin.OcclusionTestKey.Value] as UnityEngine.InputSystem.Controls.KeyControl)?.wasPressedThisFrame == true)
                {
                    RunOcclusionTest();
                }
                if (kb != null && (kb[Plugin.MarkerMaterialKey.Value] as UnityEngine.InputSystem.Controls.KeyControl)?.wasPressedThisFrame == true)
                {
                    markerMode = (markerMode + 1) % GuestMarker.ModeNames.Length;
                    if (marker != null)
                    {
                        GuestMarker.ApplyMaterial(marker, markerMode);
                    }
                }
            }
            catch (Exception)
            {
                // Bad key name in the config: keep the panel as it is.
            }
        }

        private void LateUpdate()
        {
            if (Compat.On(Compat.Feature.PlayerPose))
            {
                try
                {
                    TrackScene();
                    havePose = ReadPose();
                }
                catch (Exception e)
                {
                    havePose = false;
                    if (++poseFailures <= 3)
                    {
                        Plugin.Log.LogError($"reading V1's pose failed: {e}");
                    }
                }
            }

            if (link != null)
            {
                link.Heartbeat();
                Publish();
                ReadGuest();
            }

            try
            {
                McCamera.Frame(guest.Fov, haveGuest && link != null && link.PeerAlive);
                McScreen.Tick(link);
                HandMode.Tick(link);
                Overlay.Frame(link, HandMode.Active && link != null && link.PeerAlive);
                if (Compat.On(Compat.Feature.DamageOut) && havePose)
                {
                    Combat.Tick(link, coords, feet, playerCam, v1Bounds);
                }
                if (havePose && link != null)
                {
                    BlockWorld.Process(link, coords);
                    Projectiles.Tick(link, coords, playerCam, v1Transform);
                }
            }
            catch (Exception e)
            {
                if (++handFailures <= 3)
                {
                    Plugin.Log.LogError($"Minecraft hand/overlay failed: {e}");
                }
            }

            GuestMarkerUpdate();

            if (reprobeAt > 0f && Time.unscaledTime >= reprobeAt)
            {
                reprobeAt = -1f;
                Probe("5 s after scene change");
            }

            statusTimer += Time.unscaledDeltaTime;
            if (statusTimer >= 5f)
            {
                statusTimer = 0f;
                Plugin.Log.LogInfo(StatusLine());
            }
        }

        // ---- ULTRAKILL side ------------------------------------------------------------------
        private void TrackScene()
        {
            string now = SceneHelper.CurrentScene ?? "";
            if (now == scene)
            {
                return;
            }
            scene = now;
            Coords.LevelOrigin(scene, out double ox, out double oz);
            coords.OriginX = ox;
            coords.OriginZ = oz;
            worldId = Fnv(scene);
            epoch++;
            teleportSeq++;
            Plugin.Log.LogInfo($"scene '{scene}': world 0x{worldId:X8}, Minecraft origin ({ox}, {oz}), epoch {epoch}");
            // Shaders come with each scene's assets: rebuild the marker so it picks up this scene's.
            if (marker != null)
            {
                GuestMarker.Release(marker);
                marker = null;
            }
            HandMode.OnSceneChanged();
            BlockWorld.Clear();
            Projectiles.Clear();
            Probe("at scene change");
            // Level geometry can finish loading after the scene name changes: look again later.
            reprobeAt = Time.unscaledTime + 5f;
        }

        private float reprobeAt = -1f;
        private int handFailures;

        private void Probe(string when)
        {
            if (!Compat.On(Compat.Feature.RenderInfo))
            {
                return;
            }
            try
            {
                Plugin.Log.LogInfo($"render probe ({when}, scene '{scene}'):");
                RenderProbe.Log();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"render probe failed: {e.Message}");
            }
        }

        private bool ReadPose()
        {
            if (!MonoSingleton<NewMovement>.TryGetInstance(out NewMovement nm) || nm == null || !nm.isActiveAndEnabled)
            {
                return false;
            }
            // Feet from the capsule itself rather than an assumed offset from the transform.
            Bounds b = nm.playerCollider.bounds;
            feet = new Vector3(b.center.x, b.min.y, b.center.z);
            v1Bounds = b;
            v1Transform = nm.transform;
            try
            {
                // Off the ground and coming down: Minecraft's crit condition for Steve's swings.
                v1Airborne = !nm.gc.onGround;
                v1Falling = v1Airborne && nm.rb.velocity.y < -0.5f;
            }
            catch (Exception)
            {
                v1Airborne = v1Falling = false;
            }
            dead = nm.dead;
            if (MonoSingleton<CameraController>.TryGetInstance(out CameraController cc) && cc != null && cc.cam != null)
            {
                playerCam = cc.cam;
                Vector3 fwd = cc.cam.transform.forward;
                camYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                camPitch = Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg; // + = looking up
            }
            return true;
        }

        // ---- link ----------------------------------------------------------------------------
        private void Publish()
        {
            var s = new HostState
            {
                WorldId = worldId,
                Epoch = epoch,
                TeleportSeq = teleportSeq,
                ViewportW = (uint)Screen.width,
                ViewportH = (uint)Screen.height,
                GameHour = 12f,
            };
            if (havePose)
            {
                coords.ToMc(feet.x, feet.y, feet.z, out s.X, out s.Y, out s.Z);
                s.Yaw = Coords.YawToMc(camYaw);
                s.Pitch = Coords.PitchToMc(camPitch);
                s.Flags = dead ? Proto.HostMenuOpen : Proto.HostInGame;
                if (v1Airborne) s.Flags |= Proto.HostAirborne;
                if (v1Falling) s.Flags |= Proto.HostFalling;
                if (McMovement.Sprinting) s.Flags |= Proto.HostSprinting;
                if (McMovement.Controlling && McMovement.Sneaking) s.Flags |= Proto.HostSneaking;
            }
            else
            {
                s.Flags = Proto.HostLoading;
            }
            link.WriteHostState(s);
        }

        private void ReadGuest()
        {
            bool alive = link.PeerAlive;
            if (alive != peerWasAlive)
            {
                Plugin.Log.LogInfo(alive ? $"Minecraft side connected (pid {link.PeerPid})" : $"Minecraft side stopped answering ({link.PeerBeatAgeMs} ms)");
                peerWasAlive = alive;
                if (alive)
                {
                    teleportSeq++; // a (re)connected guest moves its player to V1 first
                }
            }
            if (!alive)
            {
                haveGuest = false;
                return;
            }
            if (link.TryReadGuestState(out GuestState fresh)) // a torn read keeps last frame's value
            {
                guest = fresh;
                haveGuest = true;
                GuestFlags = fresh.Flags;
                coords.ToUnity(guest.X, guest.Y, guest.Z, out double ux, out double uy, out double uz);
                guestUnity = new Vector3((float)ux, (float)uy, (float)uz);
            }
            guestFpsTimer += Time.unscaledDeltaTime;
            if (guestFpsTimer >= 1f)
            {
                guestFps = (guest.FrameCounter - lastGuestFrame) / guestFpsTimer;
                lastGuestFrame = guest.FrameCounter;
                guestFpsTimer = 0f;
            }
        }

        // ---- render-integration test ---------------------------------------------------------
        private GameObject marker;
        // 3 = ULTRAKILL/Master with the level's keywords: the one lit and fogged like the level
        // (A/B test in 0-1, 2026-10-08 21:41).
        private int markerMode = 3;

        private string occlusionResult = "not run";

        private void RunOcclusionTest()
        {
            Material m = null;
            try
            {
                if (!MonoSingleton<CameraController>.TryGetInstance(out CameraController cc) || cc == null || cc.cam == null)
                {
                    occlusionResult = "SKIP no player camera (be in a level)";
                }
                else
                {
                    m = GuestMarker.BuildMaterial(markerMode, cc.cam.transform.position);
                    string dir = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "minekill-tests", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    occlusionResult = OcclusionTest.Run(cc.cam, m, Plugin.UnitsPerBlock.Value, dir);
                }
            }
            catch (Exception e)
            {
                occlusionResult = $"ERROR {e.GetType().Name}: {e.Message}";
                Plugin.Log.LogError(e);
            }
            finally
            {
                if (m != null) Destroy(m); // built for this test only
            }
            Plugin.Log.LogInfo($"occlusion test (material: {GuestMarker.ModeNames[markerMode]}, camera '{Camera.main?.name}'): {occlusionResult}");
        }

        // The block sits on Minecraft's player's feet (half a block up = its centre).
        private Vector3 MarkerPosition => guestUnity + Vector3.up * (Plugin.UnitsPerBlock.Value * 0.5f);

        private void GuestMarkerUpdate()
        {
            // The debug cube marks Steve's feet; with Minecraft's hand on screen it's just in the way.
            bool show = Plugin.GuestMarker.Value && !HandMode.Active && havePose && haveGuest && (guest.Flags & Proto.GuestInWorld) != 0;
            if (show && marker == null)
            {
                marker = GuestMarker.Create(Plugin.UnitsPerBlock.Value, markerMode, MarkerPosition);
            }
            if (marker != null)
            {
                if (marker.activeSelf != show)
                {
                    marker.SetActive(show);
                }
                if (show)
                {
                    marker.transform.position = MarkerPosition;
                }
            }
        }

        // ---- diagnostics ---------------------------------------------------------------------
        private float PoseError => havePose && haveGuest ? Vector3.Distance(feet, guestUnity) / Plugin.UnitsPerBlock.Value : float.NaN;

        private string StatusLine()
        {
            var sb = new StringBuilder();
            sb.Append($"status scene='{scene}' pose={(havePose ? $"({feet.x:F2},{feet.y:F2},{feet.z:F2}) yaw={camYaw:F1} pitch={camPitch:F1}" : "none")}");
            sb.Append(link == null ? $" link=OFF({linkError})" : $" peer={(link.PeerAlive ? "alive" : "none")} beatAge={(link.PeerBeatAgeMs == long.MaxValue ? "never" : link.PeerBeatAgeMs + "ms")}");
            if (haveGuest)
            {
                sb.Append($" guest=({guest.X:F2},{guest.Y:F2},{guest.Z:F2}) ack={guest.TeleportAck}/{teleportSeq} guestFps={guestFps:F0} poseErrBlocks={PoseError:F2}");
            }
            sb.Append($" fps={1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f):F0}");
            return sb.ToString();
        }

        private GUIStyle style;

        private void OnGUI()
        {
            if (!panel)
            {
                return;
            }
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 13, wordWrap = true };
                style.normal.textColor = Color.white;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"MINEKILL {Plugin.Version}  ({Plugin.DebugKey.Value} hides, {Plugin.MarkerMaterialKey.Value}: marker material = {GuestMarker.ModeNames[markerMode]})");
            foreach (Compat.Feature f in Enum.GetValues(typeof(Compat.Feature)))
            {
                sb.Append(f).Append(Compat.On(f) ? " ON  " : " OFF  ");
            }
            sb.AppendLine();
            sb.AppendLine(StatusLine().Replace(" peer=", "\npeer=").Replace(" guest=", "\nguest=").Replace(" fps=", "\nfps="));
            sb.AppendLine($"{Plugin.OcclusionTestKey.Value} occlusion test: {occlusionResult}");
            sb.AppendLine($"{Plugin.HandToggleKey.Value}: Minecraft hand {(HandMode.Active ? "ON" : "off")}  overlay frames shown={Overlay.FramesShown}");
            sb.AppendLine($"combat: mirrored={Combat.Mirrored} hits={Combat.HitsApplied} hurtsToMC={Combat.HurtsForwarded} last: {Combat.LastHit}");
            sb.AppendLine($"blocks={BlockWorld.BlockCount} litTNT={BlockWorld.TntCount}  last use: {Interaction.Last}");
            sb.AppendLine($"parries: swings={Parrying.SwingParries} shield={Parrying.ShieldParries} shieldUp={(GuestFlags & Proto.GuestBlocking) != 0}");
            sb.AppendLine($"{Plugin.MovementToggleKey.Value}: Minecraft movement {(McMovement.Enabled ? "ON" : "off")} ({McMovement.Last}) falls->MC={McMovement.FallDamageSent} camera: {McCamera.Last}  E screen: {(McScreen.Open ? "OPEN" : "closed")} ({McScreen.Last})");
            sb.AppendLine($"synergy: loot={Synergy.LootDrops} parries={Synergy.Parries} explosions->MC={Synergy.ExplosionsSent} projectiles={Projectiles.Active} last: {Projectiles.Last}");
            GUI.Box(new Rect(10, 10, 700, 310), sb.ToString(), style);
        }

        private static uint Fnv(string s)
        {
            uint h = 2166136261;
            foreach (char c in s)
            {
                h = (h ^ c) * 16777619;
            }
            return h == 0 ? 1 : h;
        }
    }
}
