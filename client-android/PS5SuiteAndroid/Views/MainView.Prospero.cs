using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PS5Upload;

namespace PS5SuiteAndroid.Views;

// Prospero Store — homebrew.page/api/v1 catalog (same as desktop).
// zip artifacts → download → SHA-256 verify → extract → upload tree to
// /data/homebrew/<TITLEID>/ → MountGame registers it on the home screen.
public partial class MainView
{
    public class ProsperoItem : System.ComponentModel.INotifyPropertyChanged
    {
        public string TitleId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Author { get; set; } = "";
        public string Version { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Format { get; set; } = "";
        public long Size { get; set; }
        public string Status { get; set; } = "";
        public string IconSmall { get; set; } = "";
        public string Updated { get; set; } = "";

        [JsonIgnore]
        private Bitmap? _icon;
        [JsonIgnore]
        public Bitmap? IconImage
        {
            get => _icon;
            set { _icon = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IconImage))); }
        }

        [JsonIgnore]
        public bool CanInstall => Status == "available" && Format == "zip";

        [JsonIgnore]
        public string ButtonLabel => CanInstall ? "📥" : "—";

        [JsonIgnore]
        public string MetaLine
        {
            get
            {
                string kind = Kind switch { "game" => "🎮", "tool" => "🔧", "app" => "🧩", _ => "📦" };
                string size = Size > 0 ? $" · {Size / 1048576.0:0.0} MB" : "";
                string st = Status == "coming_soon" ? " · coming soon"
                          : Format == "ffpfsc" ? " · ffpfsc (unsupported)" : "";
                return $"{kind} {Version}{size} · {Author}{st}";
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    private const string ProsperoApi = "https://homebrew.page/api/v1";
    private readonly List<ProsperoItem> _prosperoItems = new();
    private bool _prosperoFetching;
    private bool _prosperoInstalling;
    private static readonly HttpClient _prosperoHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly HttpClient _prosperoDl = new() { Timeout = TimeSpan.FromMinutes(15) };

    private string ProsperoCacheFile => Path.Combine(AppPaths.DownloadsDir, "PS5Suite", "prospero_cache.json");
    private string ProsperoIconDir => Path.Combine(AppPaths.CacheDir, "prospero_icons");
    private string ProsperoDlDir => Path.Combine(AppPaths.CacheDir, "prospero_dl");

    private static List<ProsperoItem> ParseProsperoIndex(string json)
    {
        var list = new List<ProsperoItem>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("apps", out var apps)) return list;
        foreach (var a in apps.EnumerateArray())
        {
            static string S(JsonElement e, string k) =>
                e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? "" : "";
            list.Add(new ProsperoItem
            {
                TitleId = S(a, "titleid"),
                Name = S(a, "name") is { Length: > 0 } n ? n : S(a, "titleid"),
                Author = S(a, "author"),
                Version = S(a, "version"),
                Kind = S(a, "kind"),
                Format = S(a, "format"),
                Status = S(a, "status"),
                IconSmall = S(a, "icon_small"),
                Updated = S(a, "updated"),
                Size = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0,
            });
        }
        return list;
    }

    private async void ProsperoRefresh_Click(object? sender, RoutedEventArgs e) => await RefreshProsperoAsync();
    private void ProsperoSearch_TextChanged(object? sender, TextChangedEventArgs e) => ApplyProsperoFilter();
    private void ProsperoCategory_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyProsperoFilter();

    private async Task RefreshProsperoAsync()
    {
        if (_prosperoFetching) return;
        _prosperoFetching = true;
        try
        {
            // show cache first if present
            try
            {
                if (File.Exists(ProsperoCacheFile))
                {
                    var items = JsonSerializer.Deserialize<List<ProsperoItem>>(File.ReadAllText(ProsperoCacheFile));
                    if (items is { Count: > 0 })
                    {
                        _prosperoItems.Clear();
                        _prosperoItems.AddRange(items);
                        ApplyProsperoFilter();
                        ProsperoStatusText.Text = $"{items.Count} apps (cached) — refreshing…";
                    }
                }
            }
            catch { }

            UpdateStatus("Fetching Prospero catalog...");
            var fresh = ParseProsperoIndex(await _prosperoHttp.GetStringAsync($"{ProsperoApi}/index.json"));
            if (fresh.Count == 0) throw new Exception("catalog returned no apps");

            _prosperoItems.Clear();
            _prosperoItems.AddRange(fresh);
            ApplyProsperoFilter();
            int avail = fresh.Count(i => i.CanInstall);
            ProsperoStatusText.Text = $"{fresh.Count} apps ({avail} installable)";
            UpdateStatus($"Prospero catalog: {fresh.Count} apps ({avail} installable)");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ProsperoCacheFile)!);
                File.WriteAllText(ProsperoCacheFile, JsonSerializer.Serialize(fresh));
            }
            catch { }
        }
        catch (Exception ex)
        {
            ProsperoStatusText.Text = _prosperoItems.Count > 0
                ? $"{_prosperoItems.Count} apps (cached — catalog unreachable)"
                : "Fetch failed";
            UpdateStatus($"Prospero catalog error: {ex.Message}");
        }
        finally
        {
            _prosperoFetching = false;
            if (_prosperoItems.Count > 0) _ = Task.Run(FetchProsperoIconsAsync);
        }
    }

    private void ApplyProsperoFilter()
    {
        if (ProsperoListBox == null || ProsperoSearchBox == null || ProsperoCategoryCombo == null) return;

        IEnumerable<ProsperoItem> view = ProsperoCategoryCombo.SelectedIndex switch
        {
            1 => _prosperoItems.Where(i => i.Kind == "app"),
            2 => _prosperoItems.Where(i => i.Kind == "game"),
            3 => _prosperoItems.Where(i => i.Kind == "tool"),
            4 => _prosperoItems.Where(i => i.Status == "coming_soon"),
            _ => _prosperoItems,
        };

        string q = ProsperoSearchBox.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(q))
            view = view.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                || i.TitleId.Contains(q, StringComparison.OrdinalIgnoreCase)
                                || i.Author.Contains(q, StringComparison.OrdinalIgnoreCase));

        var list = view.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        ProsperoListBox.ItemsSource = null;
        ProsperoListBox.ItemsSource = list;
        if (_prosperoItems.Count > 0)
            ProsperoStatusText.Text = string.IsNullOrEmpty(q) && ProsperoCategoryCombo.SelectedIndex == 0
                ? $"{_prosperoItems.Count} apps ({_prosperoItems.Count(i => i.CanInstall)} installable)"
                : $"{list.Count}/{_prosperoItems.Count} apps";
    }

    private async Task FetchProsperoIconsAsync()
    {
        try { Directory.CreateDirectory(ProsperoIconDir); } catch { }
        var items = _prosperoItems.ToList();
        await Task.WhenAll(items.Select(async it =>
        {
            if (it.IconImage != null) return;
            string cached = Path.Combine(ProsperoIconDir, it.TitleId + ".png");
            try
            {
                if (File.Exists(cached))
                {
                    var bmp = new Bitmap(cached);
                    await Dispatcher.UIThread.InvokeAsync(() => { it.IconImage = bmp; });
                    return;
                }
            }
            catch { }
            string url = !string.IsNullOrEmpty(it.IconSmall)
                ? it.IconSmall
                : $"{ProsperoApi}/icons/{it.TitleId}-256.png";
            for (int a = 0; a < 3 && it.IconImage == null; a++)
            {
                try
                {
                    var bytes = await _prosperoHttp.GetByteArrayAsync(url);
                    if (bytes.Length < 100 || bytes[0] != 0x89 || bytes[1] != 0x50) break;
                    using var ms = new MemoryStream(bytes);
                    var bmp = new Bitmap(ms);
                    await Dispatcher.UIThread.InvokeAsync(() => { it.IconImage = bmp; });
                    try { File.WriteAllBytes(cached, bytes); } catch { }
                }
                catch { await Task.Delay(500 * (a + 1)); }
            }
        }));
    }

    // ---------- install ----------

    private record ProsperoDetail(string Url, string Sha256, string Format, string Status, long Size);

    private static ProsperoDetail ParseProsperoDetail(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        static string S(JsonElement e, string k) =>
            e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
        return new ProsperoDetail(
            S(r, "artifact_url"), S(r, "sha256"), S(r, "format"), S(r, "status"),
            r.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0);
    }

    private void ProsperoUi(string text)
        => Dispatcher.UIThread.Post(() =>
        {
            ProsperoStatusText.Text = text;
            UpdateStatus(text);
        });

    private async Task<bool> DownloadProsperoZipAsync(string url, string name, long expected, string destPath)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                using var resp = await _prosperoDl.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? expected;
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
                        int bucket = (int)(done * 10 / total);
                        if (bucket != lastBucket)
                        {
                            lastBucket = bucket;
                            int pct = (int)(done * 100 / total);
                            ProsperoUi($"Downloading {name} — {done / 1048576}/{total / 1048576} MB");
                            Dispatcher.UIThread.Post(() => { ProsperoProgress.Value = pct; });
                        }
                    }
                }
                if (total > 0 && done < total) throw new IOException($"short read {done}/{total}");

                // sanity: must be a zip
                await using (var chk = new FileStream(destPath, FileMode.Open, FileAccess.Read))
                {
                    var magic = new byte[4];
                    if (await chk.ReadAsync(magic) < 4 || magic[0] != 'P' || magic[1] != 'K')
                        throw new IOException("downloaded file is not a ZIP");
                }
                return true;
            }
            catch (Exception ex)
            {
                try { File.Delete(destPath); } catch { }
                ProsperoUi($"⚠️ Download failed ({ex.Message}){(attempt < 2 ? $" — retry {attempt + 2}/3" : "")}");
                if (attempt < 2) await Task.Delay(1200);
            }
        }
        return false;
    }

    private static string? FindAppFolder(string extRoot, string titleId)
    {
        var cand = Path.Combine(extRoot, titleId);
        if (Directory.Exists(cand) && File.Exists(Path.Combine(cand, "eboot.bin")))
            return cand;
        foreach (var dir in Directory.EnumerateDirectories(extRoot))
            if (File.Exists(Path.Combine(dir, "eboot.bin")))
                return dir;
        return null;
    }

    private async void ProsperoInstall_Click(object? sender, RoutedEventArgs e)
    {
        if (_prosperoInstalling) return;
        if (_protocol?.IsConnected != true)
        { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
        string? tid = (sender as Control)?.Tag as string;
        if (string.IsNullOrEmpty(tid)) return;
        var item = _prosperoItems.FirstOrDefault(i => i.TitleId == tid);
        if (item == null || !item.CanInstall) return;
        string name = item.Name;
        if (!await ShowConfirmAsync(
            $"Install \"{name}\" ({tid}) on the PS5?\n\n" +
            $"The app folder goes to /data/homebrew/{tid} and is mounted to the home screen.",
            "Install")) return;

        _prosperoInstalling = true;
        string? zipPath = null, extDir = null;
        try
        {
            ProsperoProgress.IsVisible = true;
            ProsperoProgress.Value = 0;

            // 1. detail record
            ProsperoUi($"Resolving {name}...");
            var det = ParseProsperoDetail(await _prosperoHttp.GetStringAsync($"{ProsperoApi}/apps/{tid}.json"));
            if (det.Format != "zip" || det.Status != "available" || det.Url.Length == 0)
            { ProsperoUi($"❌ {name}: not installable ({det.Format}/{det.Status})"); return; }

            // 2. download
            Directory.CreateDirectory(ProsperoDlDir);
            zipPath = Path.Combine(ProsperoDlDir, tid + ".zip");
            ProsperoUi($"📥 {name}: downloading {(det.Size > 0 ? det.Size : item.Size) / 1048576.0:0.0} MB...");
            if (!await DownloadProsperoZipAsync(det.Url, name, det.Size, zipPath))
            { ProsperoUi("download failed"); return; }

            // 3. SHA-256 verify (thread pool)
            if (det.Sha256.Length == 64)
            {
                ProsperoUi($"Verifying {name}...");
                string hex = await Task.Run(async () =>
                    Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(zipPath))));
                if (!hex.Equals(det.Sha256, StringComparison.OrdinalIgnoreCase))
                { ProsperoUi($"❌ {name}: SHA-256 mismatch — rejected"); return; }
            }

            // 4. extract (thread pool)
            string? appDir = null;
            await Task.Run(() =>
            {
                extDir = Path.Combine(ProsperoDlDir, tid + "_ext");
                if (Directory.Exists(extDir)) Directory.Delete(extDir, true);
                ZipFile.ExtractToDirectory(zipPath, extDir);
                appDir = FindAppFolder(extDir, tid);
            });
            if (appDir == null)
            { ProsperoUi($"❌ {name}: no eboot.bin inside the zip"); return; }
            string folderName = Path.GetFileName(appDir);

            // 5. upload — parallel connections like the desktop lanes
            var files = Directory.GetFiles(appDir, "*", SearchOption.AllDirectories);
            string remoteBase = $"/data/homebrew/{folderName}";
            ProsperoUi($"📤 {name}: uploading {files.Length} files...");
            ProsperoProgress.Value = 0;

            var rels = files.Select(f => $"{remoteBase}/{Path.GetRelativePath(appDir, f).Replace('\\', '/')}").ToArray();
            Exception? uploadError = null;
            int doneFiles = 0;
            await Task.Run(async () =>
            {
                await _protocol.CreateDirAsync(remoteBase);
                foreach (var dir in Directory.GetDirectories(appDir, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(appDir, dir).Replace('\\', '/');
                    await _protocol.CreateDirAsync($"{remoteBase}/{rel}");
                }

                int lanes = Math.Min(8, files.Length);
                int nextFile = -1;
                long lastUi = 0;
                var laneTasks = Enumerable.Range(0, lanes).Select(async _ =>
                {
                    PS5Protocol? lane = null;
                    try
                    {
                        if (_ps5IpAddress == null) throw new Exception("no PS5 IP");
                        lane = new PS5Protocol();
                        if (!await lane.ConnectAsync(_ps5IpAddress)) throw new Exception("lane connect failed");
                        while (Volatile.Read(ref uploadError) == null)
                        {
                            int i = Interlocked.Increment(ref nextFile);
                            if (i >= files.Length) break;
                            if (!await lane.UploadFileAsync(files[i], rels[i]))
                            {
                                Interlocked.CompareExchange(ref uploadError,
                                    new Exception($"upload failed: {rels[i]} — {lane.LastError}"), null);
                                break;
                            }
                            int d = Interlocked.Increment(ref doneFiles);
                            long now = Environment.TickCount64;
                            if (d == files.Length || now - lastUi > 150)
                            {
                                Interlocked.Exchange(ref lastUi, now);
                                ProsperoUi($"Uploading {name} — {d}/{files.Length} files");
                                int pct = d * 100 / files.Length;
                                Dispatcher.UIThread.Post(() => { ProsperoProgress.Value = pct; });
                            }
                        }
                    }
                    finally { lane?.Dispose(); }
                }).ToList();
                await Task.WhenAll(laneTasks);
            });
            if (uploadError != null)
            { ProsperoUi($"❌ {name}: {uploadError.Message}"); return; }

            // clean temp — nothing stored twice
            try { File.Delete(zipPath); zipPath = null; } catch { }
            try { if (extDir != null) { Directory.Delete(extDir, true); extDir = null; } } catch { }

            // 6. mount
            ProsperoUi($"🗂️ {name}: mounting to home screen...");
            var mountRes = await _protocol.MountGameAsync(folderName, msg =>
                ProsperoUi($"Mounting {name} — {msg}"));
            ProsperoUi(mountRes == null
                ? $"{name} installed (use Mount All in Games to show it)"
                : $"✅ {name} installed — on the home screen");
        }
        catch (Exception ex)
        {
            ProsperoUi($"❌ Install error: {ex.Message}");
        }
        finally
        {
            _prosperoInstalling = false;
            Dispatcher.UIThread.Post(() => { ProsperoProgress.IsVisible = false; ProsperoProgress.Value = 0; });
            try { if (zipPath != null) File.Delete(zipPath); } catch { }
            try { if (extDir != null && Directory.Exists(extDir)) Directory.Delete(extDir, true); } catch { }
        }
    }
}
