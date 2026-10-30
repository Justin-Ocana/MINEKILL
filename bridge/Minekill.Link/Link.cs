using System;
using System.Threading;

namespace Minekill.Link
{
    public enum Role
    {
        Host,  // ULTRAKILL: creates the mapping, writes HostState, reads GuestState
        Guest, // Minecraft (or a test peer): opens the mapping, writes GuestState, reads HostState
    }

    // One end of the link. Not thread-safe: call it from one thread (Unity's main thread on the host).
    public sealed class Link : IDisposable
    {
        private readonly SharedMapping map;
        private readonly Func<long> clockMs;

        public Role Role { get; }
        public string MappingName => map.Name;
        public bool ReusedMapping => map.Existed;

        private Link(Role role, SharedMapping map, Func<long> clockMs)
        {
            Role = role;
            this.map = map;
            this.clockMs = clockMs;
        }

        public static long SystemClockMs() => (long)Native.GetTickCount64();

        public static uint CurrentPid() => Native.GetCurrentProcessId();

        // Host side. Resets everything the host owns, so a mapping left over from an earlier run
        // (still held open by a guest) starts from a known state, then publishes magic last.
        public static Link CreateHost(string name, out string error, Func<long> clockMs = null)
        {
            SharedMapping m = SharedMapping.Create(name, Proto.MappingBytes, out error);
            if (m == null)
            {
                return null;
            }
            var link = new Link(Role.Host, m, clockMs ?? SystemClockMs);
            m.Zero(Proto.OffHostState, Proto.HostStateBytes);
            m.Zero(Proto.OffOverlayCtl, 0x100);                 // overlay control + slot headers
            m.Zero(Proto.OffInputRing, Proto.RingData);         // input ring head/tail
            m.Zero(Proto.OffActorTable, Proto.AtRecords);       // actor table seq/count
            m.Zero(Proto.OffEventRing, Proto.RingData);         // event ring head/tail
            m.Zero(Proto.OffRenderRing, Proto.RingData);        // render ring head/tail
            m.WriteU32(Proto.OffHeader + Proto.HVersion, Proto.Version);
            m.WriteU32(Proto.OffHeader + Proto.HHostPid, CurrentPid());
            link.Heartbeat();
            m.VolatileWriteU32(Proto.OffHeader + Proto.HMagic, Proto.Magic);
            return link;
        }

        // Guest side. Fails (error set) when the host hasn't created the mapping yet or speaks
        // another protocol version, rather than reading data with a different layout.
        public static Link OpenGuest(string name, out string error, Func<long> clockMs = null)
        {
            SharedMapping m = SharedMapping.Open(name, Proto.MappingBytes, out error);
            if (m == null)
            {
                return null;
            }
            uint magic = m.VolatileReadU32(Proto.OffHeader + Proto.HMagic);
            uint version = m.ReadU32(Proto.OffHeader + Proto.HVersion);
            if (magic != Proto.Magic || version != Proto.Version)
            {
                error = $"incompatible mapping: magic 0x{magic:X8}, version {version} (expected 0x{Proto.Magic:X8}, {Proto.Version})";
                m.Dispose();
                return null;
            }
            var link = new Link(Role.Guest, m, clockMs ?? SystemClockMs);
            m.WriteU32(Proto.OffHeader + Proto.HGuestPid, CurrentPid());
            link.Heartbeat();
            return link;
        }

        private long OwnBeat => Role == Role.Host ? Proto.HHostBeatMs : Proto.HGuestBeatMs;
        private long PeerBeat => Role == Role.Host ? Proto.HGuestBeatMs : Proto.HHostBeatMs;

        public void Heartbeat() => map.VolatileWriteI64(Proto.OffHeader + OwnBeat, clockMs());

        public long PeerBeatAgeMs
        {
            get
            {
                long last = map.VolatileReadI64(Proto.OffHeader + PeerBeat);
                return last == 0 ? long.MaxValue : clockMs() - last;
            }
        }

        public bool PeerAlive => PeerBeatAgeMs < Proto.PeerTimeoutMs;

        public uint PeerPid => map.ReadU32(Proto.OffHeader + (Role == Role.Host ? Proto.HGuestPid : Proto.HHostPid));

        // ---- seqlock ------------------------------------------------------------------------
        private void BeginWrite(long region)
        {
            uint seq = map.ReadU32(region);
            map.VolatileWriteU32(region, seq | 1); // odd: write in progress
            Thread.MemoryBarrier();
        }

        private void EndWrite(long region)
        {
            Thread.MemoryBarrier();
            uint seq = map.ReadU32(region);
            map.VolatileWriteU32(region, (seq | 1) + 1); // even again, and different from before
        }

