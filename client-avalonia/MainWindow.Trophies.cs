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

namespace PS5Upload
{
    // Trophies tab — read-only NpTrophy V2 viewer.
    // Payload ships raw tropconf.json + tropmeta_<lang>.json + TRPTITLE.DAT
    // per registered set; we parse the JSON here and lazy-load PNG icons
    // through CMD_TROPHY_ICON on a dedicated connection.
    public partial class MainWindow
    {
        private List<PS5TrophySet> _trophySets = new();

        private void TrophySetSearchBox_TextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e) => ApplyTrophySetFilter();

        private void TrophySetSortComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyTrophySetFilter();

        private void ApplyTrophySetFilter()
        {
            if (TrophySetsListBox == null || TrophySetSearchBox == null || TrophySetSortComboBox == null) return;
            var prevNpwr = (TrophySetsListBox.SelectedItem as PS5TrophySet)?.NpCommunicationId;

            var q = TrophySetSearchBox.Text?.Trim() ?? "";
            IEnumerable<PS5TrophySet> view = string.IsNullOrEmpty(q)
                ? _trophySets
                : _trophySets.Where(s =>
                    s.GameName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    s.NpCommunicationId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    s.TitleId.Contains(q, StringComparison.OrdinalIgnoreCase));

            view = TrophySetSortComboBox.SelectedIndex switch
            {
                1 => view.OrderByDescending(s => s.GameName, StringComparer.OrdinalIgnoreCase),
                2 => view.OrderBy(s => s.EarnedCount).ThenByDescending(s => s.TotalCount),
                3 => view.OrderByDescending(s => s.EarnedCount).ThenByDescending(s => s.TotalCount),
                4 => view.OrderByDescending(s => s.TotalCount),
                _ => view.OrderBy(s => s.GameName, StringComparer.OrdinalIgnoreCase),
            };

            var list = view.ToList();
            TrophySetsListBox.ItemsSource = null;
            TrophySetsListBox.ItemsSource = list;
            TrophyStatusText.Text = string.IsNullOrEmpty(q) || list.Count == _trophySets.Count
                ? $"({_trophySets.Count} sets)"
                : $"({list.Count}/{_trophySets.Count} sets)";
            if (prevNpwr != null)
                TrophySetsListBox.SelectedItem =
                    list.FirstOrDefault(s => s.NpCommunicationId == prevNpwr);
        }

        private bool _trophyLoading;

