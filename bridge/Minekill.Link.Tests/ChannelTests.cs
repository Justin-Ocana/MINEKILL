using System;
using System.Runtime.InteropServices;
using Minekill.Link;
using Xunit;

namespace Minekill.Link.Tests
{
    public class ChannelTests
    {
        private static string UniqueName() => "Local\\MinekillTest_" + Guid.NewGuid().ToString("N");

        [Fact]
        public void ChannelOffsetsMatchSkyCraftV11()
        {
            Assert.Equal(0x300, Proto.OffOverlayCtl);
            Assert.Equal(0x340, Proto.OffOverlaySlotHdr);
            Assert.Equal(0x1000, Proto.OffInputRing);
            Assert.Equal(0x12000, Proto.OffActorTable);
            Assert.Equal(0x17000, Proto.OffEventRing);
            Assert.Equal(64, Proto.ActorRecordBytes);
            Assert.Equal(40, Proto.ArName);
            // The input ring must end before the actor table, the event ring before world entities.
            Assert.True(Proto.OffInputRing + Proto.RingData + Proto.InputRingEntries * Proto.InputEventBytes <= Proto.OffActorTable);
            Assert.True(Proto.OffActorTable + Proto.AtRecords + Proto.MaxActors * Proto.ActorRecordBytes <= Proto.OffEventRing);
            Assert.True(Proto.OffEventRing + Proto.RingData + Proto.EventRingEntries * Proto.EventBytes <= 0x1C000);
        }

        // MINEKILL's own message and command numbers; mcmod ChannelTest.minekillNumbersMatchCSharp
        // checks the same literals on the Java side, so the two can't drift apart silently.
        [Fact]
        public void MinekillNumbersMatchJava()
        {
            Assert.Equal(100, Proto.InUse);
            Assert.Equal(101, Proto.InBreak);
            Assert.Equal(102, Proto.InHeal);
            Assert.Equal(103, Proto.InLoot);
            Assert.Equal(104, Proto.InExplode);
            Assert.Equal(100u, Proto.RenBlocks);
            Assert.Equal(101u, Proto.RenTexSet);
            Assert.Equal(102u, Proto.RenTnt);
            Assert.Equal(103u, Proto.RenThrow);
            Assert.Equal(104u, Proto.RenItemTex);
            Assert.Equal(110u, Proto.EvParry);
            Assert.Equal(256u, Proto.HitExplosion);
            Assert.Equal(8u, Proto.HostAirborne);
            Assert.Equal(16u, Proto.HostFalling);
            Assert.Equal(32u, Proto.HostSprinting);
            Assert.Equal(64u, Proto.HostSneaking);
            Assert.Equal((1u, 2u, 3u, 4u), (Proto.ThrowArrow, Proto.ThrowSnowball, Proto.ThrowEgg, Proto.ThrowEnderPearl));
            Assert.Equal(11, Proto.LootCookedBeef);
            Assert.Equal(256u, Proto.GuestBlocking);
            Assert.Equal((105, 106, 107), (Proto.InCursor, Proto.InScreenButton, Proto.InFall));
            Assert.Equal((8, 9, 20, 41, 30), (Proto.ScanE, Proto.ScanF, Proto.ScanQ, Proto.ScanEscape, Proto.Scan1));
            Assert.Equal(10000, Proto.CursorScale);
            Assert.Equal((3, 0xC0), (Proto.ModShift, Proto.ModCtrl));
        }

        [Fact]
        public void OverlayTripleBufferDeliversNewestFrameOnce()
        {
            string name = UniqueName();
            using Link host = Link.CreateHost(name, out _);
            using Link guest = Link.OpenGuest(name, out _);
            Assert.False(host.TryAcquireOverlay(out _));

            byte[] Frame(byte v, int w, int h)
            {
                var b = new byte[w * h * 4];
                for (int i = 0; i < b.Length; i++) b[i] = v;
                return b;
            }
            guest.PublishOverlay(Frame(10, 4, 2), 4, 2, true, 1);
            guest.PublishOverlay(Frame(20, 4, 2), 4, 2, true, 2); // overwrites the unread one
            Assert.True(host.TryAcquireOverlay(out OverlayFrame f));
            Assert.Equal(2, f.FrameId);
            Assert.Equal(4, f.Width);
            Assert.Equal(2, f.Height);
            Assert.True(f.BottomUp);
            Assert.Equal(20, Marshal.ReadByte(f.Pixels, 0));
            Assert.Equal(20, Marshal.ReadByte(f.Pixels, 4 * 2 * 4 - 1));
            Assert.False(host.TryAcquireOverlay(out _)); // nothing new
            Assert.Equal(2, host.OverlayFramesPublished);

            // Many frames: every acquire sees a frame id newer than the last, never a torn mix.
            long last = 2;
            for (int i = 3; i < 200; i++)
            {
                guest.PublishOverlay(Frame((byte)i, 8, 8), 8, 8, false, i);
                if (i % 3 == 0 && host.TryAcquireOverlay(out OverlayFrame g))
                {
                    Assert.True(g.FrameId > last);
                    Assert.Equal((byte)g.FrameId, Marshal.ReadByte(g.Pixels, 100));
                    last = g.FrameId;
                }
            }
        }

