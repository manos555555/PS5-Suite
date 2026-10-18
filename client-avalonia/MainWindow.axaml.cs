using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Text;

namespace PS5Upload
{
    public partial class MainWindow : UserControl
    {
        private PS5Protocol _protocol = new PS5Protocol();
        private ObservableCollection<LocalFileItem> _localFiles = new();
        private ObservableCollection<PS5FileItem> _ps5Files = new();
        private ObservableCollection<PS5FileItem> _ps5FilesFiltered = new();
        private string _currentPS5Path = "/data";
        private bool _isDeleting;
        private CancellationTokenSource? _uploadCancellation;
        private string _ps5IpAddress = "";
        private string _searchQuery = "";

        // Multi-PS5 Support
        private Dictionary<string, string> _ps5Profiles = new();
        private const string ProfilesFileName = "ps5_profiles.json";

        // Favorites/Bookmarks
        private List<string> _favoritePaths = new();
        private const string FavoritesFileName = "ps5_favorites.json";

        // Saved NAS connections
        private List<NasManager.SavedConnection> _nasSaved = new();
        private const string NasFileName = "ps5_nas_connections.json";

        // Transfer History
        private ObservableCollection<TransferHistoryItem> _completedTransfers = new();
        private ObservableCollection<TransferHistoryItem> _failedTransfers = new();

        // Parallel upload settings - optimized for maximum throughput
        private const int MaxParallelUploads = 28;  // Increased from 24 for better saturation
        private const long SmallFileThresholdBytes = 5L * 1024 * 1024;
        private const long LargeFileThresholdBytes = 100L * 1024 * 1024;
        private const int MaxParallelLargeFiles = 8;  // Increased from 6
        private const long HugeFileThresholdBytes = 20L * 1024 * 1024 * 1024;
        private const int MaxParallelHugeFiles = 4;  // Increased from 3
        // Per-connection throughput is the bottleneck (each lane runs its own TCP
        // stream), so big files get MANY lanes instead of 3-4. The server accepts
        // 32 sessions and the pool pre-warms 27, so 16 lanes are always available.
        private const int MaxParallelChunksForLargeFile = 10;  // Increased from 8
        private const int MaxParallelChunksForHugeFile = 16;   // Increased from 12
        private const long ChunkLogIntervalBytes = 512L * 1024 * 1024;
        private const long SmallFileLogIntervalBytes = 100L * 1024 * 1024;
        private const long ChunkThresholdBytes = LargeFileThresholdBytes;
        private const long MinChunkSizeBytes = 128L * 1024 * 1024;
        private const long DefaultChunkSizeBytes = 512L * 1024 * 1024;
        private const long HugeFileChunkSizeBytes = 2048L * 1024 * 1024;  // Increased from 1536MB to 2GB

        // Total upload tracking
        private int _totalFilesToUpload = 0;
        private long _totalBytesToUpload = 0;
        private long _totalBytesUploaded = 0;
        private int _completedFiles = 0;
        private DateTime _uploadStartTime;
        private readonly object _progressLock = new();
        private ConcurrentDictionary<string, long> _fileProgressBytes = new();
        private ConcurrentDictionary<string, ConcurrentDictionary<long, long>> _fileChunkProgressBytes = new();
        private ConcurrentDictionary<string, long> _chunkLogLastBytes = new();
        private TimeSpan _smoothedETA = TimeSpan.Zero;
        private const double ETASmoothingFactor = 0.15;

        // Sliding window for real-time speed
        private const int SpeedWindowSize = 10;
        private long[] _speedWindowBytes = new long[SpeedWindowSize];
        private DateTime[] _speedWindowTimes = new DateTime[SpeedWindowSize];
        private int _speedWindowIndex = 0;
        private int _speedWindowCount = 0;
        private double _currentSpeed = 0;

        // Current file progress
        private string _currentFileName = "";
        private long _currentFileBytes = 0;
        private long _currentFileTotalBytes = 0;

        // Real-time UI update timer
        private DispatcherTimer _uiUpdateTimer;
        private int _activeTaskCount = 0;

