// RemotePlayWindow — PS5 Suite's own Remote Play stream window.
//
// The stream runs fully inside this process through ps5chiaki.dll
// (libchiaki's protocol/streaming core + ffmpeg decode + opus audio).
// Decoded frames are pulled as BGRA into a WriteableBitmap and rendered in
// the VideoImage control; input is mapped to DualShock state directly —
// no external streaming application, window docking or reparenting.

using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using static PS5Upload.ChiakiNative;

namespace PS5Upload
{
    public partial class RemotePlayWindow : Window
    {
        private readonly RemotePlayRegistration _reg;
        private readonly string _ip;

        private ChiakiStreamSession? _sess;
        private WriteableBitmap? _bmp;
        private DispatcherTimer? _pumpTimer;
        private bool _closing;
        private bool _connected;

        private uint _buttons;
        private short _lx, _ly, _rx, _ry;
        private byte _l2, _r2;

        public RemotePlayWindow() : this(null, "") { } // designer

        public RemotePlayWindow(RemotePlayRegistration? reg, string ip)
        {
            InitializeComponent();
            _reg = reg ?? new RemotePlayRegistration();
            _ip = ip;
            TitleText.Text = $"📺 Remote Play — {_reg.ServerNickname}";
            Title = $"PS5 Suite — Remote Play ({_reg.ServerNickname})";
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            StartSession();
        }

        // ---------------- stream lifecycle ----------------

        private void StartSession()
        {
            OverlayText.Text = "Starting stream…";
            _sess = new ChiakiStreamSession();
            if (!_sess.LibAvailable)
            {
                Fail("Streaming engine (ps5chiaki) not found next to PS5 Suite");
                return;
            }
            _sess.LogMessage += OnLog;
            _sess.Connected += () => Ui(() =>
            {
                _connected = true;
                Overlay.IsVisible = false;
                BarStatusText.Text = "Connected";
                App.LogToFile("[rp] session CONNECTED");
            });
            _sess.Quit += reason => { App.LogToFile($"[rp] session QUIT: {reason}"); Ui(() => StreamEnded($"Stream ended ({reason})")); };
            _sess.PinRequest += incorrect => Ui(() =>
                OverlayText.Text = incorrect ? "PIN incorrect — try again" : "PS5 asks for PIN");
            _sess.Nickname += nick => Ui(() => TitleText.Text = $"📺 Remote Play — {nick}");
            _sess.FrameAvailable += OnFrameAvailable;

            if (!_sess.Start(_reg, _ip))
            {
                Fail("Could not start stream session");
                return;
            }

            _pumpTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) };
            _pumpTimer.Tick += (_, _) => Pump();
            _pumpTimer.Start();
        }

        // Frame availability is signalled from chiaki's decode thread; actual
        // pull + blit happens here at ~120Hz so we never render stale frames.
        private void OnFrameAvailable() { /* flag consumed by Pump() timer */ }

        private void Pump()
        {
            var s = _sess;
            if (s == null) return;
            s.PumpAudio();
            var frame = s.PullFrame(out int w, out int h);
            if (frame == null) return;

            if (_bmp == null || _bmp.PixelSize.Width != w || _bmp.PixelSize.Height != h)
            {
                _bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Opaque);
                VideoImage.Source = _bmp;
            }

            using (var fb = _bmp.Lock())
            {
                int srcStride = s.FrameStride, dstStride = fb.RowBytes;
                int rowBytes = Math.Min(w * 4, Math.Min(srcStride, dstStride));
                for (int y = 0; y < h; y++)
                    Marshal.Copy(frame, y * srcStride, (IntPtr)(fb.Address.ToInt64() + (long)y * dstStride), rowBytes);
            }
            VideoImage.InvalidateVisual();
        }

        private void Ui(Action a) => Dispatcher.UIThread.Post(a);

        private void OnLog(int level, string msg)
        {
            App.LogToFile($"[chiaki:{level}] {msg}");
            // surface fatal errors early; keep the rest on the status bar
            Ui(() => { if (!_connected) BarStatusText.Text = msg; });
        }

        // ---------------- controls ----------------

        private void OnFullscreen(object? sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
        }

        private void OnDisconnect(object? sender, RoutedEventArgs e) => Close();

        private void Fail(string msg)
        {
            OverlayText.Text = $"❌ {msg}";
            OverlayProgress.IsVisible = false;
        }

        private void StreamEnded(string msg)
        {
            _pumpTimer?.Stop();
            if (_closing) return;
            Overlay.IsVisible = true;
            OverlayProgress.IsVisible = false;
            OverlayText.Text = msg;
            BarStatusText.Text = "Disconnected";
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            _closing = true;
            _pumpTimer?.Stop();
            var s = _sess;
            _sess = null;
            Task.Run(() => s?.Dispose()); // stop+join blocks; don't hold the UI
            base.OnClosing(e);
        }

        // ---------------- input ----------------

        // chiaki-style keyboard map
        private static uint? KeyToButton(Key k) => k switch
        {
            Key.Up => BTN_DPAD_UP,
            Key.Down => BTN_DPAD_DOWN,
            Key.Left => BTN_DPAD_LEFT,
            Key.Right => BTN_DPAD_RIGHT,
            Key.Enter => BTN_CROSS,
            Key.Escape => BTN_MOON,
            Key.Back => BTN_MOON,
            Key.F => BTN_BOX,
            Key.T => BTN_PYRAMID,
            Key.PageUp => BTN_L1,
            Key.PageDown => BTN_R1,
            Key.Q => BTN_L2,
            Key.E => BTN_R2,
            Key.F1 => BTN_L3,
            Key.F2 => BTN_R3,
            Key.O => BTN_OPTIONS,
            Key.N => BTN_SHARE,
            Key.P => BTN_PS,
            Key.Tab => BTN_TOUCHPAD,
            _ => null
        };

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            var b = KeyToButton(e.Key);
            if (b == null) return;
            e.Handled = true;
            _buttons |= b.Value;
            if (b.Value == BTN_L2) _l2 = 0xff;
            if (b.Value == BTN_R2) _r2 = 0xff;
            Push();
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            var b = KeyToButton(e.Key);
            if (b == null) return;
            e.Handled = true;
            _buttons &= ~b.Value;
            if (b.Value == BTN_L2) _l2 = 0;
            if (b.Value == BTN_R2) _r2 = 0;
            Push();
        }

        private void Push() => _sess?.SetController(_buttons, _lx, _ly, _rx, _ry, _l2, _r2);
    }
}
