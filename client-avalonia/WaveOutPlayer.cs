// WaveOutPlayer — minimal PCM playback via Windows waveOut API.
// 16-bit PCM, lazy-opened on first frame with whatever rate/channels the
// stream reports (PS5 Remote Play sends 48 kHz stereo).

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PS5Upload
{
    internal sealed class WaveOutPlayer : IDisposable
    {
        private const int MaxQueued = 8;

        private IntPtr _handle = IntPtr.Zero;
        private int _rate, _channels;
        private readonly object _lock = new();
        private readonly Queue<(IntPtr hdr, GCHandle data, GCHandle buf)> _pending = new();
        private bool _disposed;

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormatEx
        {
            public ushort wFormatTag, nChannels;
            public uint nSamplesPerSec, nAvgBytesPerSec;
            public ushort nBlockAlign, wBitsPerSample, cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHdr
        {
            public IntPtr lpData;
            public uint dwBufferLength, dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags, dwLoops;
            public IntPtr lpNext, reserved;
        }

        private const uint WHDR_DONE = 1, WHDR_PREPARED = 2;
        private const uint WAVE_MAPPER = 0xFFFFFFFF;

        [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr hwo, IntPtr dev, ref WaveFormatEx fmt, IntPtr cb, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr hwo);
        [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr hwo);

        public bool IsOpen => _handle != IntPtr.Zero;

        private bool EnsureOpen(int rate, int channels)
        {
            if (_handle != IntPtr.Zero)
                return _rate == rate && _channels == channels;
            var fmt = new WaveFormatEx
            {
                wFormatTag = 1,
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)rate,
                wBitsPerSample = 16,
                nBlockAlign = (ushort)(channels * 2),
                nAvgBytesPerSec = (uint)(rate * channels * 2),
                cbSize = 0
            };
            if (waveOutOpen(out _handle, (IntPtr)(long)WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0)
                return false;
            _rate = rate; _channels = channels;
            return true;
        }

        // Called from the audio callback thread — never blocks.
        public void Push(byte[] pcm, int rate, int channels)
        {
            if (!OperatingSystem.IsWindows()) return;
            lock (_lock)
            {
                if (_disposed || !EnsureOpen(rate, channels)) return;
                ReclaimDone();
                // Never touch a buffer the driver may still be playing:
                // if the queue is full of in-flight buffers, drop this packet.
                if (_pending.Count >= MaxQueued) return;

                var buf = Marshal.AllocHGlobal(pcm.Length);
                Marshal.Copy(pcm, 0, buf, pcm.Length);
                var hdr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHdr>());
                var wh = new WaveHdr { lpData = buf, dwBufferLength = (uint)pcm.Length };
                Marshal.StructureToPtr(wh, hdr, false);
                var dataPin = GCHandle.Alloc(hdr, GCHandleType.Normal);
                var bufPin = GCHandle.Alloc(buf, GCHandleType.Normal);
                // GCHandle not strictly needed for HGlobal memory; track to free
                if (waveOutPrepareHeader(_handle, hdr, Marshal.SizeOf<WaveHdr>()) != 0)
                {
                    Marshal.FreeHGlobal(hdr); Marshal.FreeHGlobal(buf);
                    dataPin.Free(); bufPin.Free();
                    return;
                }
                if (waveOutWrite(_handle, hdr, Marshal.SizeOf<WaveHdr>()) != 0)
                {
                    waveOutUnprepareHeader(_handle, hdr, Marshal.SizeOf<WaveHdr>());
                    Marshal.FreeHGlobal(hdr); Marshal.FreeHGlobal(buf);
                    dataPin.Free(); bufPin.Free();
                    return;
                }
                _pending.Enqueue((hdr, dataPin, bufPin));
            }
        }

        private void ReclaimDone()
        {
            while (_pending.Count > 0)
            {
                var (hdr, dataPin, bufPin) = _pending.Peek();
                var wh = Marshal.PtrToStructure<WaveHdr>(hdr);
                if ((wh.dwFlags & WHDR_DONE) == 0) break;
                _pending.Dequeue();
                waveOutUnprepareHeader(_handle, hdr, Marshal.SizeOf<WaveHdr>());
                Marshal.FreeHGlobal(wh.lpData); Marshal.FreeHGlobal(hdr);
                dataPin.Free(); bufPin.Free();
            }
        }

        // Reclaim completed buffers — call periodically (timer/pump).
        public void Pump()
        {
            lock (_lock) { ReclaimDone(); }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                if (_handle != IntPtr.Zero)
                {
                    waveOutReset(_handle);
                    while (_pending.Count > 0)
                    {
                        var (hdr, dataPin, bufPin) = _pending.Dequeue();
                        var wh = Marshal.PtrToStructure<WaveHdr>(hdr);
                        waveOutUnprepareHeader(_handle, hdr, Marshal.SizeOf<WaveHdr>());
                        Marshal.FreeHGlobal(wh.lpData); Marshal.FreeHGlobal(hdr);
                        dataPin.Free(); bufPin.Free();
                    }
                    waveOutClose(_handle);
                    _handle = IntPtr.Zero;
                }
            }
        }
    }
}
