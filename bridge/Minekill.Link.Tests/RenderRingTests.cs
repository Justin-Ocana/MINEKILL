using System;
using Minekill.Link;
using Xunit;

namespace Minekill.Link.Tests
{
    public class RenderRingTests
    {
        private static string UniqueName() => "Local\\MinekillTest_" + Guid.NewGuid().ToString("N");

        private static byte[] Payload(int len, int seed)
        {
            var b = new byte[len];
            for (int i = 0; i < len; i++) b[i] = (byte)(seed * 31 + i);
            return b;
        }

        [Fact]
        public void MessagesArriveInOrderWithExactPayloads()
        {
            string name = UniqueName();
            using Link host = Link.CreateHost(name, out _);
            using Link guest = Link.OpenGuest(name, out _);
            Assert.False(host.TryPopRenderMessage(out _, out _));
            int[] lens = { 0, 1, 7, 8, 9, 1000 };
            for (int i = 0; i < lens.Length; i++)
            {
                Assert.True(guest.PushRenderMessage((uint)(100 + i), Payload(lens[i], i)));
            }
            for (int i = 0; i < lens.Length; i++)
            {
                Assert.True(host.TryPopRenderMessage(out uint t, out byte[] p));
                Assert.Equal((uint)(100 + i), t);
                Assert.Equal(Payload(lens[i], i), p);
            }
            Assert.False(host.TryPopRenderMessage(out _, out _));
        }

        // 1 MB messages through the 64 MB ring several times: forces many wrap-arounds (pad messages).
        [Fact]
        public void WrapsAroundTheEndOfTheRing()
        {
            string name = UniqueName();
            using Link host = Link.CreateHost(name, out _);
            using Link guest = Link.OpenGuest(name, out _);
            int len = (1 << 20) + 13; // not a multiple of 8, so wrap points drift
            int sent = 0, received = 0;
            while (received < 200)
            {
                while (sent - received < 40 && guest.PushRenderMessage(RenderType(sent), Payload(len, sent)))
                {
                    sent++;
                }
                Assert.True(host.TryPopRenderMessage(out uint t, out byte[] p));
                Assert.Equal(RenderType(received), t);
                Assert.Equal(len, p.Length);
                Assert.Equal((byte)(received * 31), p[0]);
                Assert.Equal((byte)(received * 31 + len - 1), p[len - 1]);
                received++;
            }
        }

        private static uint RenderType(int i) => (uint)(100 + i % 3);

        [Fact]
        public void RefusesWhenFullInsteadOfOverwriting()
        {
            string name = UniqueName();
            using Link host = Link.CreateHost(name, out _);
            using Link guest = Link.OpenGuest(name, out _);
            var big = new byte[8 << 20];
            int pushed = 0;
            while (guest.PushRenderMessage(Proto.RenBlocks, big)) pushed++;
            Assert.InRange(pushed, 6, 8); // 64 MB ring, 8 MB messages
            Assert.True(host.TryPopRenderMessage(out _, out _));
            Assert.True(guest.PushRenderMessage(Proto.RenBlocks, new byte[16])); // room again
        }
    }
}
