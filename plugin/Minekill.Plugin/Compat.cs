using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;

namespace Minekill
{
    // Every ULTRAKILL API MINEKILL touches is declared here and checked by reflection at startup.
    // A feature runs only if all of its checks pass; otherwise it stays off (and says why) instead
    // of calling into a game build whose types or signatures have changed.
    internal static class Compat
    {
        public enum Feature
        {
            PlayerPose,   // read V1's position and the camera's look
            RenderInfo,   // read ULTRAKILL's camera / render-texture setup (diagnostics)
            DamageIn,     // NewMovement.GetHurt (later: enemy hits -> Minecraft health)
            DamageOut,    // EnemyIdentifier.DeliverDamage (later: Minecraft hits -> enemies)
            WeaponToggle, // GunControl.NoWeapon / YesWeapon (later: Minecraft hand slot)
            MinecraftHand, // put V1's guns and arm away and keep their controllers idle
            Synergy,       // explosions -> Minecraft blocks, Bloodcraft, parry, loot, projectiles, jump crits
            Parry,         // ULTRAKILL's punch parry done with Minecraft swings and the shield
            McMovement,    // Minecraft's walk/sprint/sneak/jump/fall rules move V1 (NewMovement paused)
            McScreens,     // Minecraft's inventory and other screens, with ULTRAKILL's cursor
        }

        private sealed class Check
        {
            public Feature Feature;
            public string What;
            public Func<bool> Test;
        }

        private static readonly Dictionary<Feature, bool> enabled = new Dictionary<Feature, bool>();
        public static readonly List<string> Report = new List<string>();

        public static bool On(Feature f) => enabled.TryGetValue(f, out bool v) && v;

        public static void Disable(Feature f) => enabled[f] = false;

        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        private static Assembly game;

        private static Type T(string name) => game?.GetType(name, false);

        private static Check Type(Feature f, string type) =>
            new Check { Feature = f, What = $"type {type}", Test = () => T(type) != null };

        private static Check Field(Feature f, string type, string field, string fieldType) =>
            new Check
            {
                Feature = f,
                What = $"field {type}.{field} : {fieldType}",
                Test = () => T(type)?.GetField(field, Any)?.FieldType.Name == fieldType,
            };

        private static Check Method(Feature f, string type, string method, params string[] parameterTypes) =>
            new Check
            {
                Feature = f,
                What = $"method {type}.{method}({string.Join(", ", parameterTypes)})",
                Test = () => T(type)?.GetMethods(Any).Any(m => m.Name == method &&
                    m.GetParameters().Select(p => p.ParameterType.Name).SequenceEqual(parameterTypes)) == true,
            };

        private static Check Generic(Feature f, string what, Func<bool> test) => new Check { Feature = f, What = what, Test = test };

        private static Check SingletonTryGet(Feature f, string type) =>
            Generic(f, $"MonoSingleton<{type}>.TryGetInstance(out {type})", () =>
            {
                Type t = T(type);
                Type open = T("MonoSingleton`1");
                if (t == null || open == null) return false;
                Type closed = open.MakeGenericType(t);
                MethodInfo m = closed.GetMethod("TryGetInstance", BindingFlags.Public | BindingFlags.Static);
                return m != null && m.ReturnType == typeof(bool) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsByRef;
            });

