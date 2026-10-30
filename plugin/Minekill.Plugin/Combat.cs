using System;
using System.Collections.Generic;
using HarmonyLib;
using Minekill.Link;
using UnityEngine;
using UnityEngine.InputSystem;
using LinkEnd = Minekill.Link.Link;

namespace Minekill
{
    // Minecraft's sword against ULTRAKILL's enemies, and ULTRAKILL's enemies against Steve:
    //  - enemies near V1 go to Minecraft (actor table) as invisible stand-ins it can hit;
    //  - a left click in Minecraft-hand mode makes Minecraft attack (its reach, cooldown, crits,
    //    enchantments); the damage it computes comes back (EvHitActor) and is dealt here with
    //    ULTRAKILL's own EnemyIdentifier.DeliverDamage;
    //  - while the hand is Minecraft's, hits on V1 go to Minecraft's health instead (armour counts),
    //    and Minecraft's death is V1's death.
    internal static class Combat
    {
        private const float RangeBlocks = 24f;
        private static readonly ActorRecord[] records = new ActorRecord[Proto.MaxActors];
        private static readonly Dictionary<uint, EnemyIdentifier> byId = new Dictionary<uint, EnemyIdentifier>();
        private static float nextScan;
        internal static bool KillingV1; // lets our own lethal hit through the GetHurt patch

        public static int Mirrored { get; private set; }
        public static int HitsApplied { get; private set; }
        public static int HurtsForwarded { get; private set; }
        public static string LastHit { get; private set; } = "none";

        public static bool Engaged(LinkEnd link) => HandMode.Active && link != null && link.PeerAlive;

        private static Coords lastCoords;

        public static void Tick(LinkEnd link, Coords coords, Vector3 v1Feet, Camera cam, Bounds v1Bounds)
        {
            if (link == null)
            {
                return;
            }
            lastCoords = coords;
            bool engaged = Engaged(link);
            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + 0.05f; // 20 Hz, Minecraft's tick rate
                Publish(link, coords, v1Feet, engaged);
            }
            if (engaged && !McScreen.Open) // clicks on an open Minecraft screen are the screen's
            {
                Interaction.Tick(link, coords, cam, v1Bounds);
                if (Compat.On(Compat.Feature.Parry))
                {
                    Parrying.ShieldTick((Bridge.GuestFlags & Proto.GuestBlocking) != 0, v1Bounds);
                }
            }
            else
            {
                Interaction.ReleaseAll(link);
                if (engaged && Compat.On(Compat.Feature.Parry))
                {
                    Parrying.ShieldTick((Bridge.GuestFlags & Proto.GuestBlocking) != 0, v1Bounds);
                }
            }
            while (link.TryPopEvent(out McEvent e))
            {
                Apply(e);
            }
        }

        private static void Publish(LinkEnd link, Coords coords, Vector3 v1Feet, bool engaged)
        {
            int count = 0;
            byId.Clear();
            if (engaged && MonoSingleton<EnemyTracker>.TryGetInstance(out EnemyTracker tracker) && tracker != null)
            {
                float range = RangeBlocks * (float)coords.UnitsPerBlock;
                foreach (EnemyIdentifier eid in tracker.GetCurrentEnemies())
                {
                    if (eid == null || eid.dead || !eid.isActiveAndEnabled || count >= Proto.MaxActors || !TryBounds(eid, out Bounds b))
                    {
                        continue;
                    }
                    if ((b.center - v1Feet).sqrMagnitude > range * range)
                    {
                        continue;
                    }
                    uint id = (uint)eid.GetInstanceID();
                    coords.ToMc(b.center.x, b.min.y, b.center.z, out double x, out double y, out double z);
                    records[count++] = new ActorRecord
                    {
                        Id = id,
                        Flags = Proto.ActorHostile,
                        X = (float)x,
                        Y = (float)y,
                        Z = (float)z,
                        Yaw = Coords.YawToMc(eid.transform.eulerAngles.y),
                        Width = Mathf.Max(b.size.x, b.size.z) / (float)coords.UnitsPerBlock,
                        Height = b.size.y / (float)coords.UnitsPerBlock,
                        HealthFrac = 1f,
                        Name = eid.enemyType.ToString(),
                    };
                    byId[id] = eid;
                }
            }
            link.WriteActors(records, count); // 0 when not engaged: Minecraft removes its stand-ins
            Mirrored = count;
        }

        // The enemy's size from its colliders (triggers excluded), or its renderers as a fallback.
        private static bool TryBounds(EnemyIdentifier eid, out Bounds b)
        {
            b = default;
            bool any = false;
            foreach (Collider c in eid.GetComponentsInChildren<Collider>())
            {
                if (c == null || !c.enabled || c.isTrigger)
                {
                    continue;
                }
                if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
            }
            if (!any)
            {
                foreach (Renderer r in eid.GetComponentsInChildren<Renderer>())
                {
                    if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
                }
            }
            return any && b.size.y > 0.01f;
        }