        // ---- host -> guest ------------------------------------------------------------------
        public void WriteHostState(in HostState s)
        {
            RequireRole(Role.Host);
            long o = Proto.OffHostState;
            BeginWrite(o + Proto.HsSeq);
            map.WriteU32(o + Proto.HsFlags, s.Flags);
            map.WriteU32(o + Proto.HsWorldId, s.WorldId);
            map.WriteU32(o + Proto.HsEpoch, s.Epoch);
            map.WriteF64(o + Proto.HsPosX, s.X);
            map.WriteF64(o + Proto.HsPosY, s.Y);
            map.WriteF64(o + Proto.HsPosZ, s.Z);
            map.WriteF32(o + Proto.HsYaw, s.Yaw);
            map.WriteF32(o + Proto.HsPitch, s.Pitch);
            map.WriteU32(o + Proto.HsTeleportSeq, s.TeleportSeq);
            map.WriteU32(o + Proto.HsViewportW, s.ViewportW);
            map.WriteU32(o + Proto.HsViewportH, s.ViewportH);
            map.WriteF32(o + Proto.HsGameHour, s.GameHour);
            EndWrite(o + Proto.HsSeq);
        }

        // False on a torn read (the writer was mid-update); keep the previous value then.
        public bool TryReadHostState(out HostState s)
        {
            long o = Proto.OffHostState;
            uint before = map.VolatileReadU32(o + Proto.HsSeq);
            s = default;
            if ((before & 1) != 0)
            {
                return false;
            }
            Thread.MemoryBarrier();
            s.Flags = map.ReadU32(o + Proto.HsFlags);
            s.WorldId = map.ReadU32(o + Proto.HsWorldId);
            s.Epoch = map.ReadU32(o + Proto.HsEpoch);
            s.X = map.ReadF64(o + Proto.HsPosX);
            s.Y = map.ReadF64(o + Proto.HsPosY);
            s.Z = map.ReadF64(o + Proto.HsPosZ);
            s.Yaw = map.ReadF32(o + Proto.HsYaw);
            s.Pitch = map.ReadF32(o + Proto.HsPitch);
            s.TeleportSeq = map.ReadU32(o + Proto.HsTeleportSeq);
            s.ViewportW = map.ReadU32(o + Proto.HsViewportW);
            s.ViewportH = map.ReadU32(o + Proto.HsViewportH);
            s.GameHour = map.ReadF32(o + Proto.HsGameHour);
            Thread.MemoryBarrier();
            return map.VolatileReadU32(o + Proto.HsSeq) == before;
        }

        // ---- guest -> host ------------------------------------------------------------------
        public void WriteGuestState(in GuestState s)
        {
            RequireRole(Role.Guest);
            long o = Proto.OffGuestState;
            BeginWrite(o + Proto.GsSeq);
            map.WriteU32(o + Proto.GsFlags, s.Flags);
            map.WriteF64(o + Proto.GsX, s.X);
            map.WriteF64(o + Proto.GsY, s.Y);
            map.WriteF64(o + Proto.GsZ, s.Z);
            map.WriteF32(o + Proto.GsYaw, s.Yaw);
            map.WriteF32(o + Proto.GsPitch, s.Pitch);
            map.WriteF32(o + Proto.GsEyeHeight, s.EyeHeight);
            map.WriteU32(o + Proto.GsTeleportAck, s.TeleportAck);
            map.WriteI64(o + Proto.GsFrameCounter, (long)s.FrameCounter);
            map.WriteF32(o + Proto.GsFov, s.Fov);
            map.WriteU32(o + Proto.GsCameraMode, s.CameraMode);
            EndWrite(o + Proto.GsSeq);
        }

        public bool TryReadGuestState(out GuestState s)
        {
            long o = Proto.OffGuestState;
            uint before = map.VolatileReadU32(o + Proto.GsSeq);
            s = default;
            if ((before & 1) != 0)
            {
                return false;
            }
            Thread.MemoryBarrier();
            s.Flags = map.ReadU32(o + Proto.GsFlags);
            s.X = map.ReadF64(o + Proto.GsX);
            s.Y = map.ReadF64(o + Proto.GsY);
            s.Z = map.ReadF64(o + Proto.GsZ);
            s.Yaw = map.ReadF32(o + Proto.GsYaw);
            s.Pitch = map.ReadF32(o + Proto.GsPitch);
            s.EyeHeight = map.ReadF32(o + Proto.GsEyeHeight);
            s.TeleportAck = map.ReadU32(o + Proto.GsTeleportAck);
            s.FrameCounter = map.ReadU64(o + Proto.GsFrameCounter);
            s.Fov = map.ReadF32(o + Proto.GsFov);
            s.CameraMode = map.ReadU32(o + Proto.GsCameraMode);
            Thread.MemoryBarrier();
            return map.VolatileReadU32(o + Proto.GsSeq) == before;
        }

