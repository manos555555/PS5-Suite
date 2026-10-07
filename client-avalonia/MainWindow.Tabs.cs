using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    // Games, Saves, Screenshots, Hardware tabs
    public partial class MainWindow
    {
        // ============================================================
        // SAVES TAB
        // ============================================================
        private async void RefreshSavesButton_Click(object? sender, RoutedEventArgs e) => await RefreshSavesAsync();

        private async Task RefreshSavesAsync()
        {
            if (!_protocol.IsConnected) { Log("❌ Not connected to PS5"); return; }
            Log("💾 Scanning save images...");
            try
            {
                var saves = await _protocol.ListSaveFilesAsync();
                _currentSaves = saves;
                var mounted = await _protocol.GetGameListAsync();
                var nameByTitle = mounted.ToDictionary(g => g.TitleId, g => g.Name, StringComparer.OrdinalIgnoreCase);

                foreach (var save in saves)
                {
                    save.GameName = nameByTitle.TryGetValue(save.TitleId, out var gn) ? gn : save.TitleId;
                    if (_iconCache.TryGetValue(save.TitleId, out var cached)) save.Icon = cached;
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SavesListBox.ItemsSource = saves;
                    SaveCountText.Text = $" ({saves.Count} saves)";
                });
                Log($"💾 Found {saves.Count} save images");

                // Fetch icons in background on a dedicated connection so bulk
                // icon downloads never serialize behind the main command lock.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var iconProto = new PS5Protocol();
                        if (!await iconProto.ConnectAsync(_ps5IpAddress)) return;
                        foreach (var s2 in saves)
                        {
                            if (s2.Icon != null) continue;
                            try
                            {
                                var bytes = await iconProto.GetGameIconAsync(s2.TitleId);
                                if (bytes != null && bytes.Length > 0)
                                {
                                    await Dispatcher.UIThread.InvokeAsync(() =>
                                    {
                                        try
                                        {
                                            using var ms = new MemoryStream(bytes);
                                            var bmp = new Bitmap(ms);
                                            _iconCache[s2.TitleId] = bmp;
                                            s2.Icon = bmp;
                                        }
                                        catch { }
                                    });
                                }
                            }
                            catch { }
                            if (!_protocol.IsConnected) break;
                        }
                    }
                    catch { }
                });

                await UpdateSaveMountStatusAsync();
            }
            catch (Exception ex) { Log($"❌ Error fetching saves: {ex.Message}"); }
        }

        private async Task UpdateSaveMountStatusAsync()
        {
            try
            {
                var (mounted, src, local, isPs4) = await _protocol.SaveMountStatusAsync();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SaveMountStatusText.Text = mounted
                        ? $"🔓 Mounted (decrypted): {src}  →  /data/save_mnt{(isPs4 ? "   [PS4 save]" : "")}"
                        : "";
                    BrowseSaveButton.IsEnabled = mounted;
                    UnmountSaveButton.IsEnabled = mounted;
                });
            }
            catch { }
        }

        // ---------- Mount / Browse / Unmount ----------

        private async void MountSaveButton_Click(object? sender, RoutedEventArgs e)
        {
            if (SavesListBox.SelectedItem is PS5SaveFile save) await MountSaveAsync(save);
            else await ShowMessageAsync("Select a save first.");
        }

        private async void MountSaveMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (SavesListBox.SelectedItem is PS5SaveFile save) await MountSaveAsync(save);
        }

        private async Task MountSaveAsync(PS5SaveFile save)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5.", "Error"); return; }
            Log($"🔓 Mounting {save.SaveName} ({save.TitleId}, {(save.IsPs4 ? "PS4" : "PS5")})...");
            var (ok, msg) = await _protocol.SaveMountAsync(save.Path, m => Log($"   {m}"));
            if (ok)
            {
                Log($"✅ Save mounted decrypted at /data/save_mnt");
            }
            else
            {
                Log($"❌ Mount failed: {msg}");
                await ShowMessageAsync($"Mount failed:\n{msg}", "Error");
            }
            await UpdateSaveMountStatusAsync();
        }

        private async void BrowseSaveButton_Click(object? sender, RoutedEventArgs e)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5.", "Error"); return; }
            var (mounted, src, local, isPs4) = await _protocol.SaveMountStatusAsync();
            if (!mounted)
            {
                await ShowMessageAsync("No save is mounted.\nSelect a save and press Mount first.", "Save Manager");
                return;
            }
            var win = new SaveBrowserWindow(_protocol, PS5Protocol.SaveMountPoint, _ps5IpAddress);
            bool unmount = await win.ShowDialog<bool>(this);
            if (unmount) await UnmountSaveAsync();
            else await UpdateSaveMountStatusAsync();
        }

        private async void UnmountSaveButton_Click(object? sender, RoutedEventArgs e) => await UnmountSaveAsync();

        private async Task UnmountSaveAsync()
        {
            if (!_protocol.IsConnected) return;
            Log("🔒 Unmounting save (writing changes back to the image)...");
            var (ok, msg) = await _protocol.SaveUnmountAsync(m => Log($"   {m}"));
            if (ok) Log($"✅ Save unmounted. {msg}");
            else
            {
                Log($"❌ Unmount failed: {msg}");
                await ShowMessageAsync($"Unmount failed:\n{msg}", "Error");
            }
            await UpdateSaveMountStatusAsync();
        }

        // ---------- Backup / Restore (decrypted via mount) ----------

        // Ensures the given save image is mounted. Returns (ok, weMountedIt).
        private async Task<(bool ok, bool weMounted)> EnsureSaveMountedAsync(PS5SaveFile save)
        {
            var (mounted, src, _, _) = await _protocol.SaveMountStatusAsync();
            if (mounted && src == save.Path) return (true, false);
            if (mounted)
            {
                await ShowMessageAsync($"A different save is mounted:\n{src}\n\nUnmount it first.", "Save Manager");
                return (false, false);
            }
            Log($"🔓 Mounting {save.SaveName} for transfer...");
            var (ok, msg) = await _protocol.SaveMountAsync(save.Path, m => Log($"   {m}"));
            if (!ok)
            {
                Log($"❌ Mount failed: {msg}");
                await ShowMessageAsync($"Mount failed:\n{msg}", "Error");
            }
            await UpdateSaveMountStatusAsync();
            return (ok, ok);
        }

        private async void BackupSaveButton_Click(object? sender, RoutedEventArgs e)
        {
            if (SavesListBox.SelectedItem is PS5SaveFile save) await BackupSaveAsync(save);
            else await ShowMessageAsync("Select a save first.");
        }

        private async void BackupSaveMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (SavesListBox.SelectedItem is PS5SaveFile save) await BackupSaveAsync(save);
        }

        // Decrypted backup: mount -> download plaintext contents -> unmount.
        private async Task BackupSaveAsync(PS5SaveFile save)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5.", "Error"); return; }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose backup destination folder"
            });
            if (folders.Count == 0) return;
            var parentDir = folders[0].TryGetLocalPath();
            if (parentDir == null) return;

            var (mountedOk, weMounted) = await EnsureSaveMountedAsync(save);
            if (!mountedOk) return;

            string backupDir = Path.Combine(parentDir,
                $"{save.TitleId}_{save.SaveName}_{DateTime.Now:yyyyMMdd_HHmmss}_decrypted");
            Directory.CreateDirectory(backupDir);

            Log($"📥 Backing up decrypted save contents to {backupDir}...");
            try
            {
                var result = await _protocol.DownloadFolderAsync(PS5Protocol.SaveMountPoint, backupDir, _ps5IpAddress, null, CancellationToken.None);
                Log($"✅ Backup complete: {result.filesDownloaded} files, {result.filesFailed} failed, {FormatFileSize(result.totalBytes)} total");
                await ShowMessageAsync($"Backup complete (decrypted)!\n\nLocation: {backupDir}\nFiles: {result.filesDownloaded}\nFailed: {result.filesFailed}\nSize: {FormatFileSize(result.totalBytes)}", "Backup Complete");
            }
            catch (Exception ex) { Log($"❌ Backup failed: {ex.Message}"); await ShowMessageAsync($"Backup failed:\n{ex.Message}", "Error"); }

            if (weMounted) await UnmountSaveAsync();
        }

        // Raw backup: copy the encrypted image file (+ .bin companion) as-is.
        private async void BackupSaveRawMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (SavesListBox.SelectedItem is not PS5SaveFile save) return;
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5.", "Error"); return; }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose destination for raw save image"
            });
            if (folders.Count == 0) return;
            var parentDir = folders[0].TryGetLocalPath();
            if (parentDir == null) return;

            string local = Path.Combine(parentDir, save.SaveName);
            Log($"📥 Downloading raw save image {save.SaveName}...");
            try
            {
                bool ok = await _protocol.DownloadFileAsync(save.Path, local);
                if (save.HasBinKey)
                {
                    try { await _protocol.DownloadFileAsync(save.Path + ".bin", local + ".bin"); } catch { }
                }
                Log(ok ? $"✅ Raw image saved: {local}" : "❌ Raw download failed");
                if (ok) await ShowMessageAsync($"Raw save image saved:\n{local}\n\n(encrypted - usable for same-console restore)", "Backup Complete");
            }
            catch (Exception ex) { Log($"❌ Raw backup failed: {ex.Message}"); }
        }

        private async void RestoreSaveButton_Click(object? sender, RoutedEventArgs e)
        {
            if (SavesListBox.SelectedItem is PS5SaveFile save) await RestoreSaveAsync(save);
            else await ShowMessageAsync("Select a save first.");
        }

        private async void RestoreSaveMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (SavesListBox.SelectedItem is PS5SaveFile save) await RestoreSaveAsync(save);
        }

        // Decrypted restore: mount -> upload plaintext files into the mounted
        // save -> unmount (the payload writes the image back automatically).
        private async Task RestoreSaveAsync(PS5SaveFile save)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5.", "Error"); return; }
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select the backup folder containing the save files"
            });
            if (folders.Count == 0) return;
            var backupFolder = folders[0].TryGetLocalPath();
            if (backupFolder == null || !Directory.Exists(backupFolder)) { await ShowMessageAsync("Invalid folder.", "Error"); return; }

            if (!await ShowConfirmAsync($"Restore decrypted files into save {save.SaveName} ({save.TitleId})?\n\nFrom: {backupFolder}\n\nThe save will be mounted, files overwritten, then written back."))
                return;

            var (mountedOk, weMounted) = await EnsureSaveMountedAsync(save);
            if (!mountedOk) return;

            string mnt = PS5Protocol.SaveMountPoint;
            Log("📤 Restoring files into mounted save...");
            int uploaded = 0, failed = 0; long totalBytes = 0;
            try
            {
                var remoteDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var localFile in Directory.EnumerateFiles(backupFolder, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(backupFolder, localFile).Replace('\\', '/');
                    string? remoteDir = Path.GetDirectoryName(rel)?.Replace('\\', '/');
                    if (string.IsNullOrEmpty(remoteDir)) continue;
                    string parts = mnt;
                    foreach (var seg in remoteDir.Split('/', StringSplitOptions.RemoveEmptyEntries))
                    {
                        parts += "/" + seg;
                        if (remoteDirs.Add(parts))
                        {
                            try { await _protocol.CreateDirAsync(parts); } catch { }
                        }
                    }
                }

                foreach (var localFile in Directory.EnumerateFiles(backupFolder, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(backupFolder, localFile).Replace('\\', '/');
                    string remote = mnt + "/" + rel;
                    try { bool ok = await _protocol.UploadFileAsync(localFile, remote); if (ok) { uploaded++; totalBytes += new FileInfo(localFile).Length; } else failed++; }
                    catch { failed++; }
                }
                Log($"✅ Restore upload done: {uploaded} uploaded, {failed} failed, {FormatFileSize(totalBytes)} - writing image back...");
            }
            catch (Exception ex) { Log($"❌ Restore failed: {ex.Message}"); await ShowMessageAsync($"Restore failed:\n{ex.Message}", "Error"); }

            if (weMounted) await UnmountSaveAsync();
            else await UpdateSaveMountStatusAsync();
        }

        // ---------- Delete ----------

        private async void DeleteSaveButton_Click(object? sender, RoutedEventArgs e)
        {
            if (SavesListBox.SelectedItem is PS5SaveFile save) await DeleteSaveAsync(save);
            else await ShowMessageAsync("Select a save first.");
        }

        private async void DeleteSaveMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (SavesListBox.SelectedItem is PS5SaveFile save) await DeleteSaveAsync(save);
        }

        private async Task DeleteSaveAsync(PS5SaveFile save)
        {
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5.", "Error"); return; }
            if (!await ShowConfirmAsync($"Delete save {save.SaveName} ({save.TitleId}) from the console?\n\n{save.Path}\n\nThis cannot be undone."))
                return;
            var (ok, msg) = await _protocol.SaveDeleteAsync(save.Path);
            if (ok) { Log($"🗑 Deleted {save.SaveName}"); await RefreshSavesAsync(); }
            else { Log($"❌ Delete failed: {msg}"); await ShowMessageAsync($"Delete failed:\n{msg}", "Error"); }
        }

        private async void CopySavePathMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (SavesListBox.SelectedItem is PS5SaveFile save)
            {
                try
                {
                    var cb = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (cb != null)
                    {
                        var item = new Avalonia.Input.DataTransferItem();
                        item.Set(Avalonia.Input.DataFormat.Text, save.Path);
                        var data = new Avalonia.Input.DataTransfer();
                        data.Add(item);
                        await cb.SetDataAsync(data);
                        Log($"📋 Copied: {save.Path}");
                    }
                }
                catch (Exception ex) { Log($"❌ Clipboard: {ex.Message}"); }
            }
        }

        // ============================================================
        // GAMES TAB
        // ============================================================
        private List<PS5MountedGame> _allGames = new();

        private async void RefreshGameListButton_Click(object? sender, RoutedEventArgs e) => await RefreshGameListAsync();

        private void GameSearchBox_TextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e) => ApplyGameFilter();

        private void GameSortComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyGameFilter();

        private void ApplyGameFilter()
        {
            if (MountedGamesListBox == null || GameSearchBox == null || GameSortComboBox == null) return;
            var q = GameSearchBox.Text?.Trim() ?? "";
            IEnumerable<PS5MountedGame> view = string.IsNullOrEmpty(q)
                ? _allGames
                : _allGames.Where(g =>
                    g.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    g.TitleId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    g.Region.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    g.Path.Contains(q, StringComparison.OrdinalIgnoreCase));

            view = GameSortComboBox.SelectedIndex switch
            {
                1 => view.OrderByDescending(g => g.Name, StringComparer.OrdinalIgnoreCase),
                2 => view.OrderBy(g => g.Size),
                3 => view.OrderByDescending(g => g.Size),
                4 => view.OrderBy(g => g.TitleId, StringComparer.OrdinalIgnoreCase),
                5 => view.OrderByDescending(g => g.IsActive),
                _ => view.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
            };

            var list = view.ToList();
            MountedGamesListBox.ItemsSource = list;
            GameCountText.Text = string.IsNullOrEmpty(q) || list.Count == _allGames.Count
                ? $" ({_allGames.Count} games)"
                : $" ({list.Count}/{_allGames.Count} games)";
        }

        private async Task RefreshGameListAsync()
        {
            if (!_protocol.IsConnected) { Log("❌ Not connected to PS5"); return; }
            Log("🎮 Fetching mounted games list...");
            try
            {
                var games = await _protocol.GetGameListAsync();
                _allGames = games;
                await Dispatcher.UIThread.InvokeAsync(() => ApplyGameFilter());

                if (games.Count > 0)
                {
                    Log($"🎮 Found {games.Count} mounted games:");
                    foreach (var game in games) Log($"   • {game.TitleId} - {game.Name} [{game.Region}]");

                    _ = Task.Run(async () =>
                    {
                        // Dedicated connection: keeps bulk icon fetches off the
                        // shared main connection (no more serialized UI stalls).
                        using var iconProto = new PS5Protocol();
                        if (!await iconProto.ConnectAsync(_ps5IpAddress)) return;

                        foreach (var game in games)
                        {
                            if (_iconCache.TryGetValue(game.TitleId, out var cached)) { await Dispatcher.UIThread.InvokeAsync(() => game.Icon = cached); continue; }
                            try
                            {
                                var iconBytes = await iconProto.GetGameIconAsync(game.TitleId);
                                if (iconBytes != null && iconBytes.Length > 0)
                                {
                                    await Dispatcher.UIThread.InvokeAsync(() =>
                                    {
                                        try { using var ms = new MemoryStream(iconBytes); var bmp = new Bitmap(ms); _iconCache[game.TitleId] = bmp; game.Icon = bmp; } catch { }
                                    });
                                }
                            }
                            catch { }
                            if (!_protocol.IsConnected) break;
                        }
                    });
                }
                else Log("🎮 No mounted games found");
            }
            catch (Exception ex) { Log($"❌ Error fetching game list: {ex.Message}"); }
        }

        private async void MountedGamesListBox_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            if (MountedGamesListBox.SelectedItem is PS5MountedGame game) await ShowGameDetailsAsync(game);
        }

        private async void ViewGameDetailsMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (MountedGamesListBox.SelectedItem is PS5MountedGame game) await ShowGameDetailsAsync(game);
        }

        private async Task ShowGameDetailsAsync(PS5MountedGame game)
        {
            if (!_protocol.IsConnected) { Log("❌ Not connected to PS5"); return; }
            Log($"ℹ️ Fetching details for {game.TitleId}...");
            var details = await _protocol.GetGameDetailsAsync(game.TitleId);
            if (details == null) { await ShowMessageAsync("Failed to fetch game details.", "Error"); return; }
            var dlg = new GameDetailsWindow(game, details, _protocol);
            await dlg.ShowDialog(this);
        }

        private async void UnmountGameMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (MountedGamesListBox.SelectedItem is PS5MountedGame game)
            {
                if (await ShowConfirmAsync($"Unmount game {game.TitleId}?\n\n{game.Name}\n\nThis will remove the game from the PS5 home screen."))
                {
                    Log($"🗑️ Unmounting {game.TitleId}...");
                    var (success, message) = await _protocol.UnmountGameAsync(game.TitleId, msg => Log($"   {msg}"));
                    if (success) { Log($"✅ {message}"); await RefreshGameListAsync(); }
                    else Log($"❌ Failed: {message}");
                }
            }
        }

        private async void OpenGamePathMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (MountedGamesListBox.SelectedItem is PS5MountedGame game)
            {
                string gamePath = game.Path;
                if (!string.IsNullOrEmpty(gamePath))
                {
                    Log($"📂 Navigating to {gamePath}");
                    _currentPS5Path = gamePath;
                    NavFiles.IsChecked = true;          // sidebar selection state
                    NavButton_Click(NavFiles, e);       // switch to the File Transfer page
                    await LoadPS5DirectoryAsync(gamePath);
                }
            }
        }

        private async void LaunchGameMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (MountedGamesListBox.SelectedItem is not PS5MountedGame game) return;
            if (!_protocol.IsConnected) { Log("❌ Not connected to PS5"); return; }
            Log($"▶ Launching {game.TitleId} ({game.Name})...");
            var (success, message) = await _protocol.LaunchGameAsync(game.TitleId);
            if (success) Log($"✅ {message}"); else Log($"❌ Launch failed: {message}");
        }

        private async void MountGameMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (MountedGamesListBox.SelectedItem is not PS5MountedGame game) return;
            if (!_protocol.IsConnected) { Log("❌ Not connected to PS5"); return; }
            Log($"📥 Mounting {game.TitleId} ({game.Name})...");
            var result = await _protocol.MountGameAsync(game.TitleId,
                onProgress: msg => Dispatcher.UIThread.Post(() => Log($"   {msg}")));
            Log(result ?? $"❌ Mount failed: {_protocol.LastError}");
            await RefreshGameListAsync();
        }

        // ============================================================
        // HARDWARE TAB
        // ============================================================
        private async void HwAutoTimer_Tick(object? sender, EventArgs e)
        {
            if (_protocol == null || !_protocol.IsConnected) { StopHwAutoRefresh(); return; }
            if (!PageSystem.IsVisible) { StopHwAutoRefresh(); return; }
            if (_activeTaskCount > 0) return;
            try { await RefreshHardwareAsync(); }
            catch { }
        }

        private async Task RefreshHardwareAsync()
        {
            if (_protocol == null || !_protocol.IsConnected) { Log("❌ Not connected to PS5"); return; }
            if (Interlocked.CompareExchange(ref _hwBusyFlag, 1, 0) != 0) { Log("⏳ Hardware refresh already in progress"); return; }
            try
            {
                // Static info — fetched once per session; none of it can change
                // while the console is running, so it stays out of the refresh loop.
                if (!_hwStaticLoaded)
                {
                    var hw = await _protocol.GetHardwareInfoAsync();
                    if (hw != null)
                    {
                        HwModelText.Text = string.IsNullOrWhiteSpace(hw.Model) ? "PlayStation 5" : hw.Model;
                        HwSerialText.Text = string.IsNullOrWhiteSpace(hw.Serial) ? "—" : hw.Serial;
                        HwMachineText.Text = string.IsNullOrWhiteSpace(hw.HwMachine) ? "—" : hw.HwMachine;
                        HwOsText.Text = string.IsNullOrWhiteSpace(hw.OsVersion) ? "—" : hw.OsVersion;
                        HwCpuCoresText.Text = hw.NumCpu > 0 ? $"{hw.NumCpu} cores" : "—";
                        HwPhysMemText.Text = hw.PhysMem > 0 ? $"{hw.PhysMem / (1024.0 * 1024.0 * 1024.0):0.0} GB" : "—";
                    }

                    var ext = await _protocol.GetExtendedInfoAsync();
                    if (ext != null)
                    {
                        HwFirmwareText.Text = !string.IsNullOrWhiteSpace(ext.FirmwareVersion) ? ext.FirmwareVersion : "—";
                        HwProductCodeText.Text = !string.IsNullOrWhiteSpace(ext.ProductCode) ? ext.ProductCode : "—";
                        HwTotalUptimeText.Text = ext.TotalOperatingTimeSec > 0
                            ? $"{ext.TotalOperatingTimeSec / 3600}h {(ext.TotalOperatingTimeSec % 3600) / 60}m"
                            : "—";
                        HwBootCountText.Text = ext.BootCount > 0 ? $"{ext.BootCount} cycles" : "—";
                        HwBdDriveText.Text = ext.BdDrivePower >= 0 ? (ext.BdDrivePower == 1 ? "On" : "Off") : "—";
                        HwThermalAlertText.Text = ext.ThermalAlert == 0 ? "Normal" : $"Alert ({ext.ThermalAlert})";
                        if (ext.ThermalAlert != 0) HwThermalAlertText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF6B35"));
                        else HwThermalAlertText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#28A745"));
                    }

                    var net = await _protocol.GetNetInfoAsync();
                    if (net != null) ApplyNetInfo(net, fillStatic: true);

                    _hwStaticLoaded = true;
                }

                // Memory info — dynamic, refreshed every tick
                var mem = await _protocol.GetMemoryInfoAsync();
                if (mem != null)
                {
                    // Physical RAM is static — fill it once from this (more reliable) source
                    if (mem.PhysicalTotal > 0 && HwPhysMemText.Text == "—")
                        HwPhysMemText.Text = FormatBytes(mem.PhysicalTotal);
                    HwMemDirectTotalText.Text = FormatBytes(mem.PhysicalTotal > 0 ? mem.PhysicalTotal : mem.DirectTotal);
                    HwMemDirectAvailText.Text = FormatBytes(mem.DirectUsed);
                    HwMemDirectUsedText.Text = FormatBytes(mem.FreeMemory);
                    HwMemFlexibleText.Text = FormatBytes(mem.FlexibleAvailable);
                    HwMemFreeText.Text = FormatBytes(mem.GpuPoolFree);
                    HwMemUsageBar.Value = Math.Clamp(mem.DirectUsedPercent, 0, 100);
                    HwMemUsageText.Text = $"{mem.DirectUsedPercent}%";
                }

                await RefreshHardwareSensorsAsync();
            }
            catch (Exception ex) { Log($"❌ Hardware refresh failed: {ex.Message}"); HwStatusText.Text = $"Error: {ex.Message}"; }
            finally { Interlocked.Exchange(ref _hwBusyFlag, 0); }
        }

        private string FormatBytes(ulong bytes)
        {
            if (bytes == 0) return "—";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.0} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00} GB";
        }

        private async Task RefreshHardwareSensorsAsync()
        {
            if (_protocol == null || !_protocol.IsConnected) return;
            try
            {
                // Temperature sensors
                var t = await _protocol.GetTemperatureInfoAsync();
                if (t != null)
                {
                    HwCpuTempText.Text = $"{t.CpuTemp}°C";
                    HwCpuTempBar.Value = Math.Clamp(t.CpuTemp, 0, 100);
                    HwSocTempText.Text = $"{t.SocTemp}°C";
                    HwSocTempBar.Value = Math.Clamp(t.SocTemp, 0, 100);
                    HwCpuFreqText.Text = t.CpuFreqMhz > 0 ? $"{t.CpuFreqMhz} MHz" : "—";
                    HwCpuFreqBar.Value = Math.Clamp(t.CpuFreqMhz, 0, 3500);
                    double watts = t.SocPowerMw / 1000.0;
                    HwSocPowerText.Text = t.SocPowerMw > 0 ? $"{watts:0.0} W" : "—";
                    HwSocPowerBar.Value = Math.Clamp(watts, 0, 250);
                }

                // CPU usage per core
                var cpu = await _protocol.GetCpuUsageAsync();
                if (cpu != null)
                {
                    HwCpu0Text.Text = $"{cpu.CoreUsage[0]}%"; HwCpu0Bar.Value = cpu.CoreUsage[0];
                    HwCpu1Text.Text = $"{cpu.CoreUsage[1]}%"; HwCpu1Bar.Value = cpu.CoreUsage[1];
                    HwCpu2Text.Text = $"{cpu.CoreUsage[2]}%"; HwCpu2Bar.Value = cpu.CoreUsage[2];
                    HwCpu3Text.Text = $"{cpu.CoreUsage[3]}%"; HwCpu3Bar.Value = cpu.CoreUsage[3];
                    HwCpu4Text.Text = $"{cpu.CoreUsage[4]}%"; HwCpu4Bar.Value = cpu.CoreUsage[4];
                    HwCpu5Text.Text = $"{cpu.CoreUsage[5]}%"; HwCpu5Bar.Value = cpu.CoreUsage[5];
                    HwCpu6Text.Text = $"{cpu.CoreUsage[6]}%"; HwCpu6Bar.Value = cpu.CoreUsage[6];
                    HwCpu7Text.Text = $"{cpu.CoreUsage[7]}%"; HwCpu7Bar.Value = cpu.CoreUsage[7];
                    HwCpuAvgText.Text = $"{cpu.Average}%";
                }

                // Live traffic rates — refresh counters every tick
                var net = await _protocol.GetNetInfoAsync();
                if (net != null) ApplyNetInfo(net, fillStatic: false);

                HwStatusText.Text = $"Last updated: {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex) { HwStatusText.Text = $"Error: {ex.Message}"; }
        }

        // ---------------- network info ----------------
        private ulong _netPrevRx, _netPrevTx;
        private DateTime _netPrevT = DateTime.MinValue;

        private void ApplyNetInfo(string txt, bool fillStatic)
        {
            var kv = new Dictionary<string, string>(StringComparer.Ordinal);
            ulong rx = 0, tx = 0;
            string? activeIf = null; ulong activeBytes = 0;
            foreach (var raw in txt.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.StartsWith("if="))
                {
                    var f = line.Split('|');
                    if (f.Length == 3 && ulong.TryParse(f[1].AsSpan(3), out ulong r) && ulong.TryParse(f[2].AsSpan(3), out ulong t))
                    {
                        rx += r; tx += t;
                        if (r + t > activeBytes) { activeBytes = r + t; activeIf = f[0].Substring(3); }
                    }
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq > 0) kv[line[..eq]] = line[(eq + 1)..];
            }

            if (fillStatic)
            {
                if (kv.TryGetValue("state", out var st))
                    NetStateText.Text = st switch { "3" => "🟢 Online", "2" => "🟡 IP obtained", "1" => "🟡 Connecting…", "0" => "🔴 Offline", _ => st };
                if (kv.TryGetValue("ssid", out var ssid)) NetSsidText.Text = ssid;
                if (kv.TryGetValue("rssi", out var rssi)) NetRssiText.Text = $"{rssi}%";
                if (kv.TryGetValue("ip", out var ip)) NetIpText.Text = ip;
                // Classify the raw fN= IPv4 fields: netmask starts with 255,
                // gateway is the first remaining non-IP address, DNS are rest.
                var fvals = kv.Where(p => p.Key.StartsWith("f") && System.Net.IPAddress.TryParse(p.Value, out _))
                              .Select(p => p.Value).Where(v => v != "0.0.0.0").ToList();
                var netmask = fvals.FirstOrDefault(v => v.StartsWith("255."));
                var rest = fvals.Where(v => v != netmask && v != ip).ToList();
                if (rest.Count > 0) NetGwText.Text = rest[0];
                if (rest.Count > 1) NetDnsText.Text = string.Join("  ", rest.Skip(1));
                if (kv.TryGetValue("mac", out var mac)) NetMacText.Text = mac;
                var mtu = kv.TryGetValue("mtu", out var m) ? m : "—";
                var link = kv.TryGetValue("link", out var l) ? (l == "0" ? "down" : "up") : "?";
                var dev = kv.TryGetValue("device", out var d) ? (d == "1" ? "WiFi" : "Ethernet") : "";
                NetMtuText.Text = $"{mtu} / {link}{(dev.Length > 0 ? $" / {dev}" : "")}";
                if (kv.TryGetValue("nat_type", out var nt))
                    NetNatText.Text = nt switch { "1" => "Type 1 (Open)", "2" => "Type 2 (Moderate)", "3" => "Type 3 (Strict)", _ => $"Type {nt}" };
                if (kv.TryGetValue("nat_ip", out var nip))
                    NetNatIpText.Text = kv.TryGetValue("nat_port", out var np) ? $"{nip}:{np}" : nip;
            }

            // rates from counter deltas
            var now = DateTime.UtcNow;
            if (_netPrevT != DateTime.MinValue)
            {
                double dt = (now - _netPrevT).TotalSeconds;
                if (dt > 0.3)
                {
                    NetRxRateText.Text = $"{FormatRate((ulong)Math.Max(0, (double)(rx - _netPrevRx)) / dt)}";
                    NetTxRateText.Text = $"{FormatRate((ulong)Math.Max(0, (double)(tx - _netPrevTx)) / dt)}";
                    if (activeIf != null) NetIfText.Text = activeIf;
                    NetTotalsText.Text = $"↓{FormatBytes(rx)}  ↑{FormatBytes(tx)}";
                }
            }
            _netPrevRx = rx; _netPrevTx = tx; _netPrevT = now;
        }

        private static string FormatRate(double bps)
        {
            if (bps >= 1024.0 * 1024 * 1024) return $"{bps / (1024.0 * 1024 * 1024):0.00} GB/s";
            if (bps >= 1024.0 * 1024) return $"{bps / (1024.0 * 1024):0.0} MB/s";
            if (bps >= 1024.0) return $"{bps / 1024.0:0.0} KB/s";
            return $"{bps:0} B/s";
        }

        private async void NetSpeedTestButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_protocol == null) return;
            NetSpeedTestButton.IsEnabled = false;
            NetSpeedText.Text = "Running…";
            try
            {
                var mbps = await _protocol.NetSpeedTestAsync();
                NetSpeedText.Text = mbps.HasValue ? $"Link: {mbps.Value:0} Mbps ↓" : $"Failed: {_protocol.LastError}";
            }
            catch (Exception ex) { NetSpeedText.Text = $"Error: {ex.Message}"; }
            finally { NetSpeedTestButton.IsEnabled = true; }
        }

        private async void LoadModulesButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_protocol == null || !_protocol.IsConnected) { Log("❌ Not connected to PS5"); return; }
            try
            {
                Log("📦 Loading modules list...");
                var modules = await _protocol.GetModuleListAsync();
                ModulesListBox.ItemsSource = modules;
                HwModuleCountText.Text = $" ({modules.Count})";
                Log($"✅ Found {modules.Count} modules");
            }
            catch (Exception ex) { Log($"❌ Failed to load modules: {ex.Message}"); }
        }

        // ============================================================
        // SCREENSHOTS TAB
        // ============================================================
        private async void RefreshScreenshotsButton_Click(object? sender, RoutedEventArgs e) => await RefreshScreenshotsAsync();

        private async Task RefreshScreenshotsAsync()
        {
            if (_protocol == null || !_protocol.IsConnected) { Log("❌ Not connected to PS5"); return; }
            Log("📷 Fetching screenshots list...");
            try
            {
                var shots = await _protocol.ListScreenshotsAsync();
                _currentScreenshots = shots;
                foreach (var s in shots)
                    if (_screenshotThumbCache.TryGetValue(s.FullPath, out var cached)) s.Thumbnail = cached;

                ScreenshotsListBox.ItemsSource = null;
                ScreenshotsListBox.ItemsSource = shots;
                ScreenshotsCountText.Text = $"({shots.Count} items)";
                Log($"✅ Found {shots.Count} screenshots");

                _ = Task.Run(async () =>
                {
                    string cacheDir = Path.Combine(Path.GetTempPath(), "PS5SuiteCache", "ss_thumbs");
                    Directory.CreateDirectory(cacheDir);

                    // Dedicated connection: thumbnail downloads used to run on
                    // the main connection and block every other UI operation.
                    using var thumbProto = new PS5Protocol();
                    if (!await thumbProto.ConnectAsync(_ps5IpAddress)) return;

                    foreach (var shot in shots)
                    {
                        if (shot.Thumbnail != null) continue;
                        if (!_protocol.IsConnected) break;
                        // Stable 64-bit FNV-1a hash instead of GetHashCode():
                        // string hashing is randomized per process, so cache
                        // names changed between runs (and could collide).
                        string hash = FnvHash64(shot.FullPath).ToString("X16");
                        string ext = Path.GetExtension(shot.FileName).ToLowerInvariant();
                        if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                        string localCache = Path.Combine(cacheDir, hash + ext);
                        try
                        {
                            if (!File.Exists(localCache) || new FileInfo(localCache).Length == 0)
                            {
                                bool ok = await thumbProto.DownloadFileAsync(shot.FullPath, localCache);
                                if (!ok) continue;
                            }
                            await Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                try
                                {
                                    var bmp = new Bitmap(localCache);
                                    _screenshotThumbCache[shot.FullPath] = bmp;
                                    shot.Thumbnail = bmp;
                                }
                                catch { }
                            });
                        }
                        catch { }
                    }
                });
            }
            catch (Exception ex) { Log($"❌ Screenshots fetch failed: {ex.Message}"); }
        }

        private async void DownloadScreenshotButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_protocol == null || !_protocol.IsConnected) { Log("❌ Not connected to PS5"); return; }
            var selected = (ScreenshotsListBox.SelectedItems ?? Array.Empty<object>()).Cast<PS5Screenshot>().ToList();
            if (selected.Count == 0) { Log("⚠️ No screenshots selected"); return; }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select folder to save screenshots" });
            if (folders.Count == 0) return;
            var targetFolder = folders[0].TryGetLocalPath();
            if (targetFolder == null) return;

            int ok = 0, fail = 0;
            foreach (var shot in selected)
            {
                string localPath = Path.Combine(targetFolder, shot.FileName);
                Log($"⬇ Downloading {shot.FileName} ({shot.SizeDisplay})...");
                try { bool success = await _protocol.DownloadFileAsync(shot.FullPath, localPath); if (success) ok++; else { fail++; Log($"❌ Failed: {shot.FileName}"); } }
                catch (Exception ex) { fail++; Log($"❌ {shot.FileName}: {ex.Message}"); }
            }
            Log($"✅ Downloaded {ok} screenshot(s), {fail} failed → {targetFolder}");
        }

        private async void DeleteScreenshotButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_protocol == null || !_protocol.IsConnected) { Log("❌ Not connected to PS5"); return; }
            var selected = (ScreenshotsListBox.SelectedItems ?? Array.Empty<object>()).Cast<PS5Screenshot>().ToList();
            if (selected.Count == 0) { Log("⚠️ No screenshots selected"); return; }

            if (!await ShowConfirmAsync($"Delete {selected.Count} screenshot(s) from PS5?\n\nThis cannot be undone."))
                return;

            int ok = 0, fail = 0;
            foreach (var shot in selected)
            {
                try { var (success, _) = await _protocol.DeleteScreenshotAsync(shot.FullPath); if (success) ok++; else fail++; }
                catch { fail++; }
            }
            Log($"🗑️ Deleted {ok} screenshot(s), {fail} failed");
            await RefreshScreenshotsAsync();
        }

        private async void ScreenshotsListBox_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            if (ScreenshotsListBox.SelectedItem is not PS5Screenshot shot) return;
            if (_protocol == null || !_protocol.IsConnected) return;
            string tempPath = Path.Combine(Path.GetTempPath(), shot.FileName);
            Log($"⬇ Opening preview: {shot.FileName}...");
            try
            {
                bool ok = await _protocol.DownloadFileAsync(shot.FullPath, tempPath);
                if (ok) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = tempPath, UseShellExecute = true });
                else Log($"❌ Failed to download preview");
            }
            catch (Exception ex) { Log($"❌ Preview failed: {ex.Message}"); }
        }

        // Stable 64-bit FNV-1a hash for cache file names
        private static long FnvHash64(string s)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                foreach (char c in s)
                {
                    hash ^= c;
                    hash *= 1099511628211UL;
                }
                return (long)hash;
            }
        }

        private async void CopyScreenshotPathMenuItem_Click(object? sender, RoutedEventArgs e)
        {
            if (ScreenshotsListBox.SelectedItem is PS5Screenshot shot)
            {
                try
                {
                    var cb = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (cb != null)
                    {
                        var item = new Avalonia.Input.DataTransferItem();
                        item.Set(Avalonia.Input.DataFormat.Text, shot.FullPath);
                        var data = new Avalonia.Input.DataTransfer();
                        data.Add(item);
                        await cb.SetDataAsync(data);
                    }
                    Log($"📋 Copied path: {shot.FullPath}");
                }
                catch { }
            }
        }
    }
}