        private static void Apply(McEvent e)
        {
            switch (e.Type)
            {
                case Proto.EvHitActor:
                    if (!byId.TryGetValue(e.Id, out EnemyIdentifier eid) || eid == null || eid.dead)
                    {
                        LastHit = $"hit on #{e.Id} ignored (enemy gone)";
                        return;
                    }
                    float damage = e.A * Plugin.DamageToUltrakill.Value;
                    // Minecraft's knockback direction (x, z; Unity's Z is Minecraft's -Z), as an ULTRAKILL-sized push.
                    Vector3 kb = new Vector3(e.B, 0f, -e.C) * (e.D * 1000f);
                    Vector3 point = TryBounds(eid, out Bounds b) ? b.center : eid.transform.position;
                    float before = eid.health;
                    bool explosion = (e.Flags & Proto.HitExplosion) != 0; // Minecraft's own damage type
                    eid.hitter = explosion ? "explosion" : "minecraft";
                    eid.DeliverDamage(eid.gameObject, kb, point, damage, false, 0f, null, false, explosion);
                    HitsApplied++;
                    if (eid.dead)
                    {
                        Synergy.Loot(Bridge.CurrentLink, eid);
                        try
                        {
                            Style.Kill(eid, explosion);
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.LogWarning($"style points failed: {ex.Message}");
                        }
                    }
                    LastHit = $"{eid.enemyType} hit for {e.A:F1} MC = {damage:F2} UK damage, health {before:F2} -> {eid.health:F2}{(eid.dead ? " (dead)" : "")}";
                    Plugin.Log.LogInfo($"combat: {LastHit}");
                    break;
                case Proto.EvParry:
                    // ULTRAKILL's own parry of the enemy in front when there is one; otherwise just the flash.
                    Camera cam = MonoSingleton<CameraController>.TryGetInstance(out CameraController cc) && cc != null ? cc.cam : null;
                    if (cam == null || !Compat.On(Compat.Feature.Parry) || !Parrying.ShieldParryMelee(cam.transform.position, cam.transform.forward))
                    {
                        Synergy.Parry();
                    }
                    break;
                case Proto.EvExplosion:
                    if (lastCoords != null)
                    {
                        BlockWorld.Explode(lastCoords, e.A, e.B, e.C, e.D);
                    }
                    break;
                case Proto.EvPlayerDied:
                    Plugin.Log.LogInfo("combat: Minecraft's player died: V1 dies too");
                    KillV1();
                    break;
            }
        }

        private static void KillV1()
        {
            if (!MonoSingleton<NewMovement>.TryGetInstance(out NewMovement nm) || nm == null || nm.dead)
            {
                return;
            }
            KillingV1 = true;
            try
            {
                nm.GetHurt(999, false, 0f, false, false, 1f, true);
            }
            finally
            {
                KillingV1 = false;
            }
        }

        internal static bool ShouldRedirectHurt() => !KillingV1 && Engaged(Bridge.CurrentLink);

        // The damage ULTRAKILL actually dealt to V1 (after its own invincibility frames, dash, difficulty...)
        // goes to Minecraft's health instead.
        internal static void ForwardHurt(int dealt, bool explosion)
        {
            LinkEnd link = Bridge.CurrentLink;
            if (dealt <= 0 || link == null)
            {
                return;
            }
            int hundredths = Mathf.RoundToInt(dealt * Plugin.DamageToMinecraft.Value * 100f);
            link.PushInput(new InputEvent { Type = Proto.InHurt, Code = explosion ? Proto.HurtOther : Proto.HurtMelee, A = hundredths });
            HurtsForwarded++;
            Plugin.Log.LogInfo($"combat: V1 took {dealt} ULTRAKILL damage -> {hundredths / 100f:F2} to Minecraft's health");
        }
    }

    // While the hand is Minecraft's, hits on V1 go to Minecraft's health; Minecraft's death kills V1.
    // ULTRAKILL still decides how much a hit does (its invincibility frames, dash, difficulty, effects):
    // V1's health is raised out of reach for the call and the drop is what Minecraft receives, then
    // V1's health is put back. Signature checked by Compat (DamageIn).
    [HarmonyPatch(typeof(NewMovement), nameof(NewMovement.GetHurt))]
    internal static class GetHurtPatch
    {
        private const int Buffer = 10000;

        private static void Prefix(NewMovement __instance, out int __state)
        {
            __state = -1;
            try
            {
                if (Combat.ShouldRedirectHurt())
                {
                    __state = __instance.hp;
                    __instance.hp = Buffer;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"redirecting a hit to Minecraft failed: {e.Message}");
            }
        }

        // A finalizer runs even if GetHurt throws, so V1's real health is always restored.
        private static Exception Finalizer(Exception __exception, NewMovement __instance, int __state, bool explosion)
        {
            if (__state >= 0)
            {
                int dealt = Buffer - __instance.hp;
                __instance.hp = __state;
                try
                {
                    Combat.ForwardHurt(dealt, explosion);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"forwarding a hit to Minecraft failed: {e.Message}");
                }
            }
            return __exception;
        }
    }
}