        // Connection pooling
        private readonly ConcurrentQueue<PS5Protocol> _connectionPool = new();
        private int _currentPoolConnections = 0;
        private int _activeLargeUploads = 0;
        private int _activeHugeUploads = 0;

        // Small file logging batches
        private readonly object _smallFileLogLock = new();
        private int _smallFileBatchRemainder = 0;
        private int _smallFileCompletedTotal = 0;
        private const int SmallFileLogBatchSize = 50;
        private long _smallFileBatchBytes = 0;
        private long _smallFileTotalBytes = 0;

        // Log throttling
        private int _logCounter = 0;
        private const int MaxLogLines = 1000;
        private const int MaxLogChars = 256 * 1024;
        private readonly StringBuilder _logBuf = new();
        private readonly object _logBufLock = new();
        private long _lastLogFlushTicks = 0;

        // Duplicate file handling
        private enum DuplicateAction { Ask, Replace, Skip, ReplaceAll, SkipAll }
        private DuplicateAction _duplicateAction = DuplicateAction.Ask;

        // Shell Terminal
        // Sort mode for PS5 file list
        private enum SortMode { Name, Size, Date }
        private SortMode _currentSortMode = SortMode.Name;

        private bool _shellActive = false;
        private ObservableCollection<string> _shellOutput = new();
        private string _shellCurrentDir = "/data";

        // Auto-send payload
        private bool _autoSendPayload = false;
        private string _payloadPath = "";
        private int _payloadPort = 9021;

        // Icon caches
        private readonly Dictionary<string, IImage> _iconCache = new();
        private readonly Dictionary<string, IImage> _screenshotThumbCache = new();

        // Saves & Screenshots
        private List<PS5SaveFile> _currentSaves = new();
        private List<PS5Screenshot> _currentScreenshots = new();

        // Hardware
        private DispatcherTimer? _hwAutoTimer;
        private int _hwBusyFlag;
        private bool _hwStaticLoaded;
        private bool _isRefreshingStorage = false;

        public MainWindow()
        {
            InitializeComponent();
            LocalFilesListBox.ItemsSource = _localFiles;
            PS5FilesListBox.ItemsSource = _ps5FilesFiltered;
            ShellOutputListBox.ItemsSource = _shellOutput;
            CompletedTransfersListBox.ItemsSource = _completedTransfers;
            FailedTransfersListBox.ItemsSource = _failedTransfers;

            _uiUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _uiUpdateTimer.Tick += UiUpdateTimer_Tick;

            _protocol.OnProgressMessage += (message) => Dispatcher.UIThread.Post(() => Log(message));

            // Drag-drop for local files
            LocalFilesListBox.AddHandler(DragDrop.DropEvent, LocalFilesListBox_Drop);
            LocalFilesListBox.AddHandler(DragDrop.DragOverEvent, LocalFilesListBox_DragOver);

            // Search text changed - use PropertyChanged event
            SearchTextBox.PropertyChanged += (s, args) =>
            {
                if (args.Property == TextBox.TextProperty)
                {
                    _searchQuery = SearchTextBox.Text?.Trim() ?? "";
                    ApplySearchFilter();
                }
            };

            Log("Application started");
            LoadProfiles();
            LoadFavorites();
            LoadNasConnections();
            LoadSettings();

            if (AutoSendPayloadCheckBox != null) AutoSendPayloadCheckBox.IsChecked = _autoSendPayload;
            if (PayloadPathTextBox != null) PayloadPathTextBox.Text = _payloadPath;
            if (PayloadPortTextBox != null) PayloadPortTextBox.Text = _payloadPort.ToString();

            if (_autoSendPayload && !string.IsNullOrEmpty(_payloadPath) && !string.IsNullOrEmpty(_ps5IpAddress))
                _ = AutoSendPayloadOnStartup();

            // The shared overlay layer hosts every modal dialog (real windows
            // don't exist on Android's single-view lifetime).
            OverlayDialogs.LayerHost = DialogLayerHost;
            OverlayDialogs.OverlayRoot = DialogOverlay;

            AttachedToVisualTree += (_, _) => _ = CheckForAppUpdateAsync();

            // Responsive layout: collapse the sidebar into a hamburger drawer
            // and stack the file panes when the view is too narrow (phones).
            EffectiveViewportChanged += (_, e) => ApplyResponsiveLayout(e.EffectiveViewport.Width < 700);
        }

