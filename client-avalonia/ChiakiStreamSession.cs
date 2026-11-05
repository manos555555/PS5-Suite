// ChiakiStreamSession — managed owner of an in-process Remote Play session.
// Wraps ps5chiaki.dll: session lifecycle, event/audio callbacks, BGRA frame
// pull, and DualShock-state injection. Video/audio/input all run inside
// PS5Suite's process — no external streaming app is involved.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static PS5Upload.ChiakiNative;

namespace PS5Upload
{
    internal sealed class ChiakiStreamSession : IDisposable
    {
        public event Action<int, string>? LogMessage;       // level, text
        public event Action? Connected;
        public event Action<string>? Quit;                  // reason string
        public event Action<bool>? PinRequest;              // pin_incorrect
        public event Action<string>? Nickname;
        public event Action<byte[], int, int>? Pcm;         // pcm, rate, channels

        private volatile IntPtr _ctx = IntPtr.Zero;
        private bool _libInit;
        private Task? _joinTask;

        // Delegates must stay referenced for the session's lifetime.
        private readonly PcLogCb _logCb;
        private readonly PcEventCb _eventCb;
        private readonly PcAudioCb _audioCb;
        private readonly PcFrameCb _frameCb;

        private readonly WaveOutPlayer _audio = new();
        private byte[]? _frameBuf;
        private GCHandle _framePin;
        private const int MaxFrameW = 1920, MaxFrameH = 1088;

        public int LastFrameW { get; private set; }
        public int LastFrameH { get; private set; }
        public double LastPts { get; private set; }
        public bool Running => _ctx != IntPtr.Zero;

        public ChiakiStreamSession()
        {
            _logCb = OnLog;
            _eventCb = OnEvent;
            _audioCb = OnAudio;
            _frameCb = OnFrame;
            try
            {
                _libInit = pc_init() == 0;
                if (_libInit) pc_set_log_cb(_logCb, IntPtr.Zero);
            }
            catch (DllNotFoundException) { _libInit = false; }
        }

        public bool LibAvailable => _libInit;

        // True when the native engine can actually be loaded — loose file next
        // to the app or self-extracted from the single-file bundle.
        public static bool EngineAvailable
        {
            get
            {
                try { return pc_init() == 0; }
                catch { return false; }
            }
        }

        public static string EnginePath
        {
            get
            {
                string dir = AppContext.BaseDirectory;
                foreach (var n in new[] { "ps5chiaki.dll", "libps5chiaki.so", "libps5chiaki.dylib" })
                {
                    var p = Path.Combine(dir, n);
                    if (File.Exists(p)) return p;
                }
                return EngineAvailable ? "embedded in PS5Suite (self-extracts to temp)"
                                       : "ps5chiaki (native) — missing";
            }
        }

        public bool Start(RemotePlayRegistration reg, string? ipOverride = null)
        {
            if (!_libInit) { LogMessage?.Invoke(1, "ps5chiaki library not loaded"); return false; }

            var cfg = new PcConfig
            {
                Host = ipOverride ?? reg.Ps5Ip,
                RegistKey = Pad(reg.RpRegistKeyChiaki, 16),
                Morning = Pad(reg.RpKey, 16),
                AccountId = Pad(Base64(reg.AccountIdB64), 8),
                Codec = 0, // h264
                Width = 1920, Height = 1080, MaxFps = 60, Bitrate = 15000,
                AutoDowngrade = 1
            };

            _ctx = pc_create(ref cfg);
            if (_ctx == IntPtr.Zero) { LogMessage?.Invoke(1, "pc_create failed"); return false; }

            pc_set_cbs(_ctx, _eventCb, IntPtr.Zero, _audioCb, IntPtr.Zero, _frameCb, IntPtr.Zero);

            _frameBuf = new byte[MaxFrameW * MaxFrameH * 4];
            _framePin = GCHandle.Alloc(_frameBuf, GCHandleType.Pinned);

            int r = pc_start(_ctx);
            if (r != 0) { LogMessage?.Invoke(1, $"pc_start failed: {r}"); Stop(); return false; }
            return true;
        }

