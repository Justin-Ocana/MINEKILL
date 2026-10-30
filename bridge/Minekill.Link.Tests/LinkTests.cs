using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Minekill.Link;
using Xunit;
using Xunit.Abstractions;

namespace Minekill.Link.Tests
{
    public class LayoutTests
    {
        // Expected values transcribed from SkyCraft's protocol/skycraft_protocol.h (v11).
        [Fact]
        public void OffsetsMatchSkyCraftV11()
        {
            Assert.Equal(0x43594B53u, Proto.Magic);
            Assert.Equal(11u, Proto.Version);
            Assert.Equal(0x20, Proto.HeaderBytes);
            Assert.Equal(0x100, Proto.OffHostState);
            Assert.Equal(0x200, Proto.OffGuestState);
            Assert.Equal(0x10, Proto.HsPosX);
            Assert.Equal(0x28, Proto.HsYaw);
            Assert.Equal(0x30, Proto.HsTeleportSeq);
            Assert.Equal(0x3C, Proto.HsGameHour);
            Assert.Equal(0x40, Proto.HostStateBytes);
            Assert.Equal(0x08, Proto.GsX);
            Assert.Equal(0x28, Proto.GsEyeHeight);
            Assert.Equal(0x30, Proto.GsTeleportAck);
            Assert.Equal(0x38, Proto.GsFrameCounter);
            Assert.Equal(0x40, Proto.GsFov);
            Assert.Equal(0x68, Proto.GsTickQpc);
            Assert.Equal(0xC0, Proto.GsCameraMode);
            Assert.Equal(0xC8, Proto.GuestStateBytes);
            Assert.Equal(0x20000L + (32L << 20) + 3840L * 2160 * 4 * 3 + (64L << 20), Proto.MappingBytes);
        }

