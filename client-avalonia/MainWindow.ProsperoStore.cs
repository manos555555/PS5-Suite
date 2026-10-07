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
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace PS5Upload
{
    // Prospero Store Site tab — homebrew.page/api/v1 catalog.
    // Signed static JSON API (Cloudflare CDN, fast + reliable):
    //   index.json          → app list (titleid/name/version/format/size/…)
    //   apps/<TID>.json     → per-app record (artifact_url, sha256, size)
    //   icons/<TID>-256.png → store icon
    //
    // Artifacts are NOT PKGs — they are ZIP archives holding the app folder
    // (<TITLEID>/ with eboot.bin + sce_sys/), hosted on the dev's GitHub
    // Releases. Install path per catalog spec:
    //   download → SHA-256 verify → extract → upload tree to /data/homebrew/
    //   → our own Mount pipeline registers it on the PS5 home screen
    //   (/data/homebrew is in the payload's GAME_SCAN_PATHS).
    public partial class MainWindow
    {
        public class ProsperoItem : System.ComponentModel.INotifyPropertyChanged
        {
            public string TitleId { get; set; } = "";
            public string Name { get; set; } = "";
            public string Author { get; set; } = "";
            public string Version { get; set; } = "";
            public string Kind { get; set; } = "";        // "app" | "game" | "tool"
            public string Format { get; set; } = "";      // "zip" | "ffpfsc" | ""
            public long Size { get; set; }
            public string Status { get; set; } = "";      // "available" | "coming_soon"
            public string IconSmall { get; set; } = "";
            public string Updated { get; set; } = "";     // ISO date

            [JsonIgnore]
            private Bitmap? _icon;
            [JsonIgnore]
            public Bitmap? Icon
            {
                get => _icon;
                set { _icon = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon))); }
            }

            [JsonIgnore]
            public string SizeDisplay => Size > 0 ? PS5StorageInfo.FormatBytes((ulong)Size) : "—";

            [JsonIgnore]
            public string KindDisplay => Kind switch
            {
                "game" => "🎮 game",
                "tool" => "🔧 tool",
                "app" => "🧩 app",
                _ => Kind,
            };

            [JsonIgnore]
            public bool CanInstall => Status == "available" && Format == "zip";

            [JsonIgnore]
            public string InstallLabel => CanInstall ? "📥 Install"
                : Status == "coming_soon" ? "Coming soon"
                : Format == "ffpfsc" ? "ffpfsc — unsupported" : "Unavailable";

            [JsonIgnore]
            public string StatusDisplay => Status == "coming_soon" ? "🕒 coming soon"
                : Format == "ffpfsc" ? "⚠️ ffpfsc image — install via Prospero Store on console"
                : Format == "zip" ? "zip — installs to /data/homebrew" : "";

            [JsonIgnore]
            public IBrush StatusColor => Status == "coming_soon"
                ? new SolidColorBrush(Color.Parse("#888888"))
                : Format == "ffpfsc"
                    ? new SolidColorBrush(Color.Parse("#FFC107"))
                    : new SolidColorBrush(Color.Parse("#6A9955"));

            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        }

        private const string ProsperoApi = "https://homebrew.page/api/v1";
        private const string ProsperoCacheFile = "prospero_cache.json";
        private const string ProsperoIconDir = "prospero_icons";
        private const string ProsperoDlDir = "prospero_dl";

        private readonly List<ProsperoItem> _prosperoItems = new();
        private bool _prosperoFetching;
        private bool _prosperoInstalling;
        private bool _prosperoLoadedOnce;

        // ---------- catalog fetch / cache ----------

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

        private int LoadProsperoCache()
        {
            try
            {
                if (!File.Exists(ProsperoCacheFile)) return 0;
                var items = JsonSerializer.Deserialize<List<ProsperoItem>>(File.ReadAllText(ProsperoCacheFile));
                if (items == null || items.Count == 0) return 0;
                _prosperoItems.Clear();
                _prosperoItems.AddRange(items);
                ApplyProsperoFilter();
                ProsperoStatusText.Text = $"{items.Count} apps (cached)";
                foreach (var it in items) TryLoadProsperoIcon(it);
                return items.Count;
            }
            catch { return 0; }
        }

        private void TryLoadProsperoIcon(ProsperoItem it)
        {
            try
            {
                string p = Path.Combine(ProsperoIconDir, it.TitleId + ".png");
                if (File.Exists(p)) it.Icon = new Bitmap(p);
            }
            catch { }
        }

        private void FetchProsperoIconsAsync()
        {
            var items = _prosperoItems.ToList();
            _ = Task.Run(async () =>
            {
                try { Directory.CreateDirectory(ProsperoIconDir); } catch { }
                await Task.WhenAll(items.Select(async it =>
                {
                    if (it.Icon != null) return;
                    TryLoadProsperoIcon(it);
                    if (it.Icon != null) return;
                    string url = !string.IsNullOrEmpty(it.IconSmall)
                        ? it.IconSmall
                        : $"{ProsperoApi}/icons/{it.TitleId}-256.png";
                    for (int a = 0; a < 4 && it.Icon == null; a++)
                    {
                        try
                        {
                            var bytes = await _storeHttp.GetByteArrayAsync(url);
                            if (bytes.Length < 100 || bytes[0] != 0x89 || bytes[1] != 0x50)
                                throw new Exception("not a PNG");
                            using var ms = new MemoryStream(bytes);
                            var bmp = new Bitmap(ms);
                            await Dispatcher.UIThread.InvokeAsync(() => { it.Icon = bmp; });
                            try { File.WriteAllBytes(Path.Combine(ProsperoIconDir, it.TitleId + ".png"), bytes); } catch { }
                        }
                        catch { await Task.Delay(600 * (a + 1)); }
                    }
                }));
            });
        }

        private async void ProsperoRefresh_Click(object? sender, RoutedEventArgs? e) => await RefreshProsperoAsync();

        private async Task RefreshProsperoAsync()
        {
            if (_prosperoFetching) return;
            _prosperoFetching = true;
            try
            {
                int cached = LoadProsperoCache();
                ProsperoStatusText.Text = cached > 0
                    ? $"{cached} apps (cached) — refreshing…"
                    : "Fetching homebrew.page catalog...";
                Log("🌐 Fetching Prospero catalog...");

                var items = ParseProsperoIndex(await _storeHttp.GetStringAsync($"{ProsperoApi}/index.json"));
                if (items.Count == 0) throw new Exception("catalog returned no apps");

                _prosperoItems.Clear();
                _prosperoItems.AddRange(items);
                ApplyProsperoFilter();
                foreach (var it in items) TryLoadProsperoIcon(it);
                int avail = items.Count(i => i.CanInstall);
                ProsperoStatusText.Text = $"{items.Count} apps ({avail} installable)";
                Log($"🌐 Prospero catalog: {items.Count} apps ({avail} installable)");
                try { File.WriteAllText(ProsperoCacheFile, JsonSerializer.Serialize(items)); } catch { }
            }
            catch (Exception ex)
            {
                ProsperoStatusText.Text = _prosperoItems.Count > 0
                    ? $"{_prosperoItems.Count} apps (cached — homebrew.page unreachable)"
                    : "Fetch failed";
                Log($"⚠️ Prospero catalog: {ex.Message}");
            }
            finally
            {
                _prosperoFetching = false;
                if (_prosperoItems.Count > 0) FetchProsperoIconsAsync();
            }
        }

        // ---------- search / sort ----------

        // Site sections: apps / games / tools / coming soon — kind field +
        // status, same split as homebrew.page. Search applies inside the
        // selected category.
        private void ApplyProsperoFilter()
        {
            if (ProsperoListBox == null || ProsperoSearchBox == null || ProsperoSortComboBox == null
                || ProsperoCategoryComboBox == null) return;

            IEnumerable<ProsperoItem> view = ProsperoCategoryComboBox.SelectedIndex switch
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

            view = ProsperoSortComboBox.SelectedIndex switch
            {
                1 => view.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase),
                2 => view.OrderBy(i => i.Size),
                3 => view.OrderByDescending(i => i.Size),
                4 => view.OrderByDescending(i => i.Updated, StringComparer.Ordinal),
                5 => view.OrderBy(i => i.Author, StringComparer.OrdinalIgnoreCase),
                _ => view.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            };

            var list = view.ToList();
            ProsperoListBox.ItemsSource = null;
            ProsperoListBox.ItemsSource = list;
            if (!string.IsNullOrEmpty(q))
                ProsperoStatusText.Text = $"{list.Count}/{_prosperoItems.Count} apps";
            else if (_prosperoItems.Count > 0)
                ProsperoStatusText.Text = $"{_prosperoItems.Count} apps ({_prosperoItems.Count(i => i.CanInstall)} installable)";
        }

        private void ProsperoSearch_TextChanged(object? sender, TextChangedEventArgs e) => ApplyProsperoFilter();
        private void ProsperoSort_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyProsperoFilter();
        private void ProsperoCategory_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyProsperoFilter();

        // ---------- install: zip → verify → /data/homebrew → mount ----------

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

        // GitHub release URLs redirect to objects.githubusercontent.com —
        // HttpClient follows redirects automatically.
        private async Task<bool> DownloadProsperoZipAsync(string url, string name, long expected, string destPath)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                    using var resp = await _storeDl.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? expected;
                    await using var src = await resp.Content.ReadAsStreamAsync(cts.Token);
                    // Fresh download targets get scanned by AV/indexers and may
                    // stay briefly locked — retry the open instead of failing
                    // the whole attempt on "file in use".
                    FileStream? dst = null;
                    for (int o = 0; o < 8 && dst == null; o++)
                    {
                        try { dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20); }
                        catch (IOException) { if (o == 7) throw; await Task.Delay(500); }
                    }
                    await using (var outFs = dst!) {
                    var buf = new byte[512 * 1024];
                    long done = 0; int rd; int lastBucket = -1;
                    while ((rd = await src.ReadAsync(buf, cts.Token)) > 0)
                    {
                        await outFs.WriteAsync(buf.AsMemory(0, rd), cts.Token);
                        done += rd;
                        if (total > 0)
                        {
                            int bucket = (int)(done * 10 / total);
                            if (bucket != lastBucket)
                            {
                                lastBucket = bucket;
                                ProsperoStatusText.Text = $"Downloading {name} — {done / 1048576}/{total / 1048576} MB";
                            }
                        }
                    }
                    if (total > 0 && done < total) throw new IOException($"short read {done}/{total}");
                    }

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
                    Log($"⚠️ Download failed ({ex.Message}){(attempt < 2 ? $" — retry {attempt + 2}/3" : "")}");
                    if (attempt < 2) await Task.Delay(1200);
                }
            }
            return false;
        }

        // Catalog spec: zip root holds the app folder <TITLEID>/ containing
        // eboot.bin + sce_sys/. Locate it defensively — name it what the
        // archive says, fall back to the folder holding eboot.bin.
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
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }
            string? tid = (sender as Control)?.Tag as string;
            if (string.IsNullOrEmpty(tid)) return;
            var item = _prosperoItems.FirstOrDefault(i => i.TitleId == tid);
            if (item == null || !item.CanInstall) return;
            string name = item.Name;
            if (!await ShowConfirmAsync(
                $"Install \"{name}\" ({tid}) on the PS5?\n\n" +
                $"The app folder will be placed in /data/homebrew/{tid} and mounted to the home screen.")) return;

            _prosperoInstalling = true;
            string? zipPath = null, extDir = null;
            try
            {
                // 1. per-app record: artifact_url + sha256 + format
                ProsperoStatusText.Text = $"Resolving {name}...";
                var det = ParseProsperoDetail(await _storeHttp.GetStringAsync($"{ProsperoApi}/apps/{tid}.json"));
                if (det.Format != "zip" || det.Status != "available" || det.Url.Length == 0)
                { Log($"❌ {name}: not installable (format={det.Format}, status={det.Status})"); return; }

                // 2. download zip (redirects followed by HttpClient)
                string dlDir = Path.Combine(AppContext.BaseDirectory, ProsperoDlDir);
                Directory.CreateDirectory(dlDir);
                zipPath = Path.Combine(dlDir, tid + ".zip");
                Log($"📥 {name}: downloading {PS5StorageInfo.FormatBytes((ulong)(det.Size > 0 ? det.Size : item.Size))}...");
                if (!await DownloadProsperoZipAsync(det.Url, name, det.Size, zipPath))
                { ProsperoStatusText.Text = "download failed"; return; }

                // 3. SHA-256 verify — mandatory per catalog spec. Runs on the
                // thread pool: File.OpenRead is a sync stream and hashing a
                // few hundred MB on the UI thread stalls repaints.
                if (det.Sha256.Length == 64)
                {
                    ProsperoStatusText.Text = $"Verifying {name}...";
                    string hex = await Task.Run(async () =>
                        Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(zipPath))));
                    if (!hex.Equals(det.Sha256, StringComparison.OrdinalIgnoreCase))
                    { Log($"❌ {name}: SHA-256 mismatch — archive rejected (got {hex.Substring(0, 16)}…, catalog says {det.Sha256.Substring(0, 16)}…)"); return; }
                }
                else Log($"⚠️ {name}: catalog has no sha256 — installing unverified");

                // 4. extract → locate <TITLEID>/ with eboot.bin. Extraction of
                // 10k+ entry zips is sync CPU+disk — off the UI thread.
                string? appDir = null; string extLocal = "";
                await Task.Run(() =>
                {
                    extLocal = Path.Combine(dlDir, tid + "_ext");
                    extDir = extLocal;
                    if (Directory.Exists(extLocal)) Directory.Delete(extLocal, true);
                    ZipFile.ExtractToDirectory(zipPath, extLocal);
                    appDir = FindAppFolder(extLocal, tid);
                });
                if (appDir == null)
                { Log($"❌ {name}: no app folder with eboot.bin inside the zip"); return; }
                string folderName = Path.GetFileName(appDir);

                // 5. upload tree → /data/homebrew/<FOLDER>/ — same pooled-lane
                // model the File Transfer tab uses: N parallel connections each
                // pulling files off a shared index. The whole fan-out runs on
                // the thread pool so the 13k+ await continuations don't flood
                // the UI dispatcher (that was the tab-switch freeze).
                var files = Directory.GetFiles(appDir, "*", SearchOption.AllDirectories);
                string remoteBase = $"/data/homebrew/{folderName}";
                Log($"📤 {name}: uploading {files.Length} files → {remoteBase}");
                ProsperoStatusText.Text = $"Uploading {name} — 0/{files.Length} files";

                var rels = files.Select(f => $"{remoteBase}/{Path.GetRelativePath(appDir, f).Replace('\\', '/')}").ToArray();
                Exception? uploadError = null;
                await Task.Run(async () =>
                {
                    await _protocol.CreateDirAsync(remoteBase);
                    foreach (var dir in Directory.GetDirectories(appDir, "*", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetRelativePath(appDir, dir).Replace('\\', '/');
                        await _protocol.CreateDirAsync($"{remoteBase}/{rel}");
                    }

                    int lanes = Math.Min(16, files.Length);
                    int nextFile = -1, doneFiles = 0;
                    long lastUiPost = 0;
                    var laneTasks = Enumerable.Range(0, lanes).Select(async _ =>
                    {
                        PS5Protocol? lane = null;
                        try
                        {
                            lane = await AcquireUploadConnectionAsync();
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
                                if (d == files.Length || now - lastUiPost > 150)
                                {
                                    Interlocked.Exchange(ref lastUiPost, now);
                                    Dispatcher.UIThread.Post(() =>
                                        ProsperoStatusText.Text = $"Uploading {name} — {d}/{files.Length} files");
                                }
                            }
                        }
                        finally
                        {
                            if (lane != null)
                            {
                                if (lane.IsConnected) ReleaseUploadConnection(lane);
                                else DestroyConnection(lane);
                            }
                        }
                    }).ToList();
                    await Task.WhenAll(laneTasks);
                });
                if (uploadError != null)
                { Log($"❌ {name}: {uploadError.Message}"); ProsperoStatusText.Text = "upload failed"; return; }
                Log($"📤 {name}: {files.Length} files uploaded");

                // zip + temp no longer needed — delete so nothing is stored twice
                try { File.Delete(zipPath); zipPath = null; } catch { }
                try { if (extDir != null) { Directory.Delete(extDir, true); extDir = null; } } catch { }

                // 6. mount via our own Mount pipeline → home screen entry
                ProsperoStatusText.Text = $"Mounting {name}...";
                Log($"🗂️ {name}: mounting {folderName} to home screen...");
                var mountRes = await _protocol.MountGameAsync(folderName, msg =>
                    Dispatcher.UIThread.Post(() => ProsperoStatusText.Text = $"Mounting {name} — {msg}"));
                if (mountRes == null)
                {
                    Log($"⚠️ {name}: uploaded to {remoteBase} but mount gave no response — try Mount Games in the Games tab");
                    ProsperoStatusText.Text = $"{name} installed (mount manually)";
                }
                else
                {
                    Log($"✅ {name}: installed to {remoteBase} — {mountRes.Split('\n')[0]}");
                    ProsperoStatusText.Text = $"{name} installed";
                }
            }
            catch (Exception ex)
            {
                Log($"❌ Install error: {ex.Message}");
                ProsperoStatusText.Text = "install failed";
            }
            finally
            {
                _prosperoInstalling = false;
                try { if (zipPath != null) File.Delete(zipPath); } catch { }
                try { if (extDir != null && Directory.Exists(extDir)) Directory.Delete(extDir, true); } catch { }
            }
        }
    }
}
