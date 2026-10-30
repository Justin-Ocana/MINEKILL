using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Minekill
{
    // ULTRAKILL's own parry, done with Minecraft things. It runs on V1's Feedbacker arm (kept hidden
    // while the hand is Minecraft's), so the parry rules, timing, reflections, heal and style are
    // ULTRAKILL's:
    //  - any swing (sword, bow, bare fist...) runs the punch's active frames: a projectile in front is
    //    knocked back, an enemy mid-attack is parried (as Killcraft does, MIT);
    //  - a raised shield parries enemy shots coming at V1 by itself;
    //  - a shield raised just in time against a melee hit parries that enemy (Punch.Parry).
    // Members checked by Compat (Parry): Punch.ActiveStart / TryParryProjectile / ParryProjectile /
    // Parry, FistControl.currentPunch / RefreshArm, Projectile.friendly / playerBullet / unparryable.
    internal static class Parrying
    {
        private static readonly MethodInfo activeStart = AccessTools.Method(typeof(Punch), "ActiveStart");
        private static readonly MethodInfo tryParryProjectile = AccessTools.Method(typeof(Punch), "TryParryProjectile", new[] { typeof(Transform), typeof(bool) });
        private static readonly MethodInfo parryProjectile = AccessTools.Method(typeof(Punch), "ParryProjectile", new[] { typeof(Projectile) });
        private static readonly HashSet<int> tried = new HashSet<int>();
        private static float nextScan, clearTried;
        private static bool failedLogged;

        public static int SwingParries { get; private set; }
        public static int ShieldParries { get; private set; }

        // V1's standard (Feedbacker) arm, set up but hidden; null if V1 has none equipped.
        private static Punch Feedbacker()
        {
            if (!MonoSingleton<FistControl>.TryGetInstance(out FistControl fc) || fc == null)
            {
                return null;
            }
            if (fc.currentPunch == null)
            {
                fc.RefreshArm();
                if (HandMode.Active)
                {
                    fc.NoFist(); // the arm works but stays out of sight
                }
            }
            Punch p = fc.currentPunch;
            return p != null && p.type == FistType.Standard ? p : null;
        }

        // A swing with anything Minecraft: the punch's active frames (parry window included).
        public static void Swing()
        {
            try
            {
                Punch p = Feedbacker();
                if (p != null && activeStart != null)
                {
                    activeStart.Invoke(p, null);
                    SwingParries++;
                }
            }
            catch (Exception e)
            {
                LogOnce($"swing parry failed: {e.InnerException?.Message ?? e.Message}");
            }
        }

        // While the shield is up: enemy shots heading for V1 within reach are parried automatically.
        public static void ShieldTick(bool blocking, Bounds v1)
        {
            if (Time.unscaledTime > clearTried)
            {
                tried.Clear();
                clearTried = Time.unscaledTime + 2f;
            }
            if (!blocking || Time.unscaledTime < nextScan)
            {
                return;
            }
            nextScan = Time.unscaledTime + 0.03f;
            try
            {
                Punch p = Feedbacker();
                if (p == null)
                {
                    return;
                }
                foreach (Collider c in Physics.OverlapSphere(v1.center, 8f, ~0, QueryTriggerInteraction.Collide))
                {
                    Projectile proj = c != null ? c.GetComponentInParent<Projectile>() : null;
                    if (proj == null || proj.friendly || proj.playerBullet || proj.unparryable || !tried.Add(proj.GetInstanceID()))
                    {
                        continue;
                    }
                    Vector3 toV1 = v1.center - proj.transform.position;
                    Rigidbody rb = proj.GetComponent<Rigidbody>();
                    if (rb != null && rb.velocity.sqrMagnitude > 0.01f && Vector3.Dot(rb.velocity, toV1) <= 0f)
                    {
                        tried.Remove(proj.GetInstanceID()); // moving away: look again later
                        continue;
                    }
                    bool done = tryParryProjectile != null && (bool)tryParryProjectile.Invoke(p, new object[] { proj.transform, false });
                    if (!done && parryProjectile != null)
                    {
                        parryProjectile.Invoke(p, new object[] { proj });
                        done = true;
                    }
                    if (done)
                    {
                        ShieldParries++;
                        Plugin.Log.LogInfo($"parry: shield sent back {proj.name}");
                    }
                }
            }
            catch (Exception e)
            {
                LogOnce($"shield parry failed: {e.InnerException?.Message ?? e.Message}");
            }
        }

        // The shield went up just in time against a melee hit (Minecraft's EvParry): ULTRAKILL's
        // parry of the nearest enemy in front. False if there's none (then MINEKILL's own flash).
        public static bool ShieldParryMelee(Vector3 eye, Vector3 forward)
        {
            try
            {
                Punch p = Feedbacker();
                if (p == null || !MonoSingleton<EnemyTracker>.TryGetInstance(out EnemyTracker tracker) || tracker == null)
                {
                    return false;
                }
                EnemyIdentifier best = null;
                float bestDist = 7f;
                foreach (EnemyIdentifier eid in tracker.GetCurrentEnemies())
                {
                    if (eid == null || eid.dead) continue;
                    Vector3 to = eid.transform.position - eye;
                    float d = to.magnitude;
                    if (d < bestDist && Vector3.Dot(to.normalized, forward) > 0.3f)
                    {
                        best = eid;
                        bestDist = d;
                    }
                }
                if (best == null)
                {
                    return false;
                }
                p.Parry(false, best, "SHIELD PARRY");
                ShieldParries++;
                Plugin.Log.LogInfo($"parry: shield parried {best.enemyType}");
                return true;
            }
            catch (Exception e)
            {
                LogOnce($"shield melee parry failed: {e.InnerException?.Message ?? e.Message}");
                return false;
            }
        }

        private static void LogOnce(string msg)
        {
            if (!failedLogged)
            {
                failedLogged = true;
                Plugin.Log.LogWarning(msg);
            }
        }
    }
}
