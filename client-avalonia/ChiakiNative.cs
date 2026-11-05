// ChiakiNative — P/Invoke bindings for ps5chiaki.dll / libps5chiaki.so
// Flat-C shim over libchiaki (chiaki-ng's protocol/streaming core, AGPL-3.0).

using System;
using System.Runtime.InteropServices;

namespace PS5Upload
{
    internal static class ChiakiNative
    {
        internal const string Lib = "ps5chiaki";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void PcLogCb(int level, IntPtr msg, IntPtr user);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void PcEventCb(int type, int reason, IntPtr msg, IntPtr user);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void PcAudioCb(IntPtr pcm, int sizeBytes, int rate, int channels, IntPtr user);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void PcFrameCb(IntPtr user);

        [StructLayout(LayoutKind.Sequential)]
        internal struct PcConfig
        {
            [MarshalAs(UnmanagedType.LPUTF8Str)] public string Host;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RegistKey;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Morning;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] AccountId;
            public int Codec;
            public uint Width, Height, MaxFps, Bitrate;
            public int AutoDowngrade;
        }

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pc_init();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pc_set_log_cb(PcLogCb cb, IntPtr user);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr pc_create(ref PcConfig cfg);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pc_set_cbs(IntPtr ctx,
            PcEventCb ev, IntPtr evUser,
            PcAudioCb au, IntPtr auUser,
            PcFrameCb fr, IntPtr frUser);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pc_start(IntPtr ctx);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pc_stop(IntPtr ctx);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pc_join(IntPtr ctx);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pc_free(IntPtr ctx);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pc_pull_bgra(IntPtr ctx, IntPtr dst, int dstStride,
            out int w, out int h, out double pts);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void pc_set_controller(IntPtr ctx, uint buttons,
            short lx, short ly, short rx, short ry, byte l2, byte r2);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pc_set_login_pin(IntPtr ctx,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string pin);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pc_go_home(IntPtr ctx);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pc_goto_bed(IntPtr ctx);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pc_keyboard_text(IntPtr ctx,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pc_keyboard_accept(IntPtr ctx);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pc_keyboard_reject(IntPtr ctx);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int pc_request_idr(IntPtr ctx);

        // chiaki event types (ChiakiEventType)
        internal const int EV_CONNECTED = 0;
        internal const int EV_LOGIN_PIN_REQUEST = 1;
        internal const int EV_HOLEPUNCH = 2;
        internal const int EV_REGIST = 3;
        internal const int EV_NICKNAME_RECEIVED = 4;
        internal const int EV_KEYBOARD_OPEN = 5;
        internal const int EV_KEYBOARD_TEXT_CHANGE = 6;
        internal const int EV_KEYBOARD_REMOTE_CLOSE = 7;
        internal const int EV_RUMBLE = 8;
        internal const int EV_QUIT = 9;

        // ChiakiControllerButton bits
        internal const uint BTN_CROSS = 1u << 0;
        internal const uint BTN_MOON = 1u << 1;      // circle
        internal const uint BTN_BOX = 1u << 2;       // square
        internal const uint BTN_PYRAMID = 1u << 3;   // triangle
        internal const uint BTN_DPAD_LEFT = 1u << 4;
        internal const uint BTN_DPAD_RIGHT = 1u << 5;
        internal const uint BTN_DPAD_UP = 1u << 6;
        internal const uint BTN_DPAD_DOWN = 1u << 7;
        internal const uint BTN_L1 = 1u << 8;
        internal const uint BTN_R1 = 1u << 9;
        internal const uint BTN_L3 = 1u << 10;
        internal const uint BTN_R3 = 1u << 11;
        internal const uint BTN_OPTIONS = 1u << 12;
        internal const uint BTN_SHARE = 1u << 13;
        internal const uint BTN_TOUCHPAD = 1u << 14;
        internal const uint BTN_PS = 1u << 15;
        internal const uint BTN_L2 = 1u << 16;
        internal const uint BTN_R2 = 1u << 17;
    }
}