        // Auto-load the catalog every time the Trophies tab gets focus —
        // no manual Refresh button needed.
        private async void GamesTabControl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (e.Source is not TabControl tc) return;
            if (tc.SelectedItem is TabItem { Header: "🏆 Trophies" })
                await RefreshTrophiesAsync();
            else if (tc.SelectedItem is TabItem { Header: "🌐 Prospero Store Site" } && !_prosperoLoadedOnce)
            {
                // Remote JSON catalog — no PS5 connection needed to browse.
                _prosperoLoadedOnce = true;
                await RefreshProsperoAsync();
            }
        }

        private async Task RefreshTrophiesAsync()
        {
            if (_trophyLoading) return;   // auto-load + unlock-refresh can overlap
            if (!_protocol.IsConnected) { Log("❌ Not connected to PS5"); return; }
            _trophyLoading = true;
            Log("🏆 Loading trophy sets...");
            TrophyStatusText.Text = "loading…";
            try
            {
                var sets = await _protocol.GetTrophyListAsync();
                foreach (var set in sets)
                    ParseTrophySet(set);

                // Game names come from tropmeta titleMetadata — the real title,
                // not a guess. Fall back to the mounted-games list or the TID.
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
                // ApplyTrophySetFilter rebuilds the view and restores the set the
                // user was working on — without this the next Unlock click hits
                // a null selection and silently no-ops.
                await Dispatcher.UIThread.InvokeAsync(() => ApplyTrophySetFilter());
                Log($"🏆 {sets.Count} trophy sets, {sets.Sum(s => s.TotalCount)} trophies total");

                // Lazy-load set icons + trophy icons on a side connection so the
                // main command channel stays responsive.
                _ = Task.Run(() => LoadTrophyIconsAsync(sets));
            }
            catch (Exception ex)
            {
                TrophyStatusText.Text = "error";
                Log($"❌ Trophy load failed: {ex.Message}");
            }
            finally { _trophyLoading = false; }
        }

        // tropconf.json → ids/grades/hidden/groups; tropmeta_<lang>.json →
        // title name + per-trophy name/detail. Id strings ("0000") map to
        // icon files trop0000.png.
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
                        StateKnown = false   // TRPTITLE.DAT parse decides below — honest "—" if unknown
                    });
                }

                ApplyTrpTitleState(set);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Trophy] parse failed for {set.NpCommunicationId}: {ex.Message}");
            }
        }

        // TRPTITLE.DAT (T2PD) — per-trophy unlock state, reversed from a real
        // user dump (verified: Witcher 3 set, 76/79 unlocked, missing trophies
        // 0=P/9=S/10=G match PSN progress exactly).
        //
        // Layout essentials:
        //   record stream of typed entries: u32be type | u32be size | 8B | body[size]
        //   type 0x500 (size 0xC0): trophy def record — body+0x00 u32be trophy id
        //   type 0x700 (size 0xB0): group state record — body+0x00 u32be group idx,
        //                           body+0x30 unlock bitmask for that group
        //   A global unlock bitmask (bit i = trophy id i, LSB-first) exists
        //   elsewhere in the file; we locate it by scanning for a zero-padded
        //   window whose set bits ⊆ [0,total) and whose popcount equals the
        //   union of the group masks (self-validating — no trust without match).
        private static void ApplyTrpTitleState(PS5TrophySet set)
        {
            var d = set.TrpTitleData;
            if (d.Length < 0x200 || d[0] != 'T' || d[1] != '2' || d[2] != 'P' || d[3] != 'D')
                return;

            static bool IsRec(byte[] d, int o, byte typeHi, byte szLo)
                => o + 8 <= d.Length && d[o] == 0 && d[o + 1] == 0 &&
                   d[o + 2] == typeHi && d[o + 3] == 0 &&
                   d[o + 4] == 0 && d[o + 5] == 0 && d[o + 6] == 0 && d[o + 7] == szLo;

            // 1) trophy count from the 0x500 definition records (max id + 1)
            int maxId = -1;
            for (int o = 0x200; o + 0x14 <= d.Length; o += 4)
            {
                if (!IsRec(d, o, 5, 0xC0)) continue;
                int id = (d[o + 0x10] << 24) | (d[o + 0x11] << 16) | (d[o + 0x12] << 8) | d[o + 0x13];
                if (id < 512 && id > maxId) maxId = id;
            }
            if (maxId < 0) return;
            int nbits = maxId + 1, nb = (nbits + 7) / 8;

            // 2) union of the per-group unlock masks (0x700 records, past headers)
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

            // 2.5) per-trophy state rows (0x800 records):
            //   body+0x00 u32be trophy id, +0x04 u32be unlocked flag,
            //   +0x10 u64be unlock time — microseconds since 0001-01-01 UTC
            //   (verified against trophy_tracker.db: rec with ts 0xE31C37B254CD80
            //    decodes to 2026-09-24T11:14:14Z, the file's last_update).
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
                // bit0 = earned; upper bits are per-game flags (LNEE rows use
                // 0x11 = earned|notified — a bare "== 1" misses them entirely)
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
                if (i >= nbits) return;          // stray bits → not a mask field here
                unionPop++;
            }

            byte[] mask;
            if (unionPop == 0)
            {
                mask = new byte[nb];             // state records exist → truly all locked
            }
            else
            {
                // 3) global mask: bits ⊆ [0,nbits), popcount == unionPop, zero tail
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
                    // Multi-group sets: no single window carries the union, so
                    // the popcount scan misses. The union of the 0x700 group
                    // masks IS the authoritative per-trophy state (it is what
                    // the console reads) — apply it directly instead of
                    // falling back to rows-only, which made the list disagree
                    // with the sidebar count (sidebar=unionPop, list=rows).
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

        // Debug-unlock path: the daemon binds the trophy commId from the RUNNING
        // game process, so we verify the matching title is actually running first.
        private async Task<bool> EnsureTrophyGameRunningAsync(PS5TrophySet set)
        {
            var apps = await _protocol.GetRunningAppsAsync();
            if (!string.IsNullOrEmpty(set.TitleId) &&
                apps.Any(a => string.Equals(a.TitleId, set.TitleId, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (apps.Count == 0)
            {
                Log($"⚠️ No game running on the PS5 — launch '{set.GameName}' first (the trophy API binds to the running game).");
                await ShowMessageAsync(
                    $"The game is not running on the PS5.\n\nLaunch \"{set.GameName}\" first — trophies can only be unlocked while the matching game is running (the unlock goes through the game's own trophy pipeline).",
                    "Launch the game first");
            }
            else
            {
                Log($"⚠️ Running title is '{apps[0].Name}' but the selected set belongs to '{set.TitleId}'. Launch the matching game first.");
                await ShowMessageAsync(
                    $"A different title is running: \"{apps[0].Name}\".\n\nLaunch \"{set.GameName}\" ({set.TitleId}) first — trophies can only be unlocked while the matching game is running.",
                    "Launch the game first");
            }
            return false;
        }

        private async Task RunTrophyUnlockAsync(string spec)
        {
            var sw = Stopwatch.StartNew();
            var (ok, msg) = await _protocol.TrophyUnlockAsync(spec);
            if (ok)
            {
                Log($"🏆 {msg} ({sw.ElapsedMilliseconds} ms)");
                // The daemon writes TRPTITLE.DAT asynchronously — give it a moment
                // then refresh so the new state/timestamp is re-read from disk.
                await Task.Delay(1200);
                await RefreshTrophiesAsync();
            }
            else
                Log($"❌ Trophy unlock failed: {msg}");
        }

        private async void TrophyUnlock_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: PS5Trophy t }) return;
            if (TrophySetsListBox.SelectedItem is not PS5TrophySet set) return;
            if (!await EnsureTrophyGameRunningAsync(set)) return;
            if (!await ShowConfirmAsync(
                $"Unlock trophy \"{t.Name}\" ({t.GradeDisplay}) on the PS5?\n\n" +
                "The unlock goes through the running game's own UDS event pipeline — it is permanent and fires a real trophy notification.")) return;
            await RunTrophyUnlockAsync($"unlock:{t.Id}");
        }

        // Re-lock: pure file rewrite on the console (TRPTITLE.DAT) — no running
        // game needed, and in fact best done with the game closed so the trophy
        // daemon can't flush a cached copy over our edit.
        private async void TrophyLock_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: PS5Trophy t }) return;
            if (TrophySetsListBox.SelectedItem is not PS5TrophySet set) return;
            if (!_protocol.IsConnected) { Log("❌ Not connected to PS5"); return; }
            if (!await ShowConfirmAsync(
                $"Re-lock trophy \"{t.Name}\" on the PS5?\n\n" +
                "This rewrites TRPTITLE.DAT on the console — the unlock bit and timestamp are cleared.\n\n" +
                "Close the game first if it's running, so the trophy daemon doesn't overwrite the edit.")) return;

            var (ok, msg) = await _protocol.TrophyUnlockAsync($"lock:{set.NpCommunicationId}:{t.Id}");
            if (ok)
            {
                Log($"🔒 {msg}");
                await Task.Delay(600);
                await RefreshTrophiesAsync();
            }
            else Log($"❌ Trophy lock failed: {msg}");
        }

        private async void TrophySetsListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (TrophySetsListBox.SelectedItem is not PS5TrophySet set) return;
            TrophyItemsListBox.ItemsSource = null;
            TrophyItemsListBox.ItemsSource = set.Trophies;
            TrophyStatusText.Text = $"{set.GameName} — {set.TotalCount} trophies";

            // Load this set's trophy icons if not already fetched.
            if (set.Trophies.Any(t => t.Icon == null))
                _ = Task.Run(() => LoadTrophyItemIconsAsync(set));
        }

        private async Task LoadTrophyIconsAsync(List<PS5TrophySet> sets)
        {
            try
            {
                using var proto = new PS5Protocol();
                if (!await proto.ConnectAsync(_ps5IpAddress)) return;
                foreach (var set in sets)
                {
                    // icon0_<lang>.png is the set cover; fall back to plain icon0.png.
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
                    if (!_protocol.IsConnected) break;
                }
            }
            catch { }
        }
    }
}
