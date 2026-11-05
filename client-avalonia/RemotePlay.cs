// Remote Play client-side support for PS5 Suite.
//
// The regist (pairing) handshake implemented here is a C# port of chiaki's
// lib/src/regist.c + rpcrypt.c (chiaki-ng, streetpea — AGPL-3.0 with OpenSSL
// exception; protocol facts reproduced for interoperability). The PS5-side
// half (PIN generation, account lookup, registry enable) lives in our payload
// (CMD_REMOTEPLAY, opcode 0x84) and returns JSON via RemotePlayStatus.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PS5Upload
{
    // ---------------- Payload-side status (JSON from rp|status) ----------------

    public class RemotePlayDevice
    {
        public int Slot { get; set; }
        public int UserId { get; set; }
        public int ClientType { get; set; }
    }

    public class RemotePlayStatus
    {
        public string State { get; set; } = "idle";       // idle|waiting|paired|failed|timeout|busy
        public string Pin { get; set; } = "";
        public int SecondsLeft { get; set; }
        public string Error { get; set; } = "";
        public string AccountId { get; set; } = "";       // base64 8-byte PSN account id
        public ulong AccountIdRaw { get; set; }
        public string AccountVia { get; set; } = "";
        public int AccountUid { get; set; }
        public int ForegroundUid { get; set; }
        public int UserSlot { get; set; }
        public string AccountType { get; set; } = "";
        public bool ServiceEnabled { get; set; }
        public bool HasPerUser { get; set; }
        public bool UserEnabled { get; set; }
        public bool SymbolsOk { get; set; }
        public bool RegmgrOk { get; set; }
        public uint RegistryErr { get; set; }
        public uint Firmware { get; set; }
        public uint Probes { get; set; }
        public int ConfirmRc { get; set; }
        public uint ConfirmStatus { get; set; }
        public uint ConfirmErr { get; set; }
        public string UserName { get; set; } = "";
        public string SigninId { get; set; } = "";
        public string OnlineId { get; set; } = "";
        public string NpId { get; set; } = "";
        public List<RemotePlayDevice> Devices { get; set; } = new();
        public int DeviceCount { get; set; }

        public static RemotePlayStatus? FromJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                var s = new RemotePlayStatus();
                s.State = Str(r, "state", "idle");
                s.Pin = Str(r, "pin", "");
                s.SecondsLeft = Int(r, "seconds_left");
                s.Error = Str(r, "err", "");
                s.AccountId = Str(r, "account_id", "");
                if (r.TryGetProperty("account_id_raw", out var ar) && ar.ValueKind == JsonValueKind.Number)
                    s.AccountIdRaw = ar.GetUInt64();
                s.AccountVia = Str(r, "account_via", "");
                s.AccountUid = Int(r, "account_uid");
                s.ForegroundUid = Int(r, "foreground_uid");
                s.UserSlot = Int(r, "user_slot");
                s.AccountType = Str(r, "account_type", "");
                s.ServiceEnabled = Int(r, "service_enabled") != 0;
                s.HasPerUser = Int(r, "has_per_user") != 0;
                s.UserEnabled = Int(r, "user_enabled") != 0;
                s.SymbolsOk = Int(r, "symbols_ok") != 0;
                s.RegmgrOk = Int(r, "regmgr_ok") != 0;
                if (r.TryGetProperty("registry_err", out var re) && re.ValueKind == JsonValueKind.Number)
                    s.RegistryErr = re.GetUInt32();
                if (r.TryGetProperty("fw", out var fw) && fw.ValueKind == JsonValueKind.Number)
                    s.Firmware = fw.GetUInt32();
                if (r.TryGetProperty("probes", out var pr) && pr.ValueKind == JsonValueKind.Number)
                    s.Probes = pr.GetUInt32();
                s.ConfirmRc = Int(r, "confirm_rc");
                if (r.TryGetProperty("confirm_status", out var cs) && cs.ValueKind == JsonValueKind.Number)
                    s.ConfirmStatus = cs.GetUInt32();
                if (r.TryGetProperty("confirm_err", out var ce) && ce.ValueKind == JsonValueKind.Number)
                    s.ConfirmErr = ce.GetUInt32();
                s.UserName = Str(r, "user_name", "");
                s.SigninId = Str(r, "signin_id", "");
                s.OnlineId = Str(r, "online_id", "");
                s.NpId = Str(r, "np_id", "");
                if (r.TryGetProperty("devices", out var devs) && devs.ValueKind == JsonValueKind.Array)
                    foreach (var d in devs.EnumerateArray())
                        s.Devices.Add(new RemotePlayDevice
                        {
                            Slot = Int(d, "slot"),
                            UserId = Int(d, "user_id"),
                            ClientType = Int(d, "client_type")
                        });
                s.DeviceCount = Int(r, "device_count", s.Devices.Count);
                return s;
            }
            catch { return null; }
        }

        private static string Str(JsonElement e, string key, string def)
            => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? def) : def;
        private static int Int(JsonElement e, string key, int def = 0)
            => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : def;
    }

    // ---------------- Registered console (persisted by us) ----------------

    public class RemotePlayRegistration
    {
        public string Ps5Ip { get; set; } = "";
        public string AccountIdB64 { get; set; } = "";
        public int Target { get; set; } = 1000100;        // CHIAKI_TARGET_PS5_1
        public string ApSsid { get; set; } = "";
        public string ApBssid { get; set; } = "";
        public string ApKey { get; set; } = "";
        public string ApName { get; set; } = "";
        public string ServerNickname { get; set; } = "";
        public string ServerMacHex { get; set; } = "";    // 6 bytes
        public string RpRegistKeyHex { get; set; } = "";  // 16 bytes
        public uint RpKeyType { get; set; }
        public string RpKeyHex { get; set; } = "";        // 16 bytes (morning)
        public DateTime RegisteredUtc { get; set; }

        public string RpRegistKeyText { get; set; } = ""; // literal PS5-RegistKey header
        public byte[] ServerMac => FromHex(ServerMacHex);
        public byte[] RpRegistKey => FromHex(RpRegistKeyHex);
        public byte[] RpKey => FromHex(RpKeyHex);

        // What chiaki must store as rp_regist_key: exactly CHIAKI_SESSION_AUTH_SIZE
        // (16) bytes — the decoded key text NUL-padded. chiaki's LoadFromSettings
        // memcpys ONLY when the QByteArray is exactly that size (otherwise the
        // key stays zeroed and the session request sends an empty RP-Registkey).
        // On the wire chiaki hex-encodes up to the first NUL, so the 16-byte
        // padded array "b81a7050\0..." becomes header "6238316137303530" — which
        // is exactly what the PS5 daemon strtol()s back to the stored key value.
        // RpRegistKeyHex already holds that 16-byte decoded form, so this is a
        // pad-or-truncate to 16; a short RpRegistKeyText (raw header text) is
        // used only as a fallback for firmware that returns a plaintext header.
        public byte[] RpRegistKeyChiaki
        {
            get
            {
                var raw = FromHex(RpRegistKeyHex);
                if (raw.Length == 0 && RpRegistKeyText.Length > 0)
                {
                    // Edge case: header arrived as plaintext (e.g. "b81a7050")
                    // instead of hex-encoded — its ASCII bytes are the key.
                    raw = Encoding.ASCII.GetBytes(RpRegistKeyText);
                }
                var p = new byte[16];
                Array.Copy(raw, p, Math.Min(raw.Length, 16));
                return p;
            }
        }

        private static byte[] FromHex(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0) return Array.Empty<byte>();
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        public static string ToHex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }

    // ---------------- Regist handshake (port of chiaki regist.c/rpcrypt.c) ----------------

    public static class RemotePlayRegist
    {
        private const int RegistPort = 9295;
        private const int SearchTimeoutMs = 3000;
        private const int ResponseTimeoutMs = 6000;

        // Fixed key vectors derived from chiaki's ps5_keys_0/ps5_keys_1 tables.
        // Our request buffer is filled with 'A' (0x41), so the offsets the PS5
        // derives are constant: key_0_off = 0x41 & 0x1F = 1, key_1_off = 0x41 >> 3 = 8.
        // bright0[i] = ps5_keys_0[i*0x20 + 1], aeroK[i] = ps5_keys_1[i*0x20 + 8]
        private static readonly byte[] BrightBase =
        {
            0xD8, 0x60, 0xE1, 0x46, 0xBD, 0xB0, 0xBD, 0x94,
            0xB4, 0x60, 0x07, 0x0B, 0xB1, 0x4F, 0xE7, 0x23
        };
        private static readonly byte[] AeroK =
        {
            0xA8, 0x37, 0x61, 0xB5, 0x27, 0xFC, 0x07, 0x6E,
            0x62, 0x43, 0x3A, 0xA7, 0x01, 0x95, 0x8A, 0x85
        };
        private static readonly byte[] HmacKeyPs5 =
        {
            0x46, 0x46, 0x87, 0xB3, 0x49, 0xCA, 0x8C, 0xE8,
            0x59, 0xC5, 0x27, 0x0F, 0x5D, 0x7A, 0x69, 0xD6
        };

        private const string ClientType =
            "dabfa2ec873de5839bee8d3f4c0239c4282c07c25c6077a2931afcf0adc0d34f";

        public class RegistResult
        {
            public bool Ok;
            public string Error = "";
            public int HttpCode;
            public uint AppReason;
            public RemotePlayRegistration? Registration;
        }

        // UDP "SRC3" probe — answers whether the console is in pairing mode.
        public static async Task<bool> SearchAsync(string ps5Ip, CancellationToken ct)
        {
            try
            {
                using var udp = new UdpClient();
                udp.Connect(IPAddress.Parse(ps5Ip), RegistPort);
                await udp.SendAsync(new byte[] { (byte)'S', (byte)'R', (byte)'C', (byte)'3', 0 }, 5);
                var waitTask = udp.ReceiveAsync();
                var timeout = Task.Delay(SearchTimeoutMs, ct);
                if (await Task.WhenAny(waitTask, timeout) != waitTask) return false;
                var res = await waitTask;
                return res.Buffer.Length >= 4 &&
                       res.Buffer[0] == 'R' && res.Buffer[1] == 'E' &&
                       res.Buffer[2] == 'S' && res.Buffer[3] == '3';
            }
            catch { return false; }
        }

        public static async Task<RegistResult> RegisterAsync(
            string ps5Ip, string accountIdB64, uint pin, CancellationToken ct)
        {
            var result = new RegistResult();
            try
            {
                byte[] accountId;
                try { accountId = Convert.FromBase64String(accountIdB64); }
                catch { result.Error = "account id is not valid base64"; return result; }
                if (accountId.Length != 8)
                {
                    result.Error = $"account id must be 8 bytes (got {accountId.Length})";
                    return result;
                }

                // ---- crypto setup (rpcrypt.c) ----
                byte[] ambassador = RandomNumberGenerator.GetBytes(16);
                byte[] bright = (byte[])BrightBase.Clone();
                bright[0xc] ^= (byte)(pin >> 24);
                bright[0xd] ^= (byte)(pin >> 16);
                bright[0xe] ^= (byte)(pin >> 8);
                bright[0xf] ^= (byte)pin;

                // aeropause[i] = (ambassador[i] ^ k) + (-0x2d) + i  (PS5 wurzelbert)
                byte[] aeropause = new byte[16];
                for (int i = 0; i < 16; i++)
                    aeropause[i] = (byte)((ambassador[i] ^ AeroK[i]) + 0xD3 + i);

                // ---- payload ----
                string inner = $"Client-Type: {ClientType}\r\nNp-AccountId: {accountIdB64}\r\n";
                byte[] innerBytes = Encoding.ASCII.GetBytes(inner);
                byte[] payload = new byte[0x1e0 + innerBytes.Length];
                Array.Fill(payload, (byte)'A', 0, 0x1e0);
                Array.Copy(aeropause, 8, payload, 0xc7, 8);
                Array.Copy(aeropause, 0, payload, 0x191, 8);
                Array.Copy(innerBytes, 0, payload, 0x1e0, innerBytes.Length);

                // encrypt ONLY the inner header — AES-128-CFB, IV = HMAC-SHA256(hmacKey, ambassador||counter0)[0..16]
                byte[] iv = MakeIv(ambassador, 0);
                AesCfb(bright, iv, payload, 0x1e0, innerBytes.Length, encrypt: true);

                // ---- TCP regist ----
                using var tcp = new TcpClient();
                var connectTask = tcp.ConnectAsync(IPAddress.Parse(ps5Ip), RegistPort);
                if (await Task.WhenAny(connectTask, Task.Delay(ResponseTimeoutMs, ct)) != connectTask)
                {
                    result.Error = "connect to port 9295 timed out (console not in pairing mode?)";
                    return result;
                }
                await connectTask;
                var ns = tcp.GetStream();
                string localIp = ((IPEndPoint)tcp.Client.LocalEndPoint!).Address.ToString();

                string header =
                    $"POST /sie/ps5/rp/sess/rgst HTTP/1.1\r\n HTTP/1.1\r\n" +
                    $"HOST: {localIp}\r\n" +
                    $"User-Agent: remoteplay Windows\r\n" +
                    $"Connection: close\r\n" +
                    $"Content-Length: {payload.Length}\r\n" +
                    $"RP-Version: 1.0\r\n\r\n";
                byte[] headerBytes = Encoding.ASCII.GetBytes(header);
                await ns.WriteAsync(headerBytes, ct);
                await ns.WriteAsync(payload, ct);

                // ---- response ----
                using var ms = new MemoryStream();
                byte[] recvBuf = new byte[8192];
                var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(ResponseTimeoutMs);
                try
                {
                    int n;
                    while ((n = await ns.ReadAsync(recvBuf, readCts.Token)) > 0)
                    {
                        ms.Write(recvBuf, 0, n);
                        if (ms.Length > 0x4000) break;
                    }
                }
                catch (OperationCanceledException) { /* use what arrived */ }

                byte[] resp = ms.ToArray();
                int sep = IndexOf(resp, new byte[] { 0x0d, 0x0a, 0x0d, 0x0a });
                if (sep < 0) { result.Error = "no HTTP response from console"; return result; }
                string headStr = Encoding.ASCII.GetString(resp, 0, sep);
                byte[] body = resp.AsSpan(sep + 4).ToArray();

                result.HttpCode = ParseHttpCode(headStr);
                if (result.HttpCode != 200)
                {
                    result.AppReason = ParseReason(headStr);
                    result.Error = $"HTTP {result.HttpCode}" +
                        (result.AppReason != 0 ? $" (RP-Application-Reason 0x{result.AppReason:x8}: {ReasonText(result.AppReason)})" : "");
                    return result;
                }

                int contentLen = ParseContentLength(headStr);
                if (contentLen > 0 && body.Length > contentLen)
                    body = body.AsSpan(0, contentLen).ToArray();
                if (body.Length == 0) { result.Error = "empty regist response"; return result; }

                // whole body is CFB-encrypted with the same context (counter 0)
                AesCfb(bright, MakeIv(ambassador, 0), body, 0, body.Length, encrypt: false);
                var innerHeaders = ParseInnerHeaders(Encoding.ASCII.GetString(body));

                var reg = new RemotePlayRegistration
                {
                    Ps5Ip = ps5Ip,
                    AccountIdB64 = accountIdB64,
                    RegisteredUtc = DateTime.UtcNow
                };
                foreach (var kv in innerHeaders)
                {
                    switch (kv.Key)
                    {
                        case "PS5-RegistKey": reg.RpRegistKeyHex = kv.Value; reg.RpRegistKeyText = kv.Value; break;
                        case "RP-Key": reg.RpKeyHex = kv.Value; break;
                        case "RP-KeyType": if (uint.TryParse(kv.Value, out var kt)) reg.RpKeyType = kt; break;
                        case "PS5-Mac": reg.ServerMacHex = kv.Value; break;
                        case "PS5-Nickname": reg.ServerNickname = kv.Value; break;
                        case "AP-Ssid": reg.ApSsid = kv.Value; break;
                        case "AP-Bssid": reg.ApBssid = kv.Value; break;
                        case "AP-Key": reg.ApKey = kv.Value; break;
                        case "AP-Name": reg.ApName = kv.Value; break;
                    }
                }
                if (reg.RpRegistKey.Length == 0 || reg.RpKey.Length != 16 || reg.ServerMac.Length != 6)
                {
                    result.Error = "regist response missing RegistKey/RP-Key/MAC";
                    return result;
                }
                // regist_key stored as raw bytes, NUL-padded to 16
                if (reg.RpRegistKey.Length < 16)
                    reg.RpRegistKeyHex = RemotePlayRegistration.ToHex(
                        PadTo(reg.RpRegistKey, 16));
                if (string.IsNullOrEmpty(reg.ServerNickname)) reg.ServerNickname = "PS5";

                result.Ok = true;
                result.Registration = reg;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
        }

        private static byte[] PadTo(byte[] src, int len)
        {
            var b = new byte[len];
            Array.Copy(src, b, Math.Min(src.Length, len));
            return b;
        }

        private static byte[] MakeIv(byte[] ambassador, ulong counter)
        {
            byte[] buf = new byte[24];
            Array.Copy(ambassador, buf, 16);
            for (int i = 0; i < 8; i++)
                buf[16 + i] = (byte)(counter >> (56 - i * 8));
            using var hmac = new HMACSHA256(HmacKeyPs5);
            byte[] hash = hmac.ComputeHash(buf);
            var iv = new byte[16];
            Array.Copy(hash, iv, 16);
            return iv;
        }

        // Manual CFB-128 — .NET's CipherMode.CFB requires full feedback blocks,
        // while Remote Play streams arbitrary lengths (mbedtls aes_crypt_cfb128).
        // ks = AES-ECB(feedback); text ^= ks; feedback = ciphertext in both directions.
        private static void AesCfb(byte[] key, byte[] iv, byte[] data, int offset, int length, bool encrypt)
        {
            using var aes = Aes.Create();
            aes.KeySize = 128;
            aes.Key = key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using var enc = aes.CreateEncryptor();

            byte[] feedback = new byte[16];
            Array.Copy(iv, feedback, 16);
            byte[] ks = new byte[16];

            int end = offset + length;
            for (int pos = offset; pos < end;)
            {
                enc.TransformBlock(feedback, 0, 16, ks, 0);
                int seg = Math.Min(16, end - pos);
                byte[] inBlock = new byte[16];
                Array.Copy(data, pos, inBlock, 0, seg);
                for (int i = 0; i < seg; i++)
                    data[pos + i] ^= ks[i];
                // feedback for the next block is the ciphertext
                Array.Clear(feedback, 0, 16);
                Array.Copy(encrypt ? data : inBlock, encrypt ? pos : 0, feedback, 0, seg);
                pos += seg;
            }
        }

        private static int IndexOf(byte[] hay, byte[] needle)
        {
            for (int i = 0; i <= hay.Length - needle.Length; i++)
            {
                bool m = true;
                for (int j = 0; j < needle.Length; j++)
                    if (hay[i + j] != needle[j]) { m = false; break; }
                if (m) return i;
            }
            return -1;
        }

        private static int ParseHttpCode(string head)
        {
            int sp1 = head.IndexOf(' ');
            int sp2 = head.IndexOf(' ', sp1 + 1);
            if (sp1 < 0 || sp2 < 0) return 0;
            return int.TryParse(head.Substring(sp1 + 1, sp2 - sp1 - 1), out int c) ? c : 0;
        }

        private static uint ParseReason(string head)
        {
            foreach (var line in head.Split("\r\n"))
                if (line.StartsWith("RP-Application-Reason:", StringComparison.OrdinalIgnoreCase))
                    return Convert.ToUInt32(line.Substring(22).Trim(), 16);
            return 0;
        }

        private static int ParseContentLength(string head)
        {
            foreach (var line in head.Split("\r\n"))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    return int.TryParse(line.Substring(15).Trim(), out int c) ? c : 0;
            return 0;
        }

        private static Dictionary<string, string> ParseInnerHeaders(string body)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in body.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                d[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }
            return d;
        }

        public static string ReasonText(uint reason) => reason switch
        {
            0x80108b02 => "Invalid PSN ID — account id not accepted",
            0x80108bff => "PIN code is invalid or expired",
            0x80108c00 => "PIN format error",
            0x80108c01 => "console is not ready (link device screen closed)",
            _ => "registration refused"
        };
    }

    // ---------------- chiaki(-ng) integration ----------------

    public static class ChiakiIntegration
    {
        // Write our registration into chiaki-ng's QSettings so
        // "chiaki.exe stream <nickname> <ip>" picks it up.
        // Windows: registry HKCU\Software\Chiaki\Chiaki (NativeFormat).
        // Linux:   ~/.config/Chiaki/Chiaki.conf (INI).
        public static (bool ok, string error) WriteRegisteredHost(RemotePlayRegistration reg)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return WriteWindows(reg);
                return WriteIni(reg);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        private static (bool, string) WriteWindows(RemotePlayRegistration reg)
        {
            if (!OperatingSystem.IsWindows()) return (false, "not windows");
            using var root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Chiaki\Chiaki\registered_hosts", true);
            if (root == null) return (false, "cannot open Chiaki registry key");

            int size = 0;
            var sizeObj = root.GetValue("size");
            if (sizeObj is int si) size = si;
            else if (sizeObj is string ss) int.TryParse(ss, out size);

            // find existing slot with same server_mac, else append
            int slot = -1;
            byte[] ourMac = reg.ServerMac;
            for (int i = 1; i <= size; i++)
            {
                using var sub = root.OpenSubKey(i.ToString());
                if (sub == null) continue;
                if (QtReadByteArray(sub.GetValue("server_mac")).AsSpan().SequenceEqual(ourMac))
                { slot = i; break; }
            }
            if (slot < 0) { slot = size + 1; root.SetValue("size", slot, Microsoft.Win32.RegistryValueKind.DWord); }

            using (var host = root.CreateSubKey(slot.ToString(), true))
            {
                host.SetValue("target", reg.Target, Microsoft.Win32.RegistryValueKind.DWord);
                host.SetValue("ap_ssid", reg.ApSsid, Microsoft.Win32.RegistryValueKind.String);
                host.SetValue("ap_bssid", reg.ApBssid, Microsoft.Win32.RegistryValueKind.String);
                host.SetValue("ap_key", reg.ApKey, Microsoft.Win32.RegistryValueKind.String);
                host.SetValue("ap_name", reg.ApName, Microsoft.Win32.RegistryValueKind.String);
                host.SetValue("server_nickname", reg.ServerNickname, Microsoft.Win32.RegistryValueKind.String);
                // QSettings serializes QByteArray to the registry as REG_BINARY
                // containing the UTF-16LE bytes of "@ByteArray(<latin-1 chars>)".
                // A raw REG_BINARY or REG_SZ copy is misread on load.
                host.SetValue("server_mac", QtRegBinary(reg.ServerMac), Microsoft.Win32.RegistryValueKind.Binary);
                host.SetValue("rp_regist_key", QtRegBinary(reg.RpRegistKeyChiaki), Microsoft.Win32.RegistryValueKind.Binary);
                host.SetValue("rp_key_type", (int)reg.RpKeyType, Microsoft.Win32.RegistryValueKind.DWord);
                host.SetValue("rp_key", QtRegBinary(PadOr(reg.RpKey, 16)), Microsoft.Win32.RegistryValueKind.Binary);
                host.SetValue("console_pin", "", Microsoft.Win32.RegistryValueKind.String);
            }
            return (true, "");
        }

        // Qt stores QByteArray in the Windows registry as REG_BINARY holding the
        // UTF-16LE encoding of "@ByteArray(" + bytes-as-Latin-1 + ")".
        private static byte[] QtRegBinary(byte[] data)
        {
            var chars = new char[data.Length + 12];
            "@ByteArray(".CopyTo(0, chars, 0, 11);
            for (int i = 0; i < data.Length; i++) chars[11 + i] = (char)data[i];
            chars[11 + data.Length] = ')';
            return Encoding.Unicode.GetBytes(new string(chars));
        }

        private static byte[] QtReadByteArray(object? v)
        {
            if (v is string s && s.StartsWith("@ByteArray(") && s.EndsWith(")"))
            {
                var inner = s.Substring(11, s.Length - 12);
                var bytes = new byte[inner.Length];
                for (int i = 0; i < inner.Length; i++) bytes[i] = (byte)inner[i];
                return bytes;
            }
            if (v is byte[] raw)
            {
                // REG_BINARY: UTF-16LE("@ByteArray(...)") → decode, strip, latin-1
                try
                {
                    var str = Encoding.Unicode.GetString(raw);
                    if (str.StartsWith("@ByteArray(") && str.EndsWith(")"))
                    {
                        var inner = str.Substring(11, str.Length - 12);
                        var bytes = new byte[inner.Length];
                        for (int i = 0; i < inner.Length; i++) bytes[i] = (byte)inner[i];
                        return bytes;
                    }
                    return raw;
                }
                catch { return raw; }
            }
            return Array.Empty<byte>();
        }

        private static byte[] PadOr(byte[] src, int len)
        {
            var b = new byte[len];
            Array.Copy(src, b, Math.Min(src.Length, len));
            return b;
        }

        private static (bool, string) WriteIni(RemotePlayRegistration reg)
        {
            // QSettings UnixFormat: ~/.config/Chiaki/Chiaki.conf — registered_hosts array.
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Chiaki");
            // Linux XDG config ~= ~/.config (ApplicationData maps there on unix)
            Directory.CreateDirectory(dir);
            string iniPath = Path.Combine(dir, "Chiaki.conf");

            var lines = File.Exists(iniPath)
                ? new List<string>(File.ReadAllLines(iniPath))
                : new List<string> { "[General]" };

            // append a new registered_hosts entry (merge minimally — chiaki merges on load)
            // Format: registered_hosts/<n>/<key>=value ; binary as @ByteArray(hex)
            int next = 1;
            foreach (var l in lines)
            {
                var m = System.Text.RegularExpressions.Regex.Match(l, @"registered_hosts[/\\](\d+)[/\\]");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n >= next)
                    next = n + 1;
            }
            // Qt INI @ByteArray: latin-1 chars, non-printable escaped as \xNN.
            string IniBA(byte[] data)
            {
                var sb = new StringBuilder("@ByteArray(");
                foreach (var b in data)
                    sb.Append(b >= 0x20 && b != 0x5c ? (char)b : $"\\x{b:x2}");
                return sb.Append(')').ToString();
            }
            string Mac() => IniBA(reg.ServerMac);
            string RegistKey() => IniBA(reg.RpRegistKeyChiaki);
            string RpKey() => IniBA(PadOr(reg.RpKey, 16));

            lines.Add($"registered_hosts/{next}/target={reg.Target}");
            lines.Add($"registered_hosts/{next}/ap_ssid={reg.ApSsid}");
            lines.Add($"registered_hosts/{next}/ap_bssid={reg.ApBssid}");
            lines.Add($"registered_hosts/{next}/ap_key={reg.ApKey}");
            lines.Add($"registered_hosts/{next}/ap_name={reg.ApName}");
            lines.Add($"registered_hosts/{next}/server_nickname={reg.ServerNickname}");
            lines.Add($"registered_hosts/{next}/server_mac={Mac()}");
            lines.Add($"registered_hosts/{next}/rp_regist_key={RegistKey()}");
            lines.Add($"registered_hosts/{next}/rp_key_type={reg.RpKeyType}");
            lines.Add($"registered_hosts/{next}/rp_key={RpKey()}");
            lines.Add($"registered_hosts/{next}/console_pin=");
            File.WriteAllLines(iniPath, lines);
            return (true, "");
        }

        // Locate chiaki.exe — check common spots around the app + saved path.
        public static string? FindChiakiExe(string? configuredPath)
        {
            var candidates = new List<string?>();
            if (!string.IsNullOrWhiteSpace(configuredPath)) candidates.Add(configuredPath);
            string baseDir = AppContext.BaseDirectory;
            candidates.Add(Path.Combine(baseDir, "chiaki", "chiaki.exe"));
            candidates.Add(Path.Combine(baseDir, "chiaki", "chiaki"));            // Linux bundled AppImage/binary
            candidates.Add(Path.Combine(baseDir, "chiaki", "chiaki-ng.app", "Contents", "MacOS", "chiaki")); // bundled macOS .app
            candidates.Add(Path.Combine(baseDir, "chiaki-ng-Win", "chiaki.exe"));
            candidates.Add(Path.Combine(baseDir, "chiaki", "chiaki-ng-Win", "chiaki.exe"));
            // dev tree: <repo>/tools/chiaki/chiaki-ng-Win/chiaki.exe (bin/Release/netX -> 4 up)
            candidates.Add(Path.Combine(baseDir, "..", "..", "..", "..", "tools", "chiaki", "chiaki-ng-Win", "chiaki.exe"));
            candidates.Add(Path.Combine(Environment.CurrentDirectory, "tools", "chiaki", "chiaki-ng-Win", "chiaki.exe"));
            candidates.Add(Path.Combine(baseDir, "third_party", "chiaki", "chiaki-ng", "chiaki-ng-Win", "chiaki.exe"));
            candidates.Add(Path.Combine(baseDir, "..", "third_party", "chiaki", "chiaki-ng", "chiaki-ng-Win", "chiaki.exe"));
            candidates.Add(Path.Combine(baseDir, "..", "..", "third_party", "chiaki", "chiaki-ng", "chiaki-ng-Win", "chiaki.exe"));
            foreach (var c in candidates)
                if (c != null && File.Exists(c)) return Path.GetFullPath(c);
            // PATH lookup
            try
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathEnv.Split(Path.PathSeparator))
                {
                    foreach (var name in new[] { "chiaki", "chiaki.exe" })
                    {
                        var p = Path.Combine(dir.Trim(), name);
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch { }
            return null;
        }

        public static void LaunchStream(string chiakiExe, RemotePlayRegistration reg, string? ps5IpOverride = null)
        {
            StartStream(chiakiExe, reg, ps5IpOverride);
        }

        // Starts chiaki's stream process; the caller owns the returned Process.
        // chiaki.exe --exit-app-on-stream-exit stream <nickname> <ip>
        // Options MUST precede the positionals — chiaki parses with
        // ParseAsPositionalArguments, so a trailing --option becomes the host.
        public static System.Diagnostics.Process? StartStream(
            string chiakiExe, RemotePlayRegistration reg, string? ps5IpOverride = null)
        {
            string ip = ps5IpOverride ?? reg.Ps5Ip;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = chiakiExe,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("--exit-app-on-stream-exit");
            psi.ArgumentList.Add("stream");
            psi.ArgumentList.Add(reg.ServerNickname);
            psi.ArgumentList.Add(ip);
            return System.Diagnostics.Process.Start(psi);
        }
    }
}
