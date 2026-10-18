using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace PS5Upload
{
    public partial class GameDetailsWindow : UserControl, OverlayDialogs.IOverlayClosable
    {
        /// <summary>Raised when the view wants its host overlay to close.</summary>
        public event Action? RequestClose;

        private readonly PS5MountedGame _game;
        private readonly PS5Protocol? _protocol;
        private readonly string _paramJsonRaw;
        private static readonly HttpClient _http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            client.Timeout = TimeSpan.FromSeconds(8);
            return client;
        }

        public GameDetailsWindow()
        {
            InitializeComponent();
            _game = new PS5MountedGame();
            _paramJsonRaw = "";
        }

        public GameDetailsWindow(PS5MountedGame game, Dictionary<string, string> details, PS5Protocol? protocol = null) : this()
        {
            _game = game;
            _protocol = protocol;
            _paramJsonRaw = details.TryGetValue("param_json", out var pj) ? pj : "";

            if (game.Icon != null) GameIconImage.Source = game.Icon;
            GameNameText.Text = details.TryGetValue("name", out var n) ? n : game.Name;
            TitleIdText.Text = game.TitleId;
            RegionText.Text = details.TryGetValue("region", out var r) ? r : game.Region;
            bool isActive = details.TryGetValue("is_active", out var act) && act == "1";
            StatusText.Text = isActive ? "✓ Mounted" : "✗ Not Mounted";
            StatusText.Foreground = isActive
                ? new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45))
                : new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45));

            if (details.TryGetValue("total_size", out var ts) && ulong.TryParse(ts, out ulong totalBytes))
                TotalSizeText.Text = FormatSize(totalBytes);
            if (details.TryGetValue("eboot_size", out var es) && ulong.TryParse(es, out ulong ebootBytes))
                EbootSizeText.Text = FormatSize(ebootBytes);

            SourcePathText.Text = details.TryGetValue("path", out var p) ? p : game.Path;
            InstallDateText.Text = details.TryGetValue("install_date", out var d) ? d : "Unknown";

            ParamJsonText.Text = details.TryGetValue("param_json", out var param) ? FormatJson(param) : "(not available)";

            _ = LoadCoverArtAsync();
        }

        private async Task LoadCoverArtAsync()
        {
            if (_protocol == null || !_protocol.IsConnected) return;
            try
            {
                var picBytes = await _protocol.GetGamePicAsync(_game.TitleId, 0);
                if (picBytes == null || picBytes.Length == 0) return;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        using var ms = new MemoryStream(picBytes);
                        var bitmap = new Bitmap(ms);
                        BackgroundCoverImage.Source = bitmap;
                    }
                    catch { }
                });
            }
            catch { }
        }

        private void OpenInBrowserButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                string query = !string.IsNullOrWhiteSpace(_game.Name) && _game.Name != "Unknown" ? _game.Name : _game.TitleId;
                string url = $"https://store.playstation.com/en-gb/search/{Uri.EscapeDataString(query)}";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch { }
        }

        private async void FetchPsnInfoButton_Click(object? sender, RoutedEventArgs e)
        {
            FetchPsnInfoButton.IsEnabled = false;
            PsnStatusText.Text = "Fetching from PSN Store...";
            PsnInfoPanel.IsVisible = false;

            try
            {
                var info = await FetchPsnInfoAsync(_game.TitleId);
                if (info == null)
                {
                    PsnStatusText.Text = "❌ Not found on PSN Store. Try the browser search button below.";
                    return;
                }
                PsnTitleText.Text = info.Title;
                PsnPublisherText.Text = string.IsNullOrEmpty(info.Publisher) ? "" : $"🏢 {info.Publisher}";
                PsnReleaseDateText.Text = string.IsNullOrEmpty(info.ReleaseDate) ? "" : $"📅 Released: {info.ReleaseDate}";
                PsnDescriptionText.Text = info.Description ?? "";
                PsnInfoPanel.IsVisible = true;
                PsnStatusText.Text = $"✓ Found on PSN Store ({info.Source})";
            }
            catch (Exception ex) { PsnStatusText.Text = $"❌ Error: {ex.Message}"; }
            finally { FetchPsnInfoButton.IsEnabled = true; }
        }

        private class PsnGameInfo
        {
            public string Title { get; set; } = "";
            public string Publisher { get; set; } = "";
            public string ReleaseDate { get; set; } = "";
            public string Description { get; set; } = "";
            public string Source { get; set; } = "";
        }

        private async Task<PsnGameInfo?> FetchPsnInfoAsync(string titleId)
        {
            var idsToTry = new List<string> { titleId + "_00" };
            var alternateIds = ExtractAlternateTitleIds(_paramJsonRaw);
            foreach (var altId in alternateIds)
                if (!idsToTry.Contains(altId)) idsToTry.Add(altId);

            string[] chihiroRegions = { "US/en", "GB/en", "DE/de", "FR/fr", "JP/ja", "IT/it", "ES/es" };
            foreach (var tryId in idsToTry)
            {
                foreach (var region in chihiroRegions)
                {
                    try
                    {
                        var parts = region.Split('/');
                        string url = $"https://store.playstation.com/store/api/chihiro/00_09_000/titlecontainer/{parts[0]}/{parts[1]}/999/{tryId}";
                        var response = await _http.GetAsync(url);
                        if (!response.IsSuccessStatusCode) continue;
                        string json = await response.Content.ReadAsStringAsync();
                        if (json.Length < 50) continue;
                        var info = ParsePsnJson(json);
                        if (info != null) { string tag = tryId == titleId + "_00" ? "" : $" via alt ID {tryId}"; info.Source = $"Chihiro API ({region}){tag}"; return info; }
                    }
                    catch { }
                }
            }

            string[] valkyrieRegions = { "gb/GB", "us/US", "de/DE", "fr/FR", "ja/JP", "it/IT", "es/ES" };
            foreach (var region in valkyrieRegions)
            {
                try
                {
                    string url = $"https://store.playstation.com/valkyrie-api/{region.Split('/')[0]}/{region.Split('/')[1]}/19/resolve/{titleId}_00";
                    var response = await _http.GetAsync(url);
                    if (!response.IsSuccessStatusCode) continue;
                    string json = await response.Content.ReadAsStringAsync();
                    if (json.Length < 50) continue;
                    var info = ParseValkyrieJson(json);
                    if (info != null) { info.Source = $"Valkyrie API ({region})"; return info; }
                }
                catch { }
            }

            string gameName = _game.Name;
            if (!string.IsNullOrWhiteSpace(gameName) && gameName != "Unknown")
            {
                try
                {
                    string encoded = Uri.EscapeDataString(gameName);
                    string url = $"https://store.playstation.com/valkyrie-api/en/US/19/tumbler-search/{encoded}?suggested_size=5&mode=game";
                    var response = await _http.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        var info = ParseValkyrieSearchJson(json, gameName);
                        if (info != null) { info.Source = "Valkyrie Search (by name)"; return info; }
                    }
                }
                catch { }
            }
            return null;
        }

        private static PsnGameInfo? ParseValkyrieJson(string json)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("included", out var included) || included.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
                foreach (var item in included.EnumerateArray())
                {
                    if (!item.TryGetProperty("attributes", out var attrs)) continue;
                    var info = new PsnGameInfo();
                    if (attrs.TryGetProperty("name", out var name)) info.Title = name.GetString() ?? "";
                    if (attrs.TryGetProperty("long-description", out var ld)) info.Description = StripHtml(ld.GetString() ?? "");
                    else if (attrs.TryGetProperty("description", out var sd)) info.Description = StripHtml(sd.GetString() ?? "");
                    if (attrs.TryGetProperty("release-date", out var rd)) { string raw = rd.GetString() ?? ""; info.ReleaseDate = DateTime.TryParse(raw, out DateTime dt) ? dt.ToString("yyyy-MM-dd") : raw; }
                    if (attrs.TryGetProperty("provider-name", out var pub)) info.Publisher = pub.GetString() ?? "";
                    else if (attrs.TryGetProperty("publisher-name", out var pub2)) info.Publisher = pub2.GetString() ?? "";
                    if (!string.IsNullOrWhiteSpace(info.Title)) return info;
                }
            }
            catch { }
            return null;
        }

        private static PsnGameInfo? ParseValkyrieSearchJson(string json, string searchName)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("included", out var included) || included.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
                PsnGameInfo? bestMatch = null;
                foreach (var item in included.EnumerateArray())
                {
                    if (!item.TryGetProperty("type", out var type) || type.GetString() != "game") continue;
                    if (!item.TryGetProperty("attributes", out var attrs) || !attrs.TryGetProperty("name", out var nameProp)) continue;
                    string name = nameProp.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (string.Equals(name, searchName, StringComparison.OrdinalIgnoreCase) || name.Contains(searchName, StringComparison.OrdinalIgnoreCase) || searchName.Contains(name, StringComparison.OrdinalIgnoreCase))
                    {
                        var info = new PsnGameInfo { Title = name };
                        if (attrs.TryGetProperty("long-description", out var ld)) info.Description = StripHtml(ld.GetString() ?? "");
                        if (attrs.TryGetProperty("release-date", out var rd)) { string raw = rd.GetString() ?? ""; if (DateTime.TryParse(raw, out DateTime dt)) info.ReleaseDate = dt.ToString("yyyy-MM-dd"); }
                        if (attrs.TryGetProperty("provider-name", out var pub)) info.Publisher = pub.GetString() ?? "";
                        if (string.Equals(name, searchName, StringComparison.OrdinalIgnoreCase)) return info;
                        bestMatch ??= info;
                    }
                }
                return bestMatch;
            }
            catch { }
            return null;
        }

        private static PsnGameInfo? ParsePsnJson(string json)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                var info = new PsnGameInfo();
                if (root.TryGetProperty("name", out var name)) info.Title = name.GetString() ?? "";
                else if (root.TryGetProperty("title_name", out var tn)) info.Title = tn.GetString() ?? "";
                if (root.TryGetProperty("provider_name", out var pub)) info.Publisher = pub.GetString() ?? "";
                else if (root.TryGetProperty("publisher_name", out var pub2)) info.Publisher = pub2.GetString() ?? "";
                if (root.TryGetProperty("release_date", out var rd)) { string rawDate = rd.GetString() ?? ""; info.ReleaseDate = DateTime.TryParse(rawDate, out DateTime dt) ? dt.ToString("yyyy-MM-dd") : rawDate; }
                if (root.TryGetProperty("long_desc", out var ld)) info.Description = StripHtml(ld.GetString() ?? "");
                else if (root.TryGetProperty("short_desc", out var sd)) info.Description = StripHtml(sd.GetString() ?? "");
                if (!string.IsNullOrWhiteSpace(info.Title)) return info;
            }
            catch { }
            return null;
        }

        private static List<string> ExtractAlternateTitleIds(string paramJson)
        {
            var ids = new List<string>();
            if (string.IsNullOrWhiteSpace(paramJson)) return ids;
            var regex = new System.Text.RegularExpressions.Regex(@"(?:[A-Z]{2}\d{4}-)?(PPSA\d{5}_\d{2}|CUSA\d{5}_\d{2})");
            var matches = regex.Matches(paramJson);
            foreach (System.Text.RegularExpressions.Match m in matches)
                if (m.Groups.Count >= 2) { string id = m.Groups[1].Value; if (!ids.Contains(id)) ids.Add(id); }
            return ids;
        }

        private static string StripHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            html = System.Text.RegularExpressions.Regex.Replace(html, @"<br\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            html = System.Text.RegularExpressions.Regex.Replace(html, @"</p>", "\n\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            html = System.Text.RegularExpressions.Regex.Replace(html, @"<[^>]+>", "");
            html = System.Net.WebUtility.HtmlDecode(html);
            html = System.Text.RegularExpressions.Regex.Replace(html, @"[ \t]+", " ");
            html = System.Text.RegularExpressions.Regex.Replace(html, @"\n{3,}", "\n\n");
            return html.Trim();
        }

        private static string FormatSize(ulong bytes)
        {
            double mb = bytes / (1024.0 * 1024.0);
            return mb < 1024 ? $"{mb:F1} MB ({bytes:N0} bytes)" : $"{mb / 1024.0:F2} GB ({bytes:N0} bytes)";
        }

        private static string FormatJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "(empty)";
            var sb = new System.Text.StringBuilder();
            int depth = 0; bool inString = false;
            foreach (char c in raw)
            {
                if (c == '"') inString = !inString;
                if (!inString)
                {
                    if (c == '{' || c == '[') { sb.Append(c); sb.Append('\n'); depth++; sb.Append(new string(' ', depth * 2)); continue; }
                    if (c == '}' || c == ']') { sb.Append('\n'); depth = Math.Max(0, depth - 1); sb.Append(new string(' ', depth * 2)); sb.Append(c); continue; }
                    if (c == ',') { sb.Append(c); sb.Append('\n'); sb.Append(new string(' ', depth * 2)); continue; }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private void CloseButton_Click(object? sender, RoutedEventArgs e) => RequestClose?.Invoke();
    }
}
