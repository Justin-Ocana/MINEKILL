using System;
using System.Collections.Generic;
using HarmonyLib;
using Minekill.Link;
using UnityEngine;
using LinkEnd = Minekill.Link.Link;

namespace Minekill
{
    // ULTRAKILL's side of the cross-game rules (Minecraft's side: mcmod Synergy.java):
    //  - Bloodcraft: blood that heals V1 heals Steve too (NewMovement.GetHealth);
    //  - loot: enemies killed with Minecraft things drop Minecraft loot and experience into the hotbar,
    //    so fighting refills the arsenal (Strays drop arrows, Schisms gunpowder and TNT, ...);
    //  - ULTRAKILL explosions break Minecraft blocks and set off Minecraft TNT (Explosion.Start);
    //  - Steve's shield parry is an ULTRAKILL parry (flash, style).
    internal static class Synergy
    {
        private static readonly Dictionary<string, (ushort Item, int Count)[]> loot = new Dictionary<string, (ushort, int)[]>
        {
            ["Filth"] = new[] { (Proto.LootRottenFlesh, 1) },
            ["Stray"] = new[] { (Proto.LootArrow, 4), (Proto.LootSnowball, 2) },
            ["Schism"] = new[] { (Proto.LootGunpowder, 2), (Proto.LootTnt, 1) },
            ["Soldier"] = new[] { (Proto.LootArrow, 6), (Proto.LootTnt, 1) },
            ["Drone"] = new[] { (Proto.LootEnderPearl, 1) },
            ["Streetcleaner"] = new[] { (Proto.LootTnt, 2), (Proto.LootGunpowder, 1) },
            ["Swordsmachine"] = new[] { (Proto.LootIronIngot, 3), (Proto.LootDiamond, 1) },
            ["Stalker"] = new[] { (Proto.LootTnt, 3) },
            ["Mindflayer"] = new[] { (Proto.LootEnderPearl, 3) },
            ["MaliciousFace"] = new[] { (Proto.LootEnderPearl, 2), (Proto.LootGunpowder, 2) },
            ["Cerberus"] = new[] { (Proto.LootGoldenApple, 1), (Proto.LootCookedBeef, 4) },
            ["HideousMass"] = new[] { (Proto.LootCookedBeef, 8), (Proto.LootGoldenApple, 2) },
            ["Virtue"] = new[] { (Proto.LootGoldenApple, 2) },
            ["Idol"] = new[] { (Proto.LootDiamond, 1) },
            ["Turret"] = new[] { (Proto.LootArrow, 8), (Proto.LootIronIngot, 1) },
            ["Gutterman"] = new[] { (Proto.LootIronIngot, 4) },
            ["Guttertank"] = new[] { (Proto.LootTnt, 4) },
            ["Mannequin"] = new[] { (Proto.LootCookedBeef, 2) },
            ["Ferryman"] = new[] { (Proto.LootEnderPearl, 2), (Proto.LootDiamond, 1) },
        };

        public static int LootDrops { get; private set; }
        public static int Parries { get; private set; }
        public static int ExplosionsSent { get; private set; }

        // A kill made with a Minecraft thing: its loot and experience go to Steve.
        public static void Loot(LinkEnd link, EnemyIdentifier eid)
        {
            if (link == null || eid == null)
            {
                return;
            }
            string type = eid.enemyType.ToString();
            int xp = Mathf.Clamp(Mathf.RoundToInt(eid.totalHealthModifier * 3f), 2, 30);
            if (!loot.TryGetValue(type, out var drops))
            {
                drops = new[] { (Proto.LootArrow, 2) };
            }
            foreach (var (item, count) in drops)
            {
                link.PushInput(new InputEvent { Type = Proto.InLoot, Code = item, A = count, B = xp });
                xp = 0; // experience once per kill
            }
            if (eid.isBoss)
            {
                link.PushInput(new InputEvent { Type = Proto.InLoot, Code = Proto.LootEnchantedGoldenApple, A = 1, B = 50 });
            }
            LootDrops++;
            Plugin.Log.LogInfo($"synergy: {type} dropped Minecraft loot");
        }

        public static void Parry()
        {
            Parries++;
            try
            {
                if (MonoSingleton<TimeController>.TryGetInstance(out TimeController tc) && tc != null)
                {
                    tc.ParryFlash();
                }
                Style.Add("minekill.shieldparry", "SHIELD PARRY", 100);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"parry effects failed: {e.Message}");
            }
            Plugin.Log.LogInfo("synergy: shield parry");
        }

        internal static void SendExplosion(Explosion ex)
        {
            LinkEnd link = Bridge.CurrentLink;
            Coords coords = Bridge.CurrentCoords;
            if (link == null || coords == null || !link.PeerAlive || ex == null || ex.harmless)
            {
                return;
            }
            Vector3 p = ex.transform.position;
            coords.ToMc(p.x, p.y, p.z, out double x, out double y, out double z);
            float radius = Mathf.Clamp(ex.maxSize / (float)coords.UnitsPerBlock, 0.5f, 8f);
            link.PushInput(new InputEvent
            {
                Type = Proto.InExplode,
                Code = (ushort)Mathf.RoundToInt(radius * 10f),
                A = (int)Math.Round(x * 100),
                B = (int)Math.Round(y * 100),
                C = (int)Math.Round(z * 100),
            });
            ExplosionsSent++;
        }

        internal static void SendHeal(int health)
        {
            LinkEnd link = Bridge.CurrentLink;
            if (health <= 0 || !Combat.Engaged(link))
            {
                return;
            }
            link.PushInput(new InputEvent { Type = Proto.InHeal, A = Mathf.RoundToInt(health * Plugin.DamageToMinecraft.Value * 100f) });
        }
    }

    // Every ULTRAKILL explosion (rockets, grenades, cores, enemies'...) reaches Minecraft's blocks and TNT.
    [HarmonyPatch(typeof(Explosion), "Start")]
    internal static class ExplosionStartPatch
    {
        private static void Postfix(Explosion __instance)
        {
            try
            {
                Synergy.SendExplosion(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"explosion to Minecraft failed: {e.Message}");
            }
        }
    }

    // Bloodcraft: whatever heals V1 (blood, mostly) heals Steve as well while the hand is Minecraft's.
    [HarmonyPatch(typeof(NewMovement), nameof(NewMovement.GetHealth))]
    internal static class GetHealthPatch
    {
        private static void Prefix(int health)
        {
            try
            {
                Synergy.SendHeal(health);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"heal to Minecraft failed: {e.Message}");
            }
        }
    }
}
