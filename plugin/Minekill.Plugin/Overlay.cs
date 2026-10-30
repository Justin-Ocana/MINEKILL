using Minekill.Link;
using UnityEngine;
using UnityEngine.UI;
using LinkEnd = Minekill.Link.Link;

namespace Minekill
{
    // Minecraft's hand, held item and HUD (RGBA, transparent where Minecraft drew nothing), shown over
    // ULTRAKILL's frame on a top-most canvas. Pixels come straight from the shared mapping.
    internal static class Overlay
    {
        private static GameObject root;
        private static RawImage image;
        private static Texture2D texture;
        private static long lastFrameId;
        private static float lastFrameTime = -100f;
        public static int FramesShown { get; private set; }

        private static void Ensure()
        {
            if (root != null)
            {
                return;
            }
            root = new GameObject("MINEKILL overlay");
            Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            var child = new GameObject("Minecraft hand + HUD");
            child.transform.SetParent(root.transform, false);
            image = child.AddComponent<RawImage>();
            image.raycastTarget = false;
            RectTransform rt = image.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            root.SetActive(false);
        }

        public static void Frame(LinkEnd link, bool show)
        {
            Ensure();
            if (link != null && link.TryAcquireOverlay(out OverlayFrame f))
            {
                if (texture == null || texture.width != f.Width || texture.height != f.Height)
                {
                    if (texture != null)
                    {
                        Object.Destroy(texture); // our own texture
                    }
                    texture = new Texture2D(f.Width, f.Height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                    image.texture = texture;
                    Plugin.Log.LogInfo($"overlay {f.Width}x{f.Height} (bottomUp={f.BottomUp})");
                }
                texture.LoadRawTextureData(f.Pixels, f.Width * f.Height * 4);
                texture.Apply(false, false);
                // Unity textures start at the bottom row, so bottom-up pixels need no flip.
                image.uvRect = f.BottomUp ? new Rect(0, 0, 1, 1) : new Rect(0, 1, 1, -1);
                lastFrameId = f.FrameId;
                lastFrameTime = Time.unscaledTime;
                FramesShown++;
            }
            // Hide a stale frame rather than freeze Minecraft's hand on screen.
            bool visible = show && texture != null && Time.unscaledTime - lastFrameTime < 1f;
            if (root.activeSelf != visible)
            {
                root.SetActive(visible);
            }
        }
    }
}
