using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PS5Upload;

namespace PS5SuiteAndroid.Views;

// Trophies — two-level view: trophy sets list → per-set trophy list.
// Parsing logic is a direct port of the desktop client (verified T2PD parse).
public partial class MainView
{
    private List<PS5TrophySet> _trophySets = new();
    private PS5TrophySet? _selectedTrophySet;
    private bool _trophyLoading;

    private void TrophySearch_TextChanged(object? sender, TextChangedEventArgs e) => ApplyTrophySetFilter();

    private void ApplyTrophySetFilter()
    {
        if (TrophySetsListBox == null || TrophySearchBox == null) return;
        var prevNpwr = _selectedTrophySet?.NpCommunicationId;

        var q = TrophySearchBox.Text?.Trim() ?? "";
        var view = string.IsNullOrEmpty(q)
            ? _trophySets
            : _trophySets.Where(s =>
                s.GameName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.NpCommunicationId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.TitleId.Contains(q, StringComparison.OrdinalIgnoreCase));

        var list = view.OrderBy(s => s.GameName, StringComparer.OrdinalIgnoreCase).ToList();
        TrophySetsListBox.ItemsSource = null;
        TrophySetsListBox.ItemsSource = list;
        if (prevNpwr != null && TrophySetsListBox.IsVisible)
            TrophySetsListBox.SelectedItem = list.FirstOrDefault(s => s.NpCommunicationId == prevNpwr);
    }

    private async void TrophyRefresh_Click(object? sender, RoutedEventArgs e)
        => await RefreshTrophiesAsync();

    private async Task RefreshTrophiesAsync()
    {
        if (_trophyLoading) return;
        if (_protocol == null || !_protocol.IsConnected) { UpdateStatus("Not connected"); return; }
        _trophyLoading = true;
        TrophyStatusText.Text = "loading…";
        UpdateStatus("Loading trophy sets...");
        try
        {
            var sets = await _protocol.GetTrophyListAsync();
            foreach (var set in sets)
                ParseTrophySet(set);

            // Real game names from tropmeta; fall back to mounted-games list.
            var mounted = await _protocol.GetGameListAsync();
            var nameByTitle = mounted.ToDictionary(g => g.TitleId, g => g.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var set in sets)
            {
                if (string.IsNullOrEmpty(set.GameName))
                    set.GameName = !string.IsNullOrEmpty(set.TitleId)
                        ? (nameByTitle.TryGetValue(set.TitleId, out var gn) ? gn : set.TitleId)
                        : set.NpCommunicationId;
            }

            _trophySets = sets;
            await Dispatcher.UIThread.InvokeAsync(() => ApplyTrophySetFilter());
            TrophyStatusText.Text = $"{sets.Count} sets, {sets.Sum(s => s.TotalCount)} trophies";
            UpdateStatus($"🏆 {sets.Count} trophy sets, {sets.Sum(s => s.TotalCount)} trophies");

            _ = Task.Run(() => LoadTrophySetIconsAsync(sets));
        }
        catch (Exception ex)
        {
            TrophyStatusText.Text = "error";
            UpdateStatus($"Trophy load failed: {ex.Message}");
        }
        finally { _trophyLoading = false; }
    }