        public static void Run(ManualLogSource log)
        {
            game = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            var checks = new List<Check>
            {
                Generic(Feature.PlayerPose, "Assembly-CSharp loaded", () => game != null),
                SingletonTryGet(Feature.PlayerPose, "NewMovement"),
                SingletonTryGet(Feature.PlayerPose, "CameraController"),
                Field(Feature.PlayerPose, "NewMovement", "playerCollider", "CapsuleCollider"),
                Field(Feature.PlayerPose, "NewMovement", "dead", "Boolean"),
                Field(Feature.PlayerPose, "NewMovement", "hp", "Int32"),
                Field(Feature.PlayerPose, "CameraController", "cam", "Camera"),
                Field(Feature.PlayerPose, "CameraController", "rotationX", "Single"),
                Generic(Feature.PlayerPose, "property SceneHelper.CurrentScene : String (static)",
                    () => T("SceneHelper")?.GetProperty("CurrentScene", BindingFlags.Public | BindingFlags.Static)?.PropertyType == typeof(string)),

                SingletonTryGet(Feature.RenderInfo, "PostProcessV2_Handler"),
                Field(Feature.RenderInfo, "PostProcessV2_Handler", "mainCam", "Camera"),
                Field(Feature.RenderInfo, "PostProcessV2_Handler", "hudCam", "Camera"),
                Field(Feature.RenderInfo, "PostProcessV2_Handler", "mainTex", "RenderTexture"),
                Field(Feature.RenderInfo, "PostProcessV2_Handler", "depthBuffer", "RenderTexture"),
                Field(Feature.RenderInfo, "PostProcessV2_Handler", "downscaleResolution", "Single"),

                Method(Feature.DamageIn, "NewMovement", "GetHurt", "Int32", "Boolean", "Single", "Boolean", "Boolean", "Single", "Boolean"),
                Method(Feature.DamageOut, "EnemyIdentifier", "DeliverDamage", "GameObject", "Vector3", "Vector3", "Single", "Boolean", "Single", "GameObject", "Boolean", "Boolean"),
                SingletonTryGet(Feature.DamageOut, "EnemyTracker"),
                Method(Feature.DamageOut, "EnemyTracker", "GetCurrentEnemies"),
                Field(Feature.DamageOut, "EnemyIdentifier", "health", "Single"),
                Field(Feature.DamageOut, "EnemyIdentifier", "dead", "Boolean"),
                Field(Feature.DamageOut, "EnemyIdentifier", "hitter", "String"),
                Field(Feature.DamageOut, "EnemyIdentifier", "enemyType", "EnemyType"),
                Method(Feature.WeaponToggle, "GunControl", "NoWeapon"),
                Method(Feature.WeaponToggle, "GunControl", "YesWeapon"),

                Method(Feature.DamageOut, "CameraController", "CameraShake", "Single"),
                Method(Feature.DamageOut, "StyleHUD", "RegisterStyleItem", "String", "String"),
                Method(Feature.DamageOut, "StyleHUD", "AddPoints", "Int32", "String", "GameObject", "EnemyIdentifier", "Int32", "String", "String"),
                Method(Feature.Synergy, "Explosion", "Start"),
                Field(Feature.Synergy, "Explosion", "maxSize", "Single"),
                Field(Feature.Synergy, "Explosion", "harmless", "Boolean"),
                Method(Feature.Synergy, "NewMovement", "GetHealth", "Int32", "Boolean", "Boolean", "Boolean"),
                Field(Feature.Synergy, "NewMovement", "gc", "GroundCheckGroup"),
                Generic(Feature.Synergy, "property GroundCheckGroup.onGround : Boolean",
                    () => T("GroundCheckGroup")?.GetProperty("onGround")?.PropertyType == typeof(bool)),
                Field(Feature.Synergy, "NewMovement", "rb", "Rigidbody"),
                SingletonTryGet(Feature.Synergy, "TimeController"),
                Method(Feature.Synergy, "TimeController", "ParryFlash"),
                Field(Feature.Synergy, "EnemyIdentifier", "totalHealthModifier", "Single"),
                Field(Feature.Synergy, "EnemyIdentifier", "isBoss", "Boolean"),
                Field(Feature.Synergy, "EnemyIdentifierIdentifier", "eid", "EnemyIdentifier"),
                Method(Feature.Parry, "Punch", "ActiveStart"),
                Method(Feature.Parry, "Punch", "TryParryProjectile", "Transform", "Boolean"),
                Method(Feature.Parry, "Punch", "ParryProjectile", "Projectile"),
                Method(Feature.Parry, "Punch", "Parry", "Boolean", "EnemyIdentifier", "String"),
                Field(Feature.Parry, "Punch", "type", "FistType"),
                Field(Feature.Parry, "FistControl", "currentPunch", "Punch"),
                Method(Feature.Parry, "FistControl", "RefreshArm"),
                Field(Feature.Parry, "Projectile", "friendly", "Boolean"),
                Field(Feature.Parry, "Projectile", "playerBullet", "Boolean"),
                Field(Feature.Parry, "Projectile", "unparryable", "Boolean"),
                SingletonTryGet(Feature.MinecraftHand, "GunControl"),
                SingletonTryGet(Feature.MinecraftHand, "FistControl"),
                Method(Feature.MinecraftHand, "GunControl", "Update"),
                Method(Feature.MinecraftHand, "GunControl", "NoWeapon"),
                Method(Feature.MinecraftHand, "GunControl", "YesWeapon"),
                Method(Feature.MinecraftHand, "FistControl", "Update"),
                Method(Feature.MinecraftHand, "FistControl", "NoFist"),
                Method(Feature.MinecraftHand, "FistControl", "YesFist"),

                SingletonTryGet(Feature.McMovement, "NewMovement"),
                Method(Feature.McMovement, "NewMovement", "Update"),
                Method(Feature.McMovement, "NewMovement", "FixedUpdate"),
                Method(Feature.McMovement, "NewMovement", "StopSlide"),
                Field(Feature.McMovement, "NewMovement", "rb", "Rigidbody"),
                Field(Feature.McMovement, "NewMovement", "gc", "GroundCheckGroup"),
                Generic(Feature.McMovement, "property GroundCheckGroup.onGround : Boolean",
                    () => T("GroundCheckGroup")?.GetProperty("onGround")?.PropertyType == typeof(bool)),
                Field(Feature.McMovement, "NewMovement", "playerCollider", "CapsuleCollider"),
                Field(Feature.McMovement, "NewMovement", "activated", "Boolean"),
                Field(Feature.McMovement, "NewMovement", "dead", "Boolean"),
                Field(Feature.McMovement, "NewMovement", "levelOver", "Boolean"),
                Field(Feature.McMovement, "NewMovement", "sliding", "Boolean"),
                Field(Feature.McMovement, "NewMovement", "hurtInvincibility", "Single"),
                Field(Feature.McMovement, "CameraController", "cam", "Camera"),

                // The screens hold V1 still through McMovement, so they need its checks too.
                Method(Feature.McScreens, "NewMovement", "Update"),
                Method(Feature.McScreens, "NewMovement", "FixedUpdate"),
                Field(Feature.McScreens, "NewMovement", "rb", "Rigidbody"),
                Field(Feature.McScreens, "NewMovement", "activated", "Boolean"),
                Field(Feature.McScreens, "CameraController", "activated", "Boolean"),
                SingletonTryGet(Feature.McScreens, "OptionsManager"),
                Method(Feature.McScreens, "OptionsManager", "Update"),
            };

            foreach (Feature f in Enum.GetValues(typeof(Feature)))
            {
                enabled[f] = true;
            }
            Report.Clear();
            foreach (Check c in checks)
            {
                bool ok;
                try
                {
                    ok = c.Test();
                }
                catch (Exception e)
                {
                    ok = false;
                    log.LogWarning($"check '{c.What}' threw {e.GetType().Name}: {e.Message}");
                }
                if (!ok)
                {
                    enabled[c.Feature] = false;
                }
                string line = $"[{(ok ? "PASS" : "FAIL")}] {c.Feature}: {c.What}";
                Report.Add(line);
                if (ok) log.LogInfo(line); else log.LogWarning(line);
            }
            if (!enabled[Feature.McMovement] || !enabled[Feature.MinecraftHand])
            {
                enabled[Feature.McScreens] = false; // V1 couldn't be held still, or there's no Minecraft hand to open them from
            }
            foreach (Feature f in Enum.GetValues(typeof(Feature)))
            {
                log.LogInfo($"feature {f}: {(enabled[f] ? "ON" : "OFF (a check failed)")}");
            }
        }
    }
}
