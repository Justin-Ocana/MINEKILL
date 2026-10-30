using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Minekill.Link;

// FakeHost [mappingName] [--seconds N] [--radius R] [--y Y] [--teleport-at S]
// Plays ULTRAKILL's side of the link at 60 Hz: a fake V1 walks a circle of radius R blocks around
// (0, Y, 0) in Minecraft coordinates. Reads Minecraft's player back and reports how far it is from
// where V1 was. With --teleport-at S, V1 jumps 100 blocks east at S seconds with a new
// teleportSeq (like a scene change). Exit code 0 = PASS: the guest connected, acknowledged every
// teleport and stayed within 0.75 blocks of V1 for at least 80% of the samples after it settled.
string name = Proto.DefaultMappingName;
double seconds = 30, radius = 6, y = 64, teleportAt = double.NaN;
string overlayPng = null;     // --overlay-png path: save the last overlay frame received
double keyAt = double.NaN;    // --key-at S: press hotbar key 2 at S seconds after connecting
int overlayFrames = 0;
long lastOverlayId = 0;
byte[] lastOverlay = null;
int lastW = 0, lastH = 0;
bool lastBottomUp = false;
bool keySent = false;
double overlayEvery = double.NaN; // --overlay-every S: also save a numbered PNG every S seconds
double nextOverlaySave = 0;
// --combat: V1 stands still at (0, y, 0) facing south (+Z) with a fake Filth 2.5 blocks ahead; attacks
// at 4-8 s, a 5-point hit on Steve at 10 s and a lethal one at 12 s. PASS needs >= 3 sword hits in
// a plausible range and Minecraft's death report.
bool combat = false;
// --blocks: V1 still at (0, y, 0). Script (seconds after connecting): 3 select cobblestone (slot 2)
// and place it on "level geometry" at (0, y-1, 2); 5 place another on its top face; 7 select TNT
// (slot 6), place at (2, y-1, 2); 9 flint and steel (slot 7) on it; 15 break (0, y, 2).
bool blocksTest = false;
// --synergy: the cross-game interactions, scripted (seconds after connecting); see the PASS line.
bool synergyTest = false;
var synScript = new System.Collections.Generic.HashSet<double>();
// --inventory: Minecraft's inventory driven like ULTRAKILL does it (seconds after connecting): 3 E opens
// it; 5-9 the cursor takes the sword from hotbar slot 1 to an empty backpack slot and back; 10 E
// closes it; 12 E opens, 13 Esc closes; 15 Esc with nothing open must not open Minecraft's pause menu.
// PASS needs those four screen changes and no screen after 15 s; what the cursor carried is in
// Minecraft's log ("cursor carries ...").
bool inventoryTest = false;
// --bob: V1 stands still 0-6 s, walks a 6-block circle 6-12 s (~3 blocks/s), and sprints 12-16 s
// (HostSprinting). PASS: the hand in the overlay sways while walking (its centre moves >= 3x more
// than standing, and >= 1 px), and Minecraft's FOV is ~70 standing and ~80.5 sprinting.
bool bobTest = false;
var handStill = new System.Collections.Generic.List<(double x, double y)>();
var handWalk = new System.Collections.Generic.List<(double x, double y)>();
var fovStill = new System.Collections.Generic.List<float>();
var fovSprint = new System.Collections.Generic.List<float>();
var invScript = new System.Collections.Generic.HashSet<double>();
bool screenOpen = false;
var screenLog = new System.Collections.Generic.List<(double t, bool open)>();
bool synBlocking = false, synArrow = false, synSnow = false, synPearl = false, synParry = false, synChain = false, synCrit = false, hostFalling = false;
float lastArrowSpeed = 0, lastHit = 0;
var blockScript = new System.Collections.Generic.HashSet<int>();
var blocksNow = new System.Collections.Generic.HashSet<(int, int, int)>();
var texNames = new System.Collections.Generic.Dictionary<uint, string>();
bool sawBlock1 = false, sawBlock2 = false, sawTnt = false, sawExplosion = false, brokeBlock2 = false;
int texPngs = 0;
int hits = 0, deaths = 0, plausibleHits = 0;
var combatDone = new System.Collections.Generic.HashSet<int>();
var inv = CultureInfo.InvariantCulture;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seconds": seconds = double.Parse(args[++i], inv); break;
        case "--radius": radius = double.Parse(args[++i], inv); break;
        case "--y": y = double.Parse(args[++i], inv); break;
        case "--teleport-at": teleportAt = double.Parse(args[++i], inv); break;
        case "--overlay-png": overlayPng = args[++i]; break;
        case "--key-at": keyAt = double.Parse(args[++i], inv); break;
        case "--overlay-every": overlayEvery = double.Parse(args[++i], inv); break;
        case "--combat": combat = true; radius = 0; break;
        case "--blocks": blocksTest = true; radius = 0; break;
        case "--synergy": synergyTest = true; radius = 0; break;
        case "--inventory": inventoryTest = true; radius = 0; break;
        case "--bob": bobTest = true; radius = 0; break;
        default: name = args[i]; break;
    }
}