    private void TrophySet_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TrophySetsListBox.SelectedItem is not PS5TrophySet set) return;
        _selectedTrophySet = set;
        TrophyItemsListBox.ItemsSource = null;
        TrophyItemsListBox.ItemsSource = set.Trophies;
        TrophySetsListBox.IsVisible = false;
        TrophyItemsListBox.IsVisible = true;
        TrophyBackBar.IsVisible = true;
        TrophyStatusText.Text = $"{set.GameName} — {set.TotalCount} trophies";

        if (set.Trophies.Any(t => t.Icon == null))
            _ = Task.Run(() => LoadTrophyItemIconsAsync(set));
    }

    private void TrophyBack_Click(object? sender, RoutedEventArgs e)
    {
        TrophyItemsListBox.IsVisible = false;
        TrophyBackBar.IsVisible = false;
        TrophySetsListBox.IsVisible = true;
        _selectedTrophySet = null;
        if (TrophySetsListBox.ItemsSource == null) ApplyTrophySetFilter();
        TrophyStatusText.Text = $"{_trophySets.Count} sets, {_trophySets.Sum(s => s.TotalCount)} trophies";
    }

    // ---------- parsing (ported 1:1 from desktop MainWindow.Trophies.cs) ----------

    private static void ParseTrophySet(PS5TrophySet set)
    {
        try
        {
            var confGrades = new Dictionary<string, (string grade, bool hidden, string group)>();
            using (var doc = JsonDocument.Parse(set.TropConfJson))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("trophies", out var trs))
                    foreach (var t in trs.EnumerateArray())
                    {
                        string id = t.TryGetProperty("id", out var idp) ? idp.GetString() ?? "" : "";
                        if (id.Length == 0) continue;
                        string grade = t.TryGetProperty("grade", out var g) ? g.GetString() ?? "" : "";
                        bool hidden = t.TryGetProperty("hidden", out var h) && h.GetBoolean();
                        string group = t.TryGetProperty("groupId", out var gr) ? gr.GetString() ?? "" : "";
                        confGrades[id] = (grade, hidden, group);
                    }
            }

            var names = new Dictionary<string, (string name, string detail)>();
            var groupNames = new Dictionary<string, string>();
            string? titleName = null;
            if (set.TropMetaJson.Length > 0)
            {
                using var doc = JsonDocument.Parse(set.TropMetaJson);
                if (doc.RootElement.TryGetProperty("metadata", out var meta))
                {
                    if (meta.TryGetProperty("titleMetadata", out var tm) &&
                        tm.TryGetProperty("name", out var tn))
                        titleName = tn.GetString();
                    if (meta.TryGetProperty("groupMetadata", out var gms))
                        foreach (var g in gms.EnumerateArray())
                        {
                            string id = g.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                            string name = g.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            if (id.Length > 0) groupNames[id] = name;
                        }
                    if (meta.TryGetProperty("trophyMetadata", out var tms))
                        foreach (var t in tms.EnumerateArray())
                        {
                            string id = t.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                            string name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            string det = t.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";
                            if (id.Length > 0) names[id] = (name, det);
                        }
                }
            }

            if (!string.IsNullOrEmpty(titleName)) set.GameName = titleName;

            set.Trophies.Clear();
            foreach (var kv in confGrades.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var (grade, hidden, group) = kv.Value;
                names.TryGetValue(kv.Key, out var nd);
                set.Trophies.Add(new PS5Trophy
                {
                    Id = int.TryParse(kv.Key, out int idNum) ? idNum : 0,
                    Name = string.IsNullOrEmpty(nd.name) ? $"Trophy {kv.Key}" : nd.name,
                    Detail = nd.detail ?? "",
                    Grade = grade,
                    Hidden = hidden,
                    GroupId = groupNames.TryGetValue(group, out var gn) ? gn : group,
                    StateKnown = false
                });
            }

            ApplyTrpTitleState(set);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Trophy] parse failed for {set.NpCommunicationId}: {ex.Message}");
        }
    }

    // TRPTITLE.DAT (T2PD) per-trophy state — verified layout, same as desktop.
    private static void ApplyTrpTitleState(PS5TrophySet set)
    {
        var d = set.TrpTitleData;
        if (d.Length < 0x200 || d[0] != 'T' || d[1] != '2' || d[2] != 'P' || d[3] != 'D')
            return;

        static bool IsRec(byte[] d, int o, byte typeHi, byte szLo)
            => o + 8 <= d.Length && d[o] == 0 && d[o + 1] == 0 &&
               d[o + 2] == typeHi && d[o + 3] == 0 &&
               d[o + 4] == 0 && d[o + 5] == 0 && d[o + 6] == 0 && d[o + 7] == szLo;

        int maxId = -1;
        for (int o = 0x200; o + 0x14 <= d.Length; o += 4)
        {
            if (!IsRec(d, o, 5, 0xC0)) continue;
            int id = (d[o + 0x10] << 24) | (d[o + 0x11] << 16) | (d[o + 0x12] << 8) | d[o + 0x13];
            if (id < 512 && id > maxId) maxId = id;
        }
        if (maxId < 0) return;
        int nbits = maxId + 1, nb = (nbits + 7) / 8;

        var unionMask = new byte[nb];
        int groupRecs = 0;
        for (int o = 0x800; o + 0x10 + 0x30 + nb <= d.Length; o += 4)
        {
            if (!IsRec(d, o, 7, 0xB0)) continue;
            groupRecs++;
            int mo = o + 0x10 + 0x30;
            for (int j = 0; j < nb; j++) unionMask[j] |= d[mo + j];
        }
        if (groupRecs == 0) return;

        var rowState = new Dictionary<int, (bool unlocked, DateTime when)>();
        var epoch = new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int o = 0x800; o + 0x10 + 0x18 <= d.Length; o += 4)
        {
            if (!IsRec(d, o, 8, 0x50)) continue;
            int id = (d[o + 0x10] << 24) | (d[o + 0x11] << 16) | (d[o + 0x12] << 8) | d[o + 0x13];
            if (id < 0 || id >= nbits) continue;
            int fl = (d[o + 0x14] << 24) | (d[o + 0x15] << 16) | (d[o + 0x16] << 8) | d[o + 0x17];
            long ts = 0;
            for (int j = 0; j < 8; j++) ts = (ts << 8) | d[o + 0x20 + j];
            bool unlocked = (fl & 1) != 0;
            DateTime when = default;
            if (unlocked && ts > 0)
            {
                try { when = epoch + TimeSpan.FromTicks(ts * 10); } catch { }
            }
            rowState[id] = (unlocked, when);
        }

        int unionPop = 0;
        for (int i = 0; i < nb * 8; i++)
        {
            if (((unionMask[i >> 3] >> (i & 7)) & 1) == 0) continue;
            if (i >= nbits) return;
            unionPop++;
        }

        byte[] mask;
        if (unionPop == 0)
        {
            mask = new byte[nb];
        }
        else
        {
            byte[]? found = null;
            for (int o = 0x200; o + nb + 4 <= d.Length && found == null; o += 8)
            {
                int pop = 0; bool ok = true;
                for (int j = 0; j < nb; j++)
                {
                    int b = d[o + j];
                    for (int bit = 0; bit < 8; bit++)
                        if (((b >> bit) & 1) != 0)
                        {
                            if (j * 8 + bit >= nbits) { ok = false; break; }
                            pop++;
                        }
                    if (!ok) break;
                }
                if (!ok || pop != unionPop) continue;
                if (d[o + nb] != 0 || d[o + nb + 1] != 0 || d[o + nb + 2] != 0 || d[o + nb + 3] != 0)
                    continue;
                found = new byte[nb];
                Array.Copy(d, o, found, 0, nb);
            }
            if (found == null)
            {
                // Multi-group sets: no single window carries the union, so the
                // popcount scan misses. The union of the 0x700 group masks IS
                // the authoritative per-trophy state — apply it directly
                // instead of rows-only (which made list disagree with sidebar).
                mask = unionMask;
            }
            else
            {
                mask = found;
            }
        }

        set.UnlockMask = mask;
        set.StateKnown = true;
        foreach (var t in set.Trophies)
        {
            if (t.Id < 0 || t.Id >= nbits) continue;
            bool unlocked = ((mask[t.Id >> 3] >> (t.Id & 7)) & 1) != 0;
            DateTime? when = null;
            if (rowState.TryGetValue(t.Id, out var rs))
            {
                unlocked |= rs.unlocked;
                if (rs.when != default) when = rs.when;
            }
            t.StateKnown = true;
            t.IsUnlocked = unlocked;
            t.UnlockedTime = when;
        }
    }

    // ---------- unlock / lock ----------

    private async Task<bool> EnsureTrophyGameRunningAsync(PS5TrophySet set)
    {
        var apps = await _protocol!.GetRunningAppsAsync();
        if (!string.IsNullOrEmpty(set.TitleId) &&
            apps.Any(a => string.Equals(a.TitleId, set.TitleId, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (apps.Count == 0)
        {
            UpdateStatus($"⚠️ No game running — launch '{set.GameName}' first");
            await ShowMessageAsync(
                $"The game is not running on the PS5.\n\nLaunch \"{set.GameName}\" first — trophies can only be unlocked while the matching game is running.",
                "Launch the game first");
        }
        else
        {
            UpdateStatus($"⚠️ Running title is '{apps[0].Name}', need '{set.TitleId}'");
            await ShowMessageAsync(
                $"A different title is running: \"{apps[0].Name}\".\n\nLaunch \"{set.GameName}\" ({set.TitleId}) first.",
                "Launch the game first");
        }
        return false;
    }

    private async Task RunTrophyUnlockAsync(string spec)
    {
        var sw = Stopwatch.StartNew();
        var (ok, msg) = await _protocol!.TrophyUnlockAsync(spec);
        if (ok)
        {
            UpdateStatus($"🏆 {msg} ({sw.ElapsedMilliseconds} ms)");
            await Task.Delay(1200);
            await RefreshTrophiesAsync();
        }
        else
        {
            UpdateStatus($"❌ Trophy unlock failed: {msg}");
            await ShowMessageAsync(msg, "Unlock failed");
        }
    }

    private async void TrophyUnlock_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PS5Trophy t }) return;
        if (_selectedTrophySet is not PS5TrophySet set) return;
        if (!await EnsureTrophyGameRunningAsync(set)) return;
        if (!await ShowConfirmAsync(
            $"Unlock trophy \"{t.Name}\" ({t.GradeDisplay}) on the PS5?\n\n" +
            "The unlock goes through the running game's trophy pipeline — it is permanent and fires a real notification.",
            "Unlock trophy")) return;
        await RunTrophyUnlockAsync($"unlock:{t.Id}");
    }

    private async void TrophyLock_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PS5Trophy t }) return;
        if (_selectedTrophySet is not PS5TrophySet set) return;
        if (_protocol?.IsConnected != true) { UpdateStatus("Not connected"); return; }
        if (!await ShowConfirmAsync(
            $"Re-lock trophy \"{t.Name}\" on the PS5?\n\n" +
            "This rewrites TRPTITLE.DAT on the console — the unlock bit and timestamp are cleared.\n\n" +
            "Close the game first if it's running, so the trophy daemon doesn't overwrite the edit.",
            "Re-lock trophy")) return;

        var (ok, msg) = await _protocol.TrophyUnlockAsync($"lock:{set.NpCommunicationId}:{t.Id}");
        if (ok)
        {
            UpdateStatus($"🔒 {msg}");
            await Task.Delay(600);
            await RefreshTrophiesAsync();
            // stay inside the set's trophy list after refresh
            if (_selectedTrophySet != null && TrophyItemsListBox.IsVisible)
            {
                var same = _trophySets.FirstOrDefault(s => s.NpCommunicationId == _selectedTrophySet.NpCommunicationId);
                if (same != null)
                {
                    _selectedTrophySet = same;
                    TrophyItemsListBox.ItemsSource = same.Trophies;
                }
            }
        }
        else
        {
            UpdateStatus($"❌ Trophy lock failed: {msg}");
            await ShowMessageAsync(msg, "Lock failed");
        }
    }

    // ---------- icons (side connection) ----------

    private async Task LoadTrophySetIconsAsync(List<PS5TrophySet> sets)
    {
        try
        {
            if (_ps5IpAddress == null) return;
            using var proto = new PS5Protocol();
            if (!await proto.ConnectAsync(_ps5IpAddress)) return;
            foreach (var set in sets)
            {
                foreach (var cand in new[] { "icon0_en-US.png", "icon0.png" })
                {
                    var bytes = await proto.GetTrophyIconAsync(set.NpCommunicationId, cand);
                    if (bytes == null || bytes.Length == 0) continue;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        try { set.Icon = new Bitmap(new MemoryStream(bytes)); } catch { }
                    });
                    break;
                }
            }
        }
        catch { }
    }

    private async Task LoadTrophyItemIconsAsync(PS5TrophySet set)
    {
        try
        {
            if (_ps5IpAddress == null) return;
            using var proto = new PS5Protocol();
            if (!await proto.ConnectAsync(_ps5IpAddress)) return;
            foreach (var t in set.Trophies)
            {
                if (t.Icon != null) continue;
                var bytes = await proto.GetTrophyIconAsync(set.NpCommunicationId, $"trop{t.Id:0000}.png");
                if (bytes == null || bytes.Length == 0) continue;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try { t.Icon = new Bitmap(new MemoryStream(bytes)); } catch { }
                });
                if (_protocol?.IsConnected != true) break;
            }
        }
        catch { }
    }
}
