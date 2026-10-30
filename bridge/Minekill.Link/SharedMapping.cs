using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Minekill.Link
{
    // A named, pagefile-backed Win32 file mapping viewed as raw bytes. The host creates it; the
    // guest opens it. All access goes through offset helpers so every read/write is explicit about
    // its width (the layout is shared with Java and C++ peers).
    public sealed unsafe class SharedMapping : IDisposable
    {
        private IntPtr handle;
        private byte* view;

        public string Name { get; }
        public long Size { get; }
        public bool Existed { get; }
        public bool IsOpen => view != null;

        private SharedMapping(string name, long size, IntPtr handle, byte* view, bool existed)
        {
            Name = name;
            Size = size;
            this.handle = handle;
            this.view = view;
            Existed = existed;
        }

        public static SharedMapping Create(string name, long size, out string error)
        {
            IntPtr h = Native.CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, Native.PageReadWrite,
                (uint)((ulong)size >> 32), (uint)((ulong)size & 0xFFFFFFFF), name);
            int lastError = Marshal.GetLastWin32Error();
            if (h == IntPtr.Zero)
            {
                error = $"CreateFileMapping({name}) failed: Win32 error {lastError}";
                return null;
            }
            return MapView(name, size, h, lastError == Native.ErrorAlreadyExists, out error);
        }

        public static SharedMapping Open(string name, long size, out string error)
        {
            IntPtr h = Native.OpenFileMappingW(Native.FileMapAllAccess, false, name);
            if (h == IntPtr.Zero)
            {
                error = $"OpenFileMapping({name}) failed: Win32 error {Marshal.GetLastWin32Error()}";
                return null;
            }
            return MapView(name, size, h, true, out error);
        }

        private static SharedMapping MapView(string name, long size, IntPtr h, bool existed, out string error)
        {
            IntPtr v = Native.MapViewOfFile(h, Native.FileMapAllAccess, 0, 0, new UIntPtr((ulong)size));
            if (v == IntPtr.Zero)
            {
                error = $"MapViewOfFile({name}) failed: Win32 error {Marshal.GetLastWin32Error()}";
                Native.CloseHandle(h);
                return null;
            }
            error = null;
            return new SharedMapping(name, size, h, (byte*)v, existed);
        }

        private byte* At(long off, int width)
        {
            if (view == null)
            {
                throw new ObjectDisposedException(Name);
            }
            if (off < 0 || off + width > Size)
            {
                throw new ArgumentOutOfRangeException(nameof(off), $"0x{off:X}+{width} outside mapping of {Size} bytes");
            }
            return view + off;
        }

        public uint ReadU32(long off) => *(uint*)At(off, 4);
        public void WriteU32(long off, uint v) => *(uint*)At(off, 4) = v;
        public long ReadI64(long off) => *(long*)At(off, 8);
        public void WriteI64(long off, long v) => *(long*)At(off, 8) = v;
        public ulong ReadU64(long off) => *(ulong*)At(off, 8);
        public float ReadF32(long off) => *(float*)At(off, 4);
        public void WriteF32(long off, float v) => *(float*)At(off, 4) = v;
        public double ReadF64(long off) => *(double*)At(off, 8);
        public void WriteF64(long off, double v) => *(double*)At(off, 8) = v;

        // Ordered accesses for values another process polls (sequence counters, heartbeats).
        public uint VolatileReadU32(long off) => (uint)Volatile.Read(ref *(int*)At(off, 4));
        public void VolatileWriteU32(long off, uint v) => Volatile.Write(ref *(int*)At(off, 4), (int)v);
        public long VolatileReadI64(long off) => Volatile.Read(ref *(long*)At(off, 8));
        public void VolatileWriteI64(long off, long v) => Volatile.Write(ref *(long*)At(off, 8), v);

        public ushort ReadU16(long off) => *(ushort*)At(off, 2);
        public void WriteU16(long off, ushort v) => *(ushort*)At(off, 2) = v;
        public int ReadI32(long off) => *(int*)At(off, 4);
        public void WriteI32(long off, int v) => *(int*)At(off, 4) = v;

        // Atomic swap, the primitive of the overlay triple buffer.
        public uint ExchangeU32(long off, uint v) => (uint)Interlocked.Exchange(ref *(int*)At(off, 4), (int)v);
        public long AddI64(long off, long v) => Interlocked.Add(ref *(long*)At(off, 8), v);

        // Raw pointer to a checked range (pixel uploads read straight from the mapping).
        public IntPtr Pointer(long off, long length)
        {
            At(off, 0);
            if (off + length > Size)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }
            return (IntPtr)(view + off);
        }

        public void CopyIn(byte[] src, int srcIndex, long off, int count) => Marshal.Copy(src, srcIndex, Pointer(off, count), count);
        public void CopyOut(long off, byte[] dst, int dstIndex, int count) => Marshal.Copy(Pointer(off, count), dst, dstIndex, count);

        public void Zero(long off, long length)
        {
            byte* p = At(off, (int)Math.Min(length, int.MaxValue));
            for (long i = 0; i < length; i++)
            {
                p[i] = 0;
            }
        }

        public void Dispose()
        {
            if (view != null)
            {
                Native.UnmapViewOfFile((IntPtr)view);
                view = null;
            }
            if (handle != IntPtr.Zero)
            {
                Native.CloseHandle(handle);
                handle = IntPtr.Zero;
            }
        }
    }

    internal static class Native
    {
        public const uint PageReadWrite = 0x04;
        public const uint FileMapAllAccess = 0xF001F;
        public const int ErrorAlreadyExists = 183;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFileMappingW(IntPtr hFile, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UnmapViewOfFile(IntPtr view);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentProcessId();
    }
}