using Link link = Link.CreateHost(name, out string error);
if (link == null)
{
    Console.WriteLine($"FAIL {error}");
    return 2;
}
Console.WriteLine($"HOST {name} pid {Link.CurrentPid()}; waiting for Minecraft");

uint teleportSeq = 1;
double offsetX = 0;
var clock = Stopwatch.StartNew();
double connectedAt = double.NaN, lastReport = -1;
int samples = 0, close = 0;
double worst = 0;
bool teleported = false;
// --seconds counts from Minecraft connecting (it can take minutes to start); give up after 180 s without it.
while (double.IsNaN(connectedAt) ? clock.Elapsed.TotalSeconds < 180 : clock.Elapsed.TotalSeconds - connectedAt < seconds)
{
    double t = clock.Elapsed.TotalSeconds;
    if (!teleported && !double.IsNaN(teleportAt) && !double.IsNaN(connectedAt) && t - connectedAt >= teleportAt)
    {
        teleported = true;
        offsetX = 100;
        teleportSeq++;
        Console.WriteLine($"TELEPORT seq {teleportSeq} (+100 blocks east)");
    }
    double bobPhase = bobTest && !double.IsNaN(connectedAt) ? t - connectedAt : 0;
    if (bobTest) radius = bobPhase >= 6 ? 6 : 0;
    double a = t * 0.5; // ~3 blocks/s at radius 6
    double hx = offsetX + radius * Math.Cos(a), hz = radius * Math.Sin(a);
    link.Heartbeat();
    if (combat)
    {
        var enemy = new ActorRecord[Proto.MaxActors];
        enemy[0] = new ActorRecord { Id = 7, Flags = Proto.ActorHostile, X = 0, Y = (float)y, Z = 2.5f, Yaw = 180, Width = 0.8f, Height = 1.9f, HealthFrac = 1, Name = "Filth" };
        link.WriteActors(enemy, 1);
        double since = double.IsNaN(connectedAt) ? -1 : t - connectedAt;
        foreach (int s in new[] { 4, 5, 6, 7, 8 })
        {
            // Every 1.2 s from 4 s so the sword's attack cooldown (0.625 s) has recharged.
            double at = 4 + (s - 4) * 1.2;
            if (since >= at && combatDone.Add(s))
            {
                link.PushInput(new InputEvent { Type = Proto.InMouseButton, Code = 1, A = 1 });
                link.PushInput(new InputEvent { Type = Proto.InMouseButton, Code = 1, A = 0 });
                Console.WriteLine(string.Format(inv, "ATTACK at {0:F1}s", since));
            }
        }
        if (since >= 10 && combatDone.Add(10))
        {
            link.PushInput(new InputEvent { Type = Proto.InHurt, Code = Proto.HurtMelee, A = 500 });
            Console.WriteLine("HURT Steve 5");
        }
        if (since >= 12 && combatDone.Add(12))
        {
            link.PushInput(new InputEvent { Type = Proto.InHurt, Code = Proto.HurtMelee, A = 4000 });
            Console.WriteLine("HURT Steve 40 (lethal)");
        }
        while (link.TryPopEvent(out McEvent ev))
        {
            if (ev.Type == Proto.EvHitActor)
            {
                hits++;
                if (ev.Id == 7 && ev.A >= 1 && ev.A <= 10) plausibleHits++;
                Console.WriteLine(string.Format(inv, "EVENT hit actor #{0} for {1:F2} (knockback {2:F2},{3:F2} weapon {4})", ev.Id, ev.A, ev.B, ev.C, ev.Weapon));
            }
            else if (ev.Type == Proto.EvPlayerDied)
            {
                deaths++;
                Console.WriteLine("EVENT Minecraft player died");
            }
        }
    }
    if (blocksTest && !double.IsNaN(connectedAt))
    {
        double since = t - connectedAt;
        int by = (int)Math.Floor(y) - 1;
        void Key(int slot) { link.PushInput(new InputEvent { Type = Proto.InKey, Code = (ushort)(29 + slot), A = 1 }); }
        void Use(ushort action, int x, int yy, int z, int face)
        {
            link.PushInput(new InputEvent { Type = Proto.InUse, Code = (ushort)(action | face << 8), A = x, B = yy, C = z });
            link.PushInput(new InputEvent { Type = Proto.InUse, Code = Proto.UseRelease });
        }
        // The Minecraft world keeps blocks between runs: clear the test cells first.
        if (since >= 1 && blockScript.Add(1))
        {
            foreach (var (cx, cy, cz) in new[] { (0, by, 2), (0, by + 1, 2), (2, by, 2) })
                link.PushInput(new InputEvent { Type = Proto.InBreak, A = cx, B = cy, C = cz });
            Console.WriteLine("SCRIPT clear the test cells");
        }
        if (since >= 3 && blockScript.Add(3)) { Key(2); Use(Proto.UsePressLevel, 0, by, 2, 0); Console.WriteLine("SCRIPT place cobblestone on level at (0," + by + ",2)"); }
        if (since >= 5 && blockScript.Add(5)) { Use(Proto.UsePressBlock, 0, by, 2, 1); Console.WriteLine("SCRIPT place on its top face"); }
        if (since >= 7 && blockScript.Add(7)) { Key(6); Use(Proto.UsePressLevel, 2, by, 2, 0); Console.WriteLine("SCRIPT place TNT at (2," + by + ",2)"); }
        if (since >= 9 && blockScript.Add(9)) { Key(7); Use(Proto.UsePressBlock, 2, by, 2, 1); Console.WriteLine("SCRIPT light the TNT"); }
        if (since >= 15 && blockScript.Add(15)) { link.PushInput(new InputEvent { Type = Proto.InBreak, A = 0, B = by + 1, C = 2 }); Console.WriteLine("SCRIPT break (0," + (by + 1) + ",2)"); }
        while (link.TryPopRenderMessage(out uint rt, out byte[] rp))
        {
            if (rt == Proto.RenTexSet)
            {
                uint id = BitConverter.ToUInt32(rp, 0), flags = BitConverter.ToUInt32(rp, 4);
                int nameLen = BitConverter.ToInt32(rp, 12);
                string tname = System.Text.Encoding.UTF8.GetString(rp, 16, nameLen);
                texNames[id] = tname;
                int o = 16 + nameLen;
                for (int f = 0; f < 3; f++)
                {
                    int len = BitConverter.ToInt32(rp, o);
                    if (len > 8 && rp[o + 4] == 0x89 && rp[o + 5] == (byte)'P') texPngs++; // a real PNG
                    if (overlayPng != null && len > 0)
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(overlayPng), $"tex-{tname.Replace(':', '_')}-{f}.png"), rp[(o + 4)..(o + 4 + len)]);
                    o += 4 + len;
                }
                Console.WriteLine($"TEXSET #{id} {tname} flags={flags}");
            }
            else if (rt == Proto.RenBlocks)
            {
                int n = BitConverter.ToInt32(rp, 0);
                blocksNow.Clear();
                for (int k = 0; k < n; k++)
                {
                    blocksNow.Add((BitConverter.ToInt32(rp, 4 + k * 16), BitConverter.ToInt32(rp, 8 + k * 16), BitConverter.ToInt32(rp, 12 + k * 16)));
                }
                sawBlock1 |= blocksNow.Contains((0, by, 2));
                sawBlock2 |= blocksNow.Contains((0, by + 1, 2));
                if (sawBlock2 && blockScript.Contains(15) && !blocksNow.Contains((0, by + 1, 2))) brokeBlock2 = true;
                Console.WriteLine($"BLOCKS {n}: " + string.Join(" ", blocksNow));
            }
            else if (rt == Proto.RenTnt)
            {
                int n = BitConverter.ToInt32(rp, 0);
                if (n > 0 && !sawTnt) { sawTnt = true; Console.WriteLine($"TNT lit, fuse {BitConverter.ToInt32(rp, 20)} ticks"); }
            }
        }
        while (link.TryPopEvent(out McEvent bev))
        {
            if (bev.Type == Proto.EvExplosion) { sawExplosion = true; Console.WriteLine($"EVENT explosion at ({bev.A:F1},{bev.B:F1},{bev.C:F1}) power {bev.D}"); }
        }
    }
    if (synergyTest && !double.IsNaN(connectedAt))
    {
        double s = t - connectedAt;
        int by = (int)Math.Floor(y) - 1;
        bool At(double when) => s >= when && synScript.Add(when);
        void Key(int slot) => link.PushInput(new InputEvent { Type = Proto.InKey, Code = (ushort)(29 + slot), A = 1 });
        void In(ushort type, ushort code, int a = 0, int b = 0, int c = 0) => link.PushInput(new InputEvent { Type = type, Code = code, A = a, B = b, C = c });
        if (At(2)) { In(Proto.InLoot, Proto.LootArrow, 5, 10); In(Proto.InLoot, Proto.LootTnt, 1, 0); Console.WriteLine("SCRIPT loot: 5 arrows, 1 TNT, 10 xp"); }
        if (At(3)) { Key(2); In(Proto.InUse, Proto.UsePressAir); Console.WriteLine("SCRIPT draw the bow"); }
        if (At(4.3)) { In(Proto.InUse, Proto.UseRelease); Console.WriteLine("SCRIPT release the bow"); }
        if (At(6)) { Key(8); In(Proto.InUse, Proto.UsePressAir); In(Proto.InUse, Proto.UseRelease); Console.WriteLine("SCRIPT throw a snowball"); }
        if (At(8)) { Key(7); In(Proto.InUse, Proto.UsePressAir); In(Proto.InUse, Proto.UseRelease); Console.WriteLine("SCRIPT throw an ender pearl"); }
        if (At(10)) { In(Proto.InHurt, Proto.HurtMelee, 600); Console.WriteLine("SCRIPT hurt Steve 6"); }
        if (At(10.5)) { In(Proto.InHeal, 0, 400); Console.WriteLine("SCRIPT blood heals 4"); }
        if (At(12)) { Key(1); In(Proto.InUse, Proto.UsePressAir); Console.WriteLine("SCRIPT raise the shield"); }
        if (At(12.4)) { In(Proto.InHurt, Proto.HurtMelee, 400); Console.WriteLine("SCRIPT hit while just raised"); }
        if (At(13)) { In(Proto.InUse, Proto.UseRelease); }
        if (At(14)) { In(Proto.InBreak, 0, 3, by, 0); Key(5); In(Proto.InUse, Proto.UsePressLevel, 3, by, 0); In(Proto.InUse, Proto.UseRelease); Console.WriteLine("SCRIPT TNT at (3," + by + ",0)"); }
        if (At(16)) { In(Proto.InExplode, 30, 350, (by + 1) * 100, 50); Console.WriteLine("SCRIPT ULTRAKILL explosion next to it"); }
        if (At(19)) { hostFalling = true; Key(1); Console.WriteLine("SCRIPT V1 falling"); }
        if (At(20.5)) { In(Proto.InMouseButton, 1, 1); In(Proto.InMouseButton, 1, 0); Console.WriteLine("SCRIPT swing while falling"); }
        if (At(21.5)) { hostFalling = false; }
        var enemy = new ActorRecord[Proto.MaxActors];
        enemy[0] = new ActorRecord { Id = 7, Flags = Proto.ActorHostile, X = 0, Y = (float)y, Z = 2.5f, Width = 0.8f, Height = 1.9f, HealthFrac = 1, Name = "Filth" };
        link.WriteActors(enemy, s >= 18 ? 1 : 0);
        while (link.TryPopRenderMessage(out uint rt, out byte[] rp))
        {
            if (rt != Proto.RenThrow) continue;
            uint kind = BitConverter.ToUInt32(rp, 0);
            float vx = BitConverter.ToSingle(rp, 16), vy = BitConverter.ToSingle(rp, 20), vz = BitConverter.ToSingle(rp, 24), dmg = BitConverter.ToSingle(rp, 28);
            float speed = MathF.Sqrt(vx * vx + vy * vy + vz * vz);
            if (kind == Proto.ThrowArrow) { synArrow = speed > 2.5f && dmg >= 6; lastArrowSpeed = speed; }
            if (kind == Proto.ThrowSnowball) synSnow = true;
            if (kind == Proto.ThrowEnderPearl) synPearl = true;
            Console.WriteLine(string.Format(inv, "THROW kind {0} speed {1:F2} blocks/tick damage {2} flags {3}", kind, speed, dmg, BitConverter.ToUInt32(rp, 32)));
        }
        while (link.TryPopEvent(out McEvent ev))
        {
            if (ev.Type == Proto.EvParry) { synParry = true; Console.WriteLine("EVENT parry"); }
            if (ev.Type == Proto.EvExplosion) { synChain = true; Console.WriteLine(string.Format(inv, "EVENT explosion at ({0:F1},{1:F1},{2:F1})", ev.A, ev.B, ev.C)); }
            if (ev.Type == Proto.EvHitActor) { lastHit = ev.A; synCrit |= hostFalling && ev.A > 7.5f; Console.WriteLine(string.Format(inv, "EVENT hit {0:F2}", ev.A)); }
        }
    }
    if (inventoryTest && !double.IsNaN(connectedAt))
    {
        double s = t - connectedAt;
        bool At(double when) => s >= when && invScript.Add(when);
        void Key(ushort scancode)
        {
            link.PushInput(new InputEvent { Type = Proto.InKey, Code = scancode, A = 1 });
            link.PushInput(new InputEvent { Type = Proto.InKey, Code = scancode, A = 0 });
        }
        // Minecraft's inventory screen, 176 x 166 GUI pixels centred at the automatic GUI scale.
        void Slot(int gx, int gy)
        {
            int w = lastW > 0 ? lastW : 854, h = lastH > 0 ? lastH : 480;
            int scale = 1;
            while (w / (scale + 1) >= 320 && h / (scale + 1) >= 240) scale++;
            int gw = (w + scale - 1) / scale, gh = (h + scale - 1) / scale;
            double px = ((gw - 176) / 2 + gx + 8) * scale, py = ((gh - 166) / 2 + gy + 8) * scale;
            link.PushInput(new InputEvent { Type = Proto.InCursor, A = (int)(px / w * Proto.CursorScale), B = (int)(py / h * Proto.CursorScale) });
            Console.WriteLine(string.Format(inv, "SCRIPT cursor to GUI slot ({0},{1}) = pixel ({2},{3}) of {4}x{5} at GUI scale {6}", gx, gy, px, py, w, h, scale));
        }
        void Click()
        {
            link.PushInput(new InputEvent { Type = Proto.InScreenButton, Code = 1, A = 1 });
            link.PushInput(new InputEvent { Type = Proto.InScreenButton, Code = 1, A = 0 });
            Console.WriteLine("SCRIPT left click");
        }
        if (At(3)) { Key(Proto.ScanE); Console.WriteLine("SCRIPT E (open)"); }
        if (At(5)) Slot(8, 142);          // hotbar slot 1: the diamond sword
        if (At(5.5)) Click();
        if (At(6.5)) Slot(8 + 5 * 18, 84); // backpack slot 15 (empty)
        if (At(7)) Click();
        if (At(8)) Click();               // pick it up again
        if (At(8.5)) Slot(8, 142);
        if (At(9)) Click();               // and back into slot 1
        if (At(10)) { Key(Proto.ScanE); Console.WriteLine("SCRIPT E (close)"); }
        if (At(12)) { Key(Proto.ScanE); Console.WriteLine("SCRIPT E (open)"); }
        if (At(13)) { Key(Proto.ScanEscape); Console.WriteLine("SCRIPT Esc (close)"); }
        if (At(15)) { Key(Proto.ScanEscape); Console.WriteLine("SCRIPT Esc with nothing open"); }
    }
    link.WriteHostState(new HostState
    {
        Flags = Proto.HostInGame | (hostFalling ? Proto.HostAirborne | Proto.HostFalling : 0) | (bobTest && bobPhase >= 12 ? Proto.HostSprinting : 0),
        WorldId = 0xC0FFEE,
        Epoch = 1,
        X = hx, Y = y, Z = hz,
        Yaw = combat || blocksTest || synergyTest || inventoryTest ? 0f : (float)(Math.Atan2(-Math.Cos(a), -Math.Sin(a)) * 180 / Math.PI),
        Pitch = 0,
        TeleportSeq = teleportSeq,
        ViewportW = 854, ViewportH = 480,
        GameHour = 12,
    });

    if (link.TryAcquireOverlay(out OverlayFrame of))
    {
        overlayFrames++;
        lastOverlayId = of.FrameId;
        lastW = of.Width;
        lastH = of.Height;
        lastBottomUp = of.BottomUp;
        lastOverlay ??= new byte[Proto.MaxOverlayW * Proto.MaxOverlayH * 4];
        System.Runtime.InteropServices.Marshal.Copy(of.Pixels, lastOverlay, 0, of.Width * of.Height * 4);
        if (bobTest && !double.IsNaN(connectedAt))
        {
            // Centre of the opaque pixels of the held item on the right, above the HUD.
            double sx = 0, sy = 0; int n = 0;
            for (int py = (int)(of.Height * 0.35); py < of.Height - 100; py++)
            {
                int row = of.BottomUp ? of.Height - 1 - py : py;
                for (int px = (int)(of.Width * 0.55); px < of.Width; px++)
                {
                    if (lastOverlay[(row * of.Width + px) * 4 + 3] > 128) { sx += px; sy += py; n++; }
                }
            }
            if (n > 50)
            {
                double s = t - connectedAt;
                if (Environment.GetEnvironmentVariable("MINEKILL_BOB_DEBUG") == "1")
                    Console.WriteLine(string.Format(inv, "HAND s={0:F2} frame {1} n={2} centre ({3:F1},{4:F1})", s, of.FrameId, n, sx / n, sy / n));
                if (s >= 2 && s < 6) handStill.Add((sx / n, sy / n));
                else if (s >= 7 && s < 12) handWalk.Add((sx / n, sy / n));
            }
        }
        if (overlayPng != null && !double.IsNaN(overlayEvery) && !double.IsNaN(connectedAt) && t >= nextOverlaySave)
        {
            nextOverlaySave = t + overlayEvery;
            string p = System.IO.Path.ChangeExtension(overlayPng, null) + string.Format(inv, "-t{0:00}s-f{1}.png", t - connectedAt, of.FrameId);
            Png.Write(p, lastOverlay, of.Width, of.Height, of.BottomUp);
        }
    }
    if (!keySent && !double.IsNaN(keyAt) && !double.IsNaN(connectedAt) && t - connectedAt >= keyAt)
    {
        keySent = true;
        link.PushInput(new InputEvent { Type = Proto.InKey, Code = 31, A = 1 }); // SDL_SCANCODE_2: hotbar slot 2
        link.PushInput(new InputEvent { Type = Proto.InKey, Code = 31, A = 0 });
        Console.WriteLine("KEY hotbar 2 sent");
    }

    if (link.PeerAlive && link.TryReadGuestState(out GuestState g) && (g.Flags & Proto.GuestInWorld) != 0)
    {
        if (double.IsNaN(connectedAt))
        {
            connectedAt = t;
            Console.WriteLine($"CONNECTED Minecraft pid {link.PeerPid} at t={t:F1}s");
        }
        if (bobTest)
        {
            double s = t - connectedAt;
            if (s >= 2 && s < 6) fovStill.Add(g.Fov);
            else if (s >= 14 && s < 16) fovSprint.Add(g.Fov);
        }
        bool open = (g.Flags & Proto.GuestScreenOpen) != 0;
        if (open != screenOpen)
        {
            screenOpen = open;
            screenLog.Add((t - connectedAt, open));
            Console.WriteLine(string.Format(inv, "GUEST screen {0} at {1:F2}s", open ? "OPEN" : "closed", t - connectedAt));
        }
        if (synergyTest && (g.Flags & Proto.GuestBlocking) != 0 && !synBlocking) { synBlocking = true; Console.WriteLine("GUEST shield up (GuestBlocking)"); }
        double err = Math.Sqrt((g.X - hx) * (g.X - hx) + (g.Y - y) * (g.Y - y) + (g.Z - hz) * (g.Z - hz));
        // Let it settle 3 s after connecting and 1 s after a teleport before scoring.
        bool settling = t - connectedAt < 3 || (teleported && t - connectedAt - teleportAt < 1);
        if (!settling && g.TeleportAck == teleportSeq)
        {
            samples++;
            if (err <= 0.75) close++;
            worst = Math.Max(worst, err);
        }
        if (t - lastReport >= 1)
        {
            lastReport = t;
            Console.WriteLine(string.Format(inv, "t={0:F0}s host=({1:F2},{2:F2},{3:F2}) mc=({4:F2},{5:F2},{6:F2}) err={7:F2} ack={8}/{9} mcFrame={10}",
                t, hx, y, hz, g.X, g.Y, g.Z, err, g.TeleportAck, teleportSeq, g.FrameCounter));
        }
    }
    Thread.Sleep(16);
}