        // ---- overlay triple buffer (guest -> host) ----------------------------------------------
        private int overlayFront = 2; // host's slot; the writer starts with back 1, middle is 0
        private int overlayBack = 1;

        public void ResetOverlay()
        {
            RequireRole(Role.Host);
            map.VolatileWriteU32(Proto.OffOverlayCtl + Proto.OcState, 0);
            overlayFront = 2;
        }

        public long OverlayFramesPublished => map.VolatileReadI64(Proto.OffOverlayCtl + Proto.OcFramesPublished);

        // Takes the newest unread frame, if any. The pixels stay valid until the next acquire.
        public bool TryAcquireOverlay(out OverlayFrame frame)
        {
            RequireRole(Role.Host);
            frame = default;
            long ctl = Proto.OffOverlayCtl + Proto.OcState;
            if ((map.VolatileReadU32(ctl) & Proto.OverlayDirty) == 0)
            {
                return false;
            }
            uint old = map.ExchangeU32(ctl, (uint)overlayFront);
            overlayFront = (int)(old & 3);
            if (overlayFront > 2)
            {
                return false; // corrupt control word: ignore rather than read past the slots
            }
            long hdr = Proto.OffOverlaySlotHdr + Proto.SlotHdrBytes * overlayFront;
            int w = (int)map.ReadU32(hdr + Proto.ShWidth), h = (int)map.ReadU32(hdr + Proto.ShHeight);
            if (w <= 0 || h <= 0 || w > Proto.MaxOverlayW || h > Proto.MaxOverlayH)
            {
                return false;
            }
            frame = new OverlayFrame
            {
                Width = w,
                Height = h,
                BottomUp = (map.ReadU32(hdr + Proto.ShFlags) & Proto.OverlayBottomUp) != 0,
                FrameId = map.ReadI64(hdr + Proto.ShFrameId),
                Pixels = map.Pointer(Proto.OffOverlayPixels + Proto.OverlaySlotBytes * overlayFront, (long)w * h * 4),
            };
            return true;
        }

        // Writer side (Minecraft's role), used by tests and tools.
        public void PublishOverlay(byte[] rgba, int width, int height, bool bottomUp, long frameId)
        {
            RequireRole(Role.Guest);
            map.CopyIn(rgba, 0, Proto.OffOverlayPixels + Proto.OverlaySlotBytes * overlayBack, width * height * 4);
            long hdr = Proto.OffOverlaySlotHdr + Proto.SlotHdrBytes * overlayBack;
            map.WriteU32(hdr + Proto.ShWidth, (uint)width);
            map.WriteU32(hdr + Proto.ShHeight, (uint)height);
            map.WriteU32(hdr + Proto.ShFlags, bottomUp ? Proto.OverlayBottomUp : 0);
            map.WriteI64(hdr + Proto.ShFrameId, frameId);
            Thread.MemoryBarrier();
            uint old = map.ExchangeU32(Proto.OffOverlayCtl + Proto.OcState, (uint)overlayBack | Proto.OverlayDirty);
            overlayBack = (int)(old & 3);
            map.AddI64(Proto.OffOverlayCtl + Proto.OcFramesPublished, 1);
        }

        // ---- rings: head = total entries written, tail = total entries consumed ---------------
        private bool RingPush(long ring, int entries, long entryBytes, Action<long> write)
        {
            long head = map.ReadI64(ring + Proto.RingHead);
            long tail = map.VolatileReadI64(ring + Proto.RingTail);
            if (head - tail >= entries)
            {
                return false; // full: the consumer is gone or far behind
            }
            write(ring + Proto.RingData + (head & (entries - 1)) * entryBytes);
            Thread.MemoryBarrier();
            map.VolatileWriteI64(ring + Proto.RingHead, head + 1);
            return true;
        }

        private bool RingPop(long ring, int entries, long entryBytes, Action<long> read)
        {
            long tail = map.ReadI64(ring + Proto.RingTail);
            long head = map.VolatileReadI64(ring + Proto.RingHead);
            if (tail >= head)
            {
                return false;
            }
            if (head - tail > entries)
            {
                tail = head - entries; // overrun (shouldn't happen: push refuses when full)
            }
            read(ring + Proto.RingData + (tail & (entries - 1)) * entryBytes);
            Thread.MemoryBarrier();
            map.VolatileWriteI64(ring + Proto.RingTail, tail + 1);
            return true;
        }

