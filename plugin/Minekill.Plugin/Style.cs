using System;

namespace Minekill
{
    // ULTRAKILL's style meter rewards Minecraft kills too, under their own names.
    // (StyleHUD.RegisterStyleItem / AddPoints signatures checked with ApiProbe.)
    internal static class Style
    {
        private const string Sword = "minekill.minecrafted", Tnt = "minekill.tnt";
        private static bool registered;
        private static bool broken;

        private static readonly System.Collections.Generic.HashSet<string> extra = new System.Collections.Generic.HashSet<string>();

        // Any other MINEKILL style bonus (shield parry, pearl teleport, arrow kill...).
        public static void Add(string id, string name, int points, EnemyIdentifier eid = null)
        {
            if (broken || !MonoSingleton<StyleHUD>.TryGetInstance(out StyleHUD hud) || hud == null)
            {
                return;
            }
            try
            {
                if (extra.Add(id))
                {
                    hud.RegisterStyleItem(id, name);
                }
                hud.AddPoints(points, id, null, eid, -1, "", "");
            }
            catch (Exception e)
            {
                broken = true;
                Plugin.Log.LogWarning($"style points disabled: {e.Message}");
            }
        }

        public static void Kill(EnemyIdentifier eid, bool explosion)
        {
            if (broken || !MonoSingleton<StyleHUD>.TryGetInstance(out StyleHUD hud) || hud == null)
            {
                return;
            }
            try
            {
                if (!registered)
                {
                    hud.RegisterStyleItem(Sword, "MINECRAFTED");
                    hud.RegisterStyleItem(Tnt, "TNT'D");
                    registered = true;
                }
                hud.AddPoints(explosion ? 120 : 80, explosion ? Tnt : Sword, null, eid, -1, "", "");
            }
            catch (Exception e)
            {
                broken = true; // don't keep failing every kill
                Plugin.Log.LogWarning($"style points disabled: {e.Message}");
            }
        }
    }
}