if (double.IsNaN(connectedAt))
{
    Console.WriteLine("FAIL Minecraft never connected");
    return 1;
}
if (overlayPng != null)
{
    if (lastOverlay == null)
    {
        Console.WriteLine("OVERLAY none received");
    }
    else
    {
        int transparent = 0, opaque = 0;
        for (int i = 0; i < lastW * lastH; i++)
        {
            byte a = lastOverlay[i * 4 + 3];
            if (a == 0) transparent++;
            else if (a == 255) opaque++;
        }
        Png.Write(overlayPng, lastOverlay, lastW, lastH, lastBottomUp);
        Console.WriteLine(string.Format(inv, "OVERLAY {0} frames ({1:F1}/s), last #{2} {3}x{4} bottomUp={5}: {6:P1} transparent, {7:P1} opaque -> {8}",
            overlayFrames, overlayFrames / Math.Max(1, clock.Elapsed.TotalSeconds - connectedAt), lastOverlayId, lastW, lastH, lastBottomUp,
            (double)transparent / (lastW * lastH), (double)opaque / (lastW * lastH), overlayPng));
    }
}
double ratio = samples == 0 ? 0 : (double)close / samples;
bool pass = samples > 50 && ratio >= 0.8;
if (synergyTest)
{
    bool spass = synArrow && synSnow && synPearl && synParry && synChain && synCrit && synBlocking;
    Console.WriteLine($"SYNERGY {(spass ? "PASS" : "FAIL")}: arrow={synArrow} (speed {lastArrowSpeed:F2}) snowball={synSnow} pearl={synPearl} parry={synParry} chainTNT={synChain} jumpCrit={synCrit} (last hit {lastHit:F2}) shieldFlag={synBlocking}");
    pass &= spass;
}
if (bobTest)
{
    double Spread(System.Collections.Generic.List<(double x, double y)> l)
    {
        if (l.Count < 2) return 0;
        double mx = 0, my = 0;
        foreach (var (x, yy) in l) { mx += x; my += yy; }
        mx /= l.Count; my /= l.Count;
        double v = 0;
        foreach (var (x, yy) in l) v += (x - mx) * (x - mx) + (yy - my) * (yy - my);
        return Math.Sqrt(v / l.Count);
    }
    double still = Spread(handStill), walk = Spread(handWalk);
    float fs = fovStill.Count > 0 ? fovStill[fovStill.Count - 1] : float.NaN, fr = fovSprint.Count > 0 ? fovSprint[fovSprint.Count - 1] : float.NaN;
    bool sway = handWalk.Count > 20 && walk >= 1 && walk >= 3 * still;
    bool fovOk = Math.Abs(fs - 70) < 0.5 && Math.Abs(fr - 80.5) < 1;
    Console.WriteLine(string.Format(inv, "BOB {0}: hand sway still {1:F2} px ({2} frames), walking {3:F2} px ({4} frames); FOV standing {5:F1}, sprinting {6:F1}",
        sway && fovOk ? "PASS" : "FAIL", still, handStill.Count, walk, handWalk.Count, fs, fr));
    pass &= sway && fovOk;
}
if (inventoryTest)
{
    bool Opened(double from, double to) => screenLog.Exists(e => e.open && e.t >= from && e.t < to);
    bool Closed(double from, double to) => screenLog.Exists(e => !e.open && e.t >= from && e.t < to);
    bool openE = Opened(3, 5), closeE = Closed(10, 12), openE2 = Opened(12, 13), closeEsc = Closed(13, 15);
    bool noPause = !Opened(15, 1e9) && !screenOpen;
    bool ipass = openE && closeE && openE2 && closeEsc && noPause;
    Console.WriteLine($"INVENTORY {(ipass ? "PASS" : "FAIL")}: E-opens={openE} E-closes={closeE} E-opens-again={openE2} Esc-closes={closeEsc} Esc-alone-no-pause-menu={noPause}");
    pass &= ipass;
}
if (blocksTest)
{
    bool bpass = sawBlock1 && sawBlock2 && sawTnt && sawExplosion && brokeBlock2 && texPngs >= 3;
    Console.WriteLine($"BLOCKS {(bpass ? "PASS" : "FAIL")}: placed-on-level={sawBlock1} placed-on-face={sawBlock2} tnt-lit={sawTnt} explosion={sawExplosion} broken={brokeBlock2} pngs={texPngs} types=[{string.Join(", ", texNames.Values)}]");
    pass &= bpass;
}
if (combat)
{
    bool combatPass = plausibleHits >= 3 && deaths >= 1;
    Console.WriteLine($"COMBAT {(combatPass ? "PASS" : "FAIL")}: {hits} hit events ({plausibleHits} plausible sword hits on the Filth), {deaths} death report(s)");
    pass &= combatPass;
}
Console.WriteLine(string.Format(inv, "{0}: {1} scored samples, {2:P0} within 0.75 blocks, worst {3:F2} blocks", pass ? "PASS" : "FAIL", samples, ratio, worst));
return pass ? 0 : 1;
