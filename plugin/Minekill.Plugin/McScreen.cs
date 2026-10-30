using System;
using HarmonyLib;
using Minekill.Link;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using LinkEnd = Minekill.Link.Link;

namespace Minekill
{
    // Minecraft's screens (E: the inventory) inside ULTRAKILL, while the hand is Minecraft's.
    // E is sent to Minecraft, which opens its own inventory screen; it is drawn in the overlay like
    // the HUD. While a Minecraft screen is open (GuestScreenOpen, or just asked for):
    //  - ULTRAKILL's cursor is freed and the camera holds still; V1 stops under Minecraft's
    //    movement rules with no keys pressed (McMovement), the game itself keeps running;
    //  - the cursor position over the overlay, the clicks and Minecraft's screen keys (E, Esc, Q,
    //    F, 1-9, with shift/ctrl) go to Minecraft's own keyboard and mouse handlers;
    //  - Esc closes the Minecraft screen instead of opening ULTRAKILL's pause menu.
    internal static class McScreen
    {
        public static bool Open { get; private set; }
        public static string Last { get; private set; } = "none";

        private static float waitForGuestUntil = -1f;
        private static float noPauseUntil = -1f;
        private static bool frozeCamera;
        private static Vector2 lastCursor = new Vector2(-1, -1);
        private static readonly bool[] buttonDown = new bool[3];

        // While a Minecraft screen is open (and a moment after Esc closed it), ULTRAKILL's pause menu stays shut.
        public static bool HoldPause => Open || Time.unscaledTime < noPauseUntil;

        private static readonly (Key key, ushort scancode)[] ScreenKeys = BuildScreenKeys();

        private static (Key, ushort)[] BuildScreenKeys()
        {
            var keys = new (Key, ushort)[13];
            keys[0] = (Key.E, Proto.ScanE);
            keys[1] = (Key.Escape, Proto.ScanEscape);
            keys[2] = (Key.Q, Proto.ScanQ);
            keys[3] = (Key.F, Proto.ScanF);
            for (int i = 0; i < 9; i++)
            {
                keys[4 + i] = ((Key)((int)Key.Digit1 + i), (ushort)(Proto.Scan1 + i));
            }
            return keys;
        }

        public static void Tick(LinkEnd link)
        {
            bool usable = Compat.On(Compat.Feature.McScreens) && HandMode.Active && link != null && link.PeerAlive;
            if (!usable)
            {
                if (Open) SetOpen(false, link);
                return;
            }
            bool guestOpen = (Bridge.GuestFlags & Proto.GuestScreenOpen) != 0;
            Keyboard kb = Keyboard.current;
            float now = Time.unscaledTime;
            if (!Open)
            {
                if (kb != null && kb.eKey.wasPressedThisFrame)
                {
                    // Press and release at once: with no screen open, Minecraft's key mapping takes the click.
                    SendKey(link, Proto.ScanE, true, 0);
                    SendKey(link, Proto.ScanE, false, 0);
                    waitForGuestUntil = now + 1.5f;
                    Last = "E: asked Minecraft to open its inventory";
                    SetOpen(true, link);
                }
                else if (guestOpen)
                {
                    Last = "Minecraft opened a screen by itself";
                    SetOpen(true, link);
                }
                return;
            }

            if (guestOpen)
            {
                waitForGuestUntil = -1f;
            }
            else if (waitForGuestUntil < 0f || now > waitForGuestUntil)
            {
                if (waitForGuestUntil >= 0f) Last = "Minecraft didn't open a screen";
                SetOpen(false, link);
                return;
            }

            // ULTRAKILL may lock the cursor again on its own: keep it free while the screen is up.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            int mods = Modifiers(kb);
            if (kb != null)
            {
                foreach (var (key, scancode) in ScreenKeys)
                {
                    KeyControl k = kb[key];
                    if (k.wasPressedThisFrame)
                    {
                        SendKey(link, scancode, true, mods);
                        if (key == Key.Escape) noPauseUntil = now + 0.5f;
                    }
                    else if (k.wasReleasedThisFrame)
                    {
                        SendKey(link, scancode, false, mods);
                    }
                }
            }

            Mouse mouse = Mouse.current;
            if (mouse != null && Screen.width > 0 && Screen.height > 0)
            {
                // The overlay covers the whole screen; Minecraft's cursor origin is its top-left corner.
                Vector2 p = mouse.position.ReadValue();
                var c = new Vector2(Mathf.Clamp01(p.x / Screen.width), Mathf.Clamp01(1f - p.y / Screen.height));
                if ((c - lastCursor).sqrMagnitude > 1e-8f)
                {
                    lastCursor = c;
                    link.PushInput(new InputEvent { Type = Proto.InCursor, A = (int)(c.x * Proto.CursorScale), B = (int)(c.y * Proto.CursorScale) });
                }
                Button(link, 0, mouse.leftButton, 1, mods);
                Button(link, 1, mouse.rightButton, 3, mods);
                Button(link, 2, mouse.middleButton, 2, mods);
            }
        }