        // ============================================================
        // RESPONSIVE LAYOUT (mobile drawer + single-pane file view)
        // ============================================================
        private bool _isMobileLayout;
        private bool _mobileDrawerOpen;
        private bool _mobileShowingPs5;

        private void ApplyResponsiveLayout(bool mobile)
        {
            if (mobile == _isMobileLayout) return;
            _isMobileLayout = mobile;
            CloseMobileDrawer();
            SidebarPane.IsVisible = !mobile;
            RootLayoutGrid.ColumnDefinitions[0].Width = mobile ? new GridLength(0) : new GridLength(220);
            MobileTopBar.IsVisible = mobile;
            MobileFilesTabBar.IsVisible = mobile;
            ContentArea.Margin = new Thickness(mobile ? 4 : 15);

            if (mobile)
            {
                // Single-pane file browser — tab bar switches Local/PS5
                Grid.SetColumn(Ps5FilesPane, 0);
                Grid.SetColumnSpan(LocalFilesPane, 3);
                Grid.SetColumnSpan(Ps5FilesPane, 3);
                ShowMobileFilePane(_mobileShowingPs5);

                // Storage info: 3 columns -> vertical stack
                StorageInfoGrid.ColumnDefinitions.Clear();
                StorageInfoGrid.RowDefinitions.Clear();
                StorageInfoGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                for (int i = 0; i < StorageInfoGrid.Children.Count; i++)
                {
                    StorageInfoGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    Grid.SetRow(StorageInfoGrid.Children[i], i);
                    Grid.SetColumn(StorageInfoGrid.Children[i], 0);
                }
            }
            else
            {
                Grid.SetColumn(Ps5FilesPane, 2);
                Grid.SetColumnSpan(LocalFilesPane, 1);
                Grid.SetColumnSpan(Ps5FilesPane, 1);
                LocalFilesPane.IsVisible = true;
                Ps5FilesPane.IsVisible = true;

                StorageInfoGrid.RowDefinitions.Clear();
                StorageInfoGrid.ColumnDefinitions.Clear();
                for (int i = 0; i < StorageInfoGrid.Children.Count; i++)
                {
                    StorageInfoGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                    Grid.SetColumn(StorageInfoGrid.Children[i], i);
                    Grid.SetRow(StorageInfoGrid.Children[i], 0);
                }
            }
        }

        private void OpenMobileDrawer()
        {
            if (!_isMobileLayout) return;
            _mobileDrawerOpen = true;
            SidebarPane.Width = 280;
            SidebarPane.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            SidebarPane.ZIndex = 900;
            SidebarPane.IsVisible = true;
            MobileNavDimmer.IsVisible = true;
        }

        private void CloseMobileDrawer()
        {
            _mobileDrawerOpen = false;
            MobileNavDimmer.IsVisible = false;
            SidebarPane.ZIndex = 0;
            SidebarPane.Width = double.NaN;
            SidebarPane.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            if (_isMobileLayout) SidebarPane.IsVisible = false;
        }