        public bool PushInput(in InputEvent e)
        {
            RequireRole(Role.Host);
            InputEvent ev = e;
            return RingPush(Proto.OffInputRing, Proto.InputRingEntries, Proto.InputEventBytes, at =>
            {
                map.WriteU16(at + 0, ev.Type);
                map.WriteU16(at + 2, ev.Code);
                map.WriteI32(at + 4, ev.A);
                map.WriteI32(at + 8, ev.B);
                map.WriteI32(at + 12, ev.C);
            });
        }

        public bool TryPopInput(out InputEvent e)
        {
            RequireRole(Role.Guest);
            InputEvent ev = default;
            bool ok = RingPop(Proto.OffInputRing, Proto.InputRingEntries, Proto.InputEventBytes, at =>
            {
                ev.Type = map.ReadU16(at + 0);
                ev.Code = map.ReadU16(at + 2);
                ev.A = map.ReadI32(at + 4);
                ev.B = map.ReadI32(at + 8);
                ev.C = map.ReadI32(at + 12);
            });
            e = ev;
            return ok;
        }

        public bool PushEvent(in McEvent e)
        {
            RequireRole(Role.Guest);
            McEvent ev = e;
            return RingPush(Proto.OffEventRing, Proto.EventRingEntries, Proto.EventBytes, at =>
            {
                map.WriteU32(at + 0, ev.Type);
                map.WriteU32(at + 4, ev.Id);
                map.WriteF32(at + 8, ev.A);
                map.WriteF32(at + 12, ev.B);
                map.WriteF32(at + 16, ev.C);
                map.WriteF32(at + 20, ev.D);
                map.WriteU32(at + 24, ev.Flags);
                map.WriteU32(at + 28, ev.Weapon);
            });
        }

        public bool TryPopEvent(out McEvent e)
        {
            RequireRole(Role.Host);
            McEvent ev = default;
            bool ok = RingPop(Proto.OffEventRing, Proto.EventRingEntries, Proto.EventBytes, at =>
            {
                ev.Type = map.ReadU32(at + 0);
                ev.Id = map.ReadU32(at + 4);
                ev.A = map.ReadF32(at + 8);
                ev.B = map.ReadF32(at + 12);
                ev.C = map.ReadF32(at + 16);
                ev.D = map.ReadF32(at + 20);
                ev.Flags = map.ReadU32(at + 24);
                ev.Weapon = map.ReadU32(at + 28);
            });
            e = ev;
            return ok;
        }

        // ---- render ring (guest -> host), byte ring of 8-aligned messages ------------------------
        private static long Align8(long n) => (n + 7) & ~7L;

        // Writer side (Minecraft's role), used by tests and tools. False if the ring is full.
        public bool PushRenderMessage(uint type, byte[] payload)
        {
            RequireRole(Role.Guest);
            long ring = Proto.OffRenderRing, cap = Proto.RenRingDataBytes;
            long msg = 8 + Align8(payload.Length);
            if (msg > cap / 2)
            {
                throw new ArgumentException("render message too large");
            }
            long head = map.ReadI64(ring + Proto.RingHead);
            long tail = map.VolatileReadI64(ring + Proto.RingTail);
            long pos = head % cap;
            long pad = pos + msg > cap ? cap - pos : 0;
            if (head + pad + msg - tail > cap)
            {
                return false;
            }
            if (pad > 0)
            {
                map.WriteU32(ring + Proto.RingData + pos, Proto.RenPad);
                map.WriteU32(ring + Proto.RingData + pos + 4, (uint)(pad - 8));
                head += pad;
                pos = 0;
            }
            map.WriteU32(ring + Proto.RingData + pos, type);
            map.WriteU32(ring + Proto.RingData + pos + 4, (uint)payload.Length);
            map.CopyIn(payload, 0, ring + Proto.RingData + pos + 8, payload.Length);
            Thread.MemoryBarrier();
            map.VolatileWriteI64(ring + Proto.RingHead, head + msg);
            return true;
        }

