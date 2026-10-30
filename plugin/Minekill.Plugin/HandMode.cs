using System;
using HarmonyLib;
using Minekill.Link;
using UnityEngine;
using UnityEngine.InputSystem;
using LinkEnd = Minekill.Link.Link;

namespace Minekill
{
    // "Minecraft hand" mode: V1's guns and arm are put away and Minecraft's hand, held item and HUD
    // (the overlay) take their place. ULTRAKILL keeps the body and the input; the inputs that belong
    // to the hand (hotbar keys 1-9, wheel, and later attack) are sent to Minecraft as commands.
    internal static class HandMode
    {
        public static bool Active { get; private set; }
        private static float reapplyAt = -1f;

        public static void Toggle()
        {
            if (Active) Exit(); else Enter();
        }

        public static void Enter()
        {
            Active = true;
            PutAwayV1Arms();
            reapplyAt = Time.unscaledTime + 1f; // weapons can still be spawning right after a load
            Plugin.Log.LogInfo("Minecraft hand mode ON (V1's weapons and arm put away)");
        }

        public static void Exit()
        {
            Active = false;
            try
            {
                if (MonoSingleton<GunControl>.TryGetInstance(out GunControl gc) && gc != null) gc.YesWeapon();
                if (MonoSingleton<FistControl>.TryGetInstance(out FistControl fc) && fc != null) fc.YesFist();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"restoring V1's weapons failed: {e.Message}");
            }
            Plugin.Log.LogInfo("Minecraft hand mode OFF (V1's weapons back)");
        }

        public static void OnSceneChanged()
        {
            if (Active)
            {
                reapplyAt = Time.unscaledTime + 1f;
            }
        }

        private static void PutAwayV1Arms()
        {
            try
            {
                if (MonoSingleton<GunControl>.TryGetInstance(out GunControl gc) && gc != null) gc.NoWeapon();
                if (MonoSingleton<FistControl>.TryGetInstance(out FistControl fc) && fc != null) fc.NoFist();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"putting V1's weapons away failed: {e.Message}");
            }
        }

        // Called every frame by the bridge while linked.
        public static void Tick(LinkEnd link)
        {
            if (!Active)
            {
                return;
            }
            if (reapplyAt > 0f && Time.unscaledTime >= reapplyAt)
            {
                reapplyAt = -1f;
                PutAwayV1Arms();
            }
            if (link == null || !link.PeerAlive || McScreen.Open) // an open Minecraft screen takes the keys itself
            {
                return;
            }
            Keyboard kb = Keyboard.current;
            if (kb != null)
            {
                for (int i = 0; i < 9; i++)
                {
                    var key = kb[(Key)((int)Key.Digit1 + i)];
                    if (key.wasPressedThisFrame)
                    {
                        // SDL_SCANCODE_1..9 = 30..38, the codes Minecraft's side maps to hotbar slots.
                        link.PushInput(new InputEvent { Type = Proto.InKey, Code = (ushort)(30 + i), A = 1 });
                    }
                }
            }
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                float wheel = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(wheel) > 0.01f)
                {
                    link.PushInput(new InputEvent { Type = Proto.InScroll, A = wheel > 0 ? 120 : -120 });
                }
            }
        }
    }

    // While the hand is Minecraft's, ULTRAKILL's weapon and arm controllers stay idle, so the number
    // keys, the wheel and F don't bring V1's guns or punch back. (Update() of both checked by Compat.)
    [HarmonyPatch(typeof(GunControl), "Update")]
    internal static class GunControlUpdatePatch
    {
        private static bool Prefix() => !HandMode.Active;
    }

    [HarmonyPatch(typeof(FistControl), "Update")]
    internal static class FistControlUpdatePatch
    {
        private static bool Prefix() => !HandMode.Active;
    }
}
