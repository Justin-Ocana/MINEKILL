using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Minekill
{
    [BepInPlugin(Guid, "MINEKILL", Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "dev.minekill";
        public const string Version = "0.0.1";

        internal static ManualLogSource Log;
        internal static ConfigEntry<float> UnitsPerBlock;
        internal static ConfigEntry<string> MappingName;
        internal static ConfigEntry<string> DebugKey;
        internal static ConfigEntry<bool> GuestMarker;
        internal static ConfigEntry<bool> RunInBackground;
        internal static ConfigEntry<string> MarkerMaterialKey;
        internal static ConfigEntry<string> OcclusionTestKey;
        internal static ConfigEntry<string> HandToggleKey;
        internal static ConfigEntry<string> MovementToggleKey;
        internal static ConfigEntry<bool> MinecraftCamera;
        internal static ConfigEntry<bool> MinecraftViewBob;
        internal static ConfigEntry<float> MinecraftSpeed;
        internal static ConfigEntry<float> MinecraftFovBonus;
        internal static ConfigEntry<float> DamageToUltrakill;
        internal static ConfigEntry<float> DamageToMinecraft;

        private static Bridge bridge;
        private static McMovement movement;

        private void Awake()
        {
            Log = Logger;
            UnitsPerBlock = Config.Bind("World", "UnitsPerBlock", 2.0f,
                "ULTRAKILL units per Minecraft block (2 makes Steve, 1.8 blocks, about V1's height).");
            MappingName = Config.Bind("Link", "MappingName", Minekill.Link.Proto.DefaultMappingName,
                "Name of the shared memory Minecraft opens (SkyCraft's mod: -Dskycraft.link=<this>).");
            DebugKey = Config.Bind("Debug", "PanelKey", "f10",
                "Input System key control name that shows/hides the debug panel (e.g. f10, f11, backquote).");
            MarkerMaterialKey = Config.Bind("Debug", "MarkerMaterialKey", "f9",
                "Key that cycles the marker cube's material (new ULTRAKILL/Master, copy of the nearest level material, Unity Standard).");
            DamageToUltrakill = Config.Bind("Combat", "DamageToUltrakill", 0.3f,
                "ULTRAKILL damage per point of Minecraft damage (a diamond sword hit is 7; a Filth has 0.5 health).");
            DamageToMinecraft = Config.Bind("Combat", "DamageToMinecraft", 0.2f,
                "Minecraft damage per point of ULTRAKILL damage to V1 (V1's 100 health = Minecraft's 20).");
            HandToggleKey = Config.Bind("Controls", "MinecraftHandKey", "backquote",
                "Key that switches between V1's weapons and Minecraft's hand (hotbar, held item, HUD). Input System key name; " +
                "backquote is the key left of 1 (º on a Spanish keyboard).");
            MovementToggleKey = Config.Bind("Controls", "MinecraftMovementKey", "m",
                "Key that switches V1 between ULTRAKILL's movement and Minecraft's (walk, sprint with Ctrl or double-tap W, " +
                "sneak with Shift without falling off edges, Minecraft's jump with Space, gravity and fall damage).");
            MinecraftCamera = Config.Bind("Controls", "MinecraftCamera", true,
                "With Minecraft's movement on, ULTRAKILL's camera uses Minecraft's field of view (its FOV setting, +15% sprinting, " +
                "bow zoom), Steve's eye height (lower when sneaking) and Minecraft's view bobbing.");
            MinecraftViewBob = Config.Bind("Controls", "MinecraftViewBob", true,
                "Minecraft's view bobbing on ULTRAKILL's camera while walking with Minecraft's movement (needs MinecraftCamera).");
            MinecraftSpeed = Config.Bind("Controls", "MinecraftSpeed", 1.5f,
                new ConfigDescription("Walking and sprinting speed under Minecraft's movement, times Minecraft's (1 = Minecraft: 4.3 and 5.6 blocks/s; " +
                    "1.5 = 6.5 and 8.4, between Minecraft and ULTRAKILL). Sneaking and the jump stay Minecraft's.", new AcceptableValueRange<float>(0.5f, 3f)));
            MinecraftFovBonus = Config.Bind("Controls", "MinecraftFovBonus", 10f,
                new ConfigDescription("Degrees added to Minecraft's field of view on ULTRAKILL's camera (Minecraft's FOV setting, 70 by default, plus this).",
                    new AcceptableValueRange<float>(-30f, 60f)));
            // Not F8: that opens ULTRAKILL's own developer console (seen 2026-10-08).
            OcclusionTestKey = Config.Bind("Debug", "SelfTestKey", "f6",
                "Key that runs the occlusion self-test against the wall straight ahead (results in the log, images in BepInEx\\minekill-tests). Avoid F8 (ULTRAKILL's console).");
            GuestMarker = Config.Bind("Debug", "GuestMarker", true,
                "Draw a one-block cube with ULTRAKILL's own shader where Minecraft's player is (render-integration test).");

            RunInBackground = Config.Bind("Link", "RunInBackground", true,
                "Keep ULTRAKILL running while its window is unfocused. Unity otherwise stops every frame (and MINEKILL's " +
                "heartbeat) as soon as another window, such as Minecraft's or a console, gets the focus.");

            Log.LogInfo($"MINEKILL {Version} | Unity {Application.unityVersion} | game {Application.version} | {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType})");
            Log.LogInfo($"Application.runInBackground was {Application.runInBackground}; config RunInBackground={RunInBackground.Value}");
            if (RunInBackground.Value)
            {
                Application.runInBackground = true;
            }
            Compat.Run(Log);
            ApplyPatches();

            EnsureBridge();
            // ULTRAKILL's scene loader disables scripts that aren't in the DontDestroyOnLoad scene
            // (observed by Killcraft); re-check on every load.
            SceneManager.sceneLoaded += (scene, _) => EnsureBridge();
        }

        // Harmony patches only for features whose compatibility checks passed.
        private static void ApplyPatches()
        {
            var harmony = new HarmonyLib.Harmony(Guid);
            if (Compat.On(Compat.Feature.Synergy))
            {
                foreach (System.Type patch in new[] { typeof(ExplosionStartPatch), typeof(GetHealthPatch) })
                {
                    try
                    {
                        harmony.CreateClassProcessor(patch).Patch();
                        Log.LogInfo($"patched: {patch.Name}");
                    }
                    catch (System.Exception e)
                    {
                        Log.LogError($"patch {patch.Name} failed, cross-game synergies off: {e.Message}");
                        Compat.Disable(Compat.Feature.Synergy);
                    }
                }
            }
            if (Compat.On(Compat.Feature.MinecraftHand))
            {
                if (Compat.On(Compat.Feature.DamageIn))
                {
                    try
                    {
                        harmony.CreateClassProcessor(typeof(GetHurtPatch)).Patch();
                        Log.LogInfo("patched: GetHurtPatch");
                    }
                    catch (System.Exception e)
                    {
                        Log.LogError($"patch GetHurtPatch failed, hits on V1 stay ULTRAKILL's: {e.Message}");
                        Compat.Disable(Compat.Feature.DamageIn);
                    }
                }
                foreach (System.Type patch in new[] { typeof(GunControlUpdatePatch), typeof(FistControlUpdatePatch) })
                {
                    try
                    {
                        harmony.CreateClassProcessor(patch).Patch();
                        Log.LogInfo($"patched: {patch.Name}");
                    }
                    catch (System.Exception e)
                    {
                        Log.LogError($"patch {patch.Name} failed, Minecraft hand mode disabled: {e.Message}");
                        Compat.Disable(Compat.Feature.MinecraftHand);
                    }
                }
            }
            if (Compat.On(Compat.Feature.McMovement))
            {
                TryPatch(harmony, typeof(NewMovementUpdatePatch), Compat.Feature.McMovement);
                TryPatch(harmony, typeof(NewMovementFixedUpdatePatch), Compat.Feature.McMovement);
            }
            if (!Compat.On(Compat.Feature.McMovement) || !Compat.On(Compat.Feature.MinecraftHand))
            {
                Compat.Disable(Compat.Feature.McScreens);
            }
            if (Compat.On(Compat.Feature.McScreens))
            {
                TryPatch(harmony, typeof(OptionsManagerUpdatePatch), Compat.Feature.McScreens);
            }
        }

        // A Harmony patch class, or the feature goes off if it can't be applied.
        private static void TryPatch(HarmonyLib.Harmony harmony, System.Type patch, Compat.Feature feature)
        {
            try
            {
                harmony.CreateClassProcessor(patch).Patch();
                Log.LogInfo($"patched: {patch.Name}");
            }
            catch (System.Exception e)
            {
                Log.LogError($"patch {patch.Name} failed, {feature} off: {e.Message}");
                Compat.Disable(feature);
            }
        }

        private static void EnsureBridge()
        {
            if (bridge == null)
            {
                var go = new GameObject("MINEKILL");
                go.hideFlags = HideFlags.HideInHierarchy;
                DontDestroyOnLoad(go);
                bridge = go.AddComponent<Bridge>();
                movement = go.AddComponent<McMovement>();
            }
            if (!bridge.enabled)
            {
                Log.LogInfo("bridge script was disabled by a scene load; re-enabling it");
                bridge.enabled = true;
            }
            if (movement != null && !movement.enabled)
            {
                movement.enabled = true;
            }
        }
    }
}