        [Fact]
        public void InputRingIsFifoAndRefusesWhenFull()
        {
            string name = UniqueName();
            using Link host = Link.CreateHost(name, out _);
            using Link guest = Link.OpenGuest(name, out _);
            Assert.False(guest.TryPopInput(out _));
            for (int i = 0; i < 10; i++)
            {
                Assert.True(host.PushInput(new InputEvent { Type = Proto.InKey, Code = (ushort)(30 + i), A = 1, B = -i, C = i * 7 }));
            }
            for (int i = 0; i < 10; i++)
            {
                Assert.True(guest.TryPopInput(out InputEvent e));
                Assert.Equal(Proto.InKey, e.Type);
                Assert.Equal(30 + i, e.Code);
                Assert.Equal(-i, e.B);
                Assert.Equal(i * 7, e.C);
            }
            Assert.False(guest.TryPopInput(out _));
            int pushed = 0;
            while (host.PushInput(new InputEvent { Type = Proto.InMouseButton, Code = 1, A = 1 })) pushed++;
            Assert.Equal(Proto.InputRingEntries, pushed); // full, nothing overwritten
            Assert.True(guest.TryPopInput(out _));
            Assert.True(host.PushInput(new InputEvent { Type = Proto.InScroll, A = 120 })); // room again
        }

        [Fact]
        public void EventRingCarriesHits()
        {
            string name = UniqueName();
            using Link host = Link.CreateHost(name, out _);
            using Link guest = Link.OpenGuest(name, out _);
            Assert.True(guest.PushEvent(new McEvent { Type = Proto.EvHitActor, Id = 42, A = 7.5f, B = 0.1f, C = -0.9f, D = 0.4f, Flags = Proto.HitCritical, Weapon = Proto.WeaponBlade }));
            Assert.True(host.TryPopEvent(out McEvent e));
            Assert.Equal(Proto.EvHitActor, e.Type);
            Assert.Equal(42u, e.Id);
            Assert.Equal(7.5f, e.A);
            Assert.Equal(Proto.HitCritical, e.Flags);
            Assert.Equal(Proto.WeaponBlade, e.Weapon);
            Assert.False(host.TryPopEvent(out _));
        }

        [Fact]
        public void ActorTableRoundTripAndNameTruncation()
        {
            string name = UniqueName();
            using Link host = Link.CreateHost(name, out _);
            using Link guest = Link.OpenGuest(name, out _);
            var actors = new ActorRecord[Proto.MaxActors];
            actors[0] = new ActorRecord { Id = 7, Flags = Proto.ActorHostile, X = 1, Y = 2, Z = 3, Yaw = 90, Width = 0.8f, Height = 1.9f, HealthFrac = 0.5f, Name = "Filth" };
            actors[1] = new ActorRecord { Id = 8, Name = "A name much longer than twenty-three bytes" };
            host.WriteActors(actors, 2);
            var read = new ActorRecord[Proto.MaxActors];
            Assert.True(guest.TryReadActors(read, out int n));
            Assert.Equal(2, n);
            Assert.Equal(7u, read[0].Id);
            Assert.Equal(1.9f, read[0].Height);
            Assert.Equal("Filth", read[0].Name);
            Assert.Equal(23, read[1].Name.Length); // NUL-terminated within 24 bytes
            host.WriteActors(actors, 0);
            Assert.True(guest.TryReadActors(read, out n));
            Assert.Equal(0, n);
        }

        [Fact]
        public void HostRecreateResetsChannels()
        {
            string name = UniqueName();
            using Link guest0Host = Link.CreateHost(name, out _);
            using Link guest = Link.OpenGuest(name, out _);
            guest0Host.PushInput(new InputEvent { Type = Proto.InKey, Code = 30, A = 1 });
            guest.PushEvent(new McEvent { Type = Proto.EvHitActor });
            guest.PublishOverlay(new byte[16], 2, 2, false, 1);
            guest0Host.Dispose();
            using Link host2 = Link.CreateHost(name, out _);
            Assert.False(guest.TryPopInput(out _));
            Assert.False(host2.TryPopEvent(out _));
            Assert.False(host2.TryAcquireOverlay(out _));
        }
    }
}
