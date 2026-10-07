using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PS5Upload
{
    // Protocol commands
    public enum Command : byte
    {
        Ping = 0x01,
        ListStorage = 0x02,
        ListDir = 0x03,
        CreateDir = 0x04,
        DeleteFile = 0x05,
        DeleteDir = 0x06,
        Rename = 0x07,
        CopyFile = 0x08,
        MoveFile = 0x09,
        StartUpload = 0x10,
        UploadChunk = 0x11,
        EndUpload = 0x12,
        DownloadFile = 0x13,
        ShellOpen = 0x20,
        ShellExec = 0x21,
        ShellInterrupt = 0x22,
        ShellClose = 0x23,
        IndexStart = 0x40,
        IndexStatus = 0x41,
        SearchIndex = 0x42,
        MountGames = 0x30,
        GetFileInfo = 0x31,
        GetSystemInfo = 0x32,
        VerifyFile = 0x33,
        GetHwInfo = 0x34,
        GetTemps = 0x35,
        GetRunningApps = 0x36,
        KillApp = 0x37,
        LaunchBrowser = 0x38,
        GetPowerInfo = 0x39,
        GetGameList = 0x3A,
        UnmountGame = 0x3B,
        GetGameIcon = 0x3C,
        GetGameDetails = 0x3D,
        GetGamePic = 0x3E,
        ListSaves = 0x3F,
        IndexCancel = 0x43,
        LaunchGame = 0x44,
        ListScreenshots = 0x45,
        DeleteScreenshot = 0x46,
        GetExtendedInfo = 0x47,
        GetCpuUsage = 0x48,
        GetMemoryInfo = 0x49,
        GetModuleList = 0x4A,
        
        // New features (ps5upload-style)
        PkgInstall = 0x50,
        PkgInstallStatus = 0x51,
        FanGetThreshold = 0x52,
        FanSetThreshold = 0x53,

        // Save manager (garlic-savemgr style)
        SaveScan = 0x60,
        SaveMount = 0x61,
        SaveUnmount = 0x62,
        SaveMountStatus = 0x63,
        SaveDelete = 0x64,

        ProcList = 0x66,
        RestartUI = 0x67,
        SelfUpdate = 0x68,
        MemRead = 0x69,
        MemWrite = 0x6A,
        MemRegions = 0x6B,
        MemSearch = 0x6C,
        KlogRead = 0x6D,
        MountGame = 0x6F,
        // App Manager v2
        AppListV2 = 0x70,
        AppSuspend = 0x71,
        AppResume = 0x72,
        AppKill = 0x73,
        AppCoredump = 0x74,
        NetInfo = 0x75,
        NetSpeedTest = 0x76,
        PowerAction = 0x77,
        UsbList = 0x78,
        PadInfo = 0x79,
        Screenshot = 0x7B,
        Notify = 0x7C,
        PadAction = 0x7D,
        IccControl = 0x7E,

        // Trophy viewer (NpTrophy V2) — read-only listing + icons
        TrophyList = 0x80,
        TrophyIcon = 0x81,
        TrophyUnlock = 0x82,

        Shutdown = 0xFF
    }

    // Protocol responses
    public enum Response : byte
    {
        Ok = 0x01,
        Error = 0x02,
        Data = 0x03,
        Ready = 0x04,
        Progress = 0x05
    }

    public class PS5Protocol : IDisposable
    {
        private TcpClient? _client;
        private NetworkStream? _stream;
        private const int BufferSize = 16 * 1024 * 1024; // 16MB buffer - matches payload socket buffers for maximum throughput
        private readonly SemaphoreSlim _commandLock = new SemaphoreSlim(1, 1); // Serialize all command-response cycles to prevent protocol desync

        public bool IsConnected => _client != null && _stream != null && _client.Connected;

        /// <summary>Local IP on the LAN interface used to reach the PS5 — the address the
        /// console can reach this PC on (for the PKG stream-install HTTP server).
        /// Dual-stack sockets report IPv4-mapped IPv6 (::ffff:x.x.x.x) which the
        /// payload's URL parser can't handle — always map back to dotted IPv4.</summary>
        public string? LocalIp
        {
            get
            {
                if (_client?.Client?.LocalEndPoint is not System.Net.IPEndPoint ep) return null;
                var a = ep.Address;
                if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
                return a.ToString();
            }
        }
        
        public string LastError { get; private set; } = "";

        /// <summary>The port this connection actually landed on.</summary>
        public int Port { get; private set; } = 9113;

        private enum ConnectResult { Ok, Refused, Timeout }

        public async Task<bool> ConnectAsync(string ipAddress, int port = 9113, CancellationToken ct = default)
        {
            // A wedged payload keeps its listener alive in the kernel — TCP
            // connect completes but nothing ever answers. Verify each
            // candidate with a bounded PING, then scan the fallback ports the
            // server binds when 9113 is held by a dead process.
            // If the TCP connect itself times out the host is unreachable —
            // every other port will fail identically, so bail immediately
            // instead of hanging ~30s on a wrong IP.
            var ports = new List<int> { port };
            for (int p = 9113; p <= 9118; p++)
                if (!ports.Contains(p)) ports.Add(p);

            // Probe ALL ports in parallel — a wedged payload accepts TCP but
            // never answers PING, and probing serially wastes ~3s per dead port.
            var probes = ports.Select(p => ProbePortAsync(ipAddress, p, ct)).ToList();
            (int port, TcpClient? client)[] results;
            try { results = await Task.WhenAll(probes); }
            catch (OperationCanceledException)
            {
                foreach (var pr in probes)
                    try { var r = pr.Result; r.client?.Dispose(); } catch { }
                throw;
            }

            TcpClient? winner = null; int winnerPort = 0;
            foreach (var (p, client) in results)
            {
                if (client != null && winner == null) { winner = client; winnerPort = p; }
                else if (client != null) { client.Close(); client.Dispose(); }
            }
            if (winner == null) { Disconnect(); return false; }

            _client = winner;
            TrySetSocketBuffers(_client, 16 * 1024 * 1024);
            _stream = winner.GetStream();
            Port = winnerPort;
            return true;
        }

        /// <summary>TCP-connect + one bounded PING on a single port. Returns the
        /// live socket on success so the caller can keep using it.</summary>
        private static async Task<(int port, TcpClient? client)> ProbePortAsync(
            string ipAddress, int port, CancellationToken ct)
        {
            TcpClient? c = null;
            try
            {
                c = new TcpClient { NoDelay = true, LingerState = new System.Net.Sockets.LingerOption(false, 0) };
                var connectTask = c.ConnectAsync(ipAddress, port);
                if (await Task.WhenAny(connectTask, Task.Delay(2000, ct)) != connectTask)
                { c.Dispose(); return (port, null); }
                await connectTask;
                if (!c.Connected) { c.Dispose(); return (port, null); }

                var s = c.GetStream();
                byte[] ping = { (byte)Command.Ping, 0, 0, 0, 0 };
                using var probe = new CancellationTokenSource(2000);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(probe.Token, ct);
                await s.WriteAsync(ping, linked.Token);
                // Read the FULL framed response (5-byte header + payload) —
                // PING answers "PONG" (4 bytes); leaving them in the stream
                // desyncs every subsequent command.
                var hdr = new byte[5];
                int got = 0;
                while (got < 5)
                {
                    int n = await s.ReadAsync(hdr, got, 5 - got, linked.Token);
                    if (n == 0) break;
                    got += n;
                }
                uint payloadLen = got == 5 ? BitConverter.ToUInt32(hdr, 1) : 0;
                var body = new byte[payloadLen];
                got = 0;
                while (got < payloadLen)
                {
                    int n = await s.ReadAsync(body, got, (int)payloadLen - got, linked.Token);
                    if (n == 0) break;
                    got += n;
                }
                if (hdr[0] == (byte)Response.Ok && got == payloadLen) return (port, c);
                c.Close(); c.Dispose();
                return (port, null);
            }
            catch
            {
                c?.Dispose();
                return (port, null);
            }
        }

        /// <summary>
        /// Broadcasts a UDP discovery probe; the running payload answers
        /// "PS5SUITE|&lt;port&gt;|&lt;ver&gt;" with its real IP + bound port.
        /// Returns null when nothing answers within the timeout.
        /// </summary>
        public static async Task<(string ip, int port)?> DiscoverPS5Async(int timeoutMs = 2500, CancellationToken ct = default)
        {
            // The payload's discovery responder binds its UDP socket to the same
            // port number as its TCP listener, which is the first free port in
            // the takeover range 9113..9118 (a wedged instance keeps 9113, so the
            // live server may sit on a fallback). Probe the whole range.
            var discoveryPorts = Enumerable.Range(9113, 6).ToList();
            try
            {
                using var udp = new UdpClient();
                udp.EnableBroadcast = true;
                var probe = Encoding.ASCII.GetBytes("PS5SUITE_DISCOVER");

                var targets = new List<IPEndPoint>();
                foreach (var dp in discoveryPorts)
                    targets.Add(new IPEndPoint(IPAddress.Broadcast, dp));
                // Subnet-directed broadcasts per local interface — plain
                // 255.255.255.255 is dropped by some routers/NICs.
                try
                {
                    foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    {
                        var props = ni.GetIPProperties();
                        if (props == null || ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                        foreach (var ua in props.UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                            var ip = ua.Address.GetAddressBytes();
                            var mask = ua.IPv4Mask.GetAddressBytes();
                            var bcast = new byte[4];
                            for (int i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | ~mask[i]);
                            var b = new IPAddress(bcast);
                            foreach (var dp in discoveryPorts)
                            {
                                var ep = new IPEndPoint(b, dp);
                                if (!targets.Any(t => t.Equals(ep))) targets.Add(ep);
                            }
                        }
                    }
                }
                catch { }

                foreach (var ep in targets)
                {
                    try { await udp.SendAsync(probe, probe.Length, ep); } catch { }
                }

                var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    int remain = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    var recvTask = udp.ReceiveAsync();
                    var delayTask = Task.Delay(Math.Min(remain, 500), ct);
                    var done = await Task.WhenAny(recvTask, delayTask);
                    if (done == delayTask) continue;   // re-send window elapsed
                    var res = await recvTask;
                    string msg = Encoding.ASCII.GetString(res.Buffer);
                    var p = msg.Split('|');
                    if (p.Length >= 2 && p[0] == "PS5SUITE" && int.TryParse(p[1], out int port))
                        return (res.RemoteEndPoint.Address.ToString(), port);
                }
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        private async Task<bool> VerifyAliveAsync()
        {
            try
            {
                await SendCommandAsync(Command.Ping);
                var (response, _) = await ReceiveResponseAsync(3000);
                return response == Response.Ok;
            }
            catch { return false; }
        }

        private async Task<ConnectResult> ConnectSingleAsync(string ipAddress, int port, CancellationToken ct)
        {
            try
            {
                _client = new TcpClient();
                TrySetSocketBuffers(_client, 16 * 1024 * 1024); // 16MB - matches payload SO_RCVBUF setting
                _client.NoDelay = true;
                _client.LingerState = new System.Net.Sockets.LingerOption(false, 0);

                // BUG FIX #1: Add 5 second timeout for connection to fail fast on wrong IP
                var connectTask = _client.ConnectAsync(ipAddress, port);
                var timeoutTask = Task.Delay(5000, ct); // 5 second timeout, cancellable

                var completedTask = await Task.WhenAny(connectTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    // Observe the abandoned connect task so a late failure
                    // (host unreachable etc.) does not surface as an unobserved
                    // task exception.
                    _ = connectTask.ContinueWith(t => _ = t.Exception,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

                    _client?.Close();
                    _client?.Dispose();
                    _client = null;
                    ct.ThrowIfCancellationRequested();
                    return ConnectResult.Timeout;
                }

                // Await the connect task to observe any exception it produced (prevents unobserved task crashes)
                await connectTask.ConfigureAwait(false);

                // Check if connection actually succeeded
                if (!_client.Connected)
                {
                    return ConnectResult.Refused;
                }

                _stream = _client.GetStream();
                return ConnectResult.Ok;
            }
            catch (OperationCanceledException)
            {
                _client?.Close();
                _client?.Dispose();
                _client = null;
                throw;
            }
            catch
            {
                _client?.Close();
                _client?.Dispose();
                _client = null;
                return ConnectResult.Refused;
            }
        }

        public void Disconnect()
        {
            _stream?.Dispose();
            _client?.Dispose();
            _stream = null;
            _client = null;
        }

        private async Task SendCommandAsync(Command cmd, byte[]? data = null)
        {
            if (_stream == null) throw new InvalidOperationException("Not connected");

            byte[] header = new byte[5];
            header[0] = (byte)cmd;
            
            uint dataLen = data != null ? (uint)data.Length : 0;
            BitConverter.GetBytes(dataLen).CopyTo(header, 1);

            await _stream.WriteAsync(header, 0, 5);
            if (data != null && data.Length > 0)
            {
                await WriteSocketChunkedAsync(_stream, data, 0, data.Length, CancellationToken.None);
            }
        }

        // macOS caps SO_SNDBUF/SO_RCVBUF at kern.ipc.maxsockbuf (8MB default).
        // setsockopt above the cap returns ENOBUFS — clamp down until it sticks.
        private static void TrySetSocketBuffers(TcpClient client, int desired)
        {
            for (int sz = desired; sz >= 1 * 1024 * 1024; sz >>= 1)
            {
                try { client.ReceiveBufferSize = sz; client.SendBufferSize = sz; return; }
                catch (SocketException) { }
            }
        }

        // macOS kern.ipc.maxsockbuf defaults to 8MB — a single send() larger
        // than that fails with ENOBUFS. Slice every socket write to 4MB;
        // invisible overhead on Windows/Linux, fixes Darwin transfers.
        private const int MaxSocketWriteBytes = 4 * 1024 * 1024;

        private static async Task WriteSocketChunkedAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            while (count > 0)
            {
                int n = Math.Min(count, MaxSocketWriteBytes);
                await stream.WriteAsync(buffer, offset, n, ct);
                offset += n;
                count -= n;
            }
        }

        private async Task<(Response response, byte[] data)> ReceiveResponseAsync(int timeoutMs = 120000)
        {
            if (_stream == null) throw new InvalidOperationException("Not connected");

            byte[] header = new byte[5];
            await ReadExactAsync(header, 5, timeoutMs);

            Response response = (Response)header[0];
            uint dataLen = BitConverter.ToUInt32(header, 1);

            byte[] data = new byte[dataLen];
            if (dataLen > 0)
            {
                await ReadExactAsync(data, (int)dataLen, timeoutMs);
            }

            return (response, data);
        }

        private async Task ReadExactAsync(byte[] buffer, int count, int timeoutMs = 120000)
        {
            if (_stream == null) throw new InvalidOperationException("Not connected");

            int offset = 0;
            using var cts = new CancellationTokenSource(timeoutMs);
            
            try
            {
                while (offset < count)
                {
                    int read = await _stream.ReadAsync(buffer, offset, count - offset, cts.Token);
                    if (read == 0)
                    {
                        // Give PS5 a moment to recover before declaring connection dead
                        await Task.Delay(100);
                        read = await _stream.ReadAsync(buffer, offset, count - offset, cts.Token);
                        if (read == 0) throw new IOException("Connection closed");
                    }
                    offset += read;
                }
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Read timeout");
            }
        }

        public async Task<bool> PingAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.Ping);
                var (response, _) = await ReceiveResponseAsync();
                return response == Response.Ok;
            }
            catch (Exception ex)
            {
                LastError = $"PingAsync: {ex.GetType().Name}: {ex.Message}";
                return false;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<PS5StorageInfo?> ListStorageAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.ListStorage);
                var (response, data) = await ReceiveResponseAsync();

                if (response != Response.Data)
                {
                    Console.WriteLine($"[DEBUG] ListStorage: Response is not Data, got {response}");
                    return null;
                }

                // Parse response: total|free|available|reserved|mounted_games|user_data|real_free
                string responseStr = Encoding.UTF8.GetString(data);
                Console.WriteLine($"[DEBUG] ListStorage raw response: '{responseStr}'");
                
                string[] parts = responseStr.Split('|');
                Console.WriteLine($"[DEBUG] ListStorage parts count: {parts.Length}");
                
                if (parts.Length < 7)
                {
                    Console.WriteLine($"[DEBUG] ListStorage: Expected at least 7 parts, got {parts.Length}");
                    for (int i = 0; i < parts.Length; i++)
                    {
                        Console.WriteLine($"[DEBUG]   Part[{i}]: '{parts[i]}'");
                    }
                    return null;
                }
                
                // Get path if available (part 8); part 9 was a dev-only mount
                // dump — intentionally ignored now.
                string storagePath = (parts.Length >= 8) ? parts[7].Trim() : "unknown";

                return new PS5StorageInfo
                {
                    TotalBytes = ulong.Parse(parts[0].Trim()),
                    FreeBytes = ulong.Parse(parts[1].Trim()),
                    AvailableBytes = ulong.Parse(parts[2].Trim()),
                    ReservedBytes = ulong.Parse(parts[3].Trim()),
                    MountedGamesSize = ulong.Parse(parts[4].Trim()),
                    UserDataSize = ulong.Parse(parts[5].Trim()),
                    RealFreeSpace = ulong.Parse(parts[6].Trim()),
                    StoragePath = storagePath
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG] ListStorage exception: {ex.Message}");
                Console.WriteLine($"[DEBUG] Stack trace: {ex.StackTrace}");
                throw;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Get detailed file information
        public async Task<PS5FileInfo?> GetFileInfoAsync(string path)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] pathBytes = Encoding.UTF8.GetBytes(path + "\0");
                await SendCommandAsync(Command.GetFileInfo, pathBytes);
                var (response, data) = await ReceiveResponseAsync();

                if (response != Response.Data)
                    return null;

                // Parse: size|mtime|atime|mode|is_dir|is_link
                string responseStr = Encoding.UTF8.GetString(data);
                string[] parts = responseStr.Split('|');
                if (parts.Length < 6) return null;

                return new PS5FileInfo
                {
                    Size = long.Parse(parts[0]),
                    ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[1])).LocalDateTime,
                    AccessTime = DateTimeOffset.FromUnixTimeSeconds(long.Parse(parts[2])).LocalDateTime,
                    Permissions = Convert.ToInt32(parts[3], 8),
                    IsDirectory = parts[4] == "1",
                    IsSymlink = parts[5] == "1"
                };
            }
            catch
            {
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Get PS5 system information
        public async Task<PS5SystemInfo?> GetSystemInfoAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.GetSystemInfo);
                var (response, data) = await ReceiveResponseAsync();

                if (response != Response.Data)
                    return null;

                string responseStr = Encoding.UTF8.GetString(data);
                var info = new PS5SystemInfo();

                foreach (var line in responseStr.Split('\n'))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;

                    var key = parts[0].Trim();
                    var value = parts[1].Trim();

                    switch (key)
                    {
                        case "hostname": info.Hostname = value; break;
                        case "server_version": info.ServerVersion = value; break;
                        case "protocol_version": int.TryParse(value, out int pv); info.ProtocolVersion = pv; break;
                        case "total_memory": ulong.TryParse(value, out ulong tm); info.TotalMemory = tm; break;
                        case "storage_total": ulong.TryParse(value, out ulong st); info.StorageTotal = st; break;
                        case "storage_free": ulong.TryParse(value, out ulong sf); info.StorageFree = sf; break;
                        case "mounted_games": int.TryParse(value, out int mg); info.MountedGames = mg; break;
                        case "index_ready": info.IndexReady = value == "1"; break;
                        case "index_files": int.TryParse(value, out int ifi); info.IndexFiles = ifi; break;
                        case "index_dirs": int.TryParse(value, out int idi); info.IndexDirs = idi; break;
                        case "server_uptime": long.TryParse(value, out long su); info.ServerUptime = su; break;
                    }
                }

                return info;
            }
            catch
            {
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Verify file integrity with CRC32
        public async Task<FileVerificationResult> VerifyFileAsync(string path)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] pathBytes = Encoding.UTF8.GetBytes(path + "\0");
                await SendCommandAsync(Command.VerifyFile, pathBytes);
                var (response, data) = await ReceiveResponseAsync();

                if (response == Response.Error)
                {
                    return new FileVerificationResult
                    {
                        Success = false,
                        Error = Encoding.UTF8.GetString(data)
                    };
                }

                if (response != Response.Data)
                {
                    return new FileVerificationResult
                    {
                        Success = false,
                        Error = "Unexpected response"
                    };
                }

                // Parse: CRC32|size
                string responseStr = Encoding.UTF8.GetString(data);
                string[] parts = responseStr.Split('|');
                if (parts.Length < 2)
                {
                    return new FileVerificationResult
                    {
                        Success = false,
                        Error = "Invalid response format"
                    };
                }

                return new FileVerificationResult
                {
                    Success = true,
                    CRC32 = parts[0],
                    Size = ulong.Parse(parts[1])
                };
            }
            catch (Exception ex)
            {
                return new FileVerificationResult
                {
                    Success = false,
                    Error = ex.Message
                };
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Get hardware info (model, serial, features)
        public async Task<PS5HardwareInfo?> GetHardwareInfoAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.GetHwInfo);
                var (response, data) = await ReceiveResponseAsync();

                if (response != Response.Data)
                    return null;

                string responseStr = Encoding.UTF8.GetString(data);
                var info = new PS5HardwareInfo();

                foreach (var line in responseStr.Split('\n'))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;

                    var key = parts[0].Trim();
                    var value = parts[1].Trim();

                    switch (key)
                    {
                        case "model": info.Model = value; break;
                        case "serial": info.Serial = value; break;
                        case "has_wlan_bt": info.HasWlanBt = value == "1"; break;
                        case "has_optical_out": info.HasOpticalOut = value == "1"; break;
                        case "hw_machine": info.HwMachine = value; break;
                        case "os": info.OsVersion = value; break;
                        case "ncpu": int.TryParse(value, out int n); info.NumCpu = n; break;
                        case "physmem": ulong.TryParse(value, out ulong pm); info.PhysMem = pm; break;
                    }
                }

                return info;
            }
            catch
            {
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // Store last raw diagnostic response for logging
        public string LastTempRawResponse { get; private set; } = "";

        // NEW: Get temperature and CPU info
        public async Task<PS5TemperatureInfo?> GetTemperatureInfoAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.GetTemps);
                var (response, data) = await ReceiveResponseAsync();

                if (response != Response.Data)
                    return null;

                string responseStr = Encoding.UTF8.GetString(data);
                LastTempRawResponse = responseStr;
                var info = new PS5TemperatureInfo();

                foreach (var line in responseStr.Split('\n'))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;

                    var key = parts[0].Trim();
                    var value = parts[1].Trim();

                    switch (key)
                    {
                        case "cpu_temp": int.TryParse(value, out int ct); info.CpuTemp = ct; break;
                        case "soc_temp": int.TryParse(value, out int st); info.SocTemp = st; break;
                        case "cpu_freq_mhz": long.TryParse(value, out long cf); info.CpuFreqMhz = cf; break;
                        case "soc_power_mw": uint.TryParse(value, out uint pw); info.SocPowerMw = pw; break;
                        case "cpu_usage_0": int.TryParse(value, out int c0); info.CpuUsage[0] = c0; break;
                        case "cpu_usage_1": int.TryParse(value, out int c1); info.CpuUsage[1] = c1; break;
                        case "cpu_usage_2": int.TryParse(value, out int c2); info.CpuUsage[2] = c2; break;
                        case "cpu_usage_3": int.TryParse(value, out int c3); info.CpuUsage[3] = c3; break;
                        case "cpu_usage_4": int.TryParse(value, out int c4); info.CpuUsage[4] = c4; break;
                        case "cpu_usage_5": int.TryParse(value, out int c5); info.CpuUsage[5] = c5; break;
                        case "cpu_usage_6": int.TryParse(value, out int c6); info.CpuUsage[6] = c6; break;
                        case "cpu_usage_7": int.TryParse(value, out int c7); info.CpuUsage[7] = c7; break;
                    }
                }

                return info;
            }
            catch (Exception ex)
            {
                LastError = $"GetTemperatureInfoAsync: {ex.GetType().Name}: {ex.Message}";
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Get list of running apps
        public async Task<List<PS5RunningApp>> GetRunningAppsAsync()
        {
            var apps = new List<PS5RunningApp>();
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.GetRunningApps);
                var (response, data) = await ReceiveResponseAsync();

                if (response != Response.Data)
                    return apps;

                string responseStr = Encoding.UTF8.GetString(data);

                foreach (var line in responseStr.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line) || line == "No apps running")
                        continue;

                    var app = new PS5RunningApp();
                    foreach (var part in line.Split('|'))
                    {
                        var kv = part.Split('=', 2);
                        if (kv.Length != 2) continue;

                        switch (kv[0])
                        {
                            case "pid": int.TryParse(kv[1], out int pid); app.Pid = pid; break;
                            case "name": app.Name = kv[1]; break;
                            case "title_id": app.TitleId = kv[1]; break;
                            case "app_id": uint.TryParse(kv[1], out uint aid); app.AppId = aid; break;
                        }
                    }

                    if (!string.IsNullOrEmpty(app.TitleId))
                        apps.Add(app);
                }

                return apps;
            }
            catch
            {
                return apps;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Kill an app by title ID
        public async Task<(bool success, string message)> KillAppAsync(string titleId)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(titleId + "\0");
                await SendCommandAsync(Command.KillApp, data);
                var (response, respData) = await ReceiveResponseAsync();

                string message = Encoding.UTF8.GetString(respData);
                return (response == Response.Ok, message);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Launch browser with URL
        public async Task<(bool success, string message)> LaunchBrowserAsync(string url)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(url + "\0");
                await SendCommandAsync(Command.LaunchBrowser, data);
                var (response, respData) = await ReceiveResponseAsync();

                string message = Encoding.UTF8.GetString(respData);
                return (response == Response.Ok, message);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Get power info (operating time, boot count, power consumption)
        public async Task<PS5PowerInfo?> GetPowerInfoAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.GetPowerInfo);
                var (response, data) = await ReceiveResponseAsync();

                if (response != Response.Data)
                    return null;

                string responseStr = Encoding.UTF8.GetString(data);
                var info = new PS5PowerInfo();

                foreach (var line in responseStr.Split('\n'))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;

                    var key = parts[0].Trim();
                    var value = parts[1].Trim();

                    switch (key)
                    {
                        case "operating_time_sec": ulong.TryParse(value, out ulong ots); info.OperatingTimeSec = ots; break;
                        case "operating_time_hours": ulong.TryParse(value, out ulong oth); info.OperatingTimeHours = oth; break;
                        case "operating_time_minutes": ulong.TryParse(value, out ulong otm); info.OperatingTimeMinutes = otm; break;
                        case "boot_count": uint.TryParse(value, out uint bc); info.BootCount = bc; break;
                        case "power_consumption_mw": uint.TryParse(value, out uint pc); info.PowerConsumptionMw = pc; break;
                    }
                }

                return info;
            }
            catch
            {
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Get extended system info (firmware, product, ICC data)
        public async Task<PS5ExtendedInfo?> GetExtendedInfoAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.GetExtendedInfo);
                var (response, data) = await ReceiveResponseAsync();

                if (response != Response.Data)
                    return null;

                string responseStr = Encoding.UTF8.GetString(data);
                var info = new PS5ExtendedInfo();

                foreach (var line in responseStr.Split('\n'))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;

                    var key = parts[0].Trim();
                    var value = parts[1].Trim();

                    switch (key)
                    {
                        case "firmware_version": info.FirmwareVersion = value; break;
                        case "prospero_version": info.ProsperoVersion = value; break;
                        case "product_code": info.ProductCode = value; break;
                        case "product_str": info.ProductStr = value; break;
                        case "total_operating_time_sec": ulong.TryParse(value, out ulong tots); info.TotalOperatingTimeSec = tots; break;
                        case "total_operating_time_hours": ulong.TryParse(value, out ulong toth); info.TotalOperatingTimeHours = toth; break;
                        case "boot_count": uint.TryParse(value, out uint bc); info.BootCount = bc; break;
                        case "shutdown_count": uint.TryParse(value, out uint sc); info.ShutdownCount = sc; break;
                        case "thermal_alert": int.TryParse(value, out int ta); info.ThermalAlert = ta; break;
                        case "bd_drive_power": int.TryParse(value, out int bd); info.BdDrivePower = bd; break;
                    }
                }

                return info;
            }
            catch
            {
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Get CPU usage per core
        public async Task<PS5CpuUsage?> GetCpuUsageAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.GetCpuUsage);
                var (response, data) = await ReceiveResponseAsync();

                if (response != Response.Data)
                    return null;

                string responseStr = Encoding.UTF8.GetString(data);
                var info = new PS5CpuUsage();

                foreach (var line in responseStr.Split('\n'))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;

                    var key = parts[0].Trim();
                    var value = parts[1].Trim();

                    if (key.StartsWith("core") && key.Length == 5)
                    {
                        int coreNum = key[4] - '0';
                        if (coreNum >= 0 && coreNum < 8 && int.TryParse(value, out int usage))
                            info.CoreUsage[coreNum] = usage;
                    }
                    else if (key == "average")
                    {
                        int.TryParse(value, out int avg);
                        info.Average = avg;
                    }
                }

                return info;
            }
            catch
            {
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Get memory info
        public async Task<PS5MemoryInfo?> GetMemoryInfoAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.GetMemoryInfo);
                var (response, data) = await ReceiveResponseAsync();

                if (response != Response.Data)
                    return null;

                string responseStr = Encoding.UTF8.GetString(data);
                var info = new PS5MemoryInfo();

                foreach (var line in responseStr.Split('\n'))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;

                    var key = parts[0].Trim();
                    var value = parts[1].Trim();

                    switch (key)
                    {
                        case "direct_total": ulong.TryParse(value, out ulong dt); info.DirectTotal = dt; break;
                        case "direct_available": ulong.TryParse(value, out ulong da); info.DirectAvailable = da; break;
                        case "direct_used": ulong.TryParse(value, out ulong du); info.DirectUsed = du; break;
                        case "direct_used_percent": int.TryParse(value, out int dup); info.DirectUsedPercent = dup; break;
                        case "flexible_available": ulong.TryParse(value, out ulong fa); info.FlexibleAvailable = fa; break;
                        case "physical_total": ulong.TryParse(value, out ulong pt); info.PhysicalTotal = pt; break;
                        case "user_memory": ulong.TryParse(value, out ulong um); info.UserMemory = um; break;
                        case "free_memory": ulong.TryParse(value, out ulong fm); info.FreeMemory = fm; break;
                        case "cpu_pool_total": ulong.TryParse(value, out ulong cpt); info.CpuPoolTotal = cpt; break;
                        case "cpu_pool_free": ulong.TryParse(value, out ulong cpf); info.CpuPoolFree = cpf; break;
                        case "gpu_pool_total": ulong.TryParse(value, out ulong gpt); info.GpuPoolTotal = gpt; break;
                        case "gpu_pool_free": ulong.TryParse(value, out ulong gpf); info.GpuPoolFree = gpf; break;
                    }
                }

                return info;
            }
            catch
            {
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Get loaded modules list
        public async Task<List<PS5ModuleInfo>> GetModuleListAsync()
        {
            var modules = new List<PS5ModuleInfo>();
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.GetModuleList);
                var (response, data) = await ReceiveResponseAsync();

                if (response != Response.Data)
                    return modules;

                string responseStr = Encoding.UTF8.GetString(data);
                
                // Parse JSON-like format: {"id":123,"name":"module.sprx"}
                foreach (var line in responseStr.Split('\n'))
                {
                    if (!line.Contains("\"id\"")) continue;
                    
                    var module = new PS5ModuleInfo();
                    
                    // Extract id
                    var idMatch = System.Text.RegularExpressions.Regex.Match(line, "\"id\":(\\d+)");
                    if (idMatch.Success)
                        uint.TryParse(idMatch.Groups[1].Value, out uint id);
                    
                    // Extract name
                    var nameMatch = System.Text.RegularExpressions.Regex.Match(line, "\"name\":\"([^\"]+)\"");
                    if (nameMatch.Success)
                        module.Name = nameMatch.Groups[1].Value;
                    
                    // Extract path if present
                    var pathMatch = System.Text.RegularExpressions.Regex.Match(line, "\"path\":\"([^\"]+)\"");
                    if (pathMatch.Success)
                        module.Path = pathMatch.Groups[1].Value;
                    
                    if (!string.IsNullOrEmpty(module.Name))
                        modules.Add(module);
                }

                return modules;
            }
            catch
            {
                return modules;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // Power action: "reboot" or "shutdown" — the console goes down right
        // after the ACK, so callers should expect the connection to drop.
        public async Task<(bool success, string message)> PowerActionAsync(string action)
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.PowerAction, Encoding.UTF8.GetBytes(action));
                var (response, data) = await ReceiveResponseAsync(10000);
                string msg = data.Length > 0 ? Encoding.UTF8.GetString(data) : response.ToString();
                return (response == Response.Ok, msg.TrimEnd('\0', '\n', '\r'));
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<List<PS5UsbDrive>> ListUsbDrivesAsync()
        {
            var drives = new List<PS5UsbDrive>();
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.UsbList);
                var (response, data) = await ReceiveResponseAsync();
                if (response != Response.Data) return drives;

                foreach (var line in Encoding.UTF8.GetString(data).Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line) || line == "NONE") continue;
                    var p = line.Trim().Split('|');
                    if (p.Length < 5) continue;
                    drives.Add(new PS5UsbDrive
                    {
                        MountPath = p[0],
                        FsType = p[1],
                        Device = p[2],
                        TotalBytes = ulong.TryParse(p[3], out var t) ? t : 0,
                        FreeBytes = ulong.TryParse(p[4], out var f) ? f : 0
                    });
                }
                return drives;
            }
            catch
            {
                return drives;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<string?> GetPadInfoAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.PadInfo);
                var (response, data) = await ReceiveResponseAsync(15000);
                if (response != Response.Data) return null;
                return Encoding.UTF8.GetString(data);
            }
            catch
            {
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // Generic simple command that takes a string arg and returns a short
        // status string — used by the devices/fun features.
        public async Task<(bool success, string message)> SendTextCommandAsync(Command cmd, string arg, int timeoutMs = 15000)
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(cmd, Encoding.UTF8.GetBytes(arg));
                var (response, data) = await ReceiveResponseAsync(timeoutMs);
                string msg = data.Length > 0 ? Encoding.UTF8.GetString(data).TrimEnd('\0', '\n', '\r') : response.ToString();
                return (response == Response.Ok, msg);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<(bool success, string message)> CaptureScreenshotAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.Screenshot);
                var (response, data) = await ReceiveResponseAsync(20000);
                string msg = data.Length > 0 ? Encoding.UTF8.GetString(data).TrimEnd('\0', '\n', '\r') : response.ToString();
                return (response == Response.Ok, msg);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public Task<(bool success, string message)> NotifyAsync(string text)
            => SendTextCommandAsync(Command.Notify, text);

        public Task<(bool success, string message)> PadActionAsync(string cmd)
            => SendTextCommandAsync(Command.PadAction, cmd);

        public Task<(bool success, string message)> IccControlAsync(string cmd)
            => SendTextCommandAsync(Command.IccControl, cmd);

        // NEW: Get list of mounted games
        public async Task<List<PS5MountedGame>> GetGameListAsync()
        {
            var games = new List<PS5MountedGame>();
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.GetGameList);
                var (response, data) = await ReceiveResponseAsync();

                if (response != Response.Data)
                    return games;

                string responseStr = Encoding.UTF8.GetString(data);

                if (responseStr.Trim() == "NO_GAMES")
                    return games;

                foreach (var line in responseStr.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    // Format: title_id|name|path|size|region|active
                    var parts = line.Split('|');
                    if (parts.Length < 6) continue;

                    var game = new PS5MountedGame
                    {
                        TitleId = parts[0],
                        Name = parts[1],
                        Path = parts[2],
                        Region = parts[4],
                        IsActive = parts[5] == "1"
                    };

                    if (ulong.TryParse(parts[3], out ulong size))
                        game.Size = size;

                    games.Add(game);
                }

                return games;
            }
            catch
            {
                return games;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: List all save games on the PS5
        public async Task<List<PS5SaveGame>> ListSavesAsync()
        {
            var saves = new List<PS5SaveGame>();
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.ListSaves);
                var (response, data) = await ReceiveResponseAsync();
                if (response != Response.Data) return saves;

                string responseStr = Encoding.UTF8.GetString(data);
                if (responseStr.Trim() == "NO_SAVES") return saves;

                foreach (var line in responseStr.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    // Format: title_id|user_id|save_path|size|mtime
                    var parts = line.Split('|');
                    if (parts.Length < 5) continue;

                    var save = new PS5SaveGame
                    {
                        TitleId = parts[0],
                        UserId = parts[1],
                        SavePath = parts[2],
                    };

                    if (ulong.TryParse(parts[3], out ulong sz)) save.Size = sz;
                    if (long.TryParse(parts[4], out long mt)) save.ModifiedUnixTime = mt;

                    saves.Add(save);
                }
                return saves;
            }
            catch { return saves; }
            finally { _commandLock.Release(); }
        }

        // ============================================================
        // SAVE MANAGER (garlic-savemgr style)
        // Scans individual save image files, mounts them decrypted at
        // /data/save_mnt for browsing/editing, unmounts (write-back).
        // ============================================================

        public const string SaveMountPoint = "/data/save_mnt";

        public async Task<List<PS5SaveFile>> ListSaveFilesAsync()
        {
            var saves = new List<PS5SaveFile>();
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.SaveScan);
                var (response, data) = await ReceiveResponseAsync();
                if (response != Response.Data) return saves;

                string responseStr = Encoding.UTF8.GetString(data);
                if (responseStr.Trim() == "NO_SAVES") return saves;

                foreach (var line in responseStr.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    // Format: path|title_id|save_name|user|size|is_ps4|has_bin
                    var parts = line.Split('|');
                    if (parts.Length < 7) continue;

                    var save = new PS5SaveFile
                    {
                        Path = parts[0],
                        TitleId = parts[1],
                        SaveName = parts[2],
                        UserId = parts[3],
                    };
                    if (ulong.TryParse(parts[4], out ulong sz)) save.Size = sz;
                    save.IsPs4 = parts[5] == "1";
                    save.HasBinKey = parts[6] == "1";
                    saves.Add(save);
                }
                return saves;
            }
            catch { return saves; }
            finally { _commandLock.Release(); }
        }

        // Mount a save image decrypted at /data/save_mnt. The payload emits
        // progress heartbeats; the mount itself can take up to ~90s worst case
        // (key ioctl + filesystem mount watchdogs).
        public async Task<(bool success, string message)> SaveMountAsync(string path, Action<string>? onProgress = null)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(path + "\0");
                await SendCommandAsync(Command.SaveMount, data);

                var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
                while (true)
                {
                    int remainingMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (remainingMs <= 0) return (false, "Mount timed out");
                    var (response, respData) = await ReceiveResponseAsync(remainingMs);
                    string message = respData != null ? Encoding.UTF8.GetString(respData).TrimEnd('\0') : "";
                    if (response == Response.Progress) { if (!string.IsNullOrEmpty(message)) onProgress?.Invoke(message); continue; }
                    if (response == Response.Data) return (true, message);
                    if (response == Response.Error) return (false, message);
                    return (false, $"Unexpected response: {response}");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
            finally { _commandLock.Release(); }
        }

        public async Task<(bool success, string message)> SaveUnmountAsync(Action<string>? onProgress = null)
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.SaveUnmount);
                var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
                while (true)
                {
                    int remainingMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (remainingMs <= 0) return (false, "Unmount timed out");
                    var (response, respData) = await ReceiveResponseAsync(remainingMs);
                    string message = respData != null ? Encoding.UTF8.GetString(respData).TrimEnd('\0') : "";
                    if (response == Response.Progress) { if (!string.IsNullOrEmpty(message)) onProgress?.Invoke(message); continue; }
                    if (response == Response.Data) return (true, message);
                    if (response == Response.Error) return (false, message);
                    return (false, $"Unexpected response: {response}");
                }
            }
            catch (Exception ex) { return (false, ex.Message); }
            finally { _commandLock.Release(); }
        }

        public async Task<(bool mounted, string src, string local, bool isPs4)> SaveMountStatusAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.SaveMountStatus);
                var (response, data) = await ReceiveResponseAsync();
                if (response != Response.Data || data == null) return (false, "", "", false);
                string s = Encoding.UTF8.GetString(data).TrimEnd('\0').Trim();
                if (!s.StartsWith("MOUNTED|")) return (false, "", "", false);
                var p = s.Split('|');
                return (true, p.Length > 1 ? p[1] : "", p.Length > 2 ? p[2] : "", p.Length > 3 && p[3] == "1");
            }
            catch { return (false, "", "", false); }
            finally { _commandLock.Release(); }
        }

        public async Task<(bool success, string message)> SaveDeleteAsync(string path)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(path + "\0");
                await SendCommandAsync(Command.SaveDelete, data);
                var (response, respData) = await ReceiveResponseAsync();
                string message = respData != null ? Encoding.UTF8.GetString(respData).TrimEnd('\0') : "";
                if (response == Response.Data || response == Response.Ok) return (true, message);
                return (false, message);
            }
            catch (Exception ex) { return (false, ex.Message); }
            finally { _commandLock.Release(); }
        }

        // NEW: Unmount a game by title ID.
        // v6.1.1: the server streams RESP_PROGRESS heartbeats while the unmount
        // grinds (busy vnodes can stall kernel calls for a while) — we must
        // consume them or the next frame would be misread as the final answer.
        // A dedicated 10-minute budget replaces the default 120s read timeout.
        public async Task<(bool success, string message)> UnmountGameAsync(string titleId, Action<string>? onProgress = null)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(titleId + "\0");
                await SendCommandAsync(Command.UnmountGame, data);

                var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
                while (true)
                {
                    int remainingMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (remainingMs <= 0) return (false, "Unmount timed out after 10 minutes");

                    var (response, respData) = await ReceiveResponseAsync(remainingMs);
                    string message = respData != null ? Encoding.UTF8.GetString(respData).TrimEnd('\0') : "";

                    if (response == Response.Progress)
                    {
                        if (!string.IsNullOrEmpty(message)) onProgress?.Invoke(message);
                        continue;
                    }
                    if (response == Response.Ok) return (true, message);
                    if (response == Response.Error) return (false, message);
                    return (false, $"Unexpected response: {response}");
                }
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Launch a game by title ID
        public async Task<(bool success, string message)> LaunchGameAsync(string titleId)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(titleId + "\0");
                await SendCommandAsync(Command.LaunchGame, data);
                var (response, respData) = await ReceiveResponseAsync();

                string message = Encoding.UTF8.GetString(respData);
                return (response == Response.Ok, message);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Delete a screenshot and its matching thumbnail
        public async Task<(bool success, string message)> DeleteScreenshotAsync(string fullPath)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(fullPath + "\0");
                await SendCommandAsync(Command.DeleteScreenshot, data);
                var (response, respData) = await ReceiveResponseAsync();
                string message = Encoding.UTF8.GetString(respData);
                return (response == Response.Ok, message);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: List all screenshots stored on the PS5
        public async Task<List<PS5Screenshot>> ListScreenshotsAsync()
        {
            var shots = new List<PS5Screenshot>();
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.ListScreenshots);
                var (response, data) = await ReceiveResponseAsync();
                if (response != Response.Data) return shots;

                string body = Encoding.UTF8.GetString(data);
                if (body.StartsWith("NO_SCREENSHOTS")) return shots;

                foreach (var line in body.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('|');
                    if (parts.Length < 4) continue;

                    long.TryParse(parts[2], out long size);
                    long.TryParse(parts[3], out long mtimeUnix);

                    shots.Add(new PS5Screenshot
                    {
                        FullPath = parts[0],
                        FileName = parts[1],
                        Size = size,
                        ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(mtimeUnix).LocalDateTime
                    });
                }
            }
            catch { }
            finally
            {
                _commandLock.Release();
            }
            return shots.OrderByDescending(s => s.ModifiedTime).ToList();
        }

        // NEW: Get icon0.png binary data for a game
        public async Task<byte[]?> GetGameIconAsync(string titleId)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(titleId + "\0");
                await SendCommandAsync(Command.GetGameIcon, data);
                var (response, respData) = await ReceiveResponseAsync();
                if (response != Response.Data || respData == null || respData.Length == 0)
                    return null;
                return respData;
            }
            catch
            {
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Get pic0.png (picType=0) or pic1.png (picType=1) binary data for a game
        public async Task<byte[]?> GetGamePicAsync(string titleId, int picType)
        {
            if (picType != 0 && picType != 1) return null;

            await _commandLock.WaitAsync();
            try
            {
                string request = $"{titleId}:{picType}";
                byte[] data = Encoding.UTF8.GetBytes(request + "\0");
                await SendCommandAsync(Command.GetGamePic, data);
                var (response, respData) = await ReceiveResponseAsync();
                if (response != Response.Data || respData == null || respData.Length == 0)
                    return null;
                return respData;
            }
            catch
            {
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // Trophy viewer — returns every registered trophy set with its raw
        // tropconf.json / tropmeta.json payloads and the per-user TRPTITLE.DAT
        // blob (empty when the user never earned anything in that set).
        public async Task<List<PS5TrophySet>> GetTrophyListAsync()
        {
            var sets = new List<PS5TrophySet>();
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.TrophyList);
                var (response, data) = await ReceiveResponseAsync(180000);
                if (response != Response.Data || data == null || data.Length < 2)
                    return sets;

                int off = 0;
                ushort count = BitConverter.ToUInt16(data, off); off += 2;

                string rdStr()
                {
                    int l = data[off]; off++;
                    string s = Encoding.UTF8.GetString(data, off, l); off += l;
                    return s;
                }
                byte[] rdBlob()
                {
                    uint l = BitConverter.ToUInt32(data, off); off += 4;
                    var b = new byte[l];
                    Array.Copy(data, off, b, 0, l); off += (int)l;
                    return b;
                }

                for (int i = 0; i < count; i++)
                {
                    var set = new PS5TrophySet
                    {
                        NpCommunicationId = rdStr(),
                        TitleId = rdStr(),
                        UserId = rdStr(),
                        TropConfJson = rdBlob(),
                        TropMetaJson = rdBlob(),
                        TrpTitleData = rdBlob()
                    };
                    sets.Add(set);
                }
            }
            catch (Exception ex)
            {
                LastError = $"GetTrophyListAsync: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                _commandLock.Release();
            }
            return sets;
        }

        // Fetch a trophy PNG ("trop0000.png", "icon0_en-US.png", …) from a set's UCP.
        public async Task<byte[]?> GetTrophyIconAsync(string npwr, string entry)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] req = Encoding.UTF8.GetBytes($"{npwr}|{entry}");
                await SendCommandAsync(Command.TrophyIcon, req);
                var (response, data) = await ReceiveResponseAsync();
                if (response != Response.Data || data == null || data.Length == 0)
                    return null;
                return data;
            }
            catch
            {
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // Trophy unlock — routed through the trophy daemon's debug API inside
        // the RUNNING game process (the daemon binds the commId from it).
        // spec: "unlock:<id|all>" or "lock:<id>". Returns daemon diagnostics.
        public async Task<(bool ok, string msg)> TrophyUnlockAsync(string spec)
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.TrophyUnlock, Encoding.UTF8.GetBytes(spec + "\0"));
                var (response, data) = await ReceiveResponseAsync(120000);
                var msg = Encoding.UTF8.GetString(data ?? Array.Empty<byte>());
                return (response == Response.Data || response == Response.Ok, msg);
            }
            catch (Exception ex)
            {
                return (false, $"TrophyUnlockAsync: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // NEW: Get detailed info about a game
        public async Task<Dictionary<string, string>?> GetGameDetailsAsync(string titleId)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(titleId + "\0");
                await SendCommandAsync(Command.GetGameDetails, data);
                var (response, respData) = await ReceiveResponseAsync();
                if (response != Response.Data) return null;

                var dict = new Dictionary<string, string>();
                string responseStr = Encoding.UTF8.GetString(respData);
                foreach (var line in responseStr.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    dict[key] = value;
                }
                return dict;
            }
            catch
            {
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<FileEntry[]> ListDirAsync(string path)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] pathBytes = Encoding.UTF8.GetBytes(path + "\0");
                await SendCommandAsync(Command.ListDir, pathBytes);
                
                // Payload sends ONLY RESP_DATA - no RESP_OK after!
                var (response, data) = await ReceiveResponseAsync();

                if (response != Response.Data)
                {
                    return Array.Empty<FileEntry>();
                }

                using var ms = new MemoryStream(data);
                using var br = new BinaryReader(ms);

                int count = br.ReadInt32();
                
                // Handle empty directory
                if (count <= 0)
                {
                    return Array.Empty<FileEntry>();
                }
                
                var result = new FileEntry[count];

                for (int i = 0; i < count; i++)
                {
                    byte type = br.ReadByte();
                    ushort nameLen = br.ReadUInt16();
                    string name = Encoding.UTF8.GetString(br.ReadBytes(nameLen));
                    long size = br.ReadInt64();
                    long timestamp = br.ReadInt64();

                    // Clamp timestamp to valid DateTimeOffset range
                    DateTime dt;
                    try
                    {
                        if (timestamp < -62135596800 || timestamp > 253402300799)
                        {
                            dt = DateTime.Now; // Use current time for invalid timestamps
                        }
                        else
                        {
                            dt = DateTimeOffset.FromUnixTimeSeconds(timestamp).DateTime;
                        }
                    }
                    catch
                    {
                        dt = DateTime.Now;
                    }

                    result[i] = new FileEntry
                    {
                        Name = name,
                        IsDirectory = type == 1,
                        Size = size,
                        Timestamp = dt
                    };
                }

                return result;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<bool> CreateDirAsync(string path)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] pathBytes = Encoding.UTF8.GetBytes(path + "\0");
                await SendCommandAsync(Command.CreateDir, pathBytes);
                var (response, _) = await ReceiveResponseAsync();
                return response == Response.Ok;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<bool> DeleteFileAsync(string path)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] pathBytes = Encoding.UTF8.GetBytes(path + "\0");
                await SendCommandAsync(Command.DeleteFile, pathBytes);
                var (response, _) = await ReceiveResponseAsync();
                return response == Response.Ok;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public event Action<string>? OnProgressMessage;

        public async Task<bool> DeleteDirAsync(string path)
        {
            await _commandLock.WaitAsync();
            try
            {
            byte[] pathBytes = Encoding.UTF8.GetBytes(path + "\0");
            await SendCommandAsync(Command.DeleteDir, pathBytes);
            
            // Payload does NOT send initial OK - goes straight to progress messages
            // Keep reading until we get the final OK/ERROR response
            bool deletionComplete = false;
            
            try
            {
                while (!deletionComplete)
                {
                    var (progressResponse, progressData) = await ReceiveResponseAsync();
                    
                    if (progressResponse == Response.Progress)
                    {
                        string message = Encoding.UTF8.GetString(progressData).TrimEnd('\0');
                        OnProgressMessage?.Invoke(message);
                    }
                    else if (progressResponse == Response.Ok)
                    {
                        // Final OK response received - deletion complete
                        OnProgressMessage?.Invoke("🔚 Received final OK - deletion complete");
                        deletionComplete = true;
                    }
                    else if (progressResponse == Response.Error)
                    {
                        // Error response received
                        OnProgressMessage?.Invoke("❌ Received error response");
                        deletionComplete = true;
                    }
                    else
                    {
                        // Unexpected response - log it
                        OnProgressMessage?.Invoke($"⚠️ Unexpected response: {progressResponse}");
                        deletionComplete = true;
                    }
                }
            }
            catch (Exception ex)
            {
                // Connection closed - log it
                OnProgressMessage?.Invoke($"⚠️ Connection closed: {ex.Message}");
            }
            
            // Give server a moment to fully close the deletion thread
            // CRITICAL: Increased delay to prevent race condition with next delete
            await Task.Delay(500);
            
            return true;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<bool> RenameAsync(string oldPath, string newPath)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] oldBytes = Encoding.UTF8.GetBytes(oldPath + "\0");
                byte[] newBytes = Encoding.UTF8.GetBytes(newPath + "\0");
                byte[] data = new byte[oldBytes.Length + newBytes.Length];
                Array.Copy(oldBytes, 0, data, 0, oldBytes.Length);
                Array.Copy(newBytes, 0, data, oldBytes.Length, newBytes.Length);
                
                await SendCommandAsync(Command.Rename, data);
                var (response, _) = await ReceiveResponseAsync();
                return response == Response.Ok;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<bool> CopyFileAsync(string srcPath, string dstPath)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] srcBytes = Encoding.UTF8.GetBytes(srcPath + "\0");
                byte[] dstBytes = Encoding.UTF8.GetBytes(dstPath + "\0");
                byte[] data = new byte[srcBytes.Length + dstBytes.Length];
                Array.Copy(srcBytes, 0, data, 0, srcBytes.Length);
                Array.Copy(dstBytes, 0, data, srcBytes.Length, dstBytes.Length);
                
                await SendCommandAsync(Command.CopyFile, data);
                var (response, _) = await ReceiveResponseAsync();
                return response == Response.Ok;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<bool> MoveFileAsync(string srcPath, string dstPath)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] srcBytes = Encoding.UTF8.GetBytes(srcPath + "\0");
                byte[] dstBytes = Encoding.UTF8.GetBytes(dstPath + "\0");
                byte[] data = new byte[srcBytes.Length + dstBytes.Length];
                Array.Copy(srcBytes, 0, data, 0, srcBytes.Length);
                Array.Copy(dstBytes, 0, data, srcBytes.Length, dstBytes.Length);
                
                await SendCommandAsync(Command.MoveFile, data);
                var (response, _) = await ReceiveResponseAsync();
                return response == Response.Ok;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<bool> UploadFileAsync(string localPath, string remotePath, IProgress<UploadProgress>? progress = null, CancellationToken cancellationToken = default, long chunkOffset = 0, long chunkSize = 0, Action? onReadyCallback = null)
        {
            // Hold the command lock for the ENTIRE transfer. Without this,
            // concurrent commands (fan/poll/status) interleave their frames
            // into the chunk byte stream, the payload reads a corrupted
            // data_len and hard-closes the socket mid-upload.
            await _commandLock.WaitAsync(cancellationToken);
            try
            {
                return await UploadFileCoreAsync(localPath, remotePath, progress, cancellationToken, chunkOffset, chunkSize, onReadyCallback);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        private async Task<bool> UploadFileCoreAsync(string localPath, string remotePath, IProgress<UploadProgress>? progress, CancellationToken cancellationToken, long chunkOffset, long chunkSize, Action? onReadyCallback)
        {
            if (!LocalIo.FileExists(localPath)) return false;
            long fileLen = LocalIo.GetLength(localPath);

            // Determine actual upload size
            long uploadSize = chunkSize > 0 ? chunkSize : fileLen;

            // Send START_UPLOAD with optional chunk offset
            byte[] pathBytes = Encoding.UTF8.GetBytes(remotePath);
            byte[] startData = new byte[pathBytes.Length + 1 + 8 + 8]; // path + null + size + offset
            Array.Copy(pathBytes, 0, startData, 0, pathBytes.Length);
            BitConverter.GetBytes(fileLen).CopyTo(startData, pathBytes.Length + 1);
            BitConverter.GetBytes(chunkOffset).CopyTo(startData, pathBytes.Length + 9);

            await SendCommandAsync(Command.StartUpload, startData);
            var (response, responseData) = await ReceiveResponseAsync();

            // Debug logging
            OnProgressMessage?.Invoke($"[DEBUG] START_UPLOAD Response: {response} (Expected: {Response.Ready})");
            
            if (response != Response.Ready)
            {
                string serverMsg = responseData.Length > 0 ? Encoding.UTF8.GetString(responseData).TrimEnd('\0') : "";
                LastError = string.IsNullOrEmpty(serverMsg) ? $"START_UPLOAD rejected (response=0x{(byte)response:X2})" : serverMsg;
                OnProgressMessage?.Invoke($"[DEBUG] Upload failed - response=0x{(byte)response:X2} msg={serverMsg}");
                onReadyCallback?.Invoke(); // Signal even on failure so waiters don't deadlock
                return false;
            }
            
            // Signal that file is created/pre-allocated on PS5 (for parallel chunk synchronization)
            onReadyCallback?.Invoke();

            // Upload chunks - simple async approach
            long totalSent = 0;
            var startTime = DateTime.Now;
            double avgSpeed = 0;

            byte[][] sendBuffers = new byte[2][];
            sendBuffers[0] = ArrayPool<byte>.Shared.Rent(5 + BufferSize);
            sendBuffers[1] = ArrayPool<byte>.Shared.Rent(5 + BufferSize);
            
            try
            {
                // RandomAccess for chunked uploads to avoid disk I/O contention
                // when multiple workers read the same file. UNC paths go
                // through the NAS session via a seekable SMB stream.
                using (Stream fs = LocalIo.OpenRead(localPath, randomAccess: chunkOffset > 0))
                {
                    if (chunkOffset > 0)
                    {
                        fs.Seek(chunkOffset, SeekOrigin.Begin);
                    }
                    
                    int activeBufferIndex = 0;
                    long bytesRemaining = uploadSize;
                    int bytesRead = await fs.ReadAsync(sendBuffers[activeBufferIndex], 5, (int)Math.Min(BufferSize, bytesRemaining), cancellationToken);
                    int chunksSent = 0;
                    
                    while (bytesRemaining > 0 && bytesRead > 0)
                    {
                        if (_stream == null) return false;
                        if (cancellationToken.IsCancellationRequested) return false;
                        
                        byte[] writeBuffer = sendBuffers[activeBufferIndex];
                        writeBuffer[0] = (byte)Command.UploadChunk;
                        BitConverter.GetBytes((uint)bytesRead).CopyTo(writeBuffer, 1);
                        
                        long remainingAfterCurrent = bytesRemaining - bytesRead;
                        int nextBufferIndex = 1 - activeBufferIndex;
                        Task<int>? pendingReadTask = null;
                        if (remainingAfterCurrent > 0)
                        {
                            int nextReadLength = (int)Math.Min(BufferSize, remainingAfterCurrent);
                            pendingReadTask = fs.ReadAsync(sendBuffers[nextBufferIndex], 5, nextReadLength, cancellationToken);
                        }
                        
                        // FIX #6: Reduced timeout from 15 min to 3 min for faster failure detection
                        using var writeTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, writeTimeout.Token);
                        
                        try
                        {
                            await WriteSocketChunkedAsync(_stream, writeBuffer, 0, 5 + bytesRead, linkedCts.Token);
                        }
                        catch (OperationCanceledException) when (writeTimeout.IsCancellationRequested)
                        {
                            if (pendingReadTask != null)
                            {
                                try { await pendingReadTask; } catch { }
                            }
                            return false;
                        }
                        
                        totalSent += bytesRead;
                        bytesRemaining -= bytesRead;
                        chunksSent++;
                        
                        if (chunksSent % 5 == 0 || bytesRemaining == 0)
                        {
                            var elapsed = DateTime.Now - startTime;
                            double currentSpeed = elapsed.TotalSeconds > 0 ? totalSent / elapsed.TotalSeconds : 0;
                            avgSpeed = currentSpeed;
                            
                            TimeSpan eta = currentSpeed > 0
                                ? TimeSpan.FromSeconds(bytesRemaining / currentSpeed)
                                : TimeSpan.Zero;

                            progress?.Report(new UploadProgress
                            {
                                BytesSent = chunkOffset + totalSent,
                                TotalBytes = fileLen,
                                SpeedBytesPerSecond = currentSpeed,
                                AverageSpeedBytesPerSecond = avgSpeed,
                                ElapsedTime = elapsed,
                                EstimatedTimeRemaining = eta,
                                CurrentFileName = LocalIo.GetName(localPath)
                            });
                        }
                        
                        if (pendingReadTask == null)
                        {
                            break;
                        }
                        
                        activeBufferIndex = 1 - activeBufferIndex;
                        bytesRead = await pendingReadTask;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(sendBuffers[0]);
                ArrayPool<byte>.Shared.Return(sendBuffers[1]);
            }

            // Send END_UPLOAD and wait for response
            // CRITICAL: Must wait for response to avoid protocol desync on chunked uploads
            try
            {
                await SendCommandAsync(Command.EndUpload);
                var (endResponse, endData) = await ReceiveResponseAsync();
                if (endResponse != Response.Ok)
                {
                    string endMsg = endData.Length > 0 ? Encoding.UTF8.GetString(endData).TrimEnd('\0') : "";
                    LastError = string.IsNullOrEmpty(endMsg) ? $"END_UPLOAD failed (response=0x{(byte)endResponse:X2})" : endMsg;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"END_UPLOAD: {ex.Message}";
                return false; // Connection issue means upload failed
            }
        }

        public async Task<bool> DownloadFileAsync(string remotePath, string localPath, IProgress<UploadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            await _commandLock.WaitAsync();
            try
            {
                // Send download command (must include null terminator for C strlen())
                byte[] pathBytes = Encoding.UTF8.GetBytes(remotePath + "\0");
                await SendCommandAsync(Command.DownloadFile, pathBytes);
                
                // Receive response header (5 bytes: 1 response + 4 data_len)
                byte[] header = new byte[5];
                await ReadExactAsync(header, 5);
                
                Response response = (Response)header[0];
                uint dataLen = BitConverter.ToUInt32(header, 1);
                
                // Check if error response
                if (response == Response.Error)
                {
                    if (dataLen > 0)
                    {
                        byte[] errorMsg = new byte[dataLen];
                        await ReadExactAsync(errorMsg, (int)dataLen);
                    }
                    return false;
                }
                
                // Expecting RESP_DATA with 8-byte file size
                if (response != Response.Data || dataLen != 8)
                {
                    return false;
                }
                
                // Read file size (8 bytes)
                byte[] sizeBytes = new byte[8];
                await ReadExactAsync(sizeBytes, 8);
                long fileSize = BitConverter.ToInt64(sizeBytes, 0);
                
                // Now read raw file data directly from socket
                using var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
                
                byte[] buffer = new byte[BufferSize];
                long totalReceived = 0;
                var startTime = DateTime.Now;
                
                while (totalReceived < fileSize)
                {
                    if (cancellationToken.IsCancellationRequested) return false;
                    
                    int toRead = (int)Math.Min(BufferSize, fileSize - totalReceived);
                    int received = await _stream!.ReadAsync(buffer, 0, toRead, cancellationToken);
                    
                    if (received == 0) break;
                    
                    await fs.WriteAsync(buffer, 0, received, cancellationToken);
                    totalReceived += received;
                    
                    // Report progress every 5MB or at completion
                    if (totalReceived % (5 * 1024 * 1024) < BufferSize || totalReceived == fileSize)
                    {
                        var elapsed = DateTime.Now - startTime;
                        double speed = elapsed.TotalSeconds > 0 ? totalReceived / elapsed.TotalSeconds : 0;
                        
                        progress?.Report(new UploadProgress
                        {
                            BytesSent = totalReceived,
                            TotalBytes = fileSize,
                            SpeedBytesPerSecond = speed,
                            AverageSpeedBytesPerSecond = speed,
                            ElapsedTime = elapsed,
                            EstimatedTimeRemaining = speed > 0 ? TimeSpan.FromSeconds((fileSize - totalReceived) / speed) : TimeSpan.Zero,
                            CurrentFileName = Path.GetFileName(localPath)
                        });
                    }
                }
                
                return totalReceived == fileSize;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Download error: {ex.Message}");
                return false;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<bool> OpenShellAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.ShellOpen);
                var (response, _) = await ReceiveResponseAsync();
                return response == Response.Ok;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<string> ExecuteShellCommandAsync(string command)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] cmdBytes = Encoding.UTF8.GetBytes(command + "\0");
                await SendCommandAsync(Command.ShellExec, cmdBytes);
                
                // Payload sends multiple RESP_DATA responses (e.g., ls sends one per file)
                // Read all RESP_DATA until we get RESP_OK or RESP_ERROR
                var outputBuilder = new System.Text.StringBuilder();
                
                while (true)
                {
                    var (response, data) = await ReceiveResponseAsync();
                    
                    if (response == Response.Data)
                    {
                        // Accumulate output from multiple RESP_DATA responses
                        string chunk = Encoding.UTF8.GetString(data).TrimEnd('\0');
                        outputBuilder.Append(chunk);
                    }
                    else if (response == Response.Ok)
                    {
                        // End of output - return accumulated data
                        return outputBuilder.ToString();
                    }
                    else if (response == Response.Error)
                    {
                        string error = data.Length > 0 ? Encoding.UTF8.GetString(data).TrimEnd('\0') : "Command failed";
                        return $"Error: {error}";
                    }
                    else
                    {
                        // Unexpected response
                        break;
                    }
                }
                
                return outputBuilder.ToString();
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<bool> CloseShellAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.ShellClose);
                var (response, _) = await ReceiveResponseAsync();
                return response == Response.Ok;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // Index methods
        public async Task<bool> StartIndexAsync(string paths)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] pathBytes = Encoding.UTF8.GetBytes(paths + "\0");
                await SendCommandAsync(Command.IndexStart, pathBytes);
                var (response, data) = await ReceiveResponseAsync();
                return response == Response.Ok;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<string> GetIndexStatusAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.IndexStatus);
                var (response, data) = await ReceiveResponseAsync();
                if (response == Response.Ok && data.Length > 0)
                {
                    return Encoding.UTF8.GetString(data).TrimEnd('\0');
                }
                return "Unknown";
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async Task<SearchResult[]> SearchIndexAsync(string query)
        {
            await _commandLock.WaitAsync();
            try
            {
            byte[] queryBytes = Encoding.UTF8.GetBytes(query + "\0");
            await SendCommandAsync(Command.SearchIndex, queryBytes);
            
            var results = new System.Collections.Generic.List<SearchResult>();
            
            // Payload sends: RESP_DATA(1) + raw_data for each result, then RESP_OK(1) + data_len(4) + message
            if (_stream == null) return Array.Empty<SearchResult>();
            
            while (true)
            {
                // Read response byte
                byte[] respBuf = new byte[1];
                try { await ReadExactAsync(respBuf, 1); }
                catch { break; }
                
                Response response = (Response)respBuf[0];
                
                if (response == Response.Data)
                {
                    // Read raw data: path_len(4) + path + name_len(4) + name + size(8) + mtime(8) + is_dir(1)
                    byte[] pathLenBuf = new byte[4];
                    await ReadExactAsync(pathLenBuf, 4);
                    uint pathLen = BitConverter.ToUInt32(pathLenBuf, 0);
                    
                    byte[] pathBuf = new byte[pathLen];
                    await ReadExactAsync(pathBuf, (int)pathLen);
                    string path = Encoding.UTF8.GetString(pathBuf);
                    
                    byte[] nameLenBuf = new byte[4];
                    await ReadExactAsync(nameLenBuf, 4);
                    uint nameLen = BitConverter.ToUInt32(nameLenBuf, 0);
                    
                    byte[] nameBuf = new byte[nameLen];
                    await ReadExactAsync(nameBuf, (int)nameLen);
                    string name = Encoding.UTF8.GetString(nameBuf);
                    
                    byte[] sizeBuf = new byte[8];
                    await ReadExactAsync(sizeBuf, 8);
                    long size = BitConverter.ToInt64(sizeBuf, 0);
                    
                    byte[] mtimeBuf = new byte[8];
                    await ReadExactAsync(mtimeBuf, 8);
                    long mtime = BitConverter.ToInt64(mtimeBuf, 0);
                    
                    byte[] isDirBuf = new byte[1];
                    await ReadExactAsync(isDirBuf, 1);
                    bool isDir = isDirBuf[0] == 1;
                    
                    results.Add(new SearchResult
                    {
                        Path = path,
                        Name = name,
                        Size = size,
                        Modified = DateTimeOffset.FromUnixTimeSeconds(mtime).DateTime,
                        IsDirectory = isDir
                    });
                }
                else if (response == Response.Ok)
                {
                    // Read and discard the OK message
                    byte[] dataLenBuf = new byte[4];
                    await ReadExactAsync(dataLenBuf, 4);
                    uint dataLen = BitConverter.ToUInt32(dataLenBuf, 0);
                    if (dataLen > 0)
                    {
                        byte[] msgBuf = new byte[dataLen];
                        await ReadExactAsync(msgBuf, (int)dataLen);
                    }
                    break;
                }
                else if (response == Response.Error)
                {
                    // Read error message
                    byte[] dataLenBuf = new byte[4];
                    await ReadExactAsync(dataLenBuf, 4);
                    uint dataLen = BitConverter.ToUInt32(dataLenBuf, 0);
                    if (dataLen > 0)
                    {
                        byte[] msgBuf = new byte[dataLen];
                        await ReadExactAsync(msgBuf, (int)dataLen);
                    }
                    break;
                }
            }
            
            return results.ToArray();
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // Mount all games on PS5 (scans /data/etaHEN/games, USB drives, M.2 SSD)
        // Response pattern: stream of RESP_PROGRESS lines (one per game) followed
        // by a RESP_OK with the summary text.
        public async Task<string?> MountGamesAsync(CancellationToken cancellationToken = default, Action<string>? onProgress = null)
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.MountGames);
                
                // Read the streamed progress lines, then the final OK/ERROR.
                while (true)
                {
                    var (response, data) = await ReceiveResponseAsync();
                    
                    if (response == Response.Progress)
                    {
                        string msg = Encoding.UTF8.GetString(data).TrimEnd('\0');
                        if (!string.IsNullOrEmpty(msg))
                            onProgress?.Invoke(msg);
                    }
                    else if (response == Response.Ok)
                    {
                        return data != null ? Encoding.UTF8.GetString(data) : "";
                    }
                    else if (response == Response.Error)
                    {
                        return "ERROR: " + (data != null ? Encoding.UTF8.GetString(data) : "unknown");
                    }
                    else
                    {
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] MountGamesAsync: {ex.Message}");
                return null;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>Mount a single game by title id (same worker pipeline, filtered).</summary>
        public async Task<string?> MountGameAsync(string titleId, Action<string>? onProgress = null)
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.MountGame, Encoding.UTF8.GetBytes(titleId));
                while (true)
                {
                    var (response, data) = await ReceiveResponseAsync();
                    if (response == Response.Progress)
                    {
                        string msg = Encoding.UTF8.GetString(data).TrimEnd('\0');
                        if (!string.IsNullOrEmpty(msg)) onProgress?.Invoke(msg);
                    }
                    else if (response == Response.Ok) return Encoding.UTF8.GetString(data);
                    else if (response == Response.Error) return "ERROR: " + Encoding.UTF8.GetString(data);
                    else return null;
                }
            }
            catch (Exception ex) { LastError = $"MountGameAsync: {ex.Message}"; return null; }
            finally { _commandLock.Release(); }
        }

        // Send payload ELF file to PS5 (port 9021)
        public static async Task<bool> SendPayloadAsync(string ipAddress, string payloadPath, int port = 9021, IProgress<long>? progress = null)
        {
            TcpClient? client = null;
            try
            {
                FileInfo fileInfo = new FileInfo(payloadPath);
                if (!fileInfo.Exists) return false;

                // Validate file extension
                string ext = fileInfo.Extension.ToLower();
                if (ext != ".elf" && ext != ".bin")
                {
                    return false;
                }

                client = new TcpClient();
                client.SendBufferSize = 8 * 1024 * 1024;
                client.ReceiveBufferSize = 8 * 1024 * 1024;
                client.NoDelay = true;
                client.SendTimeout = 10000;

                // Connect with 10 second timeout
                var connectTask = client.ConnectAsync(ipAddress, port);
                var timeoutTask = Task.Delay(10000);
                var completedTask = await Task.WhenAny(connectTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    client?.Close();
                    return false;
                }

                // Wait for connect task to complete
                await connectTask;

                if (!client.Connected)
                {
                    return false;
                }

                using var stream = client.GetStream();
                using var fs = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read);

                // Use larger buffer for faster transfer
                byte[] buffer = new byte[64 * 1024];
                long totalSent = 0;
                int bytesRead;

                while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await stream.WriteAsync(buffer, 0, bytesRead);
                    totalSent += bytesRead;
                    progress?.Report(totalSent);
                }

                await stream.FlushAsync();
                
                // Give PS5 time to process and execute payload
                await Task.Delay(500);
                
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                client?.Close();
            }
        }

        // Ask the running payload to replace itself: it pushes the staged ELF
        // (or an http:// download) to the local payload loader, which spawns
        // the new instance — the old one dies via the normal stale-sweep
        // takeover and this connection drops.
        public async Task<(bool success, string message)> SelfUpdateAsync(string pathOrUrl, int loaderPort = 9021)
        {
            await _commandLock.WaitAsync();
            try
            {
                string arg = $"{pathOrUrl}|{loaderPort}";
                await SendCommandAsync(Command.SelfUpdate, Encoding.UTF8.GetBytes(arg));
                var (response, data) = await ReceiveResponseAsync();
                return (response == Response.Ok, Encoding.UTF8.GetString(data));
            }
            catch (Exception ex)
            {
                LastError = $"SelfUpdateAsync: {ex.GetType().Name}: {ex.Message}";
                return (false, LastError);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // ============================================================
        // MEMORY EDITOR — ptrace/PT_IO on arbitrary PS5 processes
        // ============================================================

        public async Task<List<(int pid, string name)>> GetProcessListAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.ProcList);
                var (response, data) = await ReceiveResponseAsync();
                var list = new List<(int, string)>();
                if (response == Response.Data)
                {
                    foreach (var line in Encoding.UTF8.GetString(data).Split('\n'))
                    {
                        var p = line.Split('|', 2);
                        if (p.Length == 2 && int.TryParse(p[0], out int pid))
                            list.Add((pid, p[1].Trim()));
                    }
                }
                return list;
            }
            catch (Exception ex) { LastError = $"GetProcessListAsync: {ex.Message}"; return new(); }
            finally { _commandLock.Release(); }
        }

        public async Task<byte[]?> MemReadAsync(int pid, ulong addr, int len)
        {
            await _commandLock.WaitAsync();
            try
            {
                string arg = $"{pid}|0x{addr:X}|{len}";
                await SendCommandAsync(Command.MemRead, Encoding.UTF8.GetBytes(arg));
                var (response, data) = await ReceiveResponseAsync();
                if (response != Response.Data) { LastError = Encoding.UTF8.GetString(data); return null; }
                return data;
            }
            catch (Exception ex) { LastError = $"MemReadAsync: {ex.Message}"; return null; }
            finally { _commandLock.Release(); }
        }

        public async Task<(bool ok, string message)> MemWriteAsync(int pid, ulong addr, byte[] bytes)
        {
            await _commandLock.WaitAsync();
            try
            {
                string arg = $"{pid}|0x{addr:X}|{Convert.ToHexString(bytes)}";
                await SendCommandAsync(Command.MemWrite, Encoding.UTF8.GetBytes(arg));
                var (response, data) = await ReceiveResponseAsync();
                return (response == Response.Ok, Encoding.UTF8.GetString(data));
            }
            catch (Exception ex) { LastError = $"MemWriteAsync: {ex.Message}"; return (false, LastError); }
            finally { _commandLock.Release(); }
        }

        public async Task<string?> MemRegionsAsync(int pid)
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.MemRegions, Encoding.UTF8.GetBytes(pid.ToString()));
                var (response, data) = await ReceiveResponseAsync();
                if (response != Response.Data) { LastError = Encoding.UTF8.GetString(data); return null; }
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception ex) { LastError = $"MemRegionsAsync: {ex.Message}"; return null; }
            finally { _commandLock.Release(); }
        }

        public async Task<string?> MemSearchAsync(int pid, ulong start, ulong end, byte[] pattern)
        {
            await _commandLock.WaitAsync();
            try
            {
                string arg = $"{pid}|0x{start:X}|0x{end:X}|{Convert.ToHexString(pattern)}";
                await SendCommandAsync(Command.MemSearch, Encoding.UTF8.GetBytes(arg));
                var (response, data) = await ReceiveResponseAsync(60000);   // scans can take up to ~30s
                if (response != Response.Data) { LastError = Encoding.UTF8.GetString(data); return null; }
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception ex) { LastError = $"MemSearchAsync: {ex.Message}"; return null; }
            finally { _commandLock.Release(); }
        }

        // ================= App Manager v2 =================

        /// <summary>pid|appid|title|comm|apptype|cpu_x100|suspended per line (+ appinfo=ok line)</summary>
        public async Task<string?> AppListV2Async()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.AppListV2);
                var (response, data) = await ReceiveResponseAsync(30000);
                if (response != Response.Data) { LastError = Encoding.UTF8.GetString(data); return null; }
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception ex) { LastError = $"AppListV2Async: {ex.Message}"; return null; }
            finally { _commandLock.Release(); }
        }

        private async Task<(bool ok, string msg)> AppActionAsync(Command cmd, int appId, int pid = 0)
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(cmd, Encoding.UTF8.GetBytes($"{appId}|{pid}"));
                var (response, data) = await ReceiveResponseAsync(30000);
                return (response == Response.Ok, Encoding.UTF8.GetString(data));
            }
            catch (Exception ex) { LastError = $"AppAction: {ex.Message}"; return (false, LastError); }
            finally { _commandLock.Release(); }
        }

        public Task<(bool ok, string msg)> AppSuspendAsync(int appId, int pid)  => AppActionAsync(Command.AppSuspend, appId, pid);
        public Task<(bool ok, string msg)> AppResumeAsync(int appId, int pid)   => AppActionAsync(Command.AppResume, appId, pid);
        public Task<(bool ok, string msg)> AppKillAsync(int appId, int pid)     => AppActionAsync(Command.AppKill, appId, pid);
        public Task<(bool ok, string msg)> AppCoredumpAsync(int appId)          => AppActionAsync(Command.AppCoredump, appId);

        /// <summary>
        /// Network info: key=value lines plus per-interface byte counters
        /// ("if=name|rx=N|tx=N"). Returns the raw text for the caller to parse.
        /// </summary>
        public async Task<string?> GetNetInfoAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.NetInfo);
                var (response, data) = await ReceiveResponseAsync();
                if (response != Response.Data) { LastError = Encoding.UTF8.GetString(data); return null; }
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception ex) { LastError = $"GetNetInfoAsync: {ex.Message}"; return null; }
            finally { _commandLock.Release(); }
        }

        /// <summary>
        /// LAN link speed test: payload streams 16MB; returns receive throughput in Mbps.
        /// </summary>
        public async Task<double?> NetSpeedTestAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await SendCommandAsync(Command.NetSpeedTest);
                var (response, data) = await ReceiveResponseAsync();
                sw.Stop();
                if (response != Response.Data || data.Length == 0)
                {
                    LastError = data.Length > 0 ? Encoding.UTF8.GetString(data) : "no data";
                    return null;
                }
                double mbps = data.Length * 8.0 / sw.Elapsed.TotalSeconds / 1_000_000.0;
                return mbps;
            }
            catch (Exception ex) { LastError = $"NetSpeedTestAsync: {ex.Message}"; return null; }
            finally { _commandLock.Release(); }
        }

        /// <summary>
        /// Read the kernel message buffer (kern.msgbuf). tailBytes &gt; 0 limits to the tail.
        /// </summary>
        public async Task<string?> GetKernelLogAsync(int tailBytes = 0)
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.KlogRead, Encoding.UTF8.GetBytes(tailBytes.ToString()));
                var (response, data) = await ReceiveResponseAsync();
                if (response != Response.Data) { LastError = Encoding.UTF8.GetString(data); return null; }
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception ex) { LastError = $"GetKernelLogAsync: {ex.Message}"; return null; }
            finally { _commandLock.Release(); }
        }

        /// <summary>
        /// Recursively download an entire folder from PS5 to local PC.
        /// Uses a separate connection for listing to avoid protocol desync.
        /// </summary>
        public async Task<(int filesDownloaded, int filesFailed, long totalBytes)> DownloadFolderAsync(
            string remotePath,
            string localBasePath,
            string ps5IpAddress,
            IProgress<DownloadFolderProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            // Phase 1: Scan the remote folder tree to build a file list
            var filesToDownload = new System.Collections.Generic.List<(string remotePath, string localPath, long size)>();
            var dirsToCreate = new System.Collections.Generic.List<string>();

            await ScanRemoteFolderAsync(remotePath, localBasePath, remotePath, filesToDownload, dirsToCreate, ps5IpAddress, progress, cancellationToken);

            if (cancellationToken.IsCancellationRequested) return (0, 0, 0);

            // Phase 2: Create all local directories
            foreach (var dir in dirsToCreate)
            {
                Directory.CreateDirectory(dir);
            }

            long totalBytes = 0;
            foreach (var f in filesToDownload) totalBytes += f.size;

            progress?.Report(new DownloadFolderProgress
            {
                Phase = "Downloading",
                CurrentFile = "",
                FilesCompleted = 0,
                TotalFiles = filesToDownload.Count,
                BytesDownloaded = 0,
                TotalBytes = totalBytes
            });

            // Phase 3: Download each file using a dedicated connection per file
            int filesDownloaded = 0;
            int filesFailed = 0;
            long bytesDownloaded = 0;

            for (int i = 0; i < filesToDownload.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested) break;

                var (rPath, lPath, fSize) = filesToDownload[i];
                string fileName = Path.GetFileName(lPath);

                progress?.Report(new DownloadFolderProgress
                {
                    Phase = "Downloading",
                    CurrentFile = fileName,
                    FilesCompleted = filesDownloaded,
                    TotalFiles = filesToDownload.Count,
                    BytesDownloaded = bytesDownloaded,
                    TotalBytes = totalBytes
                });

                // Use a fresh connection for each file to avoid protocol desync
                using var dlProto = new PS5Protocol();
                if (!await dlProto.ConnectAsync(ps5IpAddress))
                {
                    filesFailed++;
                    continue;
                }

                try
                {
                    bool ok = await dlProto.DownloadFileAsync(rPath, lPath, null, cancellationToken);
                    if (ok)
                    {
                        filesDownloaded++;
                        bytesDownloaded += fSize;
                    }
                    else
                    {
                        filesFailed++;
                    }
                }
                catch
                {
                    filesFailed++;
                }
            }

            progress?.Report(new DownloadFolderProgress
            {
                Phase = "Complete",
                CurrentFile = "",
                FilesCompleted = filesDownloaded,
                TotalFiles = filesToDownload.Count,
                BytesDownloaded = bytesDownloaded,
                TotalBytes = totalBytes
            });

            return (filesDownloaded, filesFailed, bytesDownloaded);
        }

        private async Task ScanRemoteFolderAsync(
            string currentRemotePath,
            string localBasePath,
            string remoteRootPath,
            System.Collections.Generic.List<(string remotePath, string localPath, long size)> files,
            System.Collections.Generic.List<string> dirs,
            string ps5IpAddress,
            IProgress<DownloadFolderProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return;

            // Calculate relative path for local directory
            string relativePath = currentRemotePath.Length > remoteRootPath.Length
                ? currentRemotePath.Substring(remoteRootPath.Length).TrimStart('/')
                : "";
            string localDir = string.IsNullOrEmpty(relativePath)
                ? localBasePath
                : Path.Combine(localBasePath, relativePath.Replace('/', Path.DirectorySeparatorChar));

            dirs.Add(localDir);

            progress?.Report(new DownloadFolderProgress
            {
                Phase = "Scanning",
                CurrentFile = currentRemotePath,
                FilesCompleted = files.Count,
                TotalFiles = 0,
                BytesDownloaded = 0,
                TotalBytes = 0
            });

            // Use a fresh connection for listing to avoid protocol desync
            FileEntry[] entries;
            using (var listProto = new PS5Protocol())
            {
                if (!await listProto.ConnectAsync(ps5IpAddress))
                    return;

                entries = await listProto.ListDirAsync(currentRemotePath);
            }

            foreach (var entry in entries)
            {
                if (cancellationToken.IsCancellationRequested) return;
                if (entry.Name == "." || entry.Name == "..") continue;

                string entryRemotePath = currentRemotePath.TrimEnd('/') + "/" + entry.Name;
                string entryLocalPath = Path.Combine(localDir, entry.Name);

                if (entry.IsDirectory)
                {
                    await ScanRemoteFolderAsync(entryRemotePath, localBasePath, remoteRootPath, files, dirs, ps5IpAddress, progress, cancellationToken);
                }
                else
                {
                    files.Add((entryRemotePath, entryLocalPath, entry.Size));
                }
            }
        }

        // ============================================================================
        // PKG Install Methods (ps5upload-style)
        // ============================================================================
        
        /// <summary>
        /// Install a PKG from a local path on PS5 or HTTP URL
        /// </summary>
        /// <param name="pkgPath">Path to PKG (e.g., /user/data/pkg/game.pkg) or HTTP URL</param>
        /// <returns>Tuple with success flag, content_id on success, or error message</returns>
        public async Task<(bool success, string contentIdOrError)> InstallPkgAsync(string pkgPath)
        {
            await _commandLock.WaitAsync();
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(pkgPath + "\0");
                await SendCommandAsync(Command.PkgInstall, data);

                // The payload may emit RESP_PROGRESS diagnostics before the
                // final answer — consume them so they don't desync the read.
                var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
                string result = "";
                while (true)
                {
                    int remainingMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (remainingMs <= 0) return (false, "Install timed out");
                    var (response, respData) = await ReceiveResponseAsync(remainingMs);
                    result = respData != null ? Encoding.UTF8.GetString(respData).TrimEnd('\0') : "";
                    if (response == Response.Progress) { Console.WriteLine($"[PKG] {result}"); continue; }
                    break;
                }
                
                if (result.StartsWith("OK:"))
                {
                    return (true, result.Substring(3)); // Return content_id
                }
                else if (result == "STARTED")
                {
                    // Worker model: install runs detached; poll 0x51 for state
                    return (true, "");
                }
                else if (result.StartsWith("ERROR:"))
                {
                    return (false, result.Substring(6));
                }
                else
                {
                    return (false, result);
                }
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>
        /// Get PKG install status
        /// </summary>
        /// <returns>Status: IDLE, PROGRESS:status:percent, DONE:content_id, ERROR:code, or UNKNOWN</returns>
        public async Task<PkgInstallStatus> GetPkgInstallStatusAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.PkgInstallStatus);
                var (response, respData) = await ReceiveResponseAsync();

                string result = Encoding.UTF8.GetString(respData).TrimEnd('\0');
                
                var status = new PkgInstallStatus();
                
                if (result == "IDLE")
                {
                    status.State = PkgInstallState.Idle;
                }
                else if (result == "UNKNOWN")
                {
                    status.State = PkgInstallState.Unknown;
                }
                else if (result.StartsWith("PROGRESS:"))
                {
                    status.State = PkgInstallState.InProgress;
                    var parts = result.Substring(9).Split(':');
                    if (parts.Length >= 2)
                    {
                        // First field may be an int (legacy) or the daemon's
                        // status string ("downloading", "promoting", ...)
                        if (int.TryParse(parts[0], out int state))
                            status.StatusCode = state;
                        else
                            status.StatusText2 = parts[0];
                        int.TryParse(parts[1], out int progress);
                        status.Progress = progress;
                    }
                }
                else if (result.StartsWith("DONE:"))
                {
                    status.State = PkgInstallState.Done;
                    status.ContentId = result.Substring(5);
                }
                else if (result.StartsWith("ERROR:"))
                {
                    status.State = PkgInstallState.Error;
                    status.Error = result.Substring(6);
                }
                
                return status;
            }
            catch (Exception ex)
            {
                return new PkgInstallStatus { State = PkgInstallState.Error, Error = ex.Message };
            }
            finally
            {
                _commandLock.Release();
            }
        }

        // ============================================================================
        // Fan Control Methods (etaHEN/Elf Arsenal compatible)
        // ============================================================================
        
        /// <summary>
        /// Get current fan temperature threshold
        /// </summary>
        /// <returns>Tuple with success flag and threshold in Celsius (or error message)</returns>
        public async Task<(bool success, int thresholdOrError)> GetFanThresholdAsync()
        {
            await _commandLock.WaitAsync();
            try
            {
                await SendCommandAsync(Command.FanGetThreshold);
                var (response, respData) = await ReceiveResponseAsync();

                string result = Encoding.UTF8.GetString(respData).TrimEnd('\0');
                
                if (result.StartsWith("OK:"))
                {
                    // Format: "OK:<threshold|-1>:<duty0..5>" — threshold is the
                    // last value WE set (the driver has no read-back ioctl);
                    // -1 means unknown/system-managed.
                    string first = result.Substring(3).Split(':')[0];
                    if (int.TryParse(first, out int threshold))
                    {
                        return (true, threshold);
                    }
                    return (false, -1);
                }
                else if (result.StartsWith("ERROR:"))
                {
                    return (false, -1);
                }
                
                return (false, -1);
            }
            catch
            {
                return (false, -1);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>
        /// Set fan temperature threshold (30-90°C)
        /// </summary>
        /// <param name="temperatureCelsius">Temperature threshold in Celsius (30-90)</param>
        /// <returns>Tuple with success flag and actual set temperature (or error)</returns>
        public async Task<(bool success, string message)> SetFanThresholdAsync(int temperatureCelsius)
        {
            await _commandLock.WaitAsync();
            try
            {
                // Clamp to valid range
                temperatureCelsius = Math.Max(30, Math.Min(90, temperatureCelsius));
                
                byte[] data = new byte[] { (byte)temperatureCelsius };
                await SendCommandAsync(Command.FanSetThreshold, data);
                var (response, respData) = await ReceiveResponseAsync();

                string result = Encoding.UTF8.GetString(respData).TrimEnd('\0');
                
                if (result.StartsWith("OK:"))
                {
                    return (true, $"Fan threshold set to {result.Substring(3)}°C");
                }
                else if (result.StartsWith("ERROR:"))
                {
                    return (false, result.Substring(6));
                }
                
                return (false, result);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }

    // ============================================================================
    // PKG Install Status Classes
    // ============================================================================
    
    public enum PkgInstallState
    {
        Idle,
        InProgress,
        Done,
        Error,
        Unknown
    }

    public class PkgInstallStatus
    {
        public PkgInstallState State { get; set; } = PkgInstallState.Idle;
        public int StatusCode { get; set; } // 0=idle, 1=transferring, 2=promoting, 3=playable
        public string StatusText2 { get; set; } = ""; // daemon status string ("downloading", ...)
        public int Progress { get; set; } // 0-100
        public string ContentId { get; set; } = "";
        public string Error { get; set; } = "";
        
        public string StatusText => State switch
        {
            PkgInstallState.Idle => "Idle",
            PkgInstallState.InProgress => !string.IsNullOrEmpty(StatusText2)
                ? $"{StatusText2}... {Progress}%"
                : StatusCode switch
                {
                    1 => $"Transferring... {Progress}%",
                    2 => $"Promoting... {Progress}%",
                    _ => $"Installing... {Progress}%"
                },
            PkgInstallState.Done => $"Installed: {ContentId}",
            PkgInstallState.Error => $"Error: {Error}",
            PkgInstallState.Unknown => "Unknown",
            _ => "Unknown"
        };
    }

    public class DownloadFolderProgress
    {
        public string Phase { get; set; } = "";
        public string CurrentFile { get; set; } = "";
        public int FilesCompleted { get; set; }
        public int TotalFiles { get; set; }
        public long BytesDownloaded { get; set; }
        public long TotalBytes { get; set; }
    }

    public class SearchResult
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public bool IsDirectory { get; set; }
        public string SizeText => FormatFileSize(Size);
        
        private static string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }

    public class StorageInfo
    {
        public string Path { get; set; } = "";
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }
    }

    public class PS5StorageInfo
    {
        public ulong TotalBytes { get; set; }
        public ulong FreeBytes { get; set; }
        public ulong AvailableBytes { get; set; }
        public ulong ReservedBytes { get; set; }
        public ulong MountedGamesSize { get; set; }
        public ulong UserDataSize { get; set; }
        public ulong RealFreeSpace { get; set; }
        public string StoragePath { get; set; } = "unknown";

        public string TotalGB => FormatBytes(TotalBytes);
        public string FreeGB => FormatBytes(FreeBytes);
        public string RealFreeGB => FormatBytes(RealFreeSpace);
        public string MountedGamesGB => FormatBytes(MountedGamesSize);
        public string UserDataGB => FormatBytes(UserDataSize);
        public string ReservedGB => FormatBytes(ReservedBytes);

        public static string FormatBytes(ulong bytes)
        {
            // Use 1000-based (decimal) like PS5, not 1024-based (binary)
            if (bytes >= 1000UL * 1000 * 1000 * 1000)
                return $"{bytes / (1000.0 * 1000 * 1000 * 1000):F1} TB";
            if (bytes >= 1000UL * 1000 * 1000)
                return $"{bytes / (1000.0 * 1000 * 1000):F1} GB";
            if (bytes >= 1000UL * 1000)
                return $"{bytes / (1000.0 * 1000):F1} MB";
            if (bytes >= 1000UL)
                return $"{bytes / 1000.0:F1} KB";
            return $"{bytes} bytes";
        }
    }

    public class FileEntry
    {
        public string Name { get; set; } = "";
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class UploadProgress
    {
        public long BytesSent { get; set; }
        public long TotalBytes { get; set; }
        public double SpeedBytesPerSecond { get; set; }
        public double AverageSpeedBytesPerSecond { get; set; }
        public TimeSpan ElapsedTime { get; set; }
        public TimeSpan EstimatedTimeRemaining { get; set; }
        public string CurrentFileName { get; set; } = "";
        
        // For folder progress
        public int CurrentFileIndex { get; set; }
        public int TotalFiles { get; set; }
        public long TotalFolderBytes { get; set; }
        public long TotalFolderBytesSent { get; set; }
    }
    
    // High-speed parallel uploader using multiple connections
    public class ParallelUploader : IDisposable
    {
        private readonly string _ipAddress;
        private readonly int _port;
        private readonly int _connectionCount;
        private const int ChunkSize = 4 * 1024 * 1024; // 4MB chunks per connection
        
        public ParallelUploader(string ipAddress, int port = 9113, int connectionCount = 4)
        {
            _ipAddress = ipAddress;
            _port = port;
            _connectionCount = connectionCount;
        }
        
        public async Task<bool> UploadFileAsync(string localPath, string remotePath, IProgress<UploadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            FileInfo fileInfo = new FileInfo(localPath);
            if (!fileInfo.Exists) return false;
            
            // For small files, use single connection
            if (fileInfo.Length < ChunkSize * 2)
            {
                using var protocol = new PS5Protocol();
                if (!await protocol.ConnectAsync(_ipAddress, _port)) return false;
                return await protocol.UploadFileAsync(localPath, remotePath, progress, cancellationToken);
            }
            
            // For large files, use parallel connections
            // This requires server support for chunked/parallel uploads
            // For now, fall back to single connection
            using var proto = new PS5Protocol();
            if (!await proto.ConnectAsync(_ipAddress, _port)) return false;
            return await proto.UploadFileAsync(localPath, remotePath, progress, cancellationToken);
        }
        
        public void Dispose() { }
    }

    // New data classes for enhanced features
    public class PS5FileInfo
    {
        public long Size { get; set; }
        public DateTime ModifiedTime { get; set; }
        public DateTime AccessTime { get; set; }
        public int Permissions { get; set; }
        public bool IsDirectory { get; set; }
        public bool IsSymlink { get; set; }
    }

    public class PS5SystemInfo
    {
        public string Hostname { get; set; } = "PS5";
        public string ServerVersion { get; set; } = "";
        public int ProtocolVersion { get; set; }
        public ulong TotalMemory { get; set; }
        public ulong StorageTotal { get; set; }
        public ulong StorageFree { get; set; }
        public int MountedGames { get; set; }
        public bool IndexReady { get; set; }
        public int IndexFiles { get; set; }
        public int IndexDirs { get; set; }
        public long ServerUptime { get; set; }
    }

    public class FileVerificationResult
    {
        public string CRC32 { get; set; } = "";
        public ulong Size { get; set; }
        public bool Success { get; set; }
        public string? Error { get; set; }
    }

    // Hardware info
    public class PS5HardwareInfo
    {
        public string Model { get; set; } = "";
        public string Serial { get; set; } = "";
        public bool HasWlanBt { get; set; }
        public bool HasOpticalOut { get; set; }
        public string HwMachine { get; set; } = "";
        public string OsVersion { get; set; } = "";
        public int NumCpu { get; set; }
        public ulong PhysMem { get; set; }
    }

    // Temperature and CPU info
    public class PS5TemperatureInfo
    {
        public int CpuTemp { get; set; }
        public int SocTemp { get; set; }
        public long CpuFreqMhz { get; set; }
        public uint SocPowerMw { get; set; }
        public int[] CpuUsage { get; set; } = new int[8];
    }

    // Running app info
    public class PS5RunningApp
    {
        public int Pid { get; set; }
        public string Name { get; set; } = "";
        public string TitleId { get; set; } = "";
        public uint AppId { get; set; }
    }

    // Screenshot info (notifies UI when thumbnail loads)
    public class PS5Screenshot : System.ComponentModel.INotifyPropertyChanged
    {
        public string FullPath { get; set; } = "";
        public string FileName { get; set; } = "";
        public long Size { get; set; }
        public DateTime ModifiedTime { get; set; }

        private Avalonia.Media.IImage? _thumbnail;
        public Avalonia.Media.IImage? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Thumbnail))); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string SizeDisplay
        {
            get
            {
                if (Size < 1024) return $"{Size} B";
                if (Size < 1024 * 1024) return $"{Size / 1024.0:0.0} KB";
                return $"{Size / (1024.0 * 1024.0):0.0} MB";
            }
        }

        public string DateDisplay => ModifiedTime.ToString("yyyy-MM-dd HH:mm");
    }

    // Power info
    public class PS5PowerInfo
    {
        public ulong OperatingTimeSec { get; set; }
        public ulong OperatingTimeHours { get; set; }
        public ulong OperatingTimeMinutes { get; set; }
        public uint BootCount { get; set; }
        public uint PowerConsumptionMw { get; set; }
    }

    // Extended system info
    public class PS5ExtendedInfo
    {
        public string FirmwareVersion { get; set; } = "";
        public string ProsperoVersion { get; set; } = "";
        public string ProductCode { get; set; } = "";
        public string ProductStr { get; set; } = "";
        public ulong TotalOperatingTimeSec { get; set; }
        public ulong TotalOperatingTimeHours { get; set; }
        public uint BootCount { get; set; }
        public uint ShutdownCount { get; set; }
        public int ThermalAlert { get; set; }
        public int BdDrivePower { get; set; }
    }

    // CPU usage info
    public class PS5CpuUsage
    {
        public int[] CoreUsage { get; set; } = new int[8];
        public int Average { get; set; }
    }

    // Memory info
    public class PS5MemoryInfo
    {
        public ulong DirectTotal { get; set; }
        public ulong DirectAvailable { get; set; }
        public ulong DirectUsed { get; set; }
        public int DirectUsedPercent { get; set; }
        public ulong FlexibleAvailable { get; set; }
        public ulong PhysicalTotal { get; set; }
        public ulong UserMemory { get; set; }
        public ulong FreeMemory { get; set; }
        public ulong CpuPoolTotal { get; set; }
        public ulong CpuPoolFree { get; set; }
        public ulong GpuPoolTotal { get; set; }
        public ulong GpuPoolFree { get; set; }
    }

    // Module info
    public class PS5ModuleInfo
    {
        public uint Id { get; set; }
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
    }

    public class PS5UsbDrive
    {
        public string MountPath { get; set; } = "";
        public string FsType { get; set; } = "";
        public string Device { get; set; } = "";
        public ulong TotalBytes { get; set; }
        public ulong FreeBytes { get; set; }
        public string TotalGB => FormatBytes(TotalBytes);
        public string FreeGB => FormatBytes(FreeBytes);

        private static string FormatBytes(ulong bytes)
        {
            double b = bytes;
            if (b >= 1000.0 * 1000 * 1000) return $"{b / (1000.0 * 1000 * 1000):F1} GB";
            if (b >= 1000.0 * 1000) return $"{b / (1000.0 * 1000):F1} MB";
            return $"{b:F0} B";
        }
    }

    // Save game info
    public class PS5SaveGame : System.ComponentModel.INotifyPropertyChanged
    {
        public string TitleId { get; set; } = "";
        public string UserId { get; set; } = "";
        public string SavePath { get; set; } = "";
        public ulong Size { get; set; }
        public long ModifiedUnixTime { get; set; }

        // Display helpers
        public string SizeDisplay
        {
            get
            {
                double mb = Size / (1024.0 * 1024.0);
                if (mb < 1) return $"{Size / 1024.0:F1} KB";
                if (mb < 1024) return $"{mb:F1} MB";
                return $"{mb / 1024.0:F2} GB";
            }
        }

        public string ModifiedDisplay
        {
            get
            {
                try
                {
                    var dt = DateTimeOffset.FromUnixTimeSeconds(ModifiedUnixTime).LocalDateTime;
                    return dt.ToString("yyyy-MM-dd HH:mm");
                }
                catch { return "—"; }
            }
        }

        public string ShortUserId => UserId.Length > 8 ? UserId.Substring(0, 8) + "…" : UserId;

        private string _gameName = "";
        public string GameName
        {
            get => _gameName;
            set { _gameName = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(GameName))); }
        }

        private Avalonia.Media.IImage? _icon;
        public Avalonia.Media.IImage? Icon
        {
            get => _icon;
            set { _icon = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon))); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    // Individual save IMAGE file (garlic-savemgr style: one entry per save
    // image under savedata_prospero/savedata, not per title folder)
    public class PS5SaveFile : System.ComponentModel.INotifyPropertyChanged
    {
        public string Path { get; set; } = "";
        public string TitleId { get; set; } = "";
        public string SaveName { get; set; } = "";
        public string UserId { get; set; } = "";
        public ulong Size { get; set; }
        public bool IsPs4 { get; set; }
        public bool HasBinKey { get; set; }

        public string TypeBadge => IsPs4 ? "PS4" : "PS5";
        public string TypeBadgeColor => IsPs4 ? "#9C27B0" : "#007ACC";

        public string SizeDisplay
        {
            get
            {
                double mb = Size / (1024.0 * 1024.0);
                if (mb < 1) return $"{Size / 1024.0:F1} KB";
                if (mb < 1024) return $"{mb:F1} MB";
                return $"{mb / 1024.0:F2} GB";
            }
        }

        public string ShortUserId => UserId.Length > 8 ? UserId.Substring(0, 8) + "…" : UserId;

        private string _gameName = "";
        public string GameName
        {
            get => _gameName;
            set { _gameName = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(GameName))); }
        }

        private Avalonia.Media.IImage? _icon;
        public Avalonia.Media.IImage? Icon
        {
            get => _icon;
            set { _icon = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon))); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    // Mounted game info
    public class PS5MountedGame : System.ComponentModel.INotifyPropertyChanged
    {
        public string TitleId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public ulong Size { get; set; }
        public string Region { get; set; } = "";
        public bool IsActive { get; set; }

        private Avalonia.Media.IImage? _icon;
        public Avalonia.Media.IImage? Icon
        {
            get => _icon;
            set
            {
                _icon = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon)));
            }
        }

        public string SizeDisplay
        {
            get
            {
                double mb = Size / (1024.0 * 1024.0);
                if (mb < 1024) return $"{mb:F1} MB";
                return $"{mb / 1024.0:F2} GB";
            }
        }

        public string StatusDisplay => IsActive ? "✓ Mounted" : "✗ Not Mounted";
        public Avalonia.Media.IBrush StatusBrush => IsActive
            ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#28A745"))
            : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#6C757D"));

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    // ============ Trophy viewer (NpTrophy V2) ============

    // One registered trophy set: raw UCP payloads + per-user TRPTITLE.DAT.
    public class PS5TrophySet : System.ComponentModel.INotifyPropertyChanged
    {
        public string NpCommunicationId { get; set; } = "";
        public string TitleId { get; set; } = "";      // empty when npbind map misses
        public string UserId { get; set; } = "";       // uid that owns TRPTITLE.DAT
        public byte[] TropConfJson { get; set; } = Array.Empty<byte>();
        public byte[] TropMetaJson { get; set; } = Array.Empty<byte>();
        public byte[] TrpTitleData { get; set; } = Array.Empty<byte>();

        public List<PS5Trophy> Trophies { get; } = new();

        // Parsed from TRPTITLE.DAT (client-side T2PD parse). StateKnown=false
        // → show honest "—"; UnlockMask bit i = trophy id i (LSB-first).
        public byte[] UnlockMask { get; set; } = Array.Empty<byte>();
        public bool StateKnown { get; set; }
        public int EarnedFallback { get; set; } = -1;   // count known, flags unknown

        private string _gameName = "";
        public string GameName
        {
            get => _gameName;
            set { _gameName = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(GameName))); }
        }

        private Avalonia.Media.IImage? _icon;
        public Avalonia.Media.IImage? Icon
        {
            get => _icon;
            set { _icon = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon))); }
        }

        public int EarnedCount => StateKnown
            ? Trophies.Count(t => t.IsUnlocked)
            : EarnedFallback >= 0 ? EarnedFallback : Trophies.Count(t => t.IsUnlocked);
        public int TotalCount => Trophies.Count;
        public string ProgressDisplay => TotalCount == 0
            ? "—"
            : (StateKnown || EarnedFallback >= 0)
                ? $"{EarnedCount}/{TotalCount}"
                : $"—/{TotalCount}";

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    // One trophy definition merged with (optional) per-user unlock state.
    public class PS5Trophy : System.ComponentModel.INotifyPropertyChanged
    {
        public int Id { get; set; }                    // index → icon "trop%04d.png"
        public string Name { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Grade { get; set; } = "";        // B/S/G/P
        public bool Hidden { get; set; }
        public string GroupId { get; set; } = "";      // "default" or dlc group
        public bool IsUnlocked { get; set; }
        public bool StateKnown { get; set; }         // false until TRPTITLE.DAT format is parsed
        public DateTime? UnlockedTime { get; set; }

        public string GradeDisplay => Grade switch
        {
            "P" => "Platinum",
            "G" => "Gold",
            "S" => "Silver",
            "B" => "Bronze",
            _ => "?"
        };
        public Avalonia.Media.IBrush GradeBrush => Grade switch
        {
            "P" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7FD4FF")),
            "G" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFD24A")),
            "S" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#C0C0C0")),
            "B" => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#CD7F32")),
            _ => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#6C757D"))
        };
        public string StateDisplay => !StateKnown
            ? "—"
            : IsUnlocked
                ? (UnlockedTime?.ToString("yyyy-MM-dd HH:mm") ?? "Unlocked")
                : "Locked";
        public string HiddenDisplay => Hidden ? "🙈" : "";
        public string GroupDisplay => string.IsNullOrEmpty(GroupId) || GroupId == "default" ? "Base" : GroupId;
        public bool CanUnlock => StateKnown && !IsUnlocked;
        public bool CanLock => StateKnown && IsUnlocked;

        private Avalonia.Media.IImage? _icon;
        public Avalonia.Media.IImage? Icon
        {
            get => _icon;
            set { _icon = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon))); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
}
