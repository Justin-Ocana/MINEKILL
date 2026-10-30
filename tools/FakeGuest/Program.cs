using System;
using System.Diagnostics;
using System.Threading;
using Minekill.Link;

// FakeGuest [mappingName] [--seconds N] [--stay [--anchor-delay S]]
// Plays Minecraft's side of the link at 20 Hz: waits for the host, heartbeats, and reports its
// player standing where the host's player is (offset 0.5 blocks up, so a test can tell it echoed).
// --stay: after each teleport request (scene change, reconnect) wait --anchor-delay seconds
// (default 3), then report that host position and stay there (a fixed marker to walk around).
// Prints one line per second; exits 0 after --seconds, 3 if the host never appears or goes away.
string name = Proto.DefaultMappingName;
double seconds = double.PositiveInfinity;
bool stay = false;
double anchorDelay = 3;
bool verbose = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--seconds") seconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    else if (args[i] == "--stay") stay = true;
    else if (args[i] == "--verbose") verbose = true;
    else if (args[i] == "--anchor-delay") anchorDelay = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    else name = args[i];
}
// Console output goes through a queue drained by its own thread: a blocked console (QuickEdit
// selection in the window, an unread pipe) must never stop the heartbeat. Lines are dropped when
// the queue is full.
var outQueue = new System.Collections.Concurrent.BlockingCollection<string>(256);
var writer = new Thread(() => { foreach (string l in outQueue.GetConsumingEnumerable()) Console.WriteLine(l); }) { IsBackground = true };
writer.Start();
void Out(string line) => outQueue.TryAdd(line);
AppDomain.CurrentDomain.ProcessExit += (_, _) => { outQueue.CompleteAdding(); writer.Join(500); };

HostState? anchor = null;
uint pendingSeq = 0;
var pendingSince = Stopwatch.StartNew();

Link link = null;
var wait = Stopwatch.StartNew();
while (link == null)
{
    link = Link.OpenGuest(name, out string error);
    if (link == null)
    {
        if (wait.Elapsed.TotalSeconds > 60)
        {
            Out($"FAIL no host after 60 s: {error}");
            return 3;
        }
        Thread.Sleep(250);
    }
}
Out($"CONNECTED {name} host pid {link.PeerPid}, my pid {Link.CurrentPid()}");

var run = Stopwatch.StartNew();
ulong frame = 0;
HostState host = default;
int lastReport = -1;
bool paused = false;
while (run.Elapsed.TotalSeconds < seconds)
{
    link.Heartbeat();
    if (link.TryReadHostState(out HostState fresh))
    {
        host = fresh;
    }
    // A stale heartbeat usually means ULTRAKILL is paused (window unfocused, loading), not gone:
    // keep our side running and wait. Only a host process that no longer exists ends the run.
    if (!link.PeerAlive)
    {
        if (!paused)
        {
            paused = true;
            Out($"WAIT host heartbeat stale ({link.PeerBeatAgeMs} ms): paused or loading");
        }
        if (!ProcessExists(link.PeerPid))
        {
            Out($"FAIL host process {link.PeerPid} exited");
            return 3;
        }
    }
    else if (paused)
    {
        paused = false;
        Out("RESUMED host heartbeat");
    }
    frame++;
    // A new teleport request (scene change, reconnect) moves the player to the host, as Minecraft
    // does; with --stay it then stays at that spot.
    // ULTRAKILL levels start with V1 dropping in, so the anchor is taken --anchor-delay seconds
    // after the request, once V1 has landed; until then the guest follows the host.
    if (stay && (anchor == null || anchor.Value.TeleportSeq != host.TeleportSeq))
    {
        anchor = null;
        if (host.TeleportSeq != pendingSeq || (host.Flags & Proto.HostInGame) == 0)
        {
            pendingSeq = host.TeleportSeq;
            pendingSince.Restart();
        }
    }
    if (stay && anchor == null && (host.Flags & Proto.HostInGame) != 0 && pendingSince.Elapsed.TotalSeconds >= anchorDelay)
    {
        anchor = host;
        Out(FormattableString.Invariant($"ANCHORED at ({host.X:F2},{host.Y:F2},{host.Z:F2}) world={host.WorldId:X8} teleport={host.TeleportSeq}"));
    }
    HostState at = anchor ?? host;
    link.WriteGuestState(new GuestState
    {
        Flags = Proto.GuestInWorld | Proto.GuestOnGround,
        X = at.X, Y = at.Y + 0.5, Z = at.Z,
        Yaw = host.Yaw, Pitch = host.Pitch,
        EyeHeight = 1.62f, Fov = 70f,
        TeleportAck = host.TeleportSeq,
        FrameCounter = frame,
    });
    int s = (int)run.Elapsed.TotalSeconds;
    if (verbose)
    {
        Out(FormattableString.Invariant($"frame={frame} host=({host.X:F3},{host.Y:F3},{host.Z:F3}) yaw={host.Yaw:F2} pitch={host.Pitch:F2} seq={host.TeleportSeq}"));
    }
    if (s != lastReport)
    {
        lastReport = s;
        Out(FormattableString.Invariant(
            $"t={s}s hostFlags={host.Flags} host=({host.X:F2},{host.Y:F2},{host.Z:F2}) yaw={host.Yaw:F1} pitch={host.Pitch:F1} world={host.WorldId:X8} frame={frame}"));
    }
    Thread.Sleep(50);
}
Out("DONE");
return 0;

static bool ProcessExists(uint pid)
{
    try
    {
        using Process p = Process.GetProcessById((int)pid);
        return !p.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
}
