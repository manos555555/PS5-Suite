using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace PS5Upload
{
    // Modal browser for the decrypted save mounted at /data/save_mnt.
    // Reuses the regular remote-file commands (LIST_DIR / DOWNLOAD / UPLOAD /
    // DELETE / MKDIR) — the mount point behaves like any other directory.
    public partial class SaveBrowserWindow : UserControl, OverlayDialogs.IOverlayClosable
    {
        /// <summary>Raised when the view wants its host overlay to close.</summary>
        public event Action? RequestClose;

        /// <summary>True when the user chose "Unmount &amp; Close" (read after the overlay closes).</summary>
        public bool UnmountRequested { get; private set; }

        private readonly PS5Protocol _protocol;
        private readonly string _rootPath;
        private readonly string _ps5Ip;
        private string _currentPath;

        public class SaveEntryItem
        {
            public string Name { get; set; } = "";
            public bool IsDirectory { get; set; }
            public long Size { get; set; }
            public string IconText => IsDirectory ? "📁" : "📄";
            public string SizeDisplay
            {
                get
                {
                    if (IsDirectory) return "";
                    double mb = Size / (1024.0 * 1024.0);
                    if (mb < 1) return $"{Size / 1024.0:F1} KB";
                    if (mb < 1024) return $"{mb:F1} MB";
                    return $"{mb / 1024.0:F2} GB";
                }
            }
        }

        public SaveBrowserWindow()
        {
            InitializeComponent();
            _protocol = null!;
            _ps5Ip = string.Empty;
            _rootPath = string.Empty;
            _currentPath = string.Empty;
        }

        public SaveBrowserWindow(PS5Protocol protocol, string rootPath, string ps5Ip)
        {
            InitializeComponent();
            _protocol = protocol;
            _ps5Ip = ps5Ip;
            _rootPath = rootPath.TrimEnd('/');
            _currentPath = _rootPath;
            AttachedToVisualTree += async (_, __) => await RefreshAsync();
        }

        private void SetStatus(string s) => StatusText.Text = s;

        private async Task RefreshAsync()
        {
            SetStatus("Loading…");
            try
            {
                var entries = await _protocol.ListDirAsync(_currentPath);
                var items = entries
                    .OrderByDescending(e => e.IsDirectory)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => new SaveEntryItem { Name = e.Name, IsDirectory = e.IsDirectory, Size = e.Size })
                    .ToList();
                FileListBox.ItemsSource = items;
                PathText.Text = _currentPath;
                UpButton.IsEnabled = _currentPath.Length > _rootPath.Length;
                SetStatus($"{items.Count} item(s)");
            }
            catch (Exception ex)
            {
                SetStatus($"List failed: {ex.Message}");
            }
        }

        private async void RefreshButton_Click(object? sender, RoutedEventArgs e) => await RefreshAsync();

        private async void UpButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_currentPath.Length <= _rootPath.Length) return;
            int idx = _currentPath.LastIndexOf('/');
            _currentPath = idx <= 0 ? _rootPath : _currentPath.Substring(0, idx);
            if (_currentPath.Length < _rootPath.Length) _currentPath = _rootPath;
            await RefreshAsync();
        }

        private async void FileListBox_DoubleTapped(object? sender, TappedEventArgs e)
        {
            if (FileListBox.SelectedItem is SaveEntryItem item && item.IsDirectory)
            {
                _currentPath = _currentPath.TrimEnd('/') + "/" + item.Name;
                await RefreshAsync();
            }
        }

        private async void DownloadFileButton_Click(object? sender, RoutedEventArgs e)
        {
            if (FileListBox.SelectedItem is not SaveEntryItem item) { SetStatus("Select a file first."); return; }
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            string remote = _currentPath.TrimEnd('/') + "/" + item.Name;

            if (item.IsDirectory)
            {
                var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose destination folder" });
                if (folders.Count == 0) return;
                var dest = folders[0].TryGetLocalPath();
                if (dest == null) return;
                SetStatus($"Downloading folder {item.Name}…");
                try
                {
                    var r = await _protocol.DownloadFolderAsync(remote, Path.Combine(dest, item.Name), _ps5Ip, null, System.Threading.CancellationToken.None);
                    SetStatus($"Downloaded {r.filesDownloaded} files ({r.totalBytes / 1024} KB), {r.filesFailed} failed");
                }
                catch (Exception ex) { SetStatus($"Download failed: {ex.Message}"); }
            }
            else
            {
                var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save file as",
                    SuggestedFileName = item.Name
                });
                if (file == null) return;
                var dest = file.TryGetLocalPath();
                if (dest == null) return;
                SetStatus($"Downloading {item.Name}…");
                try
                {
                    bool ok = await _protocol.DownloadFileAsync(remote, dest);
                    SetStatus(ok ? $"Saved to {dest}" : "Download failed");
                }
                catch (Exception ex) { SetStatus($"Download failed: {ex.Message}"); }
            }
        }

        private async void UploadFileButton_Click(object? sender, RoutedEventArgs e)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select file(s) to upload into the save",
                AllowMultiple = true
            });
            if (files.Count == 0) return;

            int done = 0, failed = 0;
            foreach (var f in files)
            {
                var local = f.TryGetLocalPath();
                if (local == null) { failed++; continue; }
                string remote = _currentPath.TrimEnd('/') + "/" + Path.GetFileName(local);
                SetStatus($"Uploading {Path.GetFileName(local)}…");
                try
                {
                    bool ok = await _protocol.UploadFileAsync(local, remote);
                    if (ok) done++; else failed++;
                }
                catch { failed++; }
            }
            SetStatus($"Uploaded {done}, failed {failed}");
            await RefreshAsync();
        }

        private async void NewFolderButton_Click(object? sender, RoutedEventArgs e)
        {
            string? name = null;
            await OverlayDialogs.ShowAsync(close =>
            {
                var nameBox = new TextBox { PlaceholderText = "Folder name", Margin = new Avalonia.Thickness(0, 0, 0, 8) };
                var okBtn = new Button { Content = "Create", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Padding = new Avalonia.Thickness(20, 6) };
                var panel = new StackPanel { Spacing = 8 };
                panel.Children.Add(new TextBlock { Text = "New Folder", FontSize = 17, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = Avalonia.Media.Brushes.White, Margin = new Avalonia.Thickness(0, 0, 0, 8) });
                panel.Children.Add(nameBox);
                panel.Children.Add(okBtn);
                okBtn.Click += (_, __) => { name = nameBox.Text; close(); };
                return panel;
            }, maxWidth: 360);
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                bool ok = await _protocol.CreateDirAsync(_currentPath.TrimEnd('/') + "/" + name.Trim());
                SetStatus(ok ? $"Created {name}" : "Create failed");
            }
            catch (Exception ex) { SetStatus($"Create failed: {ex.Message}"); }
            await RefreshAsync();
        }

        private async void DeleteEntryButton_Click(object? sender, RoutedEventArgs e)
        {
            if (FileListBox.SelectedItem is not SaveEntryItem item) { SetStatus("Select an entry first."); return; }
            string remote = _currentPath.TrimEnd('/') + "/" + item.Name;
            SetStatus($"Deleting {item.Name}…");
            try
            {
                bool ok = await _protocol.DeleteFileAsync(remote);
                SetStatus(ok ? $"Deleted {item.Name}" : "Delete failed");
            }
            catch (Exception ex) { SetStatus($"Delete failed: {ex.Message}"); }
            await RefreshAsync();
        }

        private void CloseStayMountedButton_Click(object? sender, RoutedEventArgs e)
        {
            UnmountRequested = false;
            RequestClose?.Invoke();
        }

        private void UnmountCloseButton_Click(object? sender, RoutedEventArgs e)
        {
            UnmountRequested = true;
            RequestClose?.Invoke();
        }
    }
}