        // Independent oracle: when SKYCRAFT_PROTOCOL_H points at SkyCraft's header, check the
        // region offsets against the file itself instead of the transcription above.
        [Fact]
        public void OffsetsMatchHeaderFileWhenAvailable()
        {
            string path = Environment.GetEnvironmentVariable("SKYCRAFT_PROTOCOL_H");
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return; // not configured: covered by OffsetsMatchSkyCraftV11
            }
            string h = File.ReadAllText(path);
            long Hex(string constant)
            {
                Match m = Regex.Match(h, constant + @"\s*=\s*0x([0-9A-Fa-f]+)");
                Assert.True(m.Success, constant + " not found in header");
                return long.Parse(m.Groups[1].Value, NumberStyles.HexNumber);
            }
            Assert.Equal(Proto.Magic, (uint)Hex("kMagic"));
            Assert.Equal(Proto.OffHostState, Hex("kOffSkyState"));
            Assert.Equal(Proto.OffGuestState, Hex("kOffMcState"));
            Assert.Equal(Proto.OffCollisionRing, Hex("kOffCollisionRing"));
            Assert.Matches(@"kVersion\s*=\s*" + Proto.Version + ";", h);
        }
    }

    public class LinkTests
    {
        private static string UniqueName() => "Local\\MinekillTest_" + Guid.NewGuid().ToString("N");

        [Fact]
        public void GuestCannotOpenBeforeHostExists()
        {
            Link g = Link.OpenGuest(UniqueName(), out string error);
            Assert.Null(g);
            Assert.Contains("OpenFileMapping", error);
        }

        [Fact]
        public void HandshakeAndStateRoundTrip()
        {
            string name = UniqueName();
            using Link host = Link.CreateHost(name, out string e1);
            Assert.True(host != null, e1);
            using Link guest = Link.OpenGuest(name, out string e2);
            Assert.True(guest != null, e2);

            Assert.Equal(Link.CurrentPid(), host.PeerPid);
            Assert.True(host.PeerAlive);
            Assert.True(guest.PeerAlive);

            var hs = new HostState { Flags = Proto.HostInGame, WorldId = 0xABCD, X = 1.25, Y = -64.5, Z = 1e5, Yaw = -179.5f, Pitch = 45f, TeleportSeq = 7, ViewportW = 1920, ViewportH = 1080 };
            host.WriteHostState(hs);
            Assert.True(guest.TryReadHostState(out HostState hr));
            Assert.Equal(hs, hr);

            var gs = new GuestState { Flags = Proto.GuestInWorld, X = 3, Y = 4, Z = 5, Yaw = 10, Pitch = -20, EyeHeight = 1.62f, TeleportAck = 7, FrameCounter = ulong.MaxValue - 1, Fov = 70, CameraMode = 1 };
            guest.WriteGuestState(gs);
            Assert.True(host.TryReadGuestState(out GuestState gr));
            Assert.Equal(gs, gr);
        }

        [Fact]
        public void EachSideCanOnlyWriteItsOwnState()
        {
            string name = UniqueName();
            using Link host = Link.CreateHost(name, out _);
            using Link guest = Link.OpenGuest(name, out _);
            Assert.Throws<InvalidOperationException>(() => host.WriteGuestState(default));
            Assert.Throws<InvalidOperationException>(() => guest.WriteHostState(default));
        }

        [Fact]
        public void PeerGoesStaleWithoutHeartbeat()
        {
            string name = UniqueName();
            long now = 1_000_000;
            using Link host = Link.CreateHost(name, out _, () => now);
            using Link guest = Link.OpenGuest(name, out _, () => now);
            Assert.True(host.PeerAlive);
            now += Proto.PeerTimeoutMs + 1;
            Assert.False(host.PeerAlive);
            guest.Heartbeat();
            Assert.True(host.PeerAlive);
        }

        [Fact]
        public void HostResetsLeftoverStateOnRecreate()
        {
            string name = UniqueName();
            using Link host1 = Link.CreateHost(name, out _);
            using Link guest = Link.OpenGuest(name, out _); // keeps the mapping alive
            host1.WriteHostState(new HostState { X = 42, Flags = Proto.HostInGame });
            host1.Dispose();
            using Link host2 = Link.CreateHost(name, out _);
            Assert.True(host2.ReusedMapping);
            Assert.True(guest.TryReadHostState(out HostState s));
            Assert.Equal(0, s.X);
            Assert.Equal(0u, s.Flags);
        }

        // Seqlock oracle: the writer always stores X == Y == Z; a read that reports success must
        // never see them differ, and the reader must make progress.
        [Fact]
        public void SeqlockNeverReturnsTornState()
        {
            string name = UniqueName();
            using Link host = Link.CreateHost(name, out _);
            using Link guest = Link.OpenGuest(name, out _);
            using var stop = new CancellationTokenSource();
            Task writer = Task.Run(() =>
            {
                double i = 0;
                while (!stop.IsCancellationRequested)
                {
                    i++;
                    host.WriteHostState(new HostState { X = i, Y = i, Z = i, Yaw = (float)i });
                }
            });
            int ok = 0, torn = 0;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1500)
            {
                if (guest.TryReadHostState(out HostState s))
                {
                    ok++;
                    Assert.True(s.X == s.Y && s.Y == s.Z, $"torn read accepted: {s.X} {s.Y} {s.Z}");
                }
                else
                {
                    torn++;
                }
            }
            stop.Cancel();
            writer.Wait();
            Assert.True(ok > 1000, $"only {ok} successful reads ({torn} rejected)");
        }
    }

    public class CoordsTests
    {
        [Fact]
        public void RoundTripAndAxes()
        {
            var c = new Coords(2.0);
            Coords.LevelOrigin("Level 0-1", out double ox, out double oz);
            c.OriginX = ox;
            c.OriginZ = oz;
            c.ToMc(10, 7, -4, out double x, out double y, out double z);
            Assert.Equal(5 + ox, x, 9);
            Assert.Equal(3.5 + Coords.YOffset, y, 9);
            Assert.Equal(2 + oz, z, 9); // Unity -Z (back) is Minecraft +Z (south)
            c.ToUnity(x, y, z, out double ux, out double uy, out double uz);
            Assert.Equal(10, ux, 9);
            Assert.Equal(7, uy, 9);
            Assert.Equal(-4, uz, 9);
        }

        [Fact]
        public void DeepAndHighLevelsStayInsideMinecraftsBuildHeight()
        {
            var c = new Coords(2.0);
            c.ToMc(0, -250, 0, out _, out double low, out _);
            c.ToMc(0, 500, 0, out _, out double high, out _);
            Assert.InRange(low, -64, 320);
            Assert.InRange(high, -64, 320);
        }

        [Theory]
        [InlineData(0.1, 0.1, 0.1)]
        [InlineData(-3.9, 7.2, -0.01)]
        [InlineData(1.99, -1.99, 5.5)]
        public void BlockCellContainsThePointAndCentreMapsBack(double ux, double uy, double uz)
        {
            var c = new Coords(2.0) { OriginX = 100, OriginZ = -50 };
            c.BlockAt(ux, uy, uz, out int bx, out int by, out int bz);
            c.BlockCenter(bx, by, bz, out double cx, out double cy, out double cz);
            // The point lies within half a block (1 Unity unit) of its block's centre on every axis.
            Assert.InRange(ux, cx - 1, cx + 1);
            Assert.InRange(uy, cy - 1, cy + 1);
            Assert.InRange(uz, cz - 1, cz + 1);
            // And the centre is inside the same block.
            c.BlockAt(cx, cy, cz, out int bx2, out int by2, out int bz2);
            Assert.Equal((bx, by, bz), (bx2, by2, bz2));
        }

        [Fact]
        public void LevelOriginsAreStableBoundedAndDistinct()
        {
            Coords.LevelOrigin("Level 0-1", out double a, out double b);
            Coords.LevelOrigin("Level 0-1", out double a2, out double b2);
            Assert.Equal((a, b), (a2, b2));
            string[] levels = { "Level 0-1", "Level 0-2", "Level 1-1", "Level 4-4", "Level P-2", "Endless" };
            var origins = levels.Select(l => { Coords.LevelOrigin(l, out double x, out double z); return (x, z); }).ToList();
            Assert.Equal(origins.Count, origins.Distinct().Count());
            Assert.All(origins, o => Assert.True(Math.Abs(o.x) <= 60000 && Math.Abs(o.z) <= 60000));
        }

        [Theory]
        [InlineData(0f, -180f)]    // Unity forward (+Z) = Minecraft north: yaw 180 == -180
        [InlineData(90f, -90f)]    // Unity +X (east) = Minecraft east = yaw -90
        [InlineData(180f, 0f)]     // Unity -Z = Minecraft south = yaw 0
        [InlineData(-90f, 90f)]    // west
        public void YawMapsCompassDirections(float unity, float mc)
        {
            float got = Coords.YawToMc(unity);
            Assert.True(Math.Abs(Coords.Wrap180(got - mc)) < 1e-4, $"{unity} -> {got}, expected {mc}");
            Assert.True(Math.Abs(Coords.Wrap180(Coords.YawToUnity(got) - unity)) < 1e-4);
        }

        [Fact]
        public void PitchSignAndClamp()
        {
            Assert.Equal(-30f, Coords.PitchToMc(30f)); // looking up
            Assert.Equal(90f, Coords.PitchToMc(-120f));
        }
    }

    // Cross-process oracle: a separate process (FakeGuest) connects through the named mapping,
    // heartbeats, and echoes the host position. Proves the link works across process boundaries.
    public class CrossProcessTests
    {
        private readonly ITestOutputHelper output;

        public CrossProcessTests(ITestOutputHelper output) => this.output = output;

        private static Process StartFakeGuest(string args, System.Collections.Concurrent.ConcurrentQueue<string> lines)
        {
            string dll = Path.Combine(AppContext.BaseDirectory, "FakeGuest.dll");
            Assert.True(File.Exists(dll), dll);
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "", "dotnet.exe"), $"\"{dll}\" {args}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            if (!File.Exists(psi.FileName)) psi.FileName = "dotnet";
            Process p = Process.Start(psi);
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lines.Enqueue(e.Data); };
            p.BeginOutputReadLine();
            return p;
        }

        // Regression (fifth in-game run): clicking into the PowerShell window (QuickEdit) blocks
        // every console write; FakeGuest's heartbeat was on the same thread as its output and froze.
        // Here stdout is redirected and never read, so writes block once the pipe buffer fills.
        [Fact]
        public void FakeGuestKeepsHeartbeatWhenConsoleBlocks()
        {
            string name = "Local\\MinekillTest_" + Guid.NewGuid().ToString("N");
            using Link host = Link.CreateHost(name, out string error);
            Assert.True(host != null, error);
            string dll = Path.Combine(AppContext.BaseDirectory, "FakeGuest.dll");
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "", "dotnet.exe"), $"\"{dll}\" \"{name}\" --seconds 8 --verbose")
            {
                RedirectStandardOutput = true, // never read: the pipe fills and writes block
                UseShellExecute = false,
            };
            if (!File.Exists(psi.FileName)) psi.FileName = "dotnet";
            using Process p = Process.Start(psi);
            try
            {
                var sw = Stopwatch.StartNew();
                ulong lastFrame = 0;
                int stalls = 0;
                while (sw.ElapsedMilliseconds < 6000)
                {
                    host.Heartbeat();
                    host.WriteHostState(new HostState { Flags = Proto.HostInGame, X = 1 });
                    Thread.Sleep(500);
                    if (sw.ElapsedMilliseconds > 2500) // well after the pipe buffer is full
                    {
                        // A torn read is legal (the writer was mid-update): retry, as the plugin does.
                        GuestState g = default;
                        bool read = false;
                        for (int tries = 0; tries < 50 && !read; tries++)
                        {
                            read = host.TryReadGuestState(out g);
                            if (!read) Thread.Sleep(1);
                        }
                        Assert.True(read, "no consistent guest state in 50 tries");
                        if (g.FrameCounter == lastFrame) stalls++;
                        lastFrame = g.FrameCounter;
                        Assert.True(host.PeerAlive, $"guest heartbeat stopped ({host.PeerBeatAgeMs} ms)");
                    }
                }
                Assert.True(stalls <= 1, $"guest frames stalled {stalls} times");
            }
            finally
            {
                if (!p.HasExited) p.Kill();
            }
        }

        // Regression (first in-game run, 2026-10-08): ULTRAKILL stops running frames while its window
        // is unfocused (and during long loads), so its heartbeat pauses. The guest must wait it out
        // and resume, not exit (CONTRACT.md, lifecycle 5).
        [Fact]
        public void FakeGuestSurvivesHostPause()
        {
            string name = "Local\\MinekillTest_" + Guid.NewGuid().ToString("N");
            using Link host = Link.CreateHost(name, out string error);
            Assert.True(host != null, error);
            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using Process p = StartFakeGuest($"\"{name}\" --seconds 12", lines);

            void Drive(int ms, uint seq)
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < ms)
                {
                    host.Heartbeat();
                    host.WriteHostState(new HostState { Flags = Proto.HostInGame, X = 1, TeleportSeq = seq });
                    Thread.Sleep(16);
                }
            }

            Drive(2000, 1);
            Assert.True(host.TryReadGuestState(out GuestState before) && before.TeleportAck == 1, "guest not echoing before the pause");
            Thread.Sleep(5000);                // host frozen: no heartbeat, longer than PeerTimeoutMs
            Assert.False(p.HasExited, "guest exited during a host pause: " + string.Join(" | ", lines));
            Drive(2000, 2);
            Assert.True(host.TryReadGuestState(out GuestState after) && after.TeleportAck == 2, "guest did not resume after the pause");
            Assert.True(host.PeerAlive);
            p.WaitForExit(10000);
            Assert.Equal(0, p.ExitCode);
            Assert.Contains(lines, l => l.StartsWith("WAIT"));
        }

        // Regression (second in-game run): the main menu has a player too, so --stay anchored in the
        // menu's area and stayed there after loading 0-1, ~41000 blocks away. A teleport request
        // (new TeleportSeq: scene change, reconnect) must move the guest, as Minecraft would.
        [Fact]
        public void FakeGuestStayModeReanchorsOnTeleport()
        {
            string name = "Local\\MinekillTest_" + Guid.NewGuid().ToString("N");
            using Link host = Link.CreateHost(name, out string error);
            Assert.True(host != null, error);
            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using Process p = StartFakeGuest($"\"{name}\" --seconds 5 --stay --anchor-delay 0", lines);

            GuestState Drive(int ms, uint seq, double x0)
            {
                var sw = Stopwatch.StartNew();
                GuestState g = default;
                while (sw.ElapsedMilliseconds < ms)
                {
                    host.Heartbeat();
                    host.WriteHostState(new HostState { Flags = Proto.HostInGame, X = x0 + sw.ElapsedMilliseconds / 100.0, TeleportSeq = seq });
                    host.TryReadGuestState(out g);
                    Thread.Sleep(16);
                }
                return g;
            }

            GuestState menu = Drive(1500, 5, -43008);   // "main menu" area
            Assert.InRange(menu.X, -43008, -42990);
            GuestState level = Drive(1500, 6, -2048);   // "Level 0-1": new teleport request
            Assert.InRange(level.X, -2048, -2030);      // re-anchored near where 0-1 started
            Assert.Equal(6u, level.TeleportAck);
            GuestState later = Drive(1000, 6, -2048 + 500);
            Assert.Equal(level.X, later.X);             // and stays there while the host walks away
            p.WaitForExit(10000);
            Assert.Equal(0, p.ExitCode);
        }

        // ULTRAKILL levels start with V1 falling in: the anchor must be taken after the delay, not
        // at the first in-game frame (which is mid-air).
        [Fact]
        public void FakeGuestStayModeAnchorsAfterDelay()
        {
            string name = "Local\\MinekillTest_" + Guid.NewGuid().ToString("N");
            using Link host = Link.CreateHost(name, out string error);
            Assert.True(host != null, error);
            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using Process p = StartFakeGuest($"\"{name}\" --seconds 4 --stay --anchor-delay 1.5", lines);
            var sw = Stopwatch.StartNew();
            GuestState g = default;
            while (sw.ElapsedMilliseconds < 3500)
            {
                host.Heartbeat();
                // x = elapsed seconds since the host started; the guest starts within ~0.5 s.
                host.WriteHostState(new HostState { Flags = Proto.HostInGame, X = sw.ElapsedMilliseconds / 1000.0, TeleportSeq = 1 });
                host.TryReadGuestState(out g);
                Thread.Sleep(16);
            }
            p.WaitForExit(10000);
            Assert.InRange(g.X, 1.4, 3.0); // anchored ~1.5 s after the guest first saw the request, not at ~0
            Assert.Contains(lines, l => l.StartsWith("ANCHORED"));
        }

        [Fact]
        public void FakeGuestStayModeHoldsFirstInGamePosition()
        {
            string name = "Local\\MinekillTest_" + Guid.NewGuid().ToString("N");
            using Link host = Link.CreateHost(name, out string error);
            Assert.True(host != null, error);
            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using Process p = StartFakeGuest($"\"{name}\" --seconds 2 --stay --anchor-delay 0", lines);
            var sw = Stopwatch.StartNew();
            double firstX = double.NaN;
            GuestState g = default;
            int frames = 0;
            while (!p.HasExited && sw.ElapsedMilliseconds < 10000)
            {
                host.Heartbeat();
                double x = 100 + sw.ElapsedMilliseconds / 10.0; // keeps moving
                host.WriteHostState(new HostState { Flags = Proto.HostInGame, X = x, Y = 10 });
                if (host.TryReadGuestState(out g) && g.FrameCounter > 0)
                {
                    if (double.IsNaN(firstX)) firstX = g.X;
                    Assert.Equal(firstX, g.X);
                    frames++;
                }
                Thread.Sleep(16);
            }
            p.WaitForExit(5000);
            Assert.True(frames > 20, $"only {frames} guest frames");
            Assert.Contains(lines, l => l.StartsWith("ANCHORED"));
            Assert.Equal(0, p.ExitCode);
        }

        [Fact]
        public void FakeGuestProcessConnectsAndEchoes()
        {
            string name = "Local\\MinekillTest_" + Guid.NewGuid().ToString("N");
            using Link host = Link.CreateHost(name, out string error);
            Assert.True(host != null, error);

            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using Process p = StartFakeGuest($"\"{name}\" --seconds 3", lines);

            var sw = Stopwatch.StartNew();
            bool echoed = false;
            double latestX = 0;
            GuestState g = default;
            while (sw.ElapsedMilliseconds < 10000 && !p.HasExited)
            {
                host.Heartbeat();
                latestX = sw.ElapsedMilliseconds / 100.0;
                host.WriteHostState(new HostState { Flags = Proto.HostInGame, X = latestX, Y = 64, Z = -3, TeleportSeq = 9 });
                if (host.PeerAlive && host.TryReadGuestState(out g) && g.TeleportAck == 9 && Math.Abs(g.Y - 64.5) < 1e-9 && g.FrameCounter > 5)
                {
                    echoed = true;
                }
                Thread.Sleep(16);
            }
            p.WaitForExit(5000);
            foreach (string l in lines) output.WriteLine(l);
            Assert.True(echoed, "guest never echoed the host state");
            Assert.Equal(0, p.ExitCode);
            Assert.Contains(lines, l => l.StartsWith("CONNECTED"));
            Assert.NotEqual(Link.CurrentPid(), host.PeerPid);
            Assert.Equal((uint)p.Id, host.PeerPid);
        }
    }
}
