using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace PS5Upload
{
    public partial class MainWindow
    {
        // ============================================================
        // SETTINGS
        // ============================================================
        private void LoadSettings()
        {
            try
            {
                // Renamed with the app (PS5 Suite); fall back to the legacy file.
                const string settingsFile = "ps5suite_settings.json";
                string effective = File.Exists(settingsFile) ? settingsFile
                    : File.Exists("ps5upload_settings.json") ? "ps5upload_settings.json" : settingsFile;
                if (File.Exists(effective))
                {
                    string json = File.ReadAllText(effective);
                    var settings = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                    if (settings != null)
                    {
                        if (settings.ContainsKey("AutoSendPayload"))
                            _autoSendPayload = settings["AutoSendPayload"].ToString() == "True";
                        if (settings.ContainsKey("PayloadPath"))
                            _payloadPath = settings["PayloadPath"].ToString() ?? "";
                        if (settings.ContainsKey("PayloadPort"))
                            int.TryParse(settings["PayloadPort"].ToString(), out _payloadPort);
                    }
                }
            }
            catch (Exception ex) { Log($"⚠️ Failed to load settings: {ex.Message}"); }
        }

        private void SaveSettings()
        {
            try
            {
                const string settingsFile = "ps5suite_settings.json";
                var settings = new Dictionary<string, object>
                {
                    ["AutoSendPayload"] = _autoSendPayload,
                    ["PayloadPath"] = _payloadPath,
                    ["PayloadPort"] = _payloadPort
                };
                string json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(settingsFile, json);
            }
            catch (Exception ex) { Log($"⚠️ Failed to save settings: {ex.Message}"); }
        }

        // ============================================================
        // CONNECTION
        // ============================================================
        private async Task AutoSendPayloadOnStartup()
        {
            await Task.Delay(1000);
            if (string.IsNullOrEmpty(_ps5IpAddress)) { Log("⚠️ Auto-send payload: No PS5 IP address configured"); return; }
            if (string.IsNullOrEmpty(_payloadPath) || !File.Exists(_payloadPath)) { Log("⚠️ Auto-send payload: Payload file not found"); return; }

            Log($"📤 Auto-sending payload to {_ps5IpAddress}:{_payloadPort}...");
            var progress = new InlineProgress<long>(bytes => { });
            bool success = await PS5Protocol.SendPayloadAsync(_ps5IpAddress, _payloadPath, _payloadPort, progress);
            if (success)
            {
                Log($"✅ Payload sent successfully ({new FileInfo(_payloadPath).Length} bytes)");
                await Task.Delay(2000);
                await ConnectToPS5Async();
            }
            else
                Log("❌ Failed to send payload");
        }

        private async Task ConnectToPS5Async()
        {
            if (await _protocol.ConnectAsync(_ps5IpAddress))
            {
                Log("✅ Connected to PS5 successfully");
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ConnectButton.Content = "🟢 Disconnect";
                    ConnectButton.Background = new SolidColorBrush(Colors.Green);
                    UploadButton.IsEnabled = true;
                    MountGamesButton.IsEnabled = true;
                });
                await LoadPS5DirectoryAsync(_currentPS5Path);
            }
            else
                Log("❌ Failed to connect to PS5");
        }

        private CancellationTokenSource? _connectCts;
        private bool _connecting;

        private async void AutoDetectButton_Click(object? sender, RoutedEventArgs e)
        {
            Log("🔍 Auto-discovering PS5 on the network...");
            AutoDetectButton.IsEnabled = false;
            try
            {
                var found = await PS5Protocol.DiscoverPS5Async(3000);
                if (found == null)
                {
                    Log("❌ No PS5 answered — is the payload running?");
                    await ShowMessageAsync("No PS5 found on the network.\nMake sure the payload is running.", "Auto-Discovery");
                    return;
                }
                IpAddressTextBox.Text = found.Value.ip;
                Log($"🔍 Found PS5 at {found.Value.ip}:{found.Value.port}");
            }
            catch (Exception ex) { Log($"❌ Discovery failed: {ex.Message}"); }
            finally { AutoDetectButton.IsEnabled = true; }
        }

        private async void ConnectButton_Click(object? sender, RoutedEventArgs e)
        {
            // While a connect attempt is running the button becomes Cancel
            if (_connecting)
            {
                Log("⏹ Connect cancelled by user");
                _connectCts?.Cancel();
                return;
            }

            string ipAddress = IpAddressTextBox.Text?.Trim() ?? "";

            // Empty IP => auto-discover: broadcast a UDP probe; the running
            // payload answers with its IP + actual bound port.
            if (string.IsNullOrEmpty(ipAddress))
            {
                Log("🔍 Auto-discovering PS5 on the network...");
                try
                {
                    var found = await PS5Protocol.DiscoverPS5Async(2500);
                    if (found == null)
                    {
                        Log("❌ No PS5 answered the discovery probe");
                        await ShowMessageAsync("No PS5 found on the network.\nMake sure the payload is running, or enter the IP manually.", "Auto-Discovery");
                        return;
                    }
                    ipAddress = found.Value.ip;
                    Log($"🔍 Found PS5 at {ipAddress}:{found.Value.port}");
                    await Dispatcher.UIThread.InvokeAsync(() => IpAddressTextBox.Text = ipAddress);
                }
                catch (Exception ex)
                {
                    Log($"❌ Discovery failed: {ex.Message}");
                    await ShowMessageAsync($"Discovery failed:\n{ex.Message}", "Auto-Discovery");
                    return;
                }
            }

            _ps5IpAddress = ipAddress;

            if (_protocol.IsConnected)
            {
                ConnectButton.IsEnabled = false;
                Log("🔌 Disconnecting from PS5...");
                StopHwAutoRefresh();
                _protocol.Disconnect();
                _shellActive = false;
                _shellCurrentDir = "/data";

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ConnectButton.Content = "🔴 Disconnected";
                    ConnectButton.Background = new SolidColorBrush(Colors.DarkRed);
                    UploadButton.IsEnabled = false;
                    MountGamesButton.IsEnabled = false;
                    _ps5Files.Clear();
                });
                Log("✅ Disconnected from PS5");

                await Task.Delay(1000);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ConnectButton.Content = "🔵 Connect";
                    ConnectButton.Background = new SolidColorBrush(Color.FromRgb(0, 122, 204));
                });
                ConnectButton.IsEnabled = true;
            }
            else
            {
                Log($"Connecting to PS5 at {ipAddress}...");
                _connecting = true;
                _connectCts = new CancellationTokenSource();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ConnectButton.Content = "⏹ Cancel";
                    ConnectButton.Background = new SolidColorBrush(Colors.DarkOrange);
                });
                try
                {
                    if (await _protocol.ConnectAsync(ipAddress, 9113, _connectCts.Token))
                    {
                        Log("✅ Connected to PS5 successfully");
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            ConnectButton.Content = "🟢 Disconnect";
                            ConnectButton.Background = new SolidColorBrush(Colors.DarkGreen);
                            UploadButton.IsEnabled = true;
                            MountGamesButton.IsEnabled = true;
                        });
                        await OpenShellAsync();
                        await LoadPS5DirectoryAsync(_currentPS5Path);
                        await RefreshStorageInfoAsync();
                    }
                    else
                    {
                        Log("❌ Failed to connect to PS5");
                        await ShowMessageAsync("Failed to connect to PS5. Make sure the payload is running.", "Connection Error");
                    }
                }
                catch (OperationCanceledException)
                {
                    Log("⏹ Connect cancelled");
                }
                finally
                {
                    _connecting = false;
                    _connectCts?.Dispose();
                    _connectCts = null;
                    bool stillConnected = _protocol.IsConnected;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!stillConnected)
                        {
                            ConnectButton.Content = "🔵 Connect";
                            ConnectButton.Background = new SolidColorBrush(Color.FromRgb(0, 122, 204));
                        }
                    });
                }
            }
        }

        // ============================================================
        // PS5 DIRECTORY BROWSING
        // ============================================================
        private async Task LoadPS5DirectoryAsync(string path)
        {
            try
            {
                if (!_protocol.IsConnected)
                {
                    Log($"⚠️ Connection lost (LastError: {_protocol.LastError}), reconnecting...");
                    if (!await _protocol.ConnectAsync(_ps5IpAddress))
                    {
                        await ShowMessageAsync("Failed to reconnect to PS5", "Connection Error");
                        return;
                    }
                    Log("✅ Reconnected successfully");
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _currentPS5Path = path;
                CurrentPathTextBox.Text = path;

                var entries = await _protocol.ListDirAsync(path);
                Log($"🔍 DEBUG: ListDir returned {entries.Count()} entries for path: {path}");
                foreach (var entry in entries.Take(5))
                    Log($"  - {(entry.IsDirectory ? "📁" : "📄")} {entry.Name} ({entry.Size} bytes)");
                if (entries.Count() > 5)
                    Log($"  ... and {entries.Count() - 5} more");

                var items = new List<PS5FileItem>();
                if (path != "/")
                {
                    string? parentPath = Path.GetDirectoryName(path);
                    if (string.IsNullOrEmpty(parentPath)) parentPath = "/";
                    else parentPath = parentPath.Replace("\\", "/");
                    items.Add(new PS5FileItem { Name = "..", FullPath = parentPath, Icon = "📁", IsDirectory = true, Size = 0 });
                }

                foreach (var entry in entries)
                {
                    items.Add(new PS5FileItem
                    {
                        Name = entry.Name,
                        IsDirectory = entry.IsDirectory,
                        Size = entry.Size,
                        FullPath = $"{path}/{entry.Name}".Replace("//", "/"),
                        Icon = entry.IsDirectory ? "📁" : "📄"
                    });
                }

                var sortedItems = items.OrderBy(i => i.IsDirectory ? 0 : 1).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

                _ps5Files.Clear();
                foreach (var item in sortedItems) _ps5Files.Add(item);

                var t4 = sw.ElapsedMilliseconds;
                Log("📂 Loaded " + entries.Count().ToString() + " items (Total: " + t4.ToString() + "ms)");
                ApplySearchFilter();
            }
            catch (Exception ex)
            {
                if (_protocol.IsConnected)
                {
                    Log($"❌ Failed to load directory: {ex.Message}");
                    await ShowMessageAsync($"Failed to load directory: {ex.Message}", "Error");
                }
            }
        }

        private void ApplySearchFilter()
        {
            _ps5FilesFiltered.Clear();
            if (string.IsNullOrWhiteSpace(_searchQuery))
            {
                foreach (var file in _ps5Files) _ps5FilesFiltered.Add(file);
            }
            else
            {
                string query = _searchQuery.ToLower();
                foreach (var file in _ps5Files)
                    if (file.Name.ToLower().Contains(query))
                        _ps5FilesFiltered.Add(file);
            }
        }

        // ============================================================
        // LOCAL FILES
        // ============================================================
        private async void BrowseFilesButton_Click(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select files to upload", AllowMultiple = true });
            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (path != null)
                {
                    FileInfo info = new FileInfo(path);
                    
                    // Check if it's a ZIP file - extract and add contents
                    if (info.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        await ExtractAndAddZipContents(path, info.Name);
                    }
                    else
                    {
                        _localFiles.Add(new LocalFileItem { Name = info.Name, FullPath = path, Icon = "📄", IsDirectory = false, Size = info.Length });
                    }
                }
            }
            // Files were queued while browsing a folder — surface the queue.
            if (_localBrowsePath != null) ExitLocalBrowse();
        }

        private async void BrowseFolderButton_Click(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select folder to upload", AllowMultiple = false });
            if (folders.Count > 0)
            {
                var path = folders[0].TryGetLocalPath();
                if (path != null)
                {
                    DirectoryInfo dirInfo = new DirectoryInfo(path);
                    _localFiles.Add(new LocalFileItem { Name = dirInfo.Name, FullPath = path, Icon = "📁", IsDirectory = true, Size = 0 });
                }
            }
            if (_localBrowsePath != null) ExitLocalBrowse();
        }

        // ============================================================
        // LOCAL PANE BROWSE MODE — double-click a folder in the queue
        // to navigate into it (local + NAS); files double-clicked in
        // browse mode are added to the upload queue.
        // ============================================================
        private string? _localBrowsePath;   // null = queue view
        private readonly ObservableCollection<LocalFileItem> _localBrowse = new();
        private const string BrowseBackSentinel = "\0back";

        private void LocalFilesListBox_DoubleTapped(object? sender, TappedEventArgs e)
        {
            if (LocalFilesListBox.SelectedItem is not LocalFileItem item) return;
            if (_localBrowsePath == null)
            {
                if (item.IsDirectory) EnterLocalBrowse(item.FullPath);
            }
            else
            {
                if (item.FullPath == BrowseBackSentinel) { LocalBrowseUp(); return; }
                if (item.IsDirectory) EnterLocalBrowse(item.FullPath);
                else
                {
                    // Queue the file — keep the browse view so more can be picked.
                    _localFiles.Add(new LocalFileItem { Name = item.Name, FullPath = item.FullPath, Icon = "📄", IsDirectory = false, Size = item.Size });
                    Log($"➕ Queued from browse: {item.Name} ({FormatFileSize(item.Size)})");
                }
            }
        }

        private void EnterLocalBrowse(string path)
        {
            try
            {
                var entries = LocalIo.Enumerate(path);
                _localBrowse.Clear();
                _localBrowse.Add(new LocalFileItem { Name = "⬆ .. (Back)", FullPath = BrowseBackSentinel, Icon = "⬆", IsDirectory = false });
                foreach (var en in entries.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
                    _localBrowse.Add(new LocalFileItem { Name = en.Name, FullPath = en.FullPath, IsDirectory = en.IsDirectory, Size = en.Size, Icon = en.IsDirectory ? "📁" : "📄" });
                _localBrowsePath = path;
                LocalFilesListBox.ItemsSource = _localBrowse;
                LocalFilesHeader.Text = "📂 " + path;
                LocalBrowseBackBtn.IsVisible = true;
            }
            catch (Exception ex)
            {
                Log($"❌ Cannot browse {path}: {ex.Message}");
            }
        }

        private void LocalBrowseUp()
        {
            if (_localBrowsePath == null) { ExitLocalBrowse(); return; }
            string? parent = GetLocalParentPath(_localBrowsePath);
            if (parent == null) ExitLocalBrowse();
            else EnterLocalBrowse(parent);
        }

        private void ExitLocalBrowse()
        {
            _localBrowsePath = null;
            LocalFilesListBox.ItemsSource = _localFiles;
            LocalFilesHeader.Text = "📁 Local Files";
            LocalBrowseBackBtn.IsVisible = false;
        }

        private void LocalBrowseBack_Click(object? sender, RoutedEventArgs e) => LocalBrowseUp();

        private static string? GetLocalParentPath(string path)
        {
            if (LocalIo.IsUnc(path))
            {
                string p = NasManager.Normalize(path).TrimEnd('\\');
                string root = NasManager.ShareRoot(p);
                if (p.Length <= root.Length) return null;      // at share root → exit
                return p.Substring(0, p.LastIndexOf('\\'));
            }
            string? parent = Path.GetDirectoryName(path.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(parent) ? null : parent;
        }

        private void ClearLocalFiles_Click(object? sender, RoutedEventArgs e)
        {
            _localFiles.Clear();
            if (_localBrowsePath != null) ExitLocalBrowse();
        }

        private void LocalFilesListBox_DragOver(object? sender, DragEventArgs e) => e.DragEffects = DragDropEffects.Copy;

        private void LocalFilesListBox_Drop(object? sender, DragEventArgs e)
        {
            // Avalonia 12: Use TryGetFiles() on DataTransfer
            if (e.DataTransfer.TryGetFiles() is { } files)
            {
                foreach (var item in files)
                {
                    var path = item.Path.LocalPath;
                    if (path == null) continue;
                    
                    if (File.Exists(path))
                    {
                        FileInfo info = new FileInfo(path);
                        
                        // Check if it's a ZIP file - extract and add contents
                        if (info.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            _ = ExtractAndAddZipContents(path, info.Name);
                        }
                        else
                        {
                            _localFiles.Add(new LocalFileItem { Name = info.Name, FullPath = path, Icon = "📄", IsDirectory = false, Size = info.Length });
                        }
                    }
                    else if (Directory.Exists(path))
                    {
                        DirectoryInfo dirInfo = new DirectoryInfo(path);
                        _localFiles.Add(new LocalFileItem { Name = dirInfo.Name, FullPath = path, Icon = "📁", IsDirectory = true, Size = 0 });
                    }
                }
            }
            if (_localBrowsePath != null) ExitLocalBrowse();
        }

        // ============================================================
        // ZIP ARCHIVE SUPPORT (ps5upload-style)
        // Extracts .zip files and adds contents to upload queue
        // ============================================================
        private async Task ExtractAndAddZipContents(string zipPath, string zipName)
        {
            try
            {
                Log($"📦 Extracting ZIP: {zipName}");
                
                // Create temp extraction directory
                string tempDir = Path.Combine(Path.GetTempPath(), "ps5suite_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                
                // Extract ZIP — stream-based so NAS-sourced archives work too
                await Task.Run(() =>
                {
                    if (LocalIo.IsUnc(zipPath) && NasManager.HasSession(zipPath))
                    {
                        using var zs = LocalIo.OpenRead(zipPath);
                        using var za = new System.IO.Compression.ZipArchive(zs, System.IO.Compression.ZipArchiveMode.Read);
                        za.ExtractToDirectory(tempDir);
                    }
                    else
                    {
                        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, tempDir);
                    }
                });
                
                // Add all extracted files
                var files = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories);
                var dirs = Directory.GetDirectories(tempDir, "*", SearchOption.AllDirectories);
                
                Log($"📦 Found {files.Length} files, {dirs.Length} folders in ZIP");
                
                // Add root directory as a folder item
                string zipBaseName = Path.GetFileNameWithoutExtension(zipName);
                _localFiles.Add(new LocalFileItem { 
                    Name = zipBaseName, 
                    FullPath = tempDir, 
                    Icon = "📁", 
                    IsDirectory = true, 
                    Size = 0 
                });
                
                Log($"✅ ZIP extracted to temp folder - added '{zipBaseName}' to upload queue");
                Log($"⚠️ Will upload extracted contents, not the .zip file");
            }
            catch (Exception ex)
            {
                Log($"❌ Failed to extract ZIP: {ex.Message}");
                // Fall back to adding the zip file itself
                long sz = LocalIo.GetLength(zipPath);
                _localFiles.Add(new LocalFileItem { Name = LocalIo.GetName(zipPath), FullPath = zipPath, Icon = "📄", IsDirectory = false, Size = sz });
            }
        }

        // ============================================================
        // PS5 FILE OPERATIONS
        // ============================================================
        private void PS5FilesListBox_DoubleTapped(object? sender, TappedEventArgs e)
        {
            if (PS5FilesListBox.SelectedItem is PS5FileItem item && item.IsDirectory)
                _ = LoadPS5DirectoryAsync(item.FullPath);
        }

        private void ClearSearchButton_Click(object? sender, RoutedEventArgs e)
        {
            SearchTextBox.Text = "";
            _searchQuery = "";
            ApplySearchFilter();
        }

        private void GoButton_Click(object? sender, RoutedEventArgs e)
        {
            string path = CurrentPathTextBox.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(path)) _ = LoadPS5DirectoryAsync(path);
        }

        // ============================================================
        // NAS/SMB SUPPORT (managed SMB via SMBLibrary — works on
        // Windows/Linux/macOS/Android with explicit credentials)
        // ============================================================
        private async void AddNasPath_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                // Saved connections → quick picker (or "New" for the full dialog).
                if (_nasSaved.Count > 0)
                {
                    var pick = await ShowNasPickerAsync();
                    if (pick == null) return;
                    if (!ReferenceEquals(pick, _nasNewSentinel))
                    {
                        try
                        {
                            if (!NasManager.HasSession(pick.Path))
                            {
                                Log($"🔐 Reconnecting to \\\\{pick.Server}\\{pick.Share}...");
                                string pass = NasManager.DecodePass(pick.PassEnc);
                                await Task.Run(() => NasManager.Connect(pick.Server, pick.Share, pick.User, pass));
                                Log($"✅ Connected to \\\\{pick.Server}\\{pick.Share}");
                            }
                            AddNasItem(NasManager.Normalize(pick.Path));
                        }
                        catch (Exception ex)
                        {
                            Log($"❌ Saved NAS connection failed: {ex.Message}");
                            await ShowMessageAsync($"Saved connection failed:\n{ex.Message}\n\nUse ➕ New to enter different credentials.", "Connection Failed");
                        }
                        return;
                    }
                }

                var input = await ShowNasDialogAsync();
                if (input == null || string.IsNullOrWhiteSpace(input.Value.path)) return;

                string nasPath = NasManager.Normalize(input.Value.path);

                if (!NasManager.TrySplit(nasPath, out string server, out string share, out _))
                {
                    // "\\host" with no share — try to list the host's shares
                    // via OS tools so the user sees what's available.
                    string bare = nasPath.TrimEnd('\\');
                    if (NasManager.IsUncPath(bare) && bare.Count(c => c == '\\') == 2)
                    {
                        string host = bare.TrimStart('\\');
                        Log($"🌐 Querying shares on \\\\{host}...");
                        var shares = await Task.Run(() => NasManager.EnumerateSharesOs(host, input.Value.user, input.Value.pass));
                        if (shares != null)
                        {
                            await ShowMessageAsync(
                                $"\\\\{host} is reachable — pick a share and add it to the path.\n\n" +
                                "Shares found:\n  " + string.Join("\n  ", shares.Select(s => $"\\\\{host}\\{s}")) +
                                "\n\nExample: \\\\" + host + "\\" + shares[0],
                                "Select a Share");
                        }
                        else
                        {
                            await ShowMessageAsync(
                                $"The path \\\\{host} is missing the share name.\n\n" +
                                "SMB connects to a share, not a host:\n" +
                                "  \\\\host\\share\\folder\n\n" +
                                "Couldn't enumerate shares automatically — check your NAS admin page for share names.",
                                "Share Name Required");
                        }
                        return;
                    }
                    await ShowMessageAsync(
                        "Invalid path format.\n\n" +
                        "Use UNC format: \\\\server\\share\\folder\n" +
                        "Example: \\\\NAS\\games\\PS4\\MyGame",
                        "Invalid Path");
                    return;
                }

                // Establish/reuse the authenticated session for this share.
                if (!NasManager.HasSession(nasPath))
                {
                    // No creds entered → a Windows-authenticated share may work
                    // via System.IO; otherwise try guest, then ask to retry.
                    if (string.IsNullOrEmpty(input.Value.user))
                    {
                        if (Directory.Exists(nasPath) || File.Exists(nasPath))
                        {
                            Log($"🌐 NAS path accessible via OS session (no SMB login needed)");
                            AddNasItem(nasPath);
                            return;
                        }
                    }

                    Log($"🔐 Connecting to \\\\{server}\\{share}...");
                    try
                    {
                        await Task.Run(() => NasManager.Connect(server, share, input.Value.user, input.Value.pass));
                        Log(input.Value.user.Length > 0
                            ? $"✅ Connected to \\\\{server}\\{share} as {input.Value.user}"
                            : $"✅ Connected to \\\\{server}\\{share} (guest)");

                        // Optional persistence — path + creds for next launch.
                        if (input.Value.remember)
                        {
                            var existing = _nasSaved.FindIndex(c => c.Path.Equals(nasPath, StringComparison.OrdinalIgnoreCase));
                            var saved = new NasManager.SavedConnection
                            {
                                Path = nasPath, Server = server, Share = share,
                                User = input.Value.user, PassEnc = NasManager.EncodePass(input.Value.pass)
                            };
                            if (existing >= 0) _nasSaved[existing] = saved; else _nasSaved.Add(saved);
                            SaveNasConnections();
                            Log($"💾 Saved NAS connection: {nasPath}");
                        }
                    }
                    catch (NasManager.NasAuthException ex)
                    {
                        Log($"🔒 NAS auth failed: {ex.Message}");
                        await ShowMessageAsync($"{ex.Message}\n\nRe-open the NAS dialog and enter the share's username/password.", "Authentication Failed");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log($"❌ NAS connect failed: {ex.Message}");
                        await ShowMessageAsync($"Cannot connect to \\\\{server}\\{share}:\n{ex.Message}", "Connection Failed");
                        return;
                    }
                }

                Log($"🌐 Checking NAS path: {nasPath}");
                AddNasItem(nasPath);
            }
            catch (Exception ex)
            {
                Log($"❌ NAS error: {ex.Message}");
                await ShowMessageAsync($"Error accessing NAS path: {ex.Message}", "Error");
            }
        }

        /// Adds the NAS path (file or folder) to the upload queue — works via
        /// the SMB session when present, or the OS's own share session.
        private async void AddNasItem(string nasPath)
        {
            try
            {
                bool isDir, isFile;
                long size = 0;
                if (NasManager.HasSession(nasPath))
                {
                    var kind = NasManager.ExistsAsDir(nasPath);
                    isDir = kind == true; isFile = kind == false;
                    if (isFile) size = NasManager.GetLength(nasPath);
                }
                else
                {
                    isDir = Directory.Exists(nasPath); isFile = File.Exists(nasPath);
                    if (isFile) size = new FileInfo(nasPath).Length;
                }

                string name = LocalIo.GetName(nasPath);
                if (isDir)
                {
                    _localFiles.Add(new LocalFileItem { Name = name, FullPath = nasPath, Icon = "🌐", IsDirectory = true, Size = 0 });
                    Log($"✅ Added NAS folder: {name} ({nasPath})");
                }
                else if (isFile)
                {
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        await ExtractAndAddZipContents(nasPath, name);
                    }
                    else
                    {
                        _localFiles.Add(new LocalFileItem { Name = name, FullPath = nasPath, Icon = "🌐", IsDirectory = false, Size = size });
                        Log($"✅ Added NAS file: {name} ({FormatFileSize(size)})");
                    }
                }
                else
                {
                    Log($"❌ NAS path not found: {nasPath}");
                    await ShowMessageAsync(
                        $"Cannot access: {nasPath}\n\n" +
                        "Make sure:\n" +
                        "• The network share is accessible\n" +
                        "• You have permission to read it\n" +
                        "• The path is correct",
                        "NAS Path Not Found");
                }
            }
            catch (Exception ex)
            {
                Log($"❌ NAS error: {ex.Message}");
                await ShowMessageAsync($"Error accessing NAS path: {ex.Message}", "Error");
            }
        }

        private async void RenameMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (PS5FilesListBox.SelectedItem is PS5FileItem item)
            {
                string? newName = await ShowInputDialogAsync("Rename", "New name:", item.Name);
                if (!string.IsNullOrWhiteSpace(newName))
                {
                    try
                    {
                        string newPath = Path.Combine(Path.GetDirectoryName(item.FullPath)?.Replace("\\", "/") ?? "", newName).Replace("\\", "/");
                        await _protocol.RenameAsync(item.FullPath, newPath);
                        Log($"✅ Renamed {item.Name} to {newName}");
                        await LoadPS5DirectoryAsync(_currentPS5Path);
                    }
                    catch (Exception ex) { await ShowMessageAsync($"Rename failed: {ex.Message}", "Error"); Log($"❌ Rename failed: {ex.Message}"); }
                }
            }
        }

        private async void CopyMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (PS5FilesListBox.SelectedItem is PS5FileItem item)
            {
                string? destPath = await ShowInputDialogAsync("Copy To", "Destination path:", _currentPS5Path + "/" + item.Name);
                if (destPath != null)
                {
                    try { await _protocol.CopyFileAsync(item.FullPath, destPath); Log($"✅ Copied {item.Name} to {destPath}"); await LoadPS5DirectoryAsync(_currentPS5Path); }
                    catch (Exception ex) { await ShowMessageAsync($"Copy failed: {ex.Message}", "Error"); Log($"❌ Copy failed: {ex.Message}"); }
                }
            }
        }

        private async void MoveMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (PS5FilesListBox.SelectedItem is PS5FileItem item)
            {
                string? destPath = await ShowInputDialogAsync("Move To", "Destination path:", _currentPS5Path + "/" + item.Name);
                if (destPath != null)
                {
                    try { await _protocol.RenameAsync(item.FullPath, destPath); Log($"✅ Moved {item.Name} to {destPath}"); await LoadPS5DirectoryAsync(_currentPS5Path); }
                    catch (Exception ex) { await ShowMessageAsync($"Move failed: {ex.Message}", "Error"); Log($"❌ Move failed: {ex.Message}"); }
                }
            }
        }

        private async void DeleteMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (_isDeleting) return;
            if (PS5FilesListBox.SelectedItem is PS5FileItem item)
            {
                if (await ShowConfirmAsync($"Delete {item.Name}?"))
                {
                    _isDeleting = true;
                    try
                    {
                        if (item.IsDirectory) { Log($"🗑️ Deleting folder: {item.Name}"); bool ok = await _protocol.DeleteDirAsync(item.FullPath); Log(ok ? $"✅ Folder deletion complete: {item.Name}" : $"❌ Folder deletion failed: {item.Name}"); }
                        else { Log($"🗑️ Deleting file: {item.Name}"); await _protocol.DeleteFileAsync(item.FullPath); Log($"✅ File deletion complete: {item.Name}"); }
                        await Task.Delay(1500);
                        await LoadPS5DirectoryAsync(_currentPS5Path);
                    }
                    catch (Exception ex) { await ShowMessageAsync($"Delete failed: {ex.Message}", "Error"); Log($"❌ Delete failed: {ex.Message}"); }
                    finally { _isDeleting = false; }
                }
            }
        }

        private async void DeleteSelectedMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (_isDeleting) return;
            var selectedItems = (PS5FilesListBox.SelectedItems ?? Array.Empty<object>()).Cast<PS5FileItem>().ToList();
            if (selectedItems.Count == 0) { await ShowMessageAsync("No items selected."); return; }

            int folderCount = selectedItems.Count(i => i.IsDirectory);
            int fileCount = selectedItems.Count(i => !i.IsDirectory);
            string message = $"Delete {selectedItems.Count} items?\n\n";
            if (folderCount > 0) message += $"📁 {folderCount} folders\n";
            if (fileCount > 0) message += $"📄 {fileCount} files";

            if (!await ShowConfirmAsync(message, "Confirm Bulk Delete")) return;

            _isDeleting = true;
            try
            {
                Log($"🗑️ Starting bulk delete of {selectedItems.Count} items...");
                int successCount = 0, failCount = 0;
                foreach (var item in selectedItems)
                {
                    try
                    {
                        bool ok;
                        if (item.IsDirectory) ok = await _protocol.DeleteDirAsync(item.FullPath);
                        else ok = await _protocol.DeleteFileAsync(item.FullPath);
                        if (ok) { successCount++; Log($"✅ Deleted: {item.Name}"); }
                        else { failCount++; Log($"❌ Failed to delete {item.Name}: server error"); }
                        await Task.Delay(300);
                    }
                    catch (Exception ex) { Log($"❌ Failed to delete {item.Name}: {ex.Message}"); failCount++; }
                }
                Log($"🗑️ Bulk delete complete: {successCount} succeeded, {failCount} failed");
                await Task.Delay(500);
                await LoadPS5DirectoryAsync(_currentPS5Path);
                await ShowMessageAsync($"Bulk delete complete!\n\n✅ {successCount} deleted\n❌ {failCount} failed");
            }
            finally { _isDeleting = false; }
        }

        private async void DownloadMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (PS5FilesListBox.SelectedItem is PS5FileItem item)
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;

                if (item.IsDirectory)
                {
                    var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = $"Select destination for '{item.Name}' folder" });
                    if (folders.Count == 0) return;
                    var folderPath = folders[0].TryGetLocalPath();
                    if (folderPath == null) return;
                    string localBasePath = Path.Combine(folderPath, item.Name);

                    try
                    {
                        Log($"⬇️ Downloading folder: {item.FullPath} → {localBasePath}");
                        _uploadCancellation = new CancellationTokenSource();
                        var ct = _uploadCancellation.Token;
                        int dlCallCount = 0;
                        var progress = new InlineProgress<DownloadFolderProgress>(p =>
                        {
                            if (++dlCallCount % 8 != 0 && p.Phase != "Complete") return;
                            Dispatcher.UIThread.Post(() =>
                            {
                                if (p.Phase == "Scanning")
                                    TotalProgressText.Text = $"Scanning: {p.FilesCompleted} files found... ({p.CurrentFile})";
                                else if (p.Phase == "Downloading")
                                {
                                    double percent = p.TotalBytes > 0 ? (double)p.BytesDownloaded / p.TotalBytes * 100 : 0;
                                    TotalProgressBar.Value = percent;
                                    TotalProgressText.Text = $"Downloading: {p.FilesCompleted}/{p.TotalFiles} files ({FormatFileSize(p.BytesDownloaded)}/{FormatFileSize(p.TotalBytes)}) [{percent:F1}%]";
                                    if (!string.IsNullOrEmpty(p.CurrentFile)) UploadFileNameText.Text = $"⬇️ {p.CurrentFile}";
                                }
                                else if (p.Phase == "Complete")
                                {
                                    TotalProgressBar.Value = 100;
                                    TotalProgressText.Text = $"Complete: {p.FilesCompleted}/{p.TotalFiles} files ({FormatFileSize(p.BytesDownloaded)})";
                                }
                            });
                        });
                        var (downloaded, failed, totalBytes) = await _protocol.DownloadFolderAsync(item.FullPath, localBasePath, _ps5IpAddress, progress, ct);
                        Log($"✅ Folder download complete: {downloaded} files ({FormatFileSize(totalBytes)}), {failed} failed");
                        await ShowMessageAsync($"Folder downloaded!\n\nFiles: {downloaded} downloaded, {failed} failed\nSize: {FormatFileSize(totalBytes)}\nSaved to: {localBasePath}", "Download Complete");
                    }
                    catch (Exception ex) { Log($"❌ Folder download error: {ex.Message}"); await ShowMessageAsync($"Folder download error: {ex.Message}", "Error"); }
                    finally { _uploadCancellation = null; TotalProgressBar.Value = 0; TotalProgressText.Text = ""; UploadFileNameText.Text = ""; }
                    return;
                }

                // Single file download
                var saveFile = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Save Downloaded File", SuggestedFileName = item.Name });
                if (saveFile != null)
                {
                    var savePath = saveFile.TryGetLocalPath();
                    if (savePath == null) return;
                    try
                    {
                        Log($"⬇️ Downloading: {item.Name} ({FormatFileSize(item.Size)})");
                        int sfCallCount = 0;
                        var progress = new InlineProgress<UploadProgress>(p =>
                        {
                            if (++sfCallCount % 8 != 0 && p.BytesSent != p.TotalBytes) return;
                            Dispatcher.UIThread.Post(() =>
                            {
                                double percent = p.TotalBytes > 0 ? (double)p.BytesSent / p.TotalBytes * 100 : 0;
                                TotalProgressBar.Value = percent;
                                TotalProgressText.Text = $"Downloading: {FormatFileSize(p.BytesSent)}/{FormatFileSize(p.TotalBytes)} ({percent:F1}%) @ {FormatFileSize((long)p.SpeedBytesPerSecond)}/s";
                            });
                        });
                        bool success = await _protocol.DownloadFileAsync(item.FullPath, savePath, progress);
                        if (success) { Log($"✅ Downloaded: {item.Name} → {savePath}"); await ShowMessageAsync($"File downloaded successfully!\n\nSaved to: {savePath}", "Success"); }
                        else { Log($"❌ Download failed: {item.Name}"); await ShowMessageAsync("Download failed", "Error"); }
                    }
                    catch (Exception ex) { Log($"❌ Download error: {ex.Message}"); await ShowMessageAsync($"Download error: {ex.Message}", "Error"); }
                    finally { TotalProgressBar.Value = 0; TotalProgressText.Text = ""; }
                }
            }
        }

        // ============================================================
        // LOG BUTTONS
        // ============================================================
        private void ClearLogButton_Click(object? sender, RoutedEventArgs e) { LogTextBox.Text = ""; Log("Log cleared"); }

        private async void CopyLogButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard != null)
                {
                    var item = new Avalonia.Input.DataTransferItem();
                    item.Set(Avalonia.Input.DataFormat.Text, LogTextBox.Text ?? "");
                    var data = new Avalonia.Input.DataTransfer();
                    data.Add(item);
                    await clipboard.SetDataAsync(data);
                    Log("📋 Log copied to clipboard!");
                }
            }
            catch (Exception ex) { Log($"⚠️ Clipboard error: {ex.Message}"); }
        }

        private async Task<PS5StorageInfo?> FetchStorageInfoWithFallbackAsync()
        {
            bool uploadsIdle = (_uploadCancellation == null || _uploadCancellation.IsCancellationRequested) && _activeTaskCount == 0;
            if (_protocol.IsConnected && uploadsIdle)
                return await _protocol.ListStorageAsync();

            var tempProtocol = new PS5Protocol();
            try
            {
                if (!await tempProtocol.ConnectAsync(_ps5IpAddress)) { Log("❌ Storage refresh: failed to open temporary connection"); return null; }
                return await tempProtocol.ListStorageAsync();
            }
            finally { tempProtocol.Dispose(); }
        }
    }
}
