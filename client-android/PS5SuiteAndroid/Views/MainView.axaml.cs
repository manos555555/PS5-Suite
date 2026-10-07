using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PS5Upload;

namespace PS5SuiteAndroid.Views;

public partial class MainView : UserControl
{
    private PS5Protocol? _protocol;
    private string? _ps5IpAddress;
    private string _currentPath = "/data";
    private ObservableCollection<FileItem> _files = new();
    private ObservableCollection<GameItem> _games = new();
    private ObservableCollection<PS5SaveFile> _saves = new();
    private ObservableCollection<PS5Screenshot> _screenshots = new();
    private ObservableCollection<string> _logLines = new();
    private readonly List<StoreItem> _storeItems = new();
    private static readonly HttpClient _storeHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    public MainView()
    {
        InitializeComponent();
        FilesListBox.ItemsSource = _files;
        GamesListBox.ItemsSource = _games;
        SavesListBox.ItemsSource = _saves;
        ScreenshotsListBox.ItemsSource = _screenshots;
        LogItems.ItemsSource = _logLines;
    }

    private void MenuButton_Click(object? sender, RoutedEventArgs e)
        => NavDrawer.IsPaneOpen = !NavDrawer.IsPaneOpen;

    private void NavList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedIndex < 0 || PageConnect == null) return;
        var pages = new Control[] {
            PageConnect, PageFiles, PageGames, PageStore, PageProspero,
            PageTrophies, PageSaves, PageSystem, PageTools, PageDevices, PageLog
        };
        for (int i = 0; i < pages.Length; i++)
            if (pages[i] != null) pages[i].IsVisible = i == NavList.SelectedIndex;
        NavDrawer.IsPaneOpen = false;

        // Auto-load pages that need the PS5 the first time they open
        if (_protocol?.IsConnected == true)
        {
            switch (NavList.SelectedIndex)
            {
                case 2: if (!_gamesLoadedOnce) { _gamesLoadedOnce = true; _ = RefreshGamesAsync(); } break;
                case 4: if (!_prosperoLoadedOnce) { _prosperoLoadedOnce = true; _ = RefreshProsperoAsync(); } break;
                case 5: if (!_trophyLoadedOnce) { _trophyLoadedOnce = true; _ = RefreshTrophiesAsync(); } break;
            }
        }
    }

    private bool _gamesLoadedOnce;
    private bool _prosperoLoadedOnce;
    private bool _trophyLoadedOnce;

    // ============================================================
    // MODAL DIALOG OVERLAY (message / confirm / input / pick)
    // ============================================================
    private TaskCompletionSource<object?>? _dialogTcs;

    private void DialogShow(string title, string message, bool showInput, bool showCancel, string inputText = "")
    {
        DialogTitle.Text = title;
        DialogMessage.Text = message;
        DialogInput.IsVisible = showInput;
        DialogInput.Text = inputText;
        DialogCancelButton.IsVisible = showCancel;
        DialogOverlay.IsVisible = true;
        if (showInput) DialogInput.Focus();
    }

    private void DialogOk_Click(object? sender, RoutedEventArgs e)
        => _dialogTcs?.TrySetResult(DialogInput.IsVisible ? DialogInput.Text : (object?)true);

    private void DialogCancel_Click(object? sender, RoutedEventArgs e)
        => _dialogTcs?.TrySetResult(null);

    private Task<object?> RunDialogAsync(string title, string message, bool showInput, bool showCancel, string inputText = "")
    {
        _dialogTcs = new TaskCompletionSource<object?>();
        DialogShow(title, message, showInput, showCancel, inputText);
        return _dialogTcs.Task.ContinueWith(t => { DialogOverlay.IsVisible = false; return t.Result; },
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private Task ShowMessageAsync(string message, string title = "PS5 Suite")
        => RunDialogAsync(title, message, false, false);

    private async Task<bool> ShowConfirmAsync(string message, string title = "Confirm")
        => await RunDialogAsync(title, message, false, true) is true;

    private async Task<string?> ShowInputAsync(string title, string message, string initial = "")
        => await RunDialogAsync(title, message, true, true, initial) as string;

    private void UpdateStatus(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = message;
            _logLines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (_logLines.Count > 800) _logLines.RemoveAt(0);
            if (LogAutoScroll?.IsChecked == true)
                LogScroller?.ScrollToEnd();
        });
    }

    private void LogClear_Click(object? sender, RoutedEventArgs e) => _logLines.Clear();

    private bool RequireConnection()
    {
        if (_protocol == null || !_protocol.IsConnected)
        {
            UpdateStatus("Not connected");
            return false;
        }
        return true;
    }

    private void UpdateConnectionStatus(bool connected)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ConnectionStatusText.Text = connected ? "Connected" : "Disconnected";
            ConnectionStatusText.Foreground = new SolidColorBrush(connected ? Color.Parse("#28A745") : Color.Parse("#FF6B35"));
            StatusIndicator.Fill = new SolidColorBrush(connected ? Color.Parse("#28A745") : Color.Parse("#FF6B35"));
        });
    }

    private async void ConnectButton_Click(object? sender, RoutedEventArgs e)
    {
        var ip = IpAddressInput.Text?.Trim();
        if (string.IsNullOrEmpty(ip))
        {
            UpdateStatus("Please enter PS5 IP address");
            return;
        }

        UpdateStatus($"Connecting to {ip}...");
        ConnectButton.IsEnabled = false;

        try
        {
            _protocol = new PS5Protocol();
            bool success = await _protocol.ConnectAsync(ip);

            if (success)
            {
                _ps5IpAddress = ip;
                UpdateConnectionStatus(true);
                UpdateStatus("Connected successfully!");
            }
            else
            {
                UpdateConnectionStatus(false);
                UpdateStatus($"Connection failed: {_protocol.LastError}");
                _protocol = null;
            }
        }
        catch (Exception ex)
        {
            UpdateConnectionStatus(false);
            UpdateStatus($"Error: {ex.Message}");
            _protocol = null;
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void NavigateTo(int index) => NavList.SelectedIndex = index;

    private void BrowseFilesButton_Click(object? sender, RoutedEventArgs e)
    {
        NavigateTo(1);
        _ = RefreshFilesAsync();
    }

    private void ViewGamesButton_Click(object? sender, RoutedEventArgs e)
    {
        NavigateTo(2);
        _ = RefreshGamesAsync();
    }

    private void SystemInfoButton_Click(object? sender, RoutedEventArgs e)
    {
        NavigateTo(7);
        _ = RefreshSystemInfoAsync();
    }

    private async void GoUpButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentPath != "/" && _currentPath.Contains("/"))
        {
            var lastSlash = _currentPath.LastIndexOf('/');
            _currentPath = lastSlash > 0 ? _currentPath.Substring(0, lastSlash) : "/";
            await RefreshFilesAsync();
        }
    }

    private async void FilesListBox_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (FilesListBox.SelectedItem is FileItem item && item.IsDirectory)
        {
            _currentPath = item.FullPath;
            await RefreshFilesAsync();
        }
    }

    private async Task RefreshFilesAsync()
    {
        if (_protocol == null || !_protocol.IsConnected)
        {
            UpdateStatus("Not connected");
            return;
        }

        UpdateStatus("Loading files...");
        CurrentPathText.Text = _currentPath;

        try
        {
            var entries = await _protocol.ListDirAsync(_currentPath);
            _files.Clear();

            foreach (var entry in entries)
            {
                var fullPath = _currentPath.TrimEnd('/') + "/" + entry.Name;
                _files.Add(new FileItem
                {
                    Name = entry.Name,
                    FullPath = fullPath,
                    IsDirectory = entry.IsDirectory,
                    Size = entry.Size,
                    Icon = entry.IsDirectory ? "📁" : "📄",
                    SizeText = entry.IsDirectory ? "Folder" : FormatSize(entry.Size)
                });
            }

            UpdateStatus($"Loaded {entries.Length} items");
        }
        catch (Exception ex)
        {
            UpdateStatus($"Error: {ex.Message}");
        }
    }

    private async void RefreshGamesButton_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshGamesAsync();
    }

    private async Task RefreshGamesAsync()
    {
        if (_protocol == null || !_protocol.IsConnected)
        {
            UpdateStatus("Not connected");
            return;
        }

        UpdateStatus("Loading games...");

        try
        {
            var games = await _protocol.GetGameListAsync();
            _allGames.Clear();

            foreach (var game in games)
            {
                _allGames.Add(new GameItem
                {
                    Name = game.Name,
                    TitleId = game.TitleId,
                    Path = game.Path,
                    Size = game.Size,
                    SizeText = game.Size > 0 ? FormatSize((long)game.Size) : "—"
                });
            }

            ApplyGameFilter();
            UpdateStatus($"Found {games.Count} games");
            _ = Task.Run(LoadGameExtrasAsync);
        }
        catch (Exception ex)
        {
            UpdateStatus($"Error: {ex.Message}");
        }
    }

    private async void RefreshSensorsButton_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshSystemInfoAsync();
    }

    // ============================================================
    // GAMES: mount / launch / unmount
    // ============================================================
    private async void MountGamesButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_protocol == null || !_protocol.IsConnected) { UpdateStatus("Not connected"); return; }
        UpdateStatus("Mounting games...");
        try
        {
            var result = await _protocol.MountGamesAsync(onProgress: msg => UpdateStatus(msg));
            UpdateStatus(result ?? "Mount finished");
            await RefreshGamesAsync();
        }
        catch (Exception ex) { UpdateStatus($"Mount error: {ex.Message}"); }
    }

    private async void LaunchGameButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_protocol == null || !_protocol.IsConnected) { UpdateStatus("Not connected"); return; }
        if (GamesListBox.SelectedItem is not GameItem game) { UpdateStatus("Select a game first"); return; }
        UpdateStatus($"Launching {game.Name}...");
        try
        {
            var (ok, msg) = await _protocol.LaunchGameAsync(game.TitleId);
            UpdateStatus(ok ? $"Launched {game.TitleId}" : $"Launch failed: {msg}");
        }
        catch (Exception ex) { UpdateStatus($"Launch error: {ex.Message}"); }
    }

    private async void UnmountGameButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_protocol == null || !_protocol.IsConnected) { UpdateStatus("Not connected"); return; }
        if (GamesListBox.SelectedItem is not GameItem game) { UpdateStatus("Select a game first"); return; }
        UpdateStatus($"Unmounting {game.TitleId}...");
        try
        {
            var (ok, msg) = await _protocol.UnmountGameAsync(game.TitleId, m => UpdateStatus(m));
            UpdateStatus(ok ? $"Unmounted {game.TitleId}" : $"Unmount failed: {msg}");
            await RefreshGamesAsync();
        }
        catch (Exception ex) { UpdateStatus($"Unmount error: {ex.Message}"); }
    }

    // ============================================================
    // TOOLS: PKG install
    // ============================================================
    private async void InstallPkgButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_protocol == null || !_protocol.IsConnected) { UpdateStatus("Not connected"); return; }
        var path = PkgPathInput.Text?.Trim();
        if (string.IsNullOrEmpty(path)) { UpdateStatus("Enter a PKG path or URL"); return; }

        PkgStatusText.Text = "Installing...";
        PkgStatusText.Foreground = new SolidColorBrush(Color.Parse("#FFC107"));
        PkgProgressBar.Value = 0;
        UpdateStatus($"Installing PKG: {path}");

        try
        {
            var (ok, result) = await _protocol.InstallPkgAsync(path);
            if (ok)
            {
                PkgStatusText.Text = $"Installed: {result}";
                PkgStatusText.Foreground = new SolidColorBrush(Color.Parse("#28A745"));
                PkgProgressBar.Value = 100;
                UpdateStatus($"PKG installed: {result}");
            }
            else
            {
                PkgStatusText.Text = $"Failed: {result}";
                PkgStatusText.Foreground = new SolidColorBrush(Color.Parse("#DC3545"));
                UpdateStatus($"PKG install failed: {result}");
            }
        }
        catch (Exception ex)
        {
            PkgStatusText.Text = $"Error: {ex.Message}";
            PkgStatusText.Foreground = new SolidColorBrush(Color.Parse("#DC3545"));
            UpdateStatus($"PKG install error: {ex.Message}");
        }
    }

    // ============================================================
    // TOOLS: Fan control
    // ============================================================
    private async void FanRefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_protocol == null || !_protocol.IsConnected) { UpdateStatus("Not connected"); return; }
        try
        {
            var (ok, threshold) = await _protocol.GetFanThresholdAsync();
            if (ok && threshold > 0)
            {
                FanCurrentText.Text = $"{threshold}°C";
                FanSlider.Value = threshold;
                FanSliderText.Text = $"{threshold}°C";
            }
            else if (ok)
            {
                FanCurrentText.Text = "auto";
                UpdateStatus("Fan threshold not set — system-managed");
            }
            else UpdateStatus("Failed to read fan threshold");
        }
        catch (Exception ex) { UpdateStatus($"Fan error: {ex.Message}"); }
    }

    private async void FanSetButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_protocol == null || !_protocol.IsConnected) { UpdateStatus("Not connected"); return; }
        int temp = (int)FanSlider.Value;
        UpdateStatus($"Setting fan threshold to {temp}°C...");
        try
        {
            var (ok, msg) = await _protocol.SetFanThresholdAsync(temp);
            if (ok) { FanCurrentText.Text = $"{temp}°C"; FanSliderText.Text = $"{temp}°C"; }
            UpdateStatus(msg);
        }
        catch (Exception ex) { UpdateStatus($"Fan error: {ex.Message}"); }
    }

    private void FanPreset_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Content is string c)
        {
            if (c.Contains("50")) { FanSlider.Value = 50; FanSliderText.Text = "50°C"; }
            else if (c.Contains("70")) { FanSlider.Value = 70; FanSliderText.Text = "70°C"; }
        }
    }

    private void FanSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (FanSliderText != null) FanSliderText.Text = $"{(int)e.NewValue}°C";
    }

    private async Task RefreshSystemInfoAsync()
    {
        if (_protocol == null || !_protocol.IsConnected)
        {
            UpdateStatus("Not connected");
            return;
        }

        UpdateStatus("Loading system info...");

        try
        {
            // Temperature/sensors
            var temps = await _protocol.GetTemperatureInfoAsync();
            if (temps != null)
            {
                CpuTempText.Text = $"{temps.CpuTemp}°C";
                CpuTempBar.Value = Math.Clamp(temps.CpuTemp, 0, 100);
                SocTempText.Text = $"{temps.SocTemp}°C";
                SocTempBar.Value = Math.Clamp(temps.SocTemp, 0, 100);
                CpuFreqText.Text = $"{temps.CpuFreqMhz} MHz";
                PowerText.Text = $"{temps.SocPowerMw / 1000.0:0.0} W";
            }

            // CPU usage
            var cpu = await _protocol.GetCpuUsageAsync();
            if (cpu != null)
            {
                Cpu0Text.Text = $"{cpu.CoreUsage[0]}%"; Cpu0Bar.Value = cpu.CoreUsage[0];
                Cpu1Text.Text = $"{cpu.CoreUsage[1]}%"; Cpu1Bar.Value = cpu.CoreUsage[1];
                Cpu2Text.Text = $"{cpu.CoreUsage[2]}%"; Cpu2Bar.Value = cpu.CoreUsage[2];
                Cpu3Text.Text = $"{cpu.CoreUsage[3]}%"; Cpu3Bar.Value = cpu.CoreUsage[3];
                Cpu4Text.Text = $"{cpu.CoreUsage[4]}%"; Cpu4Bar.Value = cpu.CoreUsage[4];
                Cpu5Text.Text = $"{cpu.CoreUsage[5]}%"; Cpu5Bar.Value = cpu.CoreUsage[5];
                Cpu6Text.Text = $"{cpu.CoreUsage[6]}%"; Cpu6Bar.Value = cpu.CoreUsage[6];
                Cpu7Text.Text = $"{cpu.CoreUsage[7]}%"; Cpu7Bar.Value = cpu.CoreUsage[7];
                CpuAvgText.Text = $"{cpu.Average}%";
            }

            // Memory
            var mem = await _protocol.GetMemoryInfoAsync();
            if (mem != null)
            {
                MemTotalText.Text = FormatSize((long)mem.DirectTotal);
                MemAvailText.Text = FormatSize((long)mem.DirectAvailable);
                MemUsedText.Text = FormatSize((long)mem.DirectUsed);
                MemUsageBar.Value = mem.DirectUsedPercent;
                MemUsageText.Text = $"{mem.DirectUsedPercent}%";
            }

            // Extended info
            var hw = await _protocol.GetHardwareInfoAsync();
            if (hw != null)
            {
                ModelText.Text = hw.Model ?? "PlayStation 5";
            }

            var ext = await _protocol.GetExtendedInfoAsync();
            if (ext != null)
            {
                FirmwareText.Text = ext.FirmwareVersion ?? "—";
                UptimeText.Text = ext.TotalOperatingTimeHours > 0 ? $"{ext.TotalOperatingTimeHours} hours" : "—";
                BootCountText.Text = ext.BootCount > 0 ? $"{ext.BootCount}" : "—";
                BdDriveText.Text = ext.BdDrivePower >= 0 ? (ext.BdDrivePower == 1 ? "On" : "Off") : "—";
            }

            UpdateStatus($"Updated: {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            UpdateStatus($"Error: {ex.Message}");
        }
    }

    private string FormatSize(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.0} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00} GB";
    }

    // ============================================================
    // SAVES & MEDIA
    // ============================================================

    private async void SaveScanButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        UpdateStatus("Scanning save images...");
        try
        {
            var saves = await _protocol!.ListSaveFilesAsync();
            _saves.Clear();
            foreach (var s in saves) _saves.Add(s);
            UpdateStatus($"Found {saves.Count} save images");

            var (mounted, src, _, _) = await _protocol!.SaveMountStatusAsync();
            SaveMountStatusText.Text = mounted ? $"Mounted: {src}" : "No save mounted";
        }
        catch (Exception ex) { UpdateStatus($"Save scan error: {ex.Message}"); }
    }

    private async void SaveMountButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        if (SavesListBox.SelectedItem is not PS5SaveFile save) { UpdateStatus("Select a save first"); return; }
        UpdateStatus($"Mounting {save.SaveName}...");
        try
        {
            var (ok, msg) = await _protocol!.SaveMountAsync(save.Path, m => UpdateStatus(m));
            UpdateStatus(ok ? $"Mounted at {PS5Protocol.SaveMountPoint}" : $"Mount failed: {msg}");
            SaveMountStatusText.Text = ok ? $"Mounted: {save.Path}" : "No save mounted";
        }
        catch (Exception ex) { UpdateStatus($"Mount error: {ex.Message}"); }
    }

    private async void SaveUnmountButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        UpdateStatus("Unmounting save...");
        try
        {
            var (ok, msg) = await _protocol!.SaveUnmountAsync(m => UpdateStatus(m));
            UpdateStatus(ok ? "Unmounted" : $"Unmount failed: {msg}");
            if (ok) SaveMountStatusText.Text = "No save mounted";
        }
        catch (Exception ex) { UpdateStatus($"Unmount error: {ex.Message}"); }
    }

    private async void SaveDeleteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        if (SavesListBox.SelectedItem is not PS5SaveFile save) { UpdateStatus("Select a save first"); return; }
        UpdateStatus($"Deleting {save.SaveName}...");
        try
        {
            var (ok, msg) = await _protocol!.SaveDeleteAsync(save.Path);
            UpdateStatus(ok ? $"Deleted {save.SaveName}" : $"Delete failed: {msg}");
            if (ok) _saves.Remove(save);
        }
        catch (Exception ex) { UpdateStatus($"Delete error: {ex.Message}"); }
    }

    private async void ScreenshotsRefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        UpdateStatus("Loading screenshots...");
        try
        {
            var shots = await _protocol!.ListScreenshotsAsync();
            _screenshots.Clear();
            foreach (var s in shots) _screenshots.Add(s);
            UpdateStatus($"Found {shots.Count} screenshots");
        }
        catch (Exception ex) { UpdateStatus($"Screenshots error: {ex.Message}"); }
    }

    private async void CaptureScreenshotButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        UpdateStatus("Capturing screenshot on PS5...");
        try
        {
            var (ok, msg) = await _protocol!.CaptureScreenshotAsync();
            UpdateStatus(ok ? $"📸 {msg}" : $"Screenshot failed: {msg}");
            if (ok) await Task.Delay(1500).ContinueWith(_ => Dispatcher.UIThread.Post(() => ScreenshotsRefreshButton_Click(null, null!)));
        }
        catch (Exception ex) { UpdateStatus($"Screenshot error: {ex.Message}"); }
    }

    private async void ScreenshotDeleteButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        if (ScreenshotsListBox.SelectedItem is not PS5Screenshot shot) { UpdateStatus("Select a screenshot first"); return; }
        try
        {
            var (ok, msg) = await _protocol!.DeleteScreenshotAsync(shot.FullPath);
            UpdateStatus(ok ? $"Deleted {shot.FileName}" : $"Delete failed: {msg}");
            if (ok) _screenshots.Remove(shot);
        }
        catch (Exception ex) { UpdateStatus($"Delete error: {ex.Message}"); }
    }

    // ============================================================
    // DEVICES
    // ============================================================

    private async void PowerReboot_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        UpdateStatus("Rebooting PS5...");
        try
        {
            var (ok, msg) = await _protocol!.PowerActionAsync("reboot");
            UpdateStatus(ok ? "Reboot command sent — connection will drop" : $"Reboot failed: {msg}");
        }
        catch (Exception ex) { UpdateStatus($"Reboot error: {ex.Message}"); }
    }

    private async void PowerShutdown_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        UpdateStatus("Shutting down PS5...");
        try
        {
            var (ok, msg) = await _protocol!.PowerActionAsync("shutdown");
            UpdateStatus(ok ? "Shutdown command sent — connection will drop" : $"Shutdown failed: {msg}");
        }
        catch (Exception ex) { UpdateStatus($"Shutdown error: {ex.Message}"); }
    }

    private async void UsbRefresh_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        UpdateStatus("Scanning USB drives...");
        try
        {
            var drives = await _protocol!.ListUsbDrivesAsync();
            if (drives.Count == 0)
            {
                UsbListText.Text = "No USB drives mounted";
                UpdateStatus("No USB drives found");
            }
            else
            {
                UsbListText.Text = string.Join("\n", drives.Select(d =>
                    $"{d.MountPath}  [{d.FsType}]  {d.Device}  —  {d.FreeGB} free / {d.TotalGB}"));
                UpdateStatus($"Found {drives.Count} USB drive(s)");
            }
        }
        catch (Exception ex) { UpdateStatus($"USB error: {ex.Message}"); }
    }

    private async void PadRefresh_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        UpdateStatus("Reading controller info...");
        try
        {
            var info = await _protocol!.GetPadInfoAsync();
            PadInfoText.Text = info ?? "Pad info unavailable";
            UpdateStatus(info != null ? "Pad info received" : "Pad info unavailable");
        }
        catch (Exception ex) { UpdateStatus($"Pad error: {ex.Message}"); }
    }

    private async void LightBar_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        string rgb = (sender as Control)?.Tag as string ?? "0,0,255";
        try
        {
            var (ok, msg) = await _protocol!.PadActionAsync($"lightbar|{rgb}");
            UpdateStatus(ok ? $"Light bar set ({rgb})" : $"Light bar failed: {msg}");
        }
        catch (Exception ex) { UpdateStatus($"Light bar error: {ex.Message}"); }
    }

    private async void LedDim_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        string level = (sender as Control)?.Tag as string ?? "0";
        try
        {
            var (ok, msg) = await _protocol!.IccControlAsync($"led|{level}");
            UpdateStatus(ok ? $"LED brightness: {level}" : $"LED failed: {msg}");
        }
        catch (Exception ex) { UpdateStatus($"LED error: {ex.Message}"); }
    }

    private async void LedEffect_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        string name = (LedEffectCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "white";
        try
        {
            var (ok, msg) = await _protocol!.IccControlAsync($"ledeffect|{name}");
            UpdateStatus(ok ? $"LED effect: {name}" : $"LED effect failed: {msg}");
        }
        catch (Exception ex) { UpdateStatus($"LED effect error: {ex.Message}"); }
    }

    private async void Beep_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        string count = (sender as Control)?.Tag as string ?? "1";
        try
        {
            var (ok, msg) = await _protocol!.IccControlAsync($"buzzer|{count}");
            UpdateStatus(ok ? $"Beep ×{count}" : $"Beeper failed: {msg}");
        }
        catch (Exception ex) { UpdateStatus($"Beeper error: {ex.Message}"); }
    }

    private async void BeepMute_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        string mute = (sender as Control)?.Tag as string ?? "0";
        try
        {
            var (ok, msg) = await _protocol!.IccControlAsync($"buzzermute|{mute}");
            UpdateStatus(ok ? (mute == "1" ? "Beeper muted" : "Beeper unmuted") : $"Mute failed: {msg}");
        }
        catch (Exception ex) { UpdateStatus($"Mute error: {ex.Message}"); }
    }

    private async void NotifySend_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        var text = NotifyTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) { UpdateStatus("Enter notification text"); return; }
        try
        {
            var (ok, msg) = await _protocol!.NotifyAsync(text);
            UpdateStatus(ok ? "Notification sent" : $"Notify failed: {msg}");
        }
        catch (Exception ex) { UpdateStatus($"Notify error: {ex.Message}"); }
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
        public Bitmap? Cover
        {
            get => _cover;
            set { _cover = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Cover))); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private static string StoreClean(string s)
        => System.Net.WebUtility.HtmlDecode(s).Trim();

    private async void StoreRefresh_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            StoreStatusText.Text = "Fetching pkg-zone.com catalog...";
            UpdateStatus("Fetching homebrew catalog...");

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
            for (int page = 1; page <= 15; page++)
            {
                string url = $"https://pkg-zone.com/?console=ps5&page={page}";
                string html;
                try { html = await _storeHttp.GetStringAsync(url); }
                catch (Exception ex)
                {
                    if (page == 1) throw;
                    UpdateStatus($"Store page {page} unreachable ({ex.Message})");
                    break;
                }
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

            _storeItems.Clear();
            _storeItems.AddRange(items);
            ApplyStoreFilter();
            StoreStatusText.Text = $"{items.Count} PS5 packages";
            UpdateStatus($"Store catalog: {items.Count} packages");

            _ = Task.Run(async () =>
            {
                foreach (var it in _storeItems)
                {
                    try
                    {
                        var bytes = await _storeHttp.GetByteArrayAsync($"https://pkg-zone.com/images/{it.Id}/cover.png");
                        using var ms = new MemoryStream(bytes);
                        var bmp = new Bitmap(ms);
                        await Dispatcher.UIThread.InvokeAsync(() => { it.Cover = bmp; });
                    }
                    catch { }
                }
            });
        }
        catch (Exception ex)
        {
            StoreStatusText.Text = "Fetch failed";
            UpdateStatus($"Store error: {ex.Message}");
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

    private async void StoreInstall_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        string? id = (sender as Control)?.Tag as string;
        if (string.IsNullOrEmpty(id)) return;
        var item = _storeItems.FirstOrDefault(i => i.Id == id);
        string name = item?.Name ?? id;
        try
        {
            string url = $"https://pkg-zone.com/download/ps5/{id}/latest";
            UpdateStatus($"Installing {name}...");
            var (success, result) = await _protocol!.InstallPkgAsync(url);
            UpdateStatus(success
                ? $"Install accepted: {name} — check the PS5 home screen"
                : $"Install failed: {result}");
        }
        catch (Exception ex) { UpdateStatus($"Install error: {ex.Message}"); }
    }

    // ============================================================
    // SYSTEM INFO — Extended / Network / Modules
    // ============================================================

    private async void ExtInfoRefresh_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        try
        {
            var ext = await _protocol!.GetExtendedInfoAsync();
            if (ext == null) { UpdateStatus("Extended info unavailable"); return; }
            ProsperoText.Text = string.IsNullOrEmpty(ext.ProsperoVersion) ? "—" : ext.ProsperoVersion;
            ProductText.Text = string.IsNullOrEmpty(ext.ProductStr) ? ext.ProductCode : ext.ProductStr;
            ShutdownCountText.Text = ext.ShutdownCount > 0 ? ext.ShutdownCount.ToString() : "—";
            ThermalAlertText.Text = ext.ThermalAlert >= 0 ? ext.ThermalAlert.ToString() : "—";
            BdPowerText.Text = ext.BdDrivePower >= 0 ? (ext.BdDrivePower == 1 ? "On" : "Off") : "—";
            UpdateStatus("Extended info updated");
        }
        catch (Exception ex) { UpdateStatus($"ExtInfo error: {ex.Message}"); }
    }

    private async void NetRefresh_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        try
        {
            var info = await _protocol!.GetNetInfoAsync();
            NetInfoText.Text = string.IsNullOrEmpty(info) ? "Unavailable" : info;
            UpdateStatus("Network info updated");
        }
        catch (Exception ex) { UpdateStatus($"NetInfo error: {ex.Message}"); }
    }

    private async void NetSpeed_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        NetSpeedText.Text = "testing...";
        try
        {
            var mbps = await _protocol!.NetSpeedTestAsync();
            NetSpeedText.Text = mbps.HasValue ? $"{mbps.Value:0.0} Mbps" : "failed";
            UpdateStatus(mbps.HasValue ? $"LAN speed: {mbps.Value:0.0} Mbps" : "Speed test failed");
        }
        catch (Exception ex) { NetSpeedText.Text = "error"; UpdateStatus($"Speed test error: {ex.Message}"); }
    }

    private async void ModulesRefresh_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        try
        {
            var modules = await _protocol!.GetModuleListAsync();
            var items = modules.Select(m => new ModuleItem
            {
                Name = string.IsNullOrEmpty(m.Name) ? $"module_{m.Id}" : m.Name,
                Info = $"id={m.Id}  {m.Path}"
            }).ToList();
            ModulesListBox.ItemsSource = items;
            UpdateStatus($"{items.Count} modules loaded");
        }
        catch (Exception ex) { UpdateStatus($"Modules error: {ex.Message}"); }
    }

    // ============================================================
    // TOOLS — Shell / Search / Memory / Apps / Kernel Log
    // ============================================================

    private bool _shellOpen;
    private readonly ObservableCollection<string> _searchResults = new();
    private readonly ObservableCollection<AppDisplayItem> _apps = new();
    private AppDisplayItem? _selectedApp;
    private bool _collectionsInit;

    private void EnsureToolCollections()
    {
        if (_collectionsInit) return;
        _collectionsInit = true;
        SearchResultsBox.ItemsSource = _searchResults;
        AppsListBox.ItemsSource = _apps;
    }

    private void ShellAppend(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ShellOutputBox.Text += text + "\n";
            if (ShellOutputBox.Text?.Length > 40000)
                ShellOutputBox.Text = ShellOutputBox.Text.Substring(ShellOutputBox.Text.Length - 30000);
        });
    }

    private async void ShellExec_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        string cmd = ShellInputBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(cmd)) return;
        try
        {
            if (!_shellOpen)
            {
                bool ok = await _protocol!.OpenShellAsync();
                if (!ok) { UpdateStatus("Shell open failed"); return; }
                _shellOpen = true;
            }
            ShellAppend($"$ {cmd}");
            ShellInputBox.Text = "";
            string output = await _protocol!.ExecuteShellCommandAsync(cmd);
            ShellAppend(string.IsNullOrEmpty(output) ? "(no output)" : output.TrimEnd());
        }
        catch (Exception ex) { UpdateStatus($"Shell error: {ex.Message}"); }
    }

    private void ShellInput_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter) ShellExec_Click(sender, e);
    }

    private async void IndexStart_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        IndexStatusText.Text = "indexing...";
        try
        {
            bool ok = await _protocol!.StartIndexAsync("/");
            if (!ok) { IndexStatusText.Text = "index failed"; return; }
            for (int i = 0; i < 60; i++)
            {
                var st = await _protocol.GetIndexStatusAsync();
                IndexStatusText.Text = st ?? "indexing...";
                if (st != null && st.Contains("done", StringComparison.OrdinalIgnoreCase)) break;
                await Task.Delay(2000);
            }
        }
        catch (Exception ex) { IndexStatusText.Text = "error"; UpdateStatus($"Index error: {ex.Message}"); }
    }

    private async void SearchRun_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        string q = SearchBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(q)) return;
        try
        {
            EnsureToolCollections();
            _searchResults.Clear();
            var results = await _protocol!.SearchIndexAsync(q);
            foreach (var r in results) _searchResults.Add(string.IsNullOrEmpty(r.Path) ? r.Name : r.Path);
            UpdateStatus($"{_searchResults.Count} results");
        }
        catch (Exception ex) { UpdateStatus($"Search error: {ex.Message}"); }
    }

    private async void MemRegions_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        if (!int.TryParse(MemPidBox.Text?.Trim(), out int pid)) { UpdateStatus("Enter a valid pid"); return; }
        try
        {
            var regions = await _protocol!.MemRegionsAsync(pid);
            MemOutputBox.Text = regions ?? "(no regions)";
            UpdateStatus("Regions loaded");
        }
        catch (Exception ex) { UpdateStatus($"MemRegions error: {ex.Message}"); }
    }

    private async void MemRead_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        if (!int.TryParse(MemPidBox.Text?.Trim(), out int pid)) { UpdateStatus("Enter a valid pid"); return; }
        string addrStr = MemAddrBox.Text?.Trim() ?? "";
        if (!ulong.TryParse(addrStr.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out ulong addr))
        {
            UpdateStatus("Enter a valid hex address");
            return;
        }
        int len = int.TryParse(MemLenBox.Text?.Trim(), out int l) ? Math.Clamp(l, 1, 4096) : 256;
        try
        {
            var data = await _protocol!.MemReadAsync(pid, addr, len);
            if (data == null) { MemOutputBox.Text = $"Read failed: {_protocol.LastError}"; return; }
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < data.Length; i += 16)
            {
                sb.Append($"{addr + (ulong)i:X8}  ");
                int n = Math.Min(16, data.Length - i);
                for (int j = 0; j < n; j++) sb.Append($"{data[i + j]:X2} ");
                for (int j = n; j < 16; j++) sb.Append("   ");
                sb.Append(' ');
                for (int j = 0; j < n; j++)
                {
                    byte b = data[i + j];
                    sb.Append(b >= 32 && b < 127 ? (char)b : '.');
                }
                sb.Append('\n');
            }
            MemOutputBox.Text = sb.ToString();
            UpdateStatus($"Read {data.Length} bytes");
        }
        catch (Exception ex) { UpdateStatus($"MemRead error: {ex.Message}"); }
    }

    private async void AppsRefresh_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        try
        {
            EnsureToolCollections();
            var raw = await _protocol!.AppListV2Async();
            _apps.Clear();
            if (raw != null)
            {
                foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("appinfo=", StringComparison.Ordinal)) continue;
                    var f = line.Split('|');
                    if (f.Length < 7 || !int.TryParse(f[0], out int pid)) continue;
                    int.TryParse(f[1], out int appId);
                    int.TryParse(f[5], out int cpuX100);
                    _apps.Add(new AppDisplayItem
                    {
                        Pid = pid,
                        AppId = appId,
                        Title = string.IsNullOrEmpty(f[2]) ? f[3] : f[2],
                        TitleId = $"appId={f[1]} pid={f[0]}",
                        Cpu = $"cpu={cpuX100 / 100.0:0.0}%",
                        Status = f[6].Trim() == "1" ? "suspended" : f[4]
                    });
                }
            }
            UpdateStatus($"{_apps.Count} apps");
        }
        catch (Exception ex) { UpdateStatus($"Apps error: {ex.Message}"); }
    }

    private void AppsListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => _selectedApp = AppsListBox.SelectedItem as AppDisplayItem;

    private async void AppSuspend_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection() || _selectedApp == null) { UpdateStatus("Select an app"); return; }
        var (ok, msg) = await _protocol!.AppSuspendAsync(_selectedApp.AppId, _selectedApp.Pid);
        UpdateStatus(ok ? "App suspended" : $"Suspend failed: {msg}");
    }

    private async void AppResume_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection() || _selectedApp == null) { UpdateStatus("Select an app"); return; }
        var (ok, msg) = await _protocol!.AppResumeAsync(_selectedApp.AppId, _selectedApp.Pid);
        UpdateStatus(ok ? "App resumed" : $"Resume failed: {msg}");
    }

    private async void AppKill_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection() || _selectedApp == null) { UpdateStatus("Select an app"); return; }
        var (ok, msg) = await _protocol!.AppKillAsync(_selectedApp.AppId, _selectedApp.Pid);
        UpdateStatus(ok ? "App killed" : $"Kill failed: {msg}");
        if (ok) AppsRefresh_Click(sender, e);
    }

    private async void KlogRefresh_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        try
        {
            var log = await _protocol!.GetKernelLogAsync(32768);
            KlogBox.Text = string.IsNullOrEmpty(log) ? "(empty)" : log;
            UpdateStatus("Kernel log loaded");
        }
        catch (Exception ex) { UpdateStatus($"Klog error: {ex.Message}"); }
    }
}

public class ModuleItem
{
    public string Name { get; set; } = "";
    public string Info { get; set; } = "";
}

public class AppDisplayItem
{
    public int Pid { get; set; }
    public int AppId { get; set; }
    public string Title { get; set; } = "";
    public string TitleId { get; set; } = "";
    public string Cpu { get; set; } = "";
    public string Status { get; set; } = "";
}

public class FileItem
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public string Icon { get; set; } = "📄";
    public string SizeText { get; set; } = "";
}

public class GameItem : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string TitleId { get; set; } = "";
    public string Path { get; set; } = "";
    public string SizeText { get; set; } = "";
    public ulong Size { get; set; }
    public bool IsRunning { get; set; }
    public string RunningBadge => IsRunning ? "   ▶ RUNNING" : "";

    private Avalonia.Media.IImage? _icon;
    public Avalonia.Media.IImage? Icon
    {
        get => _icon;
        set { _icon = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon))); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