        private static void Button(LinkEnd link, int i, ButtonControl b, ushort sdlButton, int mods)
        {
            if (b.wasPressedThisFrame)
            {
                buttonDown[i] = true;
                link.PushInput(new InputEvent { Type = Proto.InScreenButton, Code = sdlButton, A = 1, B = mods });
                Last = $"click {sdlButton} at ({lastCursor.x:F3}, {lastCursor.y:F3})";
            }
            else if (buttonDown[i] && b.wasReleasedThisFrame)
            {
                buttonDown[i] = false;
                link.PushInput(new InputEvent { Type = Proto.InScreenButton, Code = sdlButton, A = 0, B = mods });
            }
        }

        private static void SendKey(LinkEnd link, ushort scancode, bool press, int mods) =>
            link.PushInput(new InputEvent { Type = Proto.InKey, Code = scancode, A = press ? 1 : 0, B = mods });

        // SDL modifier bits: LSHIFT 0x1, RSHIFT 0x2, LCTRL 0x40, RCTRL 0x80.
        private static int Modifiers(Keyboard kb)
        {
            if (kb == null) return 0;
            int m = 0;
            if (kb.leftShiftKey.isPressed) m |= 0x1;
            if (kb.rightShiftKey.isPressed) m |= 0x2;
            if (kb.leftCtrlKey.isPressed) m |= 0x40;
            if (kb.rightCtrlKey.isPressed) m |= 0x80;
            return m;
        }

        private static void SetOpen(bool open, LinkEnd link)
        {
            Open = open;
            lastCursor = new Vector2(-1, -1);
            try
            {
                if (MonoSingleton<CameraController>.TryGetInstance(out CameraController cc) && cc != null)
                {
                    if (open && cc.activated)
                    {
                        cc.activated = false; // the camera holds still while the cursor is on Minecraft's screen
                        frozeCamera = true;
                    }
                    else if (!open && frozeCamera)
                    {
                        cc.activated = true;
                        frozeCamera = false;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"camera freeze for Minecraft's screen failed: {e.Message}");
            }
            if (open)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else
            {
                waitForGuestUntil = -1f;
                for (int i = 0; i < buttonDown.Length; i++)
                {
                    if (buttonDown[i] && link != null)
                    {
                        link.PushInput(new InputEvent { Type = Proto.InScreenButton, Code = (ushort)(i == 0 ? 1 : i == 1 ? 3 : 2), A = 0 });
                    }
                    buttonDown[i] = false;
                }
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            Plugin.Log.LogInfo($"Minecraft screen {(open ? "open: cursor free, camera still" : "closed: back to ULTRAKILL")} ({Last})");
        }
    }

    // Esc while a Minecraft screen is open closes that screen, not ULTRAKILL's pause menu.
    // (OptionsManager.Update checked by Compat.)
    [HarmonyPatch(typeof(OptionsManager), "Update")]
    internal static class OptionsManagerUpdatePatch
    {
        private static bool Prefix() => !McScreen.HoldPause;
    }
}
