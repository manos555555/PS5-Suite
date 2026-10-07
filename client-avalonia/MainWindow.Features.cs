using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace PS5Upload
{
    // Shell, Profiles, Favorites, Payload, Mount Games, Transfer History, Storage
    public partial class MainWindow
    {
        // ============================================================
        // SHELL TERMINAL
        // ============================================================
        private async Task OpenShellAsync()
        {
            try
            {
                if (_shellActive) { ShellLog("[Shell] Already connected"); return; }
                ShellLog($"[Shell] Connecting to {_ps5IpAddress}:9113...");
                bool success = await _protocol.OpenShellAsync();
                if (success)
                {
                    _shellActive = true;
                    _shellCurrentDir = "/data";
                    ShellLog($"✅ Connected to PS5 at {_ps5IpAddress}");
                    ShellLog($"PS5:{_shellCurrentDir} $");
                }
                else
                    ShellLog("[Shell] Failed to open shell session");
            }
            catch (Exception ex) { ShellLog($"[Shell] Error: {ex.Message}"); }
        }

        private async void ShellCommandInput_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key == Avalonia.Input.Key.Enter && sender is TextBox textBox)
            {
                string command = textBox.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(command)) return;
                textBox.Text = "";

                if (!_shellActive) { ShellLog("Shell not connected. Connect to PS5 first."); return; }
                ShellLog($"PS5:{_shellCurrentDir} $ {command}");

                try
                {
                    string output = await _protocol.ExecuteShellCommandAsync(command);
                    if (!string.IsNullOrEmpty(output)) ShellLog(output);
                    if (command.Trim().StartsWith("cd ") || command.Trim() == "cd")
                    {
                        try { string pwd = await _protocol.ExecuteShellCommandAsync("pwd"); if (!string.IsNullOrEmpty(pwd)) _shellCurrentDir = pwd.Trim(); } catch { }
                    }
                    ShellLog($"PS5:{_shellCurrentDir} $");
                }
                catch (Exception ex) { ShellLog($"Error: {ex.Message}"); }
            }
        }

        private void ShellLog(string message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _shellOutput.Add(message);
                if (ShellOutputListBox != null && _shellOutput.Count > 0)
                    ShellOutputListBox.ScrollIntoView(_shellOutput.Last());
            });
        }

        private void ClearShellButton_Click(object? sender, RoutedEventArgs e)
        {
            _shellOutput.Clear();
            ShellLog("PS5 Shell Terminal - Ready");
            ShellLog("Type 'help' for available commands");
            if (_shellActive) ShellLog($"PS5:{_shellCurrentDir} $");
        }

        private async void SaveShellLogButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;
                var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save Shell Log",
                    SuggestedFileName = $"ps5_shell_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
                });
                if (file != null)
                {
                    var path = file.TryGetLocalPath();
                    if (path != null)
                    {
                        File.WriteAllLines(path, _shellOutput);
                        Log($"✅ Shell log saved to {path}");
                    }
                }
            }
            catch (Exception ex) { Log($"❌ Failed to save shell log: {ex.Message}"); }
        }

        // ============================================================
        // PROFILES
        // ============================================================
        private void LoadProfiles()
        {
            try
            {
                if (File.Exists(ProfilesFileName))
                {
                    string json = File.ReadAllText(ProfilesFileName);
                    _ps5Profiles = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                    PS5ProfileComboBox.ItemsSource = _ps5Profiles.Keys.ToList();
                    if (_ps5Profiles.Count > 0) PS5ProfileComboBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex) { Log($"⚠️ Failed to load profiles: {ex.Message}"); }
        }

        private void SaveProfiles()
        {
            try
            {
                string json = System.Text.Json.JsonSerializer.Serialize(_ps5Profiles, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ProfilesFileName, json);
            }
            catch (Exception ex) { Log($"❌ Failed to save profiles: {ex.Message}"); }
        }

        private async void SaveProfileButton_Click(object? sender, RoutedEventArgs e)
        {
            string ipAddress = IpAddressTextBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(ipAddress)) { await ShowMessageAsync("Please enter a PS5 IP address first", "Error"); return; }
            string? profileName = await ShowInputDialogAsync("Save PS5 Profile", "Profile Name:", "My PS5");
            if (!string.IsNullOrWhiteSpace(profileName))
            {
                _ps5Profiles[profileName] = ipAddress;
                SaveProfiles();
                PS5ProfileComboBox.ItemsSource = _ps5Profiles.Keys.ToList();
                PS5ProfileComboBox.SelectedItem = profileName;
                Log($"💾 Saved profile: {profileName} ({ipAddress})");
            }
        }

        private async void DeleteProfileButton_Click(object? sender, RoutedEventArgs e)
        {
            if (PS5ProfileComboBox.SelectedItem is string profileName)
            {
                if (await ShowConfirmAsync($"Delete profile '{profileName}'?"))
                {
                    _ps5Profiles.Remove(profileName);
                    SaveProfiles();
                    PS5ProfileComboBox.ItemsSource = _ps5Profiles.Keys.ToList();
                    if (_ps5Profiles.Count > 0) PS5ProfileComboBox.SelectedIndex = 0;
                    Log($"🗑️ Deleted profile: {profileName}");
                }
            }
            else await ShowMessageAsync("No profile selected");
        }

        private void PS5ProfileComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (PS5ProfileComboBox.SelectedItem is string profileName && _ps5Profiles.ContainsKey(profileName))
            {
                IpAddressTextBox.Text = _ps5Profiles[profileName];
                Log($"📋 Loaded profile: {profileName}");
            }
        }

        // ============================================================
        // FAVORITES
        // ============================================================
        private void LoadFavorites()
        {
            try
            {
                if (File.Exists(FavoritesFileName))
                {
                    string json = File.ReadAllText(FavoritesFileName);
                    _favoritePaths = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new();
                    FavoritesComboBox.ItemsSource = _favoritePaths.ToList();
                }
            }
            catch (Exception ex) { Log($"⚠️ Failed to load favorites: {ex.Message}"); }
        }

        private void SaveFavorites()
        {
            try
            {
                string json = System.Text.Json.JsonSerializer.Serialize(_favoritePaths, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FavoritesFileName, json);
            }
            catch (Exception ex) { Log($"❌ Failed to save favorites: {ex.Message}"); }
        }

        private async void AddFavoriteButton_Click(object? sender, RoutedEventArgs e)
        {
            string currentPath = CurrentPathTextBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(currentPath)) { await ShowMessageAsync("No path to add to favorites"); return; }
            if (_favoritePaths.Contains(currentPath)) { await ShowMessageAsync("This path is already in favorites"); return; }
            _favoritePaths.Add(currentPath);
            SaveFavorites();
            FavoritesComboBox.ItemsSource = _favoritePaths.ToList();
            FavoritesComboBox.SelectedItem = currentPath;
            Log($"⭐ Added to favorites: {currentPath}");
        }

        private async void RemoveFavoriteButton_Click(object? sender, RoutedEventArgs e)
        {
            if (FavoritesComboBox.SelectedItem is string selectedPath)
            {
                if (await ShowConfirmAsync($"Remove from favorites?\n{selectedPath}"))
                {
                    _favoritePaths.Remove(selectedPath);
                    SaveFavorites();
                    FavoritesComboBox.ItemsSource = _favoritePaths.ToList();
                    if (_favoritePaths.Count > 0) FavoritesComboBox.SelectedIndex = 0;
                    Log($"🗑️ Removed from favorites: {selectedPath}");
                }
            }
            else await ShowMessageAsync("No favorite selected");
        }

        // ============================================================
        // SAVED NAS CONNECTIONS
        // ============================================================
        private void LoadNasConnections()
        {
            try
            {
                if (File.Exists(NasFileName))
                {
                    string json = File.ReadAllText(NasFileName);
                    _nasSaved = System.Text.Json.JsonSerializer.Deserialize<List<NasManager.SavedConnection>>(json) ?? new();
                }
            }
            catch (Exception ex) { Log($"⚠️ Failed to load NAS connections: {ex.Message}"); }
        }

        private void SaveNasConnections()
        {
            try
            {
                string json = System.Text.Json.JsonSerializer.Serialize(_nasSaved, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(NasFileName, json);
            }
            catch (Exception ex) { Log($"❌ Failed to save NAS connections: {ex.Message}"); }
        }

        private async void FavoritesComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (FavoritesComboBox.SelectedItem is string favoritePath && !string.IsNullOrWhiteSpace(favoritePath))
            {
                Log($"⭐ Navigating to favorite: {favoritePath}");
                CurrentPathTextBox.Text = favoritePath;
                await LoadPS5DirectoryAsync(favoritePath);
            }
        }

        // ============================================================
        // PAYLOAD
        // ============================================================
        private void AutoSendPayloadCheckBox_Changed(object? sender, RoutedEventArgs e)
        {
            if (AutoSendPayloadCheckBox != null) { _autoSendPayload = AutoSendPayloadCheckBox.IsChecked == true; SaveSettings(); }
        }

        private async void BrowsePayloadButton_Click(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Payload File",
                FileTypeFilter = new[] { new FilePickerFileType("Payload files") { Patterns = new[] { "*.elf", "*.bin" } } }
            });
            if (files.Count > 0)
            {
                var path = files[0].TryGetLocalPath();
                if (path != null) { _payloadPath = path; PayloadPathTextBox.Text = _payloadPath; SaveSettings(); Log($"✅ Payload file selected: {_payloadPath}"); }
            }
        }

        private void PayloadPortTextBox_Changed(object? sender, TextChangedEventArgs e)
        {
            if (PayloadPortTextBox != null && int.TryParse(PayloadPortTextBox.Text, out int port)) { _payloadPort = port; SaveSettings(); }
        }

        private async void SendPayloadButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                string ipAddress = IpAddressTextBox.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(ipAddress)) { await ShowMessageAsync("Please enter PS5 IP address first", "Error"); return; }
                if (string.IsNullOrEmpty(_payloadPath) || !File.Exists(_payloadPath)) { await ShowMessageAsync("Please select a valid payload file first", "Error"); return; }

                SendPayloadButton.IsEnabled = false;
                Log($"📤 Sending payload to {ipAddress}:{_payloadPort}...");
                var progress = new InlineProgress<long>(bytes => { });
                bool success = await PS5Protocol.SendPayloadAsync(ipAddress, _payloadPath, _payloadPort, progress);

                if (success)
                {
                    Log($"✅ Payload sent successfully ({new FileInfo(_payloadPath).Length} bytes)");
                    await ShowMessageAsync($"Payload sent successfully!\n\nFile: {Path.GetFileName(_payloadPath)}\nSize: {new FileInfo(_payloadPath).Length} bytes", "Success");
                }
                else
                {
                    Log("❌ Failed to send payload");
                    await ShowMessageAsync($"Failed to send payload to {ipAddress}:{_payloadPort}\n\nMake sure GoldHEN is running.", "Connection Failed");
                }
                SendPayloadButton.IsEnabled = true;
            }
            catch (Exception ex) { Log($"❌ Payload send error: {ex.Message}"); await ShowMessageAsync($"Error sending payload: {ex.Message}", "Error"); SendPayloadButton.IsEnabled = true; }
        }

        private async void UpdatePayloadButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (!_protocol.IsConnected) { await ShowMessageAsync("Connect to the PS5 first — self-update goes through the running server.", "Error"); return; }
                if (string.IsNullOrEmpty(_payloadPath) || !File.Exists(_payloadPath)) { await ShowMessageAsync("Please select a payload ELF file first", "Error"); return; }

                UpdatePayloadButton.IsEnabled = false;
                SendPayloadButton.IsEnabled = false;
                await DeployUpdateElfAsync(_payloadPath);
            }
            catch (Exception ex) { Log($"❌ Self-update error: {ex.Message}"); }
            finally { UpdatePayloadButton.IsEnabled = true; SendPayloadButton.IsEnabled = true; }
        }

        // Stage an ELF on the console and ask the running server to swap
        // itself in, then reconnect. Returns true once the new instance is up.
        private async Task DeployUpdateElfAsync(string localElf)
        {
            Log("📦 Staging update ELF → /data/ps5suite/update.elf ...");
            await _protocol.CreateDirAsync("/data/ps5suite");   // ok if it exists
            bool staged = await _protocol.UploadFileAsync(localElf, "/data/ps5suite/update.elf");
            if (!staged)
            {
                Log("❌ Failed to stage the update ELF");
                await ShowMessageAsync("Failed to upload the update ELF to the PS5", "Error");
                return;
            }

            Log($"🔄 Requesting self-update (loader port {_payloadPort})...");
            var (ok, msg) = await _protocol.SelfUpdateAsync("/data/ps5suite/update.elf", _payloadPort);
            if (!ok)
            {
                Log($"❌ Self-update refused: {msg}");
                await ShowMessageAsync($"Self-update failed:\n{msg}", "Error");
                return;
            }

            Log($"✅ {msg} — new instance spawning, reconnecting...");
            _protocol.Disconnect();   // the old process is about to die
            await Task.Delay(6000);       // loader spawn + stale-sweep + bind
            await ConnectToPS5Async();
            Log(_protocol.IsConnected
                ? "✅ Self-update complete — new payload is serving"
                : "⚠️ Reconnect failed — the new instance may still be starting; press Connect");
        }

        // ============================================================
        // GITHUB PAYLOAD UPDATE
        // The PC does the HTTPS part (free in .NET); the console only
        // stages + swaps — no TLS needed inside the payload.
        // ============================================================
        private const string GitHubRepo = "manos555555/PS5-Suite";
        private const string PayloadAssetName = "ps5_suite_server.elf";

        private static HttpClient? _githubHttp;
        private static HttpClient GitHubClient
        {
            get
            {
                if (_githubHttp == null)
                {
                    _githubHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                    _githubHttp.DefaultRequestHeaders.UserAgent.ParseAdd("PS5-Suite-Client");
                }
                return _githubHttp;
            }
        }

        private async void CheckUpdateButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (!_protocol.IsConnected) { await ShowMessageAsync("Connect to the PS5 first.", "Error"); return; }

                CheckUpdateButton.IsEnabled = false;
                Log("⬆️ Checking GitHub for a newer payload...");

                var release = await GitHubClient.GetAsync(
                    $"https://api.github.com/repos/{GitHubRepo}/releases/latest");
                if (!release.IsSuccessStatusCode)
                {
                    Log($"❌ GitHub API returned {(int)release.StatusCode} — is there a release with a .elf asset?");
                    await ShowMessageAsync("Could not query GitHub releases. Make sure a release exists with the payload .elf attached.", "Update Check");
                    return;
                }

                string tag = "", downloadUrl = "";
                using (var doc = JsonDocument.Parse(await release.Content.ReadAsStringAsync()))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("tag_name", out var t)) tag = t.GetString() ?? "";
                    if (root.TryGetProperty("assets", out var assets))
                    {
                        foreach (var a in assets.EnumerateArray())
                        {
                            var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            if (name.EndsWith(".elf", StringComparison.OrdinalIgnoreCase))
                            {
                                downloadUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                                break;
                            }
                        }
                    }
                }
                if (string.IsNullOrEmpty(downloadUrl))
                {
                    Log("❌ No .elf asset found in the latest release");
                    await ShowMessageAsync($"Latest release {tag} has no .elf asset.\nAttach {PayloadAssetName} to the release.", "Update Check");
                    return;
                }

                var info = await _protocol.GetSystemInfoAsync();
                string current = info?.ServerVersion ?? "0.0.0";
                Version.TryParse(tag.TrimStart('v', 'V'), out var latestVer);
                Version.TryParse(current, out var currentVer);
                if (latestVer != null && currentVer != null && latestVer <= currentVer)
                {
                    Log($"✅ Payload is up to date (v{current})");
                    await ShowMessageAsync($"The console already runs the latest payload (v{current}).", "Update Check");
                    return;
                }

                Log($"⬇️ New payload {tag} found (console: v{current}) — downloading...");
                var elf = await GitHubClient.GetByteArrayAsync(downloadUrl);
                if (elf.Length < 32 * 1024)
                {
                    Log("❌ Downloaded asset is too small to be a payload ELF");
                    await ShowMessageAsync("The downloaded asset doesn't look like a payload ELF.", "Update Check");
                    return;
                }
                string tmp = Path.Combine(Path.GetTempPath(), "ps5_suite_server_update.elf");
                await File.WriteAllBytesAsync(tmp, elf);

                await DeployUpdateElfAsync(tmp);
            }
            catch (Exception ex) { Log($"❌ Update check failed: {ex.Message}"); await ShowMessageAsync($"Update check failed:\n{ex.Message}", "Error"); }
            finally { CheckUpdateButton.IsEnabled = true; }
        }

        // ============================================================
        // MOUNT GAMES
        // ============================================================
        private async void MountGamesButton_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            MountGamesButton.IsEnabled = false;
            MountGamesButton.Content = "⏳ Mounting...";
            Log("🎮 Mount Games: Starting...");

            try
            {
                // The payload now streams RESP_PROGRESS lines (one per game);
                // show them live so long mounts never look frozen and the read
                // timeout can no longer fire mid-operation.
                int lastProgressCount = -1;
                string? result = await _protocol.MountGamesAsync(onProgress: msg =>
                {
                    int n = Interlocked.Increment(ref lastProgressCount);
                    Dispatcher.UIThread.Post(() =>
                    {
                        MountGamesButton.Content = $"⏳ {msg}";
                        if (n % 5 == 0) Log($"🎮 {msg}");
                    });
                });

                if (result != null)
                {
                    Log("🎮 Mount Games Result:");
                    foreach (var line in result.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
                        Log($"  {line.Trim()}");
                    await ShowMessageAsync(result, "Mount Games - Result");
                }
                else
                {
                    Log("❌ Mount Games: No response from PS5");
                    await ShowMessageAsync("No response from PS5. The connection may have been lost.", "Mount Games - Error");
                }
            }
            catch (Exception ex) { Log($"❌ Mount Games error: {ex.Message}"); await ShowMessageAsync($"Error mounting games: {ex.Message}", "Error"); }
            finally { MountGamesButton.Content = "🎮 Mount Games"; MountGamesButton.IsEnabled = _protocol.IsConnected; }
        }

        // ============================================================
        // TRANSFER HISTORY
        // ============================================================
        private async void RetryFailedUpload_Click(object? sender, RoutedEventArgs e)
        {
            if (FailedTransfersListBox.SelectedItem is TransferHistoryItem failedItem)
            {
                bool isUnc = LocalIo.IsUnc(failedItem.LocalPath) && NasManager.HasSession(failedItem.LocalPath);
                bool isFile = isUnc ? NasManager.IsFile(failedItem.LocalPath) : File.Exists(failedItem.LocalPath);
                bool isDir = !isFile && (isUnc ? NasManager.IsDirectory(failedItem.LocalPath) : Directory.Exists(failedItem.LocalPath));
                if (!isFile && !isDir)
                {
                    await ShowMessageAsync($"File or folder not found: {failedItem.LocalPath}\n\n(NAS items need an active session — re-add via 🌐 NAS if it was a network path.)", "Error");
                    return;
                }
                if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5. Please connect first.", "Error"); return; }

                _failedTransfers.Remove(failedItem);
                if (isFile)
                {
                    _localFiles.Add(new LocalFileItem { Name = LocalIo.GetName(failedItem.LocalPath), FullPath = failedItem.LocalPath, Size = LocalIo.GetLength(failedItem.LocalPath), IsDirectory = false, Icon = "📄" });
                }
                else
                {
                    _localFiles.Add(new LocalFileItem { Name = LocalIo.GetName(failedItem.LocalPath), FullPath = failedItem.LocalPath, Size = 0, IsDirectory = true, Icon = "📁" });
                }
                Log($"🔄 Retrying upload: {failedItem.FileName}");
                await ShowMessageAsync($"Added {failedItem.FileName} back to upload queue. Click 'Upload to PS5' to retry.", "Retry Queued");
            }
            else await ShowMessageAsync("Please select a failed transfer to retry.");
        }

        private async void RemoveFailedTransfer_Click(object? sender, RoutedEventArgs e)
        {
            if (FailedTransfersListBox.SelectedItem is TransferHistoryItem failedItem)
            {
                _failedTransfers.Remove(failedItem);
                Log($"🗑️ Removed from failed transfers: {failedItem.FileName}");
            }
            else await ShowMessageAsync("Please select a failed transfer to remove.");
        }

        private async void ClearTransferHistory_Click(object? sender, RoutedEventArgs e)
        {
            if (await ShowConfirmAsync("Clear all transfer history (completed and failed)?"))
            {
                _completedTransfers.Clear();
                _failedTransfers.Clear();
                Log("🗑️ Transfer history cleared");
            }
        }

        // ============================================================
        // STORAGE
        // ============================================================
        private async void RefreshStorageButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_isRefreshingStorage) return;
            await RefreshStorageInfoAsync();
        }

        private async Task RefreshStorageInfoAsync()
        {
            if (_isRefreshingStorage) return;
            if (string.IsNullOrWhiteSpace(_ps5IpAddress)) { Log("⚠️ Cannot refresh storage info - PS5 IP is not set"); return; }

            try
            {
                _isRefreshingStorage = true;
                Log("💾 Fetching storage info...");
                var storageInfo = await FetchStorageInfoWithFallbackAsync();
                if (storageInfo != null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        StorageInfoPanel.IsVisible = true;
                        TotalCapacityText.Text = storageInfo.TotalGB;
                        RealFreeSpaceText.Text = storageInfo.RealFreeGB;
                        ReservedSpaceText.Text = storageInfo.ReservedGB;
                        MountedGamesText.Text = storageInfo.MountedGamesGB;
                        UserDataText.Text = storageInfo.UserDataGB;
                        ulong totalUsed = storageInfo.MountedGamesSize + storageInfo.UserDataSize;
                        TotalUsedText.Text = PS5StorageInfo.FormatBytes(totalUsed);
                    });
                    Log($"✅ Storage: {storageInfo.RealFreeGB} free of {storageInfo.TotalGB} (path: {storageInfo.StoragePath})");
                }
                else Log("❌ Failed to get storage info");
            }
            catch (Exception ex) { Log($"❌ Storage info error: {ex.Message}"); }
            finally { _isRefreshingStorage = false; }
        }

        // ============================================================
        // BUY ME A COFFEE
        // ============================================================
        private void BuyMeCoffeeButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "https://buymeacoffee.com/manos555555", UseShellExecute = true });
                Log("☕ Thank you for your support!");
            }
            catch (Exception ex) { Log($"❌ Failed to open link: {ex.Message}"); }
        }

        // ============================================================
        // SEARCH INDEX
        // ============================================================
        private async void StartIndexButton_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            StartIndexButton.IsEnabled = false;
            IndexStatusText.Text = "Index Status: Starting...";
            Log("🔍 Starting index...");
            bool success = await _protocol.StartIndexAsync("/");
            if (success) { Log("✅ Indexing started"); IndexStatusText.Text = "Index Status: Indexing..."; await Task.Delay(2000); await RefreshIndexStatus(); }
            else { Log("❌ Failed to start indexing"); IndexStatusText.Text = "Index Status: Failed to start"; }
            StartIndexButton.IsEnabled = true;
        }

        private async void RefreshIndexButton_Click(object? sender, RoutedEventArgs e) => await RefreshIndexStatus();

        private async Task RefreshIndexStatus()
        {
            try
            {
                if (!_protocol.IsConnected) { IndexStatusText.Text = "Index Status: Not connected"; return; }
                string status = await _protocol.GetIndexStatusAsync();
                IndexStatusText.Text = $"Index Status: {status}";
                Log($"📊 Index status: {status}");
            }
            catch (Exception ex) { Log($"❌ Failed to get index status: {ex.Message}"); }
        }

        private async void SearchInputBox_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key == Avalonia.Input.Key.Enter && sender is TextBox textBox)
            {
                string query = textBox.Text?.Trim() ?? "";
                if (!string.IsNullOrEmpty(query)) await PerformSearch(query);
            }
        }

        private void ClearIndexSearchButton_Click(object? sender, RoutedEventArgs e)
        {
            SearchInputBox.Text = "";
            SearchResultsGrid.ItemsSource = null;
        }

        private async Task PerformSearch(string query)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            Log($"🔍 Searching: {query}");
            SearchResultsGrid.ItemsSource = null;
            var results = await _protocol.SearchIndexAsync(query);
            SearchResultsGrid.ItemsSource = results;
            Log($"✅ Found {results.Length} results");
        }

        private async void SearchResultsGrid_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            if (SearchResultsGrid.SelectedItem is SearchResult result)
            {
                string directoryPath = Path.GetDirectoryName(result.Path)?.Replace("\\", "/") ?? "/";
                Log($"📂 Navigating to: {directoryPath}");
                await LoadPS5DirectoryAsync(directoryPath);
                await Task.Delay(100);
                foreach (var item in _ps5FilesFiltered)
                {
                    if (item.Name == result.Name) { PS5FilesListBox.SelectedItem = item; PS5FilesListBox.ScrollIntoView(item); break; }
                }
            }
        }

        // ============================================================
        // FAN CONTROL (etaHEN/Elf Arsenal compatible)
        // ============================================================
        private async void RefreshFanThreshold_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            try
            {
                Log("🌀 Getting fan threshold...");
                var (success, threshold) = await _protocol.GetFanThresholdAsync();
                if (success && threshold > 0)
                {
                    FanCurrentThresholdText.Text = threshold.ToString();
                    FanThresholdSlider.Value = threshold;
                    FanSliderValueText.Text = $"{threshold}°C";
                    Log($"✅ Fan threshold: {threshold}°C");
                }
                else if (success)
                {
                    FanCurrentThresholdText.Text = "auto";
                    Log("🌀 Fan threshold: not set — system-managed (PS5 auto)");
                }
                else
                {
                    FanCurrentThresholdText.Text = "—";
                    Log("❌ Failed to get fan threshold");
                }
            }
            catch (Exception ex) { Log($"❌ Fan error: {ex.Message}"); }
        }

        private async void SetFanThreshold_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            try
            {
                int temp = (int)FanThresholdSlider.Value;
                Log($"🌀 Setting fan threshold to {temp}°C...");
                var (success, message) = await _protocol.SetFanThresholdAsync(temp);
                if (success)
                {
                    FanCurrentThresholdText.Text = temp.ToString();
                    Log($"✅ {message}");
                    await ShowMessageAsync(message, "Fan Control");
                }
                else
                {
                    Log($"❌ Failed: {message}");
                    await ShowMessageAsync($"Failed to set fan threshold: {message}", "Error");
                }
            }
            catch (Exception ex) { Log($"❌ Fan error: {ex.Message}"); }
        }

        private void FanThresholdSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (FanSliderValueText != null)
                FanSliderValueText.Text = $"{(int)e.NewValue}°C";
        }

        private async void FanCool_Click(object? sender, RoutedEventArgs e)
        {
            FanThresholdSlider.Value = 50;
            await SetFanThresholdInternal(50);
        }

        private async void FanBalanced_Click(object? sender, RoutedEventArgs e)
        {
            FanThresholdSlider.Value = 60;
            await SetFanThresholdInternal(60);
        }

        private async void FanQuiet_Click(object? sender, RoutedEventArgs e)
        {
            FanThresholdSlider.Value = 70;
            await SetFanThresholdInternal(70);
        }

        private async Task SetFanThresholdInternal(int temp)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            try
            {
                Log($"🌀 Setting fan threshold to {temp}°C...");
                var (success, message) = await _protocol.SetFanThresholdAsync(temp);
                if (success)
                {
                    FanCurrentThresholdText.Text = temp.ToString();
                    Log($"✅ {message}");
                }
                else
                {
                    Log($"❌ Failed: {message}");
                }
            }
            catch (Exception ex) { Log($"❌ Fan error: {ex.Message}"); }
        }

        // ============================================================
        // POWER & DEVICES (reboot/shutdown, USB drives, controllers)
        // ============================================================
        private async void PowerReboot_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            if (!await ShowConfirmAsync("Reboot the PS5 now?\nThe connection will drop.")) return;
            try
            {
                Log("⚡ Sending reboot...");
                var (success, message) = await _protocol.PowerActionAsync("reboot");
                Log(success ? "✅ Reboot command accepted — console is restarting" : $"❌ Reboot failed: {message}");
            }
            catch (Exception ex) { Log($"❌ Power error: {ex.Message}"); }
        }

        private async void PowerShutdown_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            if (!await ShowConfirmAsync("Shut down the PS5 now?\nThe connection will drop.")) return;
            try
            {
                Log("⚡ Sending shutdown...");
                var (success, message) = await _protocol.PowerActionAsync("shutdown");
                Log(success ? "✅ Shutdown command accepted — console is powering off" : $"❌ Shutdown failed: {message}");
            }
            catch (Exception ex) { Log($"❌ Power error: {ex.Message}"); }
        }

        private readonly List<PS5UsbDrive> _usbDrives = new();

        private async void RefreshUsbDrives_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            try
            {
                Log("🔌 Scanning USB drives...");
                var drives = await _protocol.ListUsbDrivesAsync();
                _usbDrives.Clear();
                _usbDrives.AddRange(drives);
                var items = drives.Select(d =>
                    $"🔌 {d.MountPath}   ({d.FsType}, {d.Device})   {d.FreeGB} free / {d.TotalGB}").ToList();
                UsbDrivesList.ItemsSource = items.Count > 0 ? items : new List<string> { "No USB drives mounted" };
                Log(drives.Count > 0 ? $"✅ {drives.Count} USB drive(s)" : "ℹ️ No USB drives mounted");
            }
            catch (Exception ex) { Log($"❌ USB error: {ex.Message}"); }
        }

        private async void BrowseUsbDrive_Click(object? sender, RoutedEventArgs e)
        {
            int idx = UsbDrivesList.SelectedIndex;
            if (idx < 0 || idx >= _usbDrives.Count) { await ShowMessageAsync("Select a USB drive first.", "USB"); return; }
            string path = _usbDrives[idx].MountPath;
            Log($"📂 Navigating to {path}");
            _currentPS5Path = path;
            NavFiles.IsChecked = true;
            NavButton_Click(NavFiles, e);
            await LoadPS5DirectoryAsync(path);
        }

        private async void RefreshPadInfo_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            try
            {
                Log("🎮 Reading controller info...");
                string? info = await _protocol.GetPadInfoAsync();
                if (info == null) { PadInfoText.Text = "—"; Log("❌ Pad info unavailable"); return; }

                var kv = info.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                             .Select(l => l.Split('=', 2))
                             .Where(p => p.Length == 2)
                             .ToDictionary(p => p[0], p => p[1]);

                if (kv.TryGetValue("error", out var err))
                {
                    PadInfoText.Text = $"⚠️ {err}";
                    Log($"🎮 Pad: {err}");
                    return;
                }

                int handle = int.TryParse(kv.GetValueOrDefault("handle", "-1"), out var h) ? h : -1;
                if (handle < 0)
                {
                    PadInfoText.Text = "No controller connected";
                    Log($"🎮 No controller connected (handle={handle} init={kv.GetValueOrDefault("init", "?")} user={kv.GetValueOrDefault("user", "?")})");
                    if (kv.TryGetValue("tries", out var tries))
                        Log($"🎮 open attempts: {tries}");
                    return;
                }

                // The info struct layout varies by SDK rev — show what we know
                // for sure plus the raw dump so offsets can be mapped.
                string hex = kv.GetValueOrDefault("infohex", "");
                string summary = $"Handle: {handle}   User: {kv.GetValueOrDefault("user", "?")}\n";
                if (hex.Length >= 16)
                    summary += $"Connected flag: {hex.Substring(12, 2)}   Type: {hex.Substring(10, 2)}\n";
                summary += $"Info rc: {kv.GetValueOrDefault("info", "?")}   State rc: {kv.GetValueOrDefault("state", "?")}";
                PadInfoText.Text = summary;
                Log($"🎮 Pad info: handle={handle} user={kv.GetValueOrDefault("user", "?")}");
                if (!string.IsNullOrEmpty(hex))
                    Log($"🎮 infohex: {hex}");
            }
            catch (Exception ex) { Log($"❌ Pad error: {ex.Message}"); }
        }

        // ============================================================
        // PKG INSTALL (ps5upload-style)
        // ============================================================
        private async void InstallPkg_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            string pkgPath = PkgPathTextBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(pkgPath))
            {
                await ShowMessageAsync("Please enter a PKG path", "Error");
                return;
            }

            // Multiple pkgs may be queued: the pickers join them with ';'.
            // Entries can be PS5 paths (/user/...), HTTP URLs, or LOCAL files —
            // local files are uploaded to /user/data/pkg/ first.
            var pkgPaths = pkgPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            try
            {
                string lastResult = "";
                bool anyOk = false;
                foreach (var rawPath in pkgPaths)
                {
                    string p = rawPath;
                    if (!p.StartsWith('/') && !p.Contains("://") && File.Exists(p))
                    {
                        // Local PC file — try stream-install first: we host the
                        // file on a small HTTP server and the payload's localhost
                        // responder proxies ranged GETs to it, so the console
                        // installs on the fly without staging anything to disk.
                        // Falls back to staged upload if streaming can't start.
                        string? url = await TryServePkgAsync(p);
                        if (url != null)
                        {
                            Log($"📦 Streaming install: {Path.GetFileName(p)} (no staging)");
                            PkgInstallStatusText.Text = $"Starting install ({Array.IndexOf(pkgPaths, rawPath) + 1}/{pkgPaths.Length})...";
                            PkgInstallStatusText.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#FFC107"));
                            var (streamOk, streamRes) = await _protocol.InstallPkgAsync(url);
                            if (streamOk)
                            {
                                lastResult = streamRes;
                                anyOk = true;
                                Log($"✅ PKG install queued: {streamRes}");
                                continue;
                            }
                            Log($"⚠️ Stream install failed ({streamRes}) — staging on PS5 instead");
                        }
                        string remotePath = "/user/data/pkg/" + Path.GetFileName(p);
                        if (!await UploadPkgToPs5Async(p, remotePath))
                            continue;   // error already reported
                        _pkgStagedPaths.Add(remotePath);   // deleted after successful install
                        p = remotePath;
                    }

                    Log($"📦 Installing PKG: {p}");
                    PkgInstallStatusText.Text = $"Starting install ({Array.IndexOf(pkgPaths, rawPath) + 1}/{pkgPaths.Length})...";
                    PkgInstallStatusText.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#FFC107"));

                    var (success, result) = await _protocol.InstallPkgAsync(p);
                    lastResult = result;
                    if (success)
                    {
                        anyOk = true;
                        Log($"✅ PKG install queued: {result}");
                    }
                    else
                    {
                        Log($"❌ PKG install failed: {p} — {result}");
                        await ShowMessageAsync($"Failed to install PKG:\n{p}\n\n{result}", "Error");
                    }
                }

                if (anyOk)
                {
                    PkgInstallStatusText.Text = $"Installing: {lastResult}";
                    PkgInstallStatusText.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#28A745"));
                    Log($"✅ PKG install started: {lastResult}");

                    // Start polling for status
                    _ = PollPkgInstallStatus();
                }
                else
                {
                    PkgInstallStatusText.Text = $"Error: {lastResult}";
                    PkgInstallStatusText.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#DC3545"));
                    Log($"❌ PKG install failed: {lastResult}");
                }
            }
            catch (Exception ex)
            {
                PkgInstallStatusText.Text = $"Error: {ex.Message}";
                Log($"❌ PKG install error: {ex.Message}");
            }
        }

        // ============================================================
        // PKG STREAM-INSTALL + STAGED CLEANUP
        // ============================================================
        private readonly List<string> _pkgStagedPaths = new();
        private PkgHttpServer? _pkgServer;

        /// <summary>Host a local PKG on the embedded HTTP server; returns the URL the
        /// payload can proxy (http://&lt;pc-ip&gt;:&lt;port&gt;/name.pkg), or null if the
        /// PC address can't be determined or the server fails its own self-test.</summary>
        private async Task<string?> TryServePkgAsync(string localPath)
        {
            var pcIp = _protocol.LocalIp;
            if (string.IsNullOrEmpty(pcIp)) return null;
            _pkgServer ??= new PkgHttpServer { OnRequest = m => Log($"    [pkg-http] {m}") };
            var urlPath = _pkgServer.AddFile(localPath);

            // Self-test: ranged GET on loopback. If our own server can't serve
            // bytes, don't bother the console with the URL — fall back to staging.
            try
            {
                using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_pkgServer.Port}{urlPath}");
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 65535);
                using var resp = await hc.SendAsync(req);
                if ((int)resp.StatusCode != 206 && (int)resp.StatusCode != 200)
                {
                    Log($"⚠️ PKG stream self-test failed ({(int)resp.StatusCode})");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Log($"⚠️ PKG stream self-test failed: {ex.Message}");
                return null;
            }
            return $"http://{pcIp}:{_pkgServer.Port}{urlPath}";
        }



        /// <summary>Delete PKGs that were staged in /user/data/pkg/ once the install
        /// reported success — keeps the console's disk clean.</summary>
        private async Task CleanupStagedPkgsAsync()
        {
            if (_pkgStagedPaths.Count == 0) return;
            foreach (var rp in _pkgStagedPaths.ToList())
            {
                try
                {
                    if (await _protocol.DeleteFileAsync(rp))
                        Log($"🧹 Staged PKG removed: {Path.GetFileName(rp)}");
                    else
                        Log($"⚠️ Could not remove staged PKG: {rp}");
                }
                catch { }
                _pkgStagedPaths.Remove(rp);
            }
        }

        private async Task PollPkgInstallStatus()
        {
            while (_protocol.IsConnected)
            {
                await Task.Delay(2000);
                var status = await _protocol.GetPkgInstallStatusAsync();
                
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    PkgInstallStatusText.Text = status.StatusText;
                    
                    switch (status.State)
                    {
                        case PkgInstallState.InProgress:
                            PkgInstallProgressBar.Value = status.Progress;
                            PkgInstallStatusText.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#FFC107"));
                            break;
                        case PkgInstallState.Done:
                            PkgInstallProgressBar.Value = 100;
                            PkgInstallStatusText.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#28A745"));
                            Log($"✅ PKG installed: {status.ContentId}");
                            _ = CleanupStagedPkgsAsync();
                            break;
                        case PkgInstallState.Error:
                            PkgInstallStatusText.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#DC3545"));
                            Log($"❌ PKG install error: {status.Error}");
                            break;
                        case PkgInstallState.Idle:
                            PkgInstallProgressBar.Value = 0;
                            PkgInstallStatusText.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#808080"));
                            break;
                    }
                });
                
                if (status.State == PkgInstallState.Done || status.State == PkgInstallState.Error || status.State == PkgInstallState.Idle)
                    break;
            }
        }

        private async void CheckPkgStatus_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            try
            {
                var status = await _protocol.GetPkgInstallStatusAsync();
                PkgInstallStatusText.Text = status.StatusText;
                PkgInstallProgressBar.Value = status.Progress;
                Log($"📦 PKG status: {status.StatusText}");
            }
            catch (Exception ex) { Log($"❌ PKG status error: {ex.Message}"); }
        }

        private async void BrowsePkgPath_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }

            // Modal PS5 file picker inside the Tools tab — no navigation away.
            var picker = new PkgPickerWindow(_protocol, "/user/data");
            await picker.ShowDialog(this);
            if (picker.SelectedPaths is { Count: > 0 } paths)
            {
                PkgPathTextBox.Text = string.Join(";", paths);
                Log(paths.Count == 1
                    ? $"📦 PKG selected: {paths[0]}"
                    : $"📦 {paths.Count} PKGs selected (will install in order)");
            }
        }

        // "Browse PC" — pick local .pkg file(s); Install PKG uploads them to
        // /user/data/pkg/ automatically before installing.
        private async void BrowsePcPkg_Click(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select PKG file(s) on this PC",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("PS5 Packages") { Patterns = new[] { "*.pkg" } },
                    FilePickerFileTypes.All
                }
            });

            var paths = files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Cast<string>().ToList();
            if (paths.Count > 0)
            {
                PkgPathTextBox.Text = string.Join(";", paths);
                Log(paths.Count == 1
                    ? $"💻 Local PKG selected: {paths[0]} (will upload on install)"
                    : $"💻 {paths.Count} local PKGs selected (will upload + install in order)");
            }
        }

        // Upload a local pkg to the PS5 staging dir with live progress in the PKG panel.
        private async Task<bool> UploadPkgToPs5Async(string localPath, string remotePath)
        {
            try
            {
                Log($"📤 Uploading PKG for install: {Path.GetFileName(localPath)}");
                PkgInstallStatusText.Text = $"Uploading {Path.GetFileName(localPath)}...";
                PkgInstallStatusText.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#FFC107"));

                try { await _protocol.CreateDirAsync("/user/data/pkg"); } catch { /* exists */ }

                // InlineProgress keeps reports off the UI thread; throttle the
                // actual UI post to ~10Hz so big PKGs don't flood the dispatcher.
                int pkgCallCount = 0;
                var progress = new InlineProgress<UploadProgress>(p =>
                {
                    if (++pkgCallCount % 8 != 0 && p.BytesSent != p.TotalBytes) return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (p.TotalBytes > 0)
                        {
                            PkgInstallProgressBar.Value = (double)p.BytesSent / p.TotalBytes * 100;
                            PkgInstallStatusText.Text = $"Uploading: {FileUtils.FormatFileSize(p.BytesSent)} / {FileUtils.FormatFileSize(p.TotalBytes)}";
                        }
                    });
                });

                bool ok;
                var fileInfo = new FileInfo(localPath);
                if (fileInfo.Length > ChunkThresholdBytes)
                {
                    // Same parallel-chunked upload as the file-transfer path:
                    // N lanes each pwrite() their own byte range of the file.
                    ok = await UploadFileParallelAsync(localPath, remotePath);
                }
                else
                {
                    // Upload on a pooled lane, NOT the shared command socket —
                    // the main connection stays free so fan/status/browse keep
                    // working during the upload (and nothing can interleave bytes
                    // into the chunk stream).
                    PS5Protocol? lane = await AcquireUploadConnectionAsync();
                    ok = false;
                    try
                    {
                        ok = await lane.UploadFileAsync(localPath, remotePath, progress);
                    }
                    finally
                    {
                        if (lane.IsConnected) ReleaseUploadConnection(lane); else DestroyConnection(lane);
                    }
                }
                if (!ok)
                {
                    PkgInstallStatusText.Text = "Upload failed";
                    PkgInstallStatusText.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#DC3545"));
                    Log($"❌ PKG upload failed: {Path.GetFileName(localPath)}");
                    return false;
                }

                Log("✅ PKG uploaded, starting install...");
                PkgInstallProgressBar.Value = 0;
                return true;
            }
            catch (Exception ex)
            {
                PkgInstallStatusText.Text = $"Error: {ex.Message}";
                Log($"❌ Upload error: {ex.Message}");
                return false;
            }
        }

        // Parallel-chunked single-file upload — same worker pattern the main
        // file-transfer path uses: each lane pwrite()s its own byte range, so
        // a multi-GB PKG saturates the link instead of a single stream.
        private async Task<bool> UploadFileParallelAsync(string localPath, string remotePath)
        {
            var fileInfo = new FileInfo(localPath);
            string fileName = Path.GetFileName(localPath);
            long total = fileInfo.Length;

            int maxParallelChunks = total >= HugeFileThresholdBytes ? MaxParallelChunksForHugeFile : MaxParallelChunksForLargeFile;
            long chunkSize = total >= HugeFileThresholdBytes ? HugeFileChunkSizeBytes : DefaultChunkSizeBytes;
            long scaledChunk = total / maxParallelChunks;
            long minChunk = 64L * 1024 * 1024;
            if (scaledChunk < minChunk) scaledChunk = minChunk;
            if (scaledChunk < chunkSize) chunkSize = scaledChunk;

            long totalChunks = (total + chunkSize - 1) / chunkSize;
            int workerCount = (int)Math.Min(totalChunks, Math.Max(1, maxParallelChunks));
            Log($"⬆️ Uploading {fileName} chunked: {workerCount} lanes × {FileUtils.FormatFileSize(chunkSize)}");

            var chunkBytes = new ConcurrentDictionary<long, long>();
            long lastUiPost = 0;
            int nextChunkIndex = -1;
            Exception? workerError = null;
            var chunk0Ready = new SemaphoreSlim(0, 1);
            bool chunk0Released = false;
            var chunk0Lock = new object();

            void ReleaseChunk0Once()
            {
                lock (chunk0Lock) { if (!chunk0Released) { chunk0Released = true; chunk0Ready.Release(); } }
            }

            var workers = new List<Task>(workerCount);
            for (int wid = 0; wid < workerCount; wid++) workers.Add(RunWorker());
            try { await Task.WhenAll(workers); }
            catch { /* recorded in workerError */ }
            if (workerError != null) { Log($"❌ {workerError.Message}"); return false; }
            return true;

            async Task RunWorker()
            {
                PS5Protocol? wConn = null;
                try
                {
                    wConn = await AcquireUploadConnectionAsync();
                    while (true)
                    {
                        int ci = Interlocked.Increment(ref nextChunkIndex);
                        if (ci >= totalChunks || workerError != null) break;
                        if (ci > 0) { await chunk0Ready.WaitAsync(); chunk0Ready.Release(); }
                        Action? cb = ci == 0 ? ReleaseChunk0Once : null;
                        try
                        {
                            long offset = (long)ci * chunkSize;
                            long size = Math.Min(chunkSize, total - offset);
                            var prog = new InlineProgress<UploadProgress>(p =>
                            {
                                long sent = p.BytesSent - offset;
                                if (sent < 0) sent = 0; if (sent > size) sent = size;
                                chunkBytes[offset] = sent;
                                long now = Environment.TickCount64;
                                if (now - lastUiPost < 200 && sent != size) return;
                                Interlocked.Exchange(ref lastUiPost, now);
                                long agg = chunkBytes.Values.Sum();
                                Dispatcher.UIThread.Post(() =>
                                {
                                    PkgInstallProgressBar.Value = (double)agg / total * 100;
                                    PkgInstallStatusText.Text = $"Uploading: {FileUtils.FormatFileSize(agg)} / {FileUtils.FormatFileSize(total)}";
                                });
                            });
                            bool ok = await wConn.UploadFileAsync(localPath, remotePath, prog, default, offset, size, cb);
                            if (!ok) Interlocked.CompareExchange(ref workerError, new Exception($"Chunk {ci + 1}/{totalChunks} failed: {wConn.LastError}"), null);
                        }
                        finally { if (ci == 0) ReleaseChunk0Once(); }
                    }
                }
                catch (Exception ex) { Interlocked.CompareExchange(ref workerError, ex, null); }
                finally { if (wConn != null) { if (wConn.IsConnected) ReleaseUploadConnection(wConn); else DestroyConnection(wConn); } }
            }
        }

        // Right-click a local .pkg in the Local Files list: uploads it to the
        // PS5 and starts the install in one shot (ps5upload-style flow).
        private async void InstallLocalPkgMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (LocalFilesListBox.SelectedItem is not LocalFileItem item) return;
            if (item.IsDirectory || !item.Name.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
            {
                await ShowMessageAsync("Please select a .pkg file.", "Install PKG");
                return;
            }
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }

            string remotePath = "/user/data/pkg/" + item.Name;
            if (!await ShowConfirmAsync($"Upload and install this PKG?\n\n{item.FullPath}\n({item.SizeFormatted})\n\n→ {remotePath}"))
                return;

            NavTools.IsChecked = true;
            NavButton_Click(NavTools, e);
            if (!await UploadPkgToPs5Async(item.FullPath, remotePath)) return;

            PkgPathTextBox.Text = remotePath;
            InstallPkg_Click(this, new RoutedEventArgs());
        }

        private void RemoveLocalFileMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            var selected = LocalFilesListBox.SelectedItems?.Cast<LocalFileItem>().ToList();
            if (selected == null) return;
            foreach (var f in selected) _localFiles.Remove(f);
        }

        /// Right-click a NAS item → close its SMB session (logoff + disconnect).
        /// Local paths just get a nudge — nothing to disconnect.
        private void DisconnectNasMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            var selected = LocalFilesListBox.SelectedItems?.Cast<LocalFileItem>().ToList();
            string? target = selected?.Select(f => f.FullPath).FirstOrDefault(LocalIo.IsUnc)
                ?? (_localBrowsePath != null && LocalIo.IsUnc(_localBrowsePath) ? _localBrowsePath : null);
            if (target == null)
            {
                Log("ℹ️ No NAS item selected — nothing to disconnect");
                return;
            }
            if (NasManager.Disconnect(target))
            {
                Log($"🔌 NAS session closed: {NasManager.ShareRoot(target)}");
                if (_localBrowsePath != null) ExitLocalBrowse();
            }
            else Log("ℹ️ No active SMB session for that share");
        }

        // ============================================================
        // FPKG CONVERSION (LibProsperoPkg)
        // ============================================================
        private string? _fpkgSourceFolder = null;

        private async void BrowseFpkgSource_Click(object? sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;
            
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions 
            { 
                Title = "Select decrypted game folder (must contain sce_sys/)" 
            });
            
            if (folders.Count > 0)
            {
                var path = folders[0].TryGetLocalPath();
                if (path != null)
                {
                    // Check if folder contains sce_sys
                    string sceSysPath = Path.Combine(path, "sce_sys");
                    if (!Directory.Exists(sceSysPath))
                    {
                        await ShowMessageAsync(
                            "The selected folder does not contain 'sce_sys' directory.\n\n" +
                            "Please select a decrypted game folder with the proper structure.",
                            "Invalid Folder");
                        return;
                    }
                    
                    _fpkgSourceFolder = path;
                    FpkgSourcePathTextBox.Text = path;
                    Log($"📂 FPKG source: {path}");

                    // Prefer the game's own identity from sce_sys/param.json — the CNT
                    // content-id must match it or the console installer rejects the pkg.
                    string paramPath = Path.Combine(path, "sce_sys", "param.json");
                    bool filledFromParam = false;
                    if (File.Exists(paramPath))
                    {
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(paramPath));
                            var root = doc.RootElement;
                            if (root.TryGetProperty("contentId", out var cid) &&
                                !string.IsNullOrWhiteSpace(cid.GetString()))
                            {
                                FpkgContentIdTextBox.Text = cid.GetString();
                                filledFromParam = true;
                            }
                            if (root.TryGetProperty("titleId", out var tid) &&
                                !string.IsNullOrWhiteSpace(tid.GetString()))
                                FpkgTitleIdTextBox.Text = tid.GetString();
                            if (root.TryGetProperty("contentVersion", out var cv) &&
                                !string.IsNullOrWhiteSpace(cv.GetString()))
                                FpkgVersionTextBox.Text = cv.GetString();
                            if (root.TryGetProperty("localizedParameters", out var lp) &&
                                lp.TryGetProperty("en-US", out var en) &&
                                en.TryGetProperty("titleName", out var tn) &&
                                !string.IsNullOrWhiteSpace(tn.GetString()))
                                FpkgTitleTextBox.Text = tn.GetString();
                        }
                        catch { /* malformed param.json — fall back to folder-name guess */ }
                    }
                    if (filledFromParam)
                        Log("📋 Identity auto-filled from param.json");

                    // Fallback: title ID from folder name
                    string folderName = Path.GetFileName(path);
                    if (!filledFromParam && (folderName.StartsWith("PPSA") || folderName.StartsWith("CUSA")))
                    {
                        FpkgTitleIdTextBox.Text = folderName.Substring(0, Math.Min(9, folderName.Length));
                    }
                }
            }
        }

        private async void ConvertFpkg_Click(object? sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_fpkgSourceFolder) || !Directory.Exists(_fpkgSourceFolder))
            {
                await ShowMessageAsync("Please select a valid source folder first", "Error");
                return;
            }

            string contentId = FpkgContentIdTextBox.Text?.Trim() ?? "";
            string titleId = FpkgTitleIdTextBox.Text?.Trim() ?? "";
            string title = FpkgTitleTextBox.Text?.Trim() ?? "Homebrew App";
            string version = FpkgVersionTextBox.Text?.Trim() ?? "01.00";
            bool fakeSign = FpkgFakeSignCheckBox.IsChecked == true;

            if (string.IsNullOrWhiteSpace(contentId) || string.IsNullOrWhiteSpace(titleId))
            {
                await ShowMessageAsync("Please fill in Content ID and Title ID", "Error");
                return;
            }

            ConvertFpkgButton.IsEnabled = false;
            FpkgProgressBar.IsVisible = true;
            FpkgProgressBar.Value = 0;
            FpkgStatusText.Text = "Starting conversion...";

            try
            {
                Log($"🔄 Converting to FPKG: {_fpkgSourceFolder}");
                
                // Select output folder
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return;
                
                var outputFolder = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions 
                { 
                    Title = "Select output folder for PKG" 
                });
                
                if (outputFolder.Count == 0 || outputFolder[0].TryGetLocalPath() == null)
                {
                    FpkgStatusText.Text = "Conversion cancelled";
                    return;
                }
                
                string outputPath = outputFolder[0].TryGetLocalPath()!;
                
                // Build options
                var options = new LibProsperoPkg.ProsperoBuildOptions
                {
                    Mode = LibProsperoPkg.ProsperoPackageMode.Application,
                    OutputFormat = LibProsperoPkg.ProsperoOutputFormat.DebugImage,
                    SourceFolder = _fpkgSourceFolder,
                    OutputFolder = outputPath,
                    ContentId = contentId,
                    TitleId = titleId,
                    Title = title,
                    Version = version,
                    FakeSignSelfModules = fakeSign,
                    ApplicationType = LibProsperoPkg.ProsperoApplicationType.FreemiumApp
                };

                FpkgStatusText.Text = "Building package...";
                FpkgProgressBar.Value = 25;
                
                // Build the package
                var result = await Task.Run(() => LibProsperoPkg.ProsperoPackageBuilder.Build(options, msg => 
                {
                    Dispatcher.UIThread.Post(() => 
                    {
                        FpkgStatusText.Text = msg;
                        Log($"📦 {msg}");
                    });
                }));

                FpkgProgressBar.Value = 100;
                
                if (result != null && !string.IsNullOrEmpty(result.OutputPath))
                {
                    FpkgStatusText.Text = $"✅ Success! PKG created: {result.OutputPath}";
                    Log($"✅ FPKG created: {result.OutputPath}");
                    await ShowMessageAsync($"FPKG created successfully!\n\n{result.OutputPath}", "Conversion Complete");
                }
                else
                {
                    FpkgStatusText.Text = "❌ Conversion failed";
                    Log("❌ FPKG conversion failed");
                }
            }
            catch (Exception ex)
            {
                FpkgStatusText.Text = $"❌ Error: {ex.Message}";
                Log($"❌ FPKG error: {ex.Message}");
                await ShowMessageAsync($"FPKG conversion failed: {ex.Message}", "Error");
            }
            finally
            {
                ConvertFpkgButton.IsEnabled = true;
                FpkgProgressBar.IsVisible = false;
            }
        }

        // ============================================================
        // LIGHT BAR / VIBRATION / SCREENSHOT / NOTIFY / LED / BEEPER
        // ============================================================
        private async void LightBar_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            string rgb = (sender as Control)?.Tag as string ?? "0,0,255";
            try
            {
                var (success, message) = await _protocol.PadActionAsync($"lightbar|{rgb}");
                Log(success ? $"🎨 Light bar set ({rgb})" : $"❌ Light bar: {message}");
            }
            catch (Exception ex) { Log($"❌ Light bar error: {ex.Message}"); }
        }

        private async void Screenshot_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            try
            {
                Log("📸 Requesting screenshot...");
                var (success, message) = await _protocol.CaptureScreenshotAsync();
                Log(success
                    ? $"✅ Screenshot captured ({message}) — find it under Saves & Media → Screenshots"
                    : $"❌ Screenshot failed: {message}");
            }
            catch (Exception ex) { Log($"❌ Screenshot error: {ex.Message}"); }
        }

        private async void SendNotify_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            string text = NotifyTextBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(text)) { await ShowMessageAsync("Type a notification message first.", "Notify"); return; }
            try
            {
                var (success, message) = await _protocol.NotifyAsync(text);
                Log(success ? $"🔔 Notification sent: \"{text}\"" : $"❌ Notify failed: {message}");
            }
            catch (Exception ex) { Log($"❌ Notify error: {ex.Message}"); }
        }

        private async void LedDim_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            string level = (sender as Control)?.Tag as string ?? "0";
            try
            {
                var (success, message) = await _protocol.IccControlAsync($"led|{level}");
                Log(success ? $"💡 LED brightness set" : $"❌ LED: {message}");
            }
            catch (Exception ex) { Log($"❌ LED error: {ex.Message}"); }
        }

        private async void LedColor_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            string bwo = (sender as Control)?.Tag as string ?? "0,255,0";
            try
            {
                var (success, message) = await _protocol.IccControlAsync($"ledcolor|{bwo}");
                Log(success ? $"💡 LED color set" : $"❌ LED color: {message}");
            }
            catch (Exception ex) { Log($"❌ LED color error: {ex.Message}"); }
        }

        private async void LedEffect_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            string label = ((LedEffectCombo.SelectedItem as ComboBoxItem)?.Content as string) ?? "white";
            // ComboBox display text may carry a description suffix — take the
            // first token, which is always the preset name.
            string name = label.Split(' ')[0];
            try
            {
                var (success, message) = await _protocol.IccControlAsync($"ledeffect|{name}");
                Log(success ? $"✨ LED effect: {name}" : $"❌ LED effect: {message}");
            }
            catch (Exception ex) { Log($"❌ LED effect error: {ex.Message}"); }
        }

        private async void Buzzer_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            string type = (sender as Control)?.Tag as string ?? "1";
            try
            {
                var (success, message) = await _protocol.IccControlAsync($"buzzer|{type}");
                Log(success ? "🔊 Beep!" : $"❌ Beeper: {message}");
            }
            catch (Exception ex) { Log($"❌ Beeper error: {ex.Message}"); }
        }

        private async void BuzzerVol_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            string level = (sender as Control)?.Tag as string ?? "0";
            try
            {
                var (success, message) = await _protocol.IccControlAsync($"buzzervol|{level}");
                Log(success ? "🔊 Beeper volume set" : $"❌ Beeper volume: {message}");
            }
            catch (Exception ex) { Log($"❌ Beeper volume error: {ex.Message}"); }
        }

        private async void BuzzerMute_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            string mute = (sender as Control)?.Tag as string ?? "1";
            try
            {
                var (success, message) = await _protocol.IccControlAsync($"buzzermute|{mute}");
                Log(success ? (mute == "1" ? "🔇 Beeper muted" : "🔊 Beeper unmuted") : $"❌ Beeper mute: {message}");
            }
            catch (Exception ex) { Log($"❌ Beeper mute error: {ex.Message}"); }
        }

        // ============================================================
        // HOMEBREW STORE (pkg-zone.com catalog)
        // ============================================================
        private sealed class StoreItem : System.ComponentModel.INotifyPropertyChanged
        {
            public string Name { get; set; } = "";
            public string Id { get; set; } = "";
            public string Version { get; set; } = "";
            public string Author { get; set; } = "";

            private Bitmap? _cover;
            [System.Text.Json.Serialization.JsonIgnore]
            public Bitmap? Cover
            {
                get => _cover;
                set { _cover = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Cover))); }
            }

            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        }

        private readonly List<StoreItem> _storeItems = new();
        // pkg-zone.com is extremely flaky — roughly half of all requests either
        // time out at the edge or hit a Laravel 500. Short timeout + per-request
        // retry is the only way a fetch reliably lands; a single long timeout
        // just makes the app look frozen.
        private static readonly HttpClient _storeHttp = CreateStoreHttp();
        private static HttpClient CreateStoreHttp()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            h.DefaultRequestHeaders.Accept.ParseAdd(
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            h.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            return h;
        }

        // Retry wrapper for the flaky backend. Returns null after `attempts`
        // failures so the caller can decide whether to abort or degrade.
        private static async Task<string?> FetchStorePageAsync(string url, int attempts)
        {
            Exception? lastEx = null;
            for (int a = 0; a < attempts; a++)
            {
                try { return await _storeHttp.GetStringAsync(url); }
                catch (Exception ex)
                {
                    lastEx = ex;
                    if (a + 1 < attempts) await Task.Delay(800 * (a + 1));
                }
            }
            _ = lastEx;
            return null;
        }

        private static string StoreClean(string s)
            => System.Net.WebUtility.HtmlDecode(s).Trim();

        // The catalog is tiny and rarely changes — cache it next to the exe so
        // the store paints instantly even when pkg-zone.com is having a bad day
        // (it 500s roughly half of all requests).
        private const string StoreCacheFile = "pkgzone_cache.json";
        private const string StoreCoverDir = "pkgzone_covers";

        private int LoadStoreCache()
        {
            try
            {
                if (!File.Exists(StoreCacheFile)) return 0;
                var items = JsonSerializer.Deserialize<List<StoreItem>>(File.ReadAllText(StoreCacheFile));
                if (items == null || items.Count == 0) return 0;
                _storeItems.Clear();
                _storeItems.AddRange(items);
                ApplyStoreFilter();
                StoreStatusText.Text = $"{items.Count} PS5 packages (cached)";
                foreach (var it in items) TryLoadCachedCover(it);
                return items.Count;
            }
            catch { return 0; }
        }

        private static void TryLoadCachedCover(StoreItem it)
        {
            try
            {
                string p = Path.Combine(StoreCoverDir, it.Id + ".png");
                if (File.Exists(p))
                    it.Cover = new Bitmap(p);
            }
            catch { }
        }

        private static void SaveCoverToCache(StoreItem it, byte[] png)
        {
            try
            {
                Directory.CreateDirectory(StoreCoverDir);
                File.WriteAllBytes(Path.Combine(StoreCoverDir, it.Id + ".png"), png);
            }
            catch { }
        }

        // Fetches every missing cover in parallel, writes successful ones to the
        // disk cache. Runs after every refresh attempt — success OR failure —
        // so cached catalogs still get their icons whenever the site responds.
        private void FetchStoreCoversAsync()
        {
            var items = _storeItems.ToList();
            _ = Task.Run(async () =>
            {
                await Task.WhenAll(items.Select(async it =>
                {
                    if (it.Cover != null) return;
                    TryLoadCachedCover(it);
                    if (it.Cover != null) return;
                    for (int a = 0; a < 5 && it.Cover == null; a++)
                    {
                        try
                        {
                            var bytes = await _storeHttp.GetByteArrayAsync($"https://pkg-zone.com/images/{it.Id}/cover.png");
                            // Site outages return HTML error pages — reject
                            // anything that isn't actually a PNG.
                            if (bytes.Length < 100 || bytes[0] != 0x89 || bytes[1] != 0x50)
                                throw new Exception("not a PNG");
                            using var ms = new MemoryStream(bytes);
                            var bmp = new Bitmap(ms);
                            await Dispatcher.UIThread.InvokeAsync(() => { it.Cover = bmp; });
                            SaveCoverToCache(it, bytes);
                        }
                        catch { await Task.Delay(800 * (a + 1)); }
                    }
                }));
            });
        }

        private bool _storeFetching;
        private async void StoreRefresh_Click(object? sender, RoutedEventArgs e)
        {
            if (_storeFetching) return;   // rapid clicks pile up overlapping fetches
            _storeFetching = true;
            try
            {
                int cached = LoadStoreCache();
                StoreStatusText.Text = cached > 0
                    ? $"{cached} PS5 packages (cached) — refreshing…"
                    : "Fetching pkg-zone.com catalog...";
                Log("🛒 Fetching homebrew catalog...");

                var items = new List<StoreItem>();
                var articleRx = new System.Text.RegularExpressions.Regex(
                    @"<article class=""pkg[\s\S]*?</article>",
                    System.Text.RegularExpressions.RegexOptions.Compiled);
                var idRx = new System.Text.RegularExpressions.Regex(
                    @"/details/([A-Z0-9]+)", System.Text.RegularExpressions.RegexOptions.Compiled);
                var verRx = new System.Text.RegularExpressions.Regex(
                    @"<div class=""number text-white text-sm"">\s*([^<]+)", System.Text.RegularExpressions.RegexOptions.Compiled);
                var titleRx = new System.Text.RegularExpressions.Regex(
                    @"<div class=""title font-bold"">([^<]+)</div>", System.Text.RegularExpressions.RegexOptions.Compiled);
                var authorRx = new System.Text.RegularExpressions.Regex(
                    @"<div class=""dark:text-gray-300"">([^<]*)</div>", System.Text.RegularExpressions.RegexOptions.Compiled);

                var seen = new HashSet<string>();
                int consecutiveFailures = 0;
                for (int page = 1; page <= 15; page++)
                {
                    string url = $"https://pkg-zone.com/?console=ps5&page={page}";
                    // Retry every page — the backend 500s or edge-times-out on
                    // roughly half of all requests, so a single-attempt walk
                    // dies halfway through the catalog almost every time.
                    string? html = await FetchStorePageAsync(url, 4);
                    if (html == null)
                    {
                        consecutiveFailures++;
                        if (page == 1) throw new Exception("catalog unreachable after 4 attempts");
                        if (consecutiveFailures >= 3) { Log($"🛒 Page {page} unreachable — stopping"); break; }
                        continue;
                    }
                    consecutiveFailures = 0;
                    var arts = articleRx.Matches(html);
                    if (arts.Count == 0) break;
                    int newOnes = 0;
                    foreach (System.Text.RegularExpressions.Match m in arts)
                    {
                        string block = m.Value;
                        if (!block.Contains("Supports PS5")) continue;
                        var idM = idRx.Match(block);
                        if (!idM.Success || !seen.Add(idM.Groups[1].Value)) continue;
                        items.Add(new StoreItem
                        {
                            Id = idM.Groups[1].Value,
                            Name = StoreClean(titleRx.Match(block).Groups[1].Value is var tv && tv.Length > 0 ? tv : idM.Groups[1].Value),
                            Version = StoreClean(verRx.Match(block).Groups[1].Value ?? ""),
                            Author = StoreClean(authorRx.Match(block).Groups[1].Value ?? ""),
                        });
                        newOnes++;
                    }
                    if (newOnes == 0) break;
                    StoreStatusText.Text = $"Fetched {items.Count} packages...";
                }
                if (items.Count == 0) throw new Exception("catalog returned no PS5 packages");

                _storeItems.Clear();
                _storeItems.AddRange(items);
                ApplyStoreFilter();
                foreach (var it in items) TryLoadCachedCover(it);
                StoreStatusText.Text = $"{items.Count} PS5 packages";
                Log($"🛒 Catalog: {items.Count} PS5 packages");
                try { File.WriteAllText(StoreCacheFile, JsonSerializer.Serialize(items)); } catch { }
            }
            catch (Exception ex)
            {
                if (_storeItems.Count > 0)
                {
                    StoreStatusText.Text = $"{_storeItems.Count} PS5 packages (cached — pkg-zone.com down)";
                    Log($"⚠️ pkg-zone.com unreachable, showing cached catalog ({ex.Message})");
                }
                else
                {
                    StoreStatusText.Text = "Fetch failed";
                    Log($"❌ Store error: {ex.Message}");
                }
            }
            finally
            {
                _storeFetching = false;
                // Covers are fetched regardless of whether the catalog refresh
                // succeeded — cached items deserve icons too, and the image
                // endpoint often stays up while the listing 500s.
                if (_storeItems.Count > 0) FetchStoreCoversAsync();
            }
        }

        private void ApplyStoreFilter()
        {
            string q = StoreSearchBox.Text?.Trim() ?? "";
            var view = string.IsNullOrEmpty(q)
                ? _storeItems
                : _storeItems.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                      || i.Id.Contains(q, StringComparison.OrdinalIgnoreCase)
                                      || i.Author.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            StoreListBox.ItemsSource = view;
        }

        private void StoreSearch_TextChanged(object? sender, TextChangedEventArgs e) => ApplyStoreFilter();

        // Downloads carry no client timeout — 60MB+ streams would trip the
        // 20s catalog limit; we bound them with our own cancellation instead.
        private static readonly HttpClient _storeDl = CreateStoreDl();
        private static HttpClient CreateStoreDl()
        {
            var h = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            h.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            return h;
        }

        private bool _storeInstalling;

        // pkg-zone only speaks HTTPS and the console can't fetch that itself —
        // the PC downloads the PKG once (disk cache for re-installs), then
        // serves it over plain LAN HTTP through TryServePkgAsync.
        private async Task<bool> DownloadStorePkgAsync(string id, string name, string destPath)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                    using var resp = await _storeDl.GetAsync(
                        $"https://pkg-zone.com/download/ps5/{id}/latest",
                        HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? 0;
                    await using var src = await resp.Content.ReadAsStreamAsync(cts.Token);
                    await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                    var buf = new byte[512 * 1024];
                    long done = 0; int rd; int lastBucket = -1;
                    while ((rd = await src.ReadAsync(buf, cts.Token)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, rd), cts.Token);
                        done += rd;
                        if (total > 0)
                        {
                            int bucket = (int)(done * 10 / total);   // log every ~10%
                            if (bucket != lastBucket)
                            {
                                lastBucket = bucket;
                                Log($"📥 {name}: {done / 1048576}/{total / 1048576} MB");
                                StoreStatusText.Text = $"Downloading {name} — {done / 1048576}/{total / 1048576} MB";
                            }
                        }
                    }
                    if (total > 0 && done < total) throw new IOException($"short read {done}/{total}");

                    // PKG sanity: Sony packages start with 0x7F 'CNT'.
                    await using (var chk = new FileStream(destPath, FileMode.Open, FileAccess.Read))
                    {
                        var magic = new byte[4];
                        if (await chk.ReadAsync(magic) < 4 || magic[0] != 0x7F || magic[1] != 'C')
                            throw new IOException("downloaded file is not a PKG");
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    try { File.Delete(destPath); } catch { }
                    Log($"⚠️ Download failed ({ex.Message}){(attempt < 3 ? $" — retry {attempt + 2}/4" : "")}");
                    if (attempt < 3) await Task.Delay(1500);
                }
            }
            return false;
        }

        private async void StoreInstall_Click(object? sender, RoutedEventArgs e)
        {
            if (_storeInstalling) return;   // one install at a time
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            string? id = (sender as Control)?.Tag as string;
            if (string.IsNullOrEmpty(id)) return;
            var item = _storeItems.FirstOrDefault(i => i.Id == id);
            string name = item?.Name ?? id;
            if (!await ShowConfirmAsync($"Install \"{name}\" ({id}) on the PS5?")) return;
            _storeInstalling = true;
            try
            {
                string dir = Path.Combine(AppContext.BaseDirectory, "pkgzone_pkgs");
                Directory.CreateDirectory(dir);
                string local = Path.Combine(dir, id + ".pkg");
                if (!File.Exists(local) || new FileInfo(local).Length < 1024)
                {
                    if (!await DownloadStorePkgAsync(id, name, local))
                    {
                        StoreStatusText.Text = "download failed";
                        return;
                    }
                    Log($"✅ Downloaded {name} ({new FileInfo(local).Length / 1048576} MB)");
                }
                else Log($"� Using cached {name} ({new FileInfo(local).Length / 1048576} MB)");

                StoreStatusText.Text = $"Installing {name}...";
                string? url = await TryServePkgAsync(local);
                if (url == null) { Log("❌ Could not start the PKG stream server"); return; }
                var (success, result) = await _protocol.InstallPkgAsync(url);
                Log(success
                    ? $"✅ Install accepted: {name} — check the PS5 home screen"
                    : $"❌ Install failed: {result}");
                if (success) _ = PollPkgInstallStatus();
                StoreStatusText.Text = "";
            }
            catch (Exception ex) { Log($"❌ Install error: {ex.Message}"); }
            finally { _storeInstalling = false; }
        }
    }
}