        public event Action? FrameAvailable;
        private volatile int _pendingFrames;

        // Pull latest decoded frame into the pinned BGRA scratch buffer;
        // returns it (or null when no new frame exists).
        public byte[]? PullFrame(out int w, out int h)
        {
            w = h = 0;
            if (_ctx == IntPtr.Zero || _frameBuf == null) return null;
            if (pc_pull_bgra(_ctx, _framePin.AddrOfPinnedObject(), MaxFrameW * 4,
                    out w, out h, out double pts) != 1)
                return null;
            LastFrameW = w; LastFrameH = h; LastPts = pts;
            return _frameBuf;
        }
        public int FrameStride => MaxFrameW * 4;

        public void PumpAudio() => _audio.Pump();

        // ---- input ----
        public void SetButtons(uint buttons) =>
            pc_set_controller(_ctx, buttons, 0, 0, 0, 0, 0, 0);
        public void SetController(uint buttons, short lx, short ly, short rx, short ry, byte l2, byte r2) =>
            pc_set_controller(_ctx, buttons, lx, ly, rx, ry, l2, r2);
        public void SetPin(string pin) { if (_ctx != IntPtr.Zero) pc_set_login_pin(_ctx, pin); }
        public void GoHome() { if (_ctx != IntPtr.Zero) pc_go_home(_ctx); }
        public void GotoBed() { if (_ctx != IntPtr.Zero) pc_goto_bed(_ctx); }
        public void RequestIdr() { if (_ctx != IntPtr.Zero) pc_request_idr(_ctx); }

        public void Stop()
        {
            if (_ctx == IntPtr.Zero) return;
            var ctx = _ctx;
            _ctx = IntPtr.Zero;
            pc_stop(ctx);
            _joinTask = Task.Run(() => { pc_join(ctx); pc_free(ctx); });
        }

        public void Dispose()
        {
            Stop();
            try { _joinTask?.Wait(3000); } catch { }
            _audio.Dispose();
            if (_framePin.IsAllocated) _framePin.Free();
            _frameBuf = null;
        }

        // ---- native callbacks (chiaki threads) ----

        private void OnLog(int level, IntPtr msg, IntPtr user)
        {
            try { LogMessage?.Invoke(level, Marshal.PtrToStringUTF8(msg) ?? ""); } catch { }
        }

        private void OnEvent(int type, int reason, IntPtr msg, IntPtr user)
        {
            try
            {
                switch (type)
                {
                    case EV_CONNECTED: Connected?.Invoke(); break;
                    case EV_QUIT:
                        Quit?.Invoke(Marshal.PtrToStringUTF8(msg) ?? $"reason {reason}");
                        break;
                    case EV_LOGIN_PIN_REQUEST: PinRequest?.Invoke(reason != 0); break;
                    case EV_NICKNAME_RECEIVED: Nickname?.Invoke(Marshal.PtrToStringUTF8(msg) ?? ""); break;
                }
            }
            catch { }
        }

        private void OnAudio(IntPtr pcm, int sizeBytes, int rate, int channels, IntPtr user)
        {
            if (sizeBytes <= 0) return;
            var buf = new byte[sizeBytes];
            Marshal.Copy(pcm, buf, 0, sizeBytes);
            _audio.Push(buf, rate > 0 ? rate : 48000, channels > 0 ? channels : 2);
            try { Pcm?.Invoke(buf, rate, channels); } catch { }
        }

        private void OnFrame(IntPtr user)
        {
            _pendingFrames++;
            try { FrameAvailable?.Invoke(); } catch { }
        }

        // ---- helpers ----

        private static byte[] Pad(byte[] b, int n)
        {
            var r = new byte[n];
            if (b != null) Array.Copy(b, r, Math.Min(b.Length, n));
            return r;
        }

        private static byte[] Base64(string s)
        {
            try { return Convert.FromBase64String(s ?? ""); } catch { return Array.Empty<byte>(); }
        }
    }
}
