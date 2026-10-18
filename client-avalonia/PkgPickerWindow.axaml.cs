using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PS5Upload
{
    /// <summary>
    /// Modal PS5 file browser used by Tools → PKG → "Browse PS5".
    /// Lets the user navigate the console filesystem and pick one or more
    /// .pkg files without leaving the Tools tab.
    /// </summary>
    public partial class PkgPickerWindow : UserControl, OverlayDialogs.IOverlayClosable
    {
        /// <summary>Raised when the view wants its host overlay to close.</summary>
        public event Action? RequestClose;

        private readonly PS5Protocol? _protocol;
        private string _currentPath = "/user/data";
        private List<FileEntry> _entries = new();

        /// <summary>Full PS5 paths of the selected files (null when cancelled).</summary>
        public List<string>? SelectedPaths { get; private set; }

        private class Row
        {
            public string Name { get; set; } = "";
            public string FullPath { get; set; } = "";
            public bool IsDirectory { get; set; }
            public bool IsPkg { get; set; }
            public long Size { get; set; }
            public string Icon => IsDirectory ? "📁" : (IsPkg ? "📦" : "📄");
            public string SizeText => IsDirectory ? "" : FileUtils.FormatFileSize(Size);
            public string NameColor => IsDirectory ? "#8FD4FF" : (IsPkg ? "#FFFFFF" : "#808080");
        }

        public PkgPickerWindow()
        {
            InitializeComponent();
        }

        public PkgPickerWindow(PS5Protocol protocol, string startPath) : this()
        {
            _protocol = protocol;
            if (!string.IsNullOrWhiteSpace(startPath))
                _currentPath = startPath.TrimEnd('/');
            AttachedToVisualTree += async (_, _) => await LoadDir(_currentPath);
        }

        private async Task LoadDir(string path)
        {
            StatusText.Text = "Loading…";
            PathTextBox.Text = path;
            try
            {
                if (_protocol == null) { StatusText.Text = "Not connected"; return; }
                _entries = (await _protocol.ListDirAsync(path)).ToList();
                _currentPath = path;
                RebuildList();
                StatusText.Text = $"{_entries.Count(e => e.IsDirectory)} folders, {_entries.Count(e => !e.IsDirectory)} files";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private void RebuildList()
        {
            bool onlyPkg = OnlyPkgCheckBox.IsChecked == true;
            var rows = _entries
                .Where(e => e.IsDirectory || !onlyPkg ||
                            e.Name.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => new Row
                {
                    Name = e.Name,
                    FullPath = (_currentPath == "/" ? "" : _currentPath) + "/" + e.Name,
                    IsDirectory = e.IsDirectory,
                    IsPkg = e.Name.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase),
                    Size = e.Size,
                })
                .ToList();
            FilesListBox.ItemsSource = rows;
        }

        private async void FilesListBox_DoubleTapped(object? sender, TappedEventArgs e)
        {
            if (FilesListBox.SelectedItem is Row row && row.IsDirectory)
                await LoadDir(row.FullPath);
        }

        private async void Up_Click(object? sender, RoutedEventArgs e)
        {
            string parent = _currentPath.TrimEnd('/');
            int idx = parent.LastIndexOf('/');
            parent = idx <= 0 ? "/" : parent[..idx];
            await LoadDir(parent);
        }

        private async void Refresh_Click(object? sender, RoutedEventArgs e)
        {
            await LoadDir(_currentPath);
        }

        private async void PathTextBox_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(PathTextBox.Text))
                await LoadDir(PathTextBox.Text.Trim());
        }

        private void OnlyPkg_Changed(object? sender, RoutedEventArgs e) => RebuildList();

        private void Select_Click(object? sender, RoutedEventArgs e)
        {
            var sel = FilesListBox.SelectedItems?.Cast<Row>()
                .Where(r => !r.IsDirectory)
                .Select(r => r.FullPath)
                .ToList();
            if (sel == null || sel.Count == 0)
            {
                StatusText.Text = "Select at least one file";
                return;
            }
            SelectedPaths = sel;
            RequestClose?.Invoke();
        }

        private void Cancel_Click(object? sender, RoutedEventArgs e)
        {
            SelectedPaths = null;
            RequestClose?.Invoke();
        }
    }
}