        public bool TryPopRenderMessage(out uint type, out byte[] payload)
        {
            RequireRole(Role.Host);
            long ring = Proto.OffRenderRing, cap = Proto.RenRingDataBytes;
            type = 0;
            payload = null;
            while (true)
            {
                long tail = map.ReadI64(ring + Proto.RingTail);
                long head = map.VolatileReadI64(ring + Proto.RingHead);
                if (tail >= head)
                {
                    return false;
                }
                Thread.MemoryBarrier();
                long pos = tail % cap;
                uint t = map.ReadU32(ring + Proto.RingData + pos);
                uint len = map.ReadU32(ring + Proto.RingData + pos + 4);
                if (t == Proto.RenPad)
                {
                    map.VolatileWriteI64(ring + Proto.RingTail, tail + (cap - pos)); // wrap to the start
                    continue;
                }
                if (len > cap - pos - 8)
                {
                    // Corrupt header: drop everything written so far rather than read garbage.
                    map.VolatileWriteI64(ring + Proto.RingTail, head);
                    return false;
                }
                payload = new byte[len];
                map.CopyOut(ring + Proto.RingData + pos + 8, payload, 0, (int)len);
                type = t;
                map.VolatileWriteI64(ring + Proto.RingTail, tail + 8 + Align8(len));
                return true;
            }
        }

        // ---- actor table (host -> guest, seqlock) ----------------------------------------------
        public void WriteActors(ActorRecord[] actors, int count)
        {
            RequireRole(Role.Host);
            count = Math.Max(0, Math.Min(count, Proto.MaxActors));
            long t = Proto.OffActorTable;
            BeginWrite(t + Proto.AtSeq);
            map.WriteU32(t + Proto.AtCount, (uint)count);
            for (int i = 0; i < count; i++)
            {
                long r = t + Proto.AtRecords + i * Proto.ActorRecordBytes;
                ActorRecord a = actors[i];
                map.WriteU32(r + Proto.ArId, a.Id);
                map.WriteU32(r + Proto.ArFlags, a.Flags);
                map.WriteF32(r + Proto.ArX, a.X);
                map.WriteF32(r + Proto.ArY, a.Y);
                map.WriteF32(r + Proto.ArZ, a.Z);
                map.WriteF32(r + Proto.ArYaw, a.Yaw);
                map.WriteF32(r + Proto.ArWidth, a.Width);
                map.WriteF32(r + Proto.ArHeight, a.Height);
                map.WriteF32(r + Proto.ArHealthFrac, a.HealthFrac);
                byte[] name = System.Text.Encoding.UTF8.GetBytes(a.Name ?? "");
                int n = Math.Min(name.Length, (int)Proto.ArNameBytes - 1);
                map.Zero(r + Proto.ArName, Proto.ArNameBytes);
                map.CopyIn(name, 0, r + Proto.ArName, n);
            }
            EndWrite(t + Proto.AtSeq);
        }

        // False on a torn read; `actors` must hold Proto.MaxActors entries.
        public bool TryReadActors(ActorRecord[] actors, out int count)
        {
            long t = Proto.OffActorTable;
            count = 0;
            uint before = map.VolatileReadU32(t + Proto.AtSeq);
            if ((before & 1) != 0)
            {
                return false;
            }
            Thread.MemoryBarrier();
            int n = (int)Math.Min(map.ReadU32(t + Proto.AtCount), (uint)Proto.MaxActors);
            var nameBuf = new byte[Proto.ArNameBytes];
            for (int i = 0; i < n; i++)
            {
                long r = t + Proto.AtRecords + i * Proto.ActorRecordBytes;
                map.CopyOut(r + Proto.ArName, nameBuf, 0, nameBuf.Length);
                int len = Array.IndexOf(nameBuf, (byte)0);
                actors[i] = new ActorRecord
                {
                    Id = map.ReadU32(r + Proto.ArId),
                    Flags = map.ReadU32(r + Proto.ArFlags),
                    X = map.ReadF32(r + Proto.ArX),
                    Y = map.ReadF32(r + Proto.ArY),
                    Z = map.ReadF32(r + Proto.ArZ),
                    Yaw = map.ReadF32(r + Proto.ArYaw),
                    Width = map.ReadF32(r + Proto.ArWidth),
                    Height = map.ReadF32(r + Proto.ArHeight),
                    HealthFrac = map.ReadF32(r + Proto.ArHealthFrac),
                    Name = System.Text.Encoding.UTF8.GetString(nameBuf, 0, len < 0 ? nameBuf.Length : len),
                };
            }
            Thread.MemoryBarrier();
            if (map.VolatileReadU32(t + Proto.AtSeq) != before)
            {
                return false;
            }
            count = n;
            return true;
        }

        private void RequireRole(Role r)
        {
            if (Role != r)
            {
                throw new InvalidOperationException($"{Role} side can't write the {r} state");
            }
        }

        public void Dispose() => map.Dispose();
    }
}