        private void ShowMobileFilePane(bool ps5)
        {
            _mobileShowingPs5 = ps5;
            LocalFilesPane.IsVisible = !ps5;
            Ps5FilesPane.IsVisible = ps5;
            MobileLocalTab.Background = ps5
                ? new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42))
                : new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
            MobilePs5Tab.Background = ps5
                ? new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC))
                : new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x42));
        }

        private void MobileMenuButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_mobileDrawerOpen) CloseMobileDrawer(); else OpenMobileDrawer();
        }

        private void MobileNavDimmer_Tapped(object? sender, RoutedEventArgs e) => CloseMobileDrawer();

        private void MobileLocalTab_Click(object? sender, RoutedEventArgs e) => ShowMobileFilePane(false);
        private void MobilePs5Tab_Click(object? sender, RoutedEventArgs e) => ShowMobileFilePane(true);

        // ============================================================
        // HELPER DIALOGS
        // ============================================================
        private async Task ShowMessageAsync(string message, string title = "Info")
        {
            await OverlayDialogs.ShowAsync(close =>
            {
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 15) });
                panel.Children.Add(new TextBlock { Text = message, Foreground = Brushes.White, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) });
                var btn = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Width = 80 };
                btn.Click += (s, ev) => close();
                panel.Children.Add(btn);
                return panel;
            }, maxWidth: 450);
        }

        private async Task<bool> ShowConfirmAsync(string message, string title = "Confirm")
        {
            bool result = false;
            await OverlayDialogs.ShowAsync(close =>
            {
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 15) });
                panel.Children.Add(new TextBlock { Text = message, Foreground = Brushes.White, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) });
                var btnPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Spacing = 10 };
                var yesBtn = new Button { Content = "Yes", Width = 80 };
                var noBtn = new Button { Content = "No", Width = 80 };
                yesBtn.Click += (s, ev) => { result = true; close(); };
                noBtn.Click += (s, ev) => { result = false; close(); };
                btnPanel.Children.Add(yesBtn);
                btnPanel.Children.Add(noBtn);
                panel.Children.Add(btnPanel);
                return panel;
            }, maxWidth: 450);
            return result;
        }

        private async Task<string?> ShowInputDialogAsync(string title, string label, string defaultValue = "")
        {
            string? result = null;
            await OverlayDialogs.ShowAsync(close =>
            {
                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var header = new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 15) };
                Grid.SetRow(header, 0);
                var lbl = new TextBlock { Text = label, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 10) };
                Grid.SetRow(lbl, 1);
                var textBox = new TextBox { Text = defaultValue, Margin = new Thickness(0, 0, 0, 15) };
                Grid.SetRow(textBox, 2);
                var buttonPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 10 };
                Grid.SetRow(buttonPanel, 3);
                var okButton = new Button { Content = "OK", Width = 80 };
                okButton.Click += (s, args) => { result = textBox.Text; close(); };
                var cancelButton = new Button { Content = "Cancel", Width = 80 };
                cancelButton.Click += (s, args) => { close(); };
                buttonPanel.Children.Add(okButton);
                buttonPanel.Children.Add(cancelButton);
                grid.Children.Add(header);
                grid.Children.Add(lbl);
                grid.Children.Add(textBox);
                grid.Children.Add(buttonPanel);
                return grid;
            }, maxWidth: 400);
            return result;
        }

        /// Chmod dialog: r/w/x checkboxes for Owner/Group/Others synced with
        /// an octal input. Returns the chosen mode, or null when cancelled.
        private async Task<uint?> ShowChmodDialogAsync(string fileName, int currentMode)
        {
            uint? result = null;
            bool syncing = false;
            Action? close = null;

            var ownerR = new CheckBox { IsChecked = (currentMode & 0x100) != 0 };
            var ownerW = new CheckBox { IsChecked = (currentMode & 0x80) != 0 };
            var ownerX = new CheckBox { IsChecked = (currentMode & 0x40) != 0 };
            var groupR = new CheckBox { IsChecked = (currentMode & 0x20) != 0 };
            var groupW = new CheckBox { IsChecked = (currentMode & 0x10) != 0 };
            var groupX = new CheckBox { IsChecked = (currentMode & 0x08) != 0 };
            var otherR = new CheckBox { IsChecked = (currentMode & 0x04) != 0 };
            var otherW = new CheckBox { IsChecked = (currentMode & 0x02) != 0 };
            var otherX = new CheckBox { IsChecked = (currentMode & 0x01) != 0 };

            var boxes = new[] { ownerR, ownerW, ownerX, groupR, groupW, groupX, otherR, otherW, otherX };
            int[] bits = { 0x100, 0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01 };

            var octalBox = new TextBox { Width = 100, Text = Convert.ToString(currentMode & 0x1FF, 8).PadLeft(4, '0'), Margin = new Thickness(10, 0, 0, 0) };

            void SyncFromBoxes()
            {
                if (syncing) return;
                syncing = true;
                int mode = 0;
                for (int i = 0; i < boxes.Length; i++)
                    if (boxes[i].IsChecked == true) mode |= bits[i];
                octalBox.Text = Convert.ToString(mode, 8).PadLeft(4, '0');
                syncing = false;
            }

            void SyncFromOctal()
            {
                if (syncing) return;
                try
                {
                    int mode = Convert.ToInt32(octalBox.Text?.Trim() ?? "", 8) & 0x1FF;
                    syncing = true;
                    for (int i = 0; i < boxes.Length; i++)
                        boxes[i].IsChecked = (mode & bits[i]) != 0;
                    syncing = false;
                }
                catch { }
            }

            foreach (var b in boxes)
                b.IsCheckedChanged += (s, ev) => SyncFromBoxes();
            octalBox.PropertyChanged += (s, ev) =>
            {
                if (ev.Property == TextBox.TextProperty) SyncFromOctal();
            };

            var root = new StackPanel { Margin = new Thickness(10), Spacing = 12 };
            root.Children.Add(new TextBlock
            {
                Text = $"Change Permissions - {fileName}",
                FontSize = 17, FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White
            });
            root.Children.Add(new TextBlock
            {
                Text = $"Current: {Convert.ToString(currentMode & 0x1FF, 8).PadLeft(4, '0')}  ({ModeToRwx(currentMode)})",
                Foreground = new SolidColorBrush(Color.FromRgb(153, 153, 153)),
                Margin = new Thickness(0, 0, 0, 5)
            });

            var permGrid = new Grid { ColumnSpacing = 15, RowSpacing = 8 };
            permGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            permGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            permGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            permGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            permGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            permGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            permGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            permGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            string[] headers = { "", "Read", "Write", "Execute" };
            for (int c = 0; c < 4; c++)
            {
                var h = new TextBlock { Text = headers[c], Foreground = Brushes.White, FontWeight = FontWeight.SemiBold };
                Grid.SetRow(h, 0); Grid.SetColumn(h, c);
                permGrid.Children.Add(h);
            }

            string[] rowLabels = { "Owner", "Group", "Others" };
            CheckBox[][] rows = { new[] { ownerR, ownerW, ownerX }, new[] { groupR, groupW, groupX }, new[] { otherR, otherW, otherX } };
            for (int r = 0; r < 3; r++)
            {
                var lbl = new TextBlock { Text = rowLabels[r], Foreground = Brushes.White, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                Grid.SetRow(lbl, r + 1); Grid.SetColumn(lbl, 0);
                permGrid.Children.Add(lbl);
                for (int c = 0; c < 3; c++)
                {
                    rows[r][c].VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
                    Grid.SetRow(rows[r][c], r + 1); Grid.SetColumn(rows[r][c], c + 1);
                    permGrid.Children.Add(rows[r][c]);
                }
            }
            root.Children.Add(permGrid);

            var octalPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            octalPanel.Children.Add(new TextBlock { Text = "Octal:", Foreground = Brushes.White, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            octalPanel.Children.Add(octalBox);
            root.Children.Add(octalPanel);

            var btnPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 10, Margin = new Thickness(0, 15, 0, 0) };
            var applyBtn = new Button { Content = "Apply", Width = 90 };
            var cancelBtn = new Button { Content = "Cancel", Width = 90 };
            applyBtn.Click += (s, ev) =>
            {
                try { result = (uint)Convert.ToInt32(octalBox.Text?.Trim() ?? "0", 8) & 0x1FFu; }
                catch { result = null; }
                close?.Invoke();
            };
            cancelBtn.Click += (s, ev) => close?.Invoke();
            btnPanel.Children.Add(applyBtn);
            btnPanel.Children.Add(cancelBtn);
            root.Children.Add(btnPanel);

            await OverlayDialogs.ShowAsync(c => { close = c; return root; }, maxWidth: 460);
            return result;
        }

        private static string ModeToRwx(int mode)
        {
            string[] perms = { "---", "--x", "-w-", "-wx", "r--", "r-x", "rw-", "rwx" };
            return perms[(mode >> 6) & 7] + perms[(mode >> 3) & 7] + perms[mode & 7];
        }

        /// NAS connect dialog: UNC path + optional credentials + LAN discovery
        /// + optional "remember this connection" save.
        /// Returns null on cancel; empty user = guest/anonymous attempt.
        private async Task<(string path, string user, string pass, bool remember)?> ShowNasDialogAsync()
        {
            (string, string, string, bool)? result = null;
            Action? close = null;
            var panel = new StackPanel { Margin = new Thickness(5), Spacing = 6 };
            var pathBox = new TextBox { Text = "\\\\NAS\\share\\folder", Margin = new Thickness(0, 0, 0, 8) };

            // --- Discovery row ---
            var scanRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            var scanBtn = new Button { Content = "🔍 Find NAS on LAN", Padding = new Thickness(10, 4) };
            var scanStatus = new TextBlock { Text = "", Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)), FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            scanRow.Children.Add(scanBtn);
            scanRow.Children.Add(scanStatus);
            panel.Children.Add(scanRow);

            var hostList = new ListBox
            {
                Height = 80, IsVisible = false,
                Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
                Margin = new Thickness(0, 0, 0, 6)
            };
            var foundHosts = new List<NasManager.NasHost>();
            hostList.SelectionChanged += (s, ev) =>
            {
                if (hostList.SelectedIndex >= 0 && hostList.SelectedIndex < foundHosts.Count)
                    pathBox.Text = "\\\\" + foundHosts[hostList.SelectedIndex].Ip + "\\";
            };
            panel.Children.Add(hostList);

            scanBtn.Click += async (s, ev) =>
            {
                scanBtn.IsEnabled = false;
                scanStatus.Text = "Scanning LAN…";
                hostList.IsVisible = false;
                try
                {
                    foundHosts.Clear();
                    foundHosts.AddRange(await NasManager.DiscoverAsync());
                    var items = new Avalonia.Controls.ItemsControl();
                    var strings = new System.Collections.ObjectModel.ObservableCollection<string>(foundHosts.Select(h => h.Display));
                    hostList.ItemsSource = strings;
                    hostList.IsVisible = strings.Count > 0;
                    scanStatus.Text = strings.Count == 0 ? "No SMB hosts found" : $"{strings.Count} host(s) — pick one:";
                }
                catch (Exception ex) { scanStatus.Text = "Scan failed: " + ex.Message; }
                scanBtn.IsEnabled = true;
            };

            panel.Children.Add(new TextBlock { Text = "UNC path:", Foreground = Brushes.White });
            panel.Children.Add(pathBox);

            panel.Children.Add(new TextBlock { Text = "Username (leave empty for guest):", Foreground = Brushes.White });
            var userBox = new TextBox { PlaceholderText = "e.g. admin or WORKGROUP\\user", Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(userBox);

            panel.Children.Add(new TextBlock { Text = "Password:", Foreground = Brushes.White });
            var passBox = new TextBox { PasswordChar = '●', Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(passBox);

            var rememberBox = new CheckBox { Content = "💾 Remember this connection", Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 6) };
            panel.Children.Add(rememberBox);

            panel.Children.Add(new TextBlock
            {
                Text = "Saved connections reappear when you press NAS.\nPassword is stored locally on this PC only (obfuscated, not encrypted).",
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 150)),
                FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var btnPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 10 };
            var okBtn = new Button { Content = "Connect", Width = 90 };
            okBtn.Click += (s, ev) => { result = (pathBox.Text ?? "", userBox.Text ?? "", passBox.Text ?? "", rememberBox.IsChecked == true); close?.Invoke(); };
            var cancelBtn = new Button { Content = "Cancel", Width = 90 };
            cancelBtn.Click += (s, ev) => close?.Invoke();
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            panel.Children.Add(btnPanel);

            await OverlayDialogs.ShowAsync(c =>
            {
                close = c;
                var wrap = new StackPanel();
                wrap.Children.Add(new TextBlock { Text = "Add NAS / Network Share", FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 12) });
                wrap.Children.Add(panel);
                return wrap;
            }, maxWidth: 440);
            return result;
        }

        /// Picker shown when saved NAS connections exist.
        /// Returns the chosen connection, the sentinel for "new connection", or null.
        private static readonly NasManager.SavedConnection _nasNewSentinel = new() { Path = "__new__" };
        private async Task<NasManager.SavedConnection?> ShowNasPickerAsync()
        {
            NasManager.SavedConnection? result = null;
            Action? close = null;
            var panel = new StackPanel { Margin = new Thickness(5), Spacing = 8 };

            panel.Children.Add(new TextBlock { Text = "NAS Connections", FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 10) });
            panel.Children.Add(new TextBlock { Text = "Saved connections:", Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 4) });
            var list = new ListBox
            {
                Height = 170,
                Background = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
                ItemsSource = new ObservableCollection<string>(_nasSaved.Select(c => c.Display))
            };
            list.SelectedIndex = 0;
            panel.Children.Add(list);

            var btnPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Spacing = 10 };
            var connBtn = new Button { Content = "🔌 Connect", Width = 100 };
            var newBtn = new Button { Content = "➕ New…", Width = 90 };
            var delBtn = new Button { Content = "🗑️ Delete", Width = 90 };
            var cancelBtn = new Button { Content = "Cancel", Width = 90 };

            connBtn.Click += (s, ev) => { if (list.SelectedIndex >= 0 && list.SelectedIndex < _nasSaved.Count) result = _nasSaved[list.SelectedIndex]; close?.Invoke(); };
            newBtn.Click += (s, ev) => { result = _nasNewSentinel; close?.Invoke(); };
            delBtn.Click += (s, ev) =>
            {
                if (list.SelectedIndex >= 0 && list.SelectedIndex < _nasSaved.Count)
                {
                    _nasSaved.RemoveAt(list.SelectedIndex);
                    SaveNasConnections();
                    list.ItemsSource = new ObservableCollection<string>(_nasSaved.Select(c => c.Display));
                    if (_nasSaved.Count == 0) { result = _nasNewSentinel; close?.Invoke(); }
                }
            };
            cancelBtn.Click += (s, ev) => close?.Invoke();

            // Double-click a saved entry = Connect
            list.DoubleTapped += (s, ev) => { if (list.SelectedIndex >= 0 && list.SelectedIndex < _nasSaved.Count) { result = _nasSaved[list.SelectedIndex]; close?.Invoke(); } };

            btnPanel.Children.Add(connBtn);
            btnPanel.Children.Add(newBtn);
            btnPanel.Children.Add(delBtn);
            btnPanel.Children.Add(cancelBtn);
            panel.Children.Add(btnPanel);

            await OverlayDialogs.ShowAsync(c => { close = c; return panel; }, maxWidth: 460);
            return result;
        }

        // ============================================================
        // NAVIGATION
        // ============================================================
        private void NavButton_Click(object? sender, RoutedEventArgs e)
        {
            CloseMobileDrawer();

            // Hide all pages
            PageFiles.IsVisible = false;
            PageGames.IsVisible = false;
            PageMedia.IsVisible = false;
            PageSystem.IsVisible = false;
            PageTools.IsVisible = false;
            PageDebug.IsVisible = false;

            // Show selected page
            if (sender == NavFiles) PageFiles.IsVisible = true;
            else if (sender == NavGames) PageGames.IsVisible = true;
            else if (sender == NavMedia) PageMedia.IsVisible = true;
            else if (sender == NavSystem)
            {
                PageSystem.IsVisible = true;
                StartHwAutoRefresh();
                _ = RefreshHardwareAsync();
            }
            else if (sender == NavTools) PageTools.IsVisible = true;
            else if (sender == NavDebug) PageDebug.IsVisible = true;

            if (sender != NavSystem) StopHwAutoRefresh();
        }

        private void StartHwAutoRefresh()
        {
            _hwAutoTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _hwAutoTimer.Tick -= HwAutoTimer_Tick;
            _hwAutoTimer.Tick += HwAutoTimer_Tick;
            if (!_hwAutoTimer.IsEnabled) _hwAutoTimer.Start();
        }

        private void StopHwAutoRefresh() { _hwAutoTimer?.Stop(); _hwStaticLoaded = false; }

        // ============================================================
        // LOGGING
        // ============================================================
        private void Log(string message)
        {
            if (_uploadCancellation != null && _uploadCancellation.Token.IsCancellationRequested)
            {
                if (!message.Contains("Upload cancelled") && !message.Contains("Cleanup complete") &&
                    !message.Contains("UPLOAD FINISHED") && !message.Contains("Checking main connection"))
                    return;
            }

            bool isImportant = message.Contains("❌") || message.Contains("⚠️") ||
                               message.Contains("Exception") || message.Contains("Error") ||
                               (message.Contains("File") && message.Contains("completed")) ||
                               message.Contains("Starting parallel") || message.Contains("🚀") ||
                               message.Contains("Upload complete") || message.Contains("finished");

            if (message.Contains("File") && message.Contains("/"))
            {
                _logCounter++;
                if (_logCounter % 100 == 0) isImportant = true;
            }

            if (isImportant) App.LogToFile(message);

            if (message.Contains("📊") || message.Contains("⬆️ Uploading:") ||
                message.Contains("⏳ Waiting") || message.Contains("✅ Task completed") ||
                message.Contains("✅ Task awaited") || message.Contains("🔍 Task index") ||
                message.Contains("🧹 Cleaning up") || message.Contains("📤 Starting upload") ||
                (message.Contains("✅ Connection") && message.Contains("established")))
            {
                _logCounter++;
                if (_logCounter % 50 != 0) return;
            }

            // Buffer and flush to the TextBox at most ~4x/sec — during a
            // 100k-file upload, one Dispatcher.Post + full-text Split('\n')
            // per log line saturated the UI thread (frozen tabs, wait cursor).
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            lock (_logBufLock)
            {
                _logBuf.Append('[').Append(timestamp).Append("] ").Append(message).Append('\n');
            }
            long nowTicks = DateTime.Now.Ticks;
            if (nowTicks - Interlocked.Read(ref _lastLogFlushTicks) >= TimeSpan.TicksPerSecond / 4)
                FlushLog();
        }

        // Drain the buffered log lines into the TextBox (single UI post per
        // flush, single text rewrite). Safe to call from any thread.
        private void FlushLog()
        {
            string chunk;
            lock (_logBufLock)
            {
                if (_logBuf.Length == 0) return;
                chunk = _logBuf.ToString();
                _logBuf.Clear();
            }
            Interlocked.Exchange(ref _lastLogFlushTicks, DateTime.Now.Ticks);
            Dispatcher.UIThread.Post(() =>
            {
                int caret = LogTextBox.CaretIndex;
                string t = (LogTextBox.Text ?? "") + chunk;
                int trimmed = 0;
                if (t.Length > MaxLogChars)
                {
                    trimmed = t.Length - MaxLogChars;
                    t = t.Substring(trimmed);
                }
                LogTextBox.Text = t;
                // Auto-scroll: jump to the end; otherwise keep the user's
                // scroll position (caret tracks what they're reading).
                LogTextBox.CaretIndex = (LogAutoScroll.IsChecked == true)
                    ? t.Length
                    : Math.Clamp(caret - trimmed, 0, t.Length);
            });
        }

        private static string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1) { order++; len /= 1024; }
            return $"{len:0.##} {sizes[order]}";
        }
    }

    // ============================================================
    // DATA CLASSES
    // ============================================================
    public class LocalFileItem
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string Icon { get; set; } = "";
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public string? RemotePathOverride { get; set; }
        public string SizeFormatted => IsDirectory ? "" : FileUtils.FormatFileSize(Size);
    }

    public class PS5FileItem
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string Icon { get; set; } = "";
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTime Timestamp { get; set; }
        public int Permissions { get; set; } = -1;
        public string SizeFormatted => IsDirectory ? "Folder" : FileUtils.FormatFileSize(Size);
        public string PermissionsText => Permissions < 0 ? "" : Convert.ToString(Permissions, 8).PadLeft(4, '0');
    }

    public static class FileUtils
    {
        public static string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1) { order++; len /= 1024; }
            return $"{len:0.##} {sizes[order]}";
        }
    }

    public class TransferHistoryItem
    {
        public string FileName { get; set; } = "";
        public string Status { get; set; } = "";
        public string Size { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public string FormattedTimestamp => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
        public string LocalPath { get; set; } = "";
        public string RemotePath { get; set; } = "";
        public override string ToString() => $"{FileName} - {Status} ({Size}) [{FormattedTimestamp}]";
    }
}
