using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PS5Upload;

namespace PS5SuiteAndroid.Views;

// Games — search/sort, per-game mount, stop running title, icons.
public partial class MainView
{
    private readonly List<GameItem> _allGames = new();

    private void GameSearch_TextChanged(object? sender, TextChangedEventArgs e) => ApplyGameFilter();
    private void GameSort_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyGameFilter();

    private void ApplyGameFilter()
    {
        if (GamesListBox == null || GameSearchBox == null || GameSortCombo == null) return;
        var prev = (GamesListBox.SelectedItem as GameItem)?.TitleId;

        var q = GameSearchBox.Text?.Trim() ?? "";
        IEnumerable<GameItem> view = string.IsNullOrEmpty(q)
            ? _allGames
            : _allGames.Where(g =>
                g.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                g.TitleId.Contains(q, StringComparison.OrdinalIgnoreCase));

        view = GameSortCombo.SelectedIndex switch
        {
            1 => view.OrderByDescending(g => g.Name, StringComparer.OrdinalIgnoreCase),
            2 => view.OrderBy(g => g.Size),
            3 => view.OrderByDescending(g => g.Size),
            _ => view.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase),
        };

        var list = view.ToList();
        _games.Clear();
        foreach (var g in list) _games.Add(g);
        if (GameCountText != null)
            GameCountText.Text = string.IsNullOrEmpty(q) || list.Count == _allGames.Count
                ? $"Games ({_allGames.Count})"
                : $"Games ({list.Count}/{_allGames.Count})";
        if (prev != null)
            GamesListBox.SelectedItem = list.FirstOrDefault(g => g.TitleId == prev);
    }

    private async void MountGameButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_protocol == null || !_protocol.IsConnected) { UpdateStatus("Not connected"); return; }
        if (GamesListBox.SelectedItem is not GameItem game) { UpdateStatus("Select a game first"); return; }
        UpdateStatus($"Mounting {game.TitleId}...");
        try
        {
            var result = await _protocol.MountGameAsync(game.TitleId, m => UpdateStatus(m));
            bool failed = result == null || result.StartsWith("ERROR");
            UpdateStatus(failed ? $"Mount failed: {result ?? _protocol.LastError}" : $"Mounted {game.TitleId}");
            if (!failed) await RefreshGamesAsync();
        }
        catch (Exception ex) { UpdateStatus($"Mount error: {ex.Message}"); }
    }

    // Stop = kill the running app whose title id matches the selected game.
    private async void StopGameButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_protocol == null || !_protocol.IsConnected) { UpdateStatus("Not connected"); return; }
        if (GamesListBox.SelectedItem is not GameItem game) { UpdateStatus("Select a game first"); return; }
        try
        {
            var running = await _protocol.GetRunningAppsAsync();
            var match = running.FirstOrDefault(a =>
                string.Equals(a.TitleId, game.TitleId, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                UpdateStatus($"{game.Name} is not running");
                await ShowMessageAsync($"\"{game.Name}\" is not currently running on the PS5.", "Not running");
                return;
            }
            if (!await ShowConfirmAsync($"Stop \"{game.Name}\" on the PS5?", "Stop game")) return;
            var (ok, msg) = await _protocol.KillAppAsync(game.TitleId);
            UpdateStatus(ok ? $"Stopped {game.Name}" : $"Stop failed: {msg}");
            await RefreshGamesAsync();
        }
        catch (Exception ex) { UpdateStatus($"Stop error: {ex.Message}"); }
    }

    // Mark running titles and lazy-load icons on a side connection.
    private async Task LoadGameExtrasAsync()
    {
        try
        {
            var running = await _protocol!.GetRunningAppsAsync();
            var runningIds = new HashSet<string>(running.Select(a => a.TitleId), StringComparer.OrdinalIgnoreCase);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var g in _allGames)
                    g.IsRunning = runningIds.Contains(g.TitleId);
                if (RunningAppsText != null)
                {
                    RunningAppsText.IsVisible = running.Count > 0;
                    RunningAppsText.Text = running.Count == 0 ? ""
                        : "Running: " + string.Join(", ", running.Select(a => string.IsNullOrEmpty(a.Name) ? a.TitleId : a.Name));
                }
            });

            string? ip = _ps5IpAddress;
            if (ip == null) return;
            using var iconProto = new PS5Protocol();
            if (!await iconProto.ConnectAsync(ip)) return;
            foreach (var g in _allGames)
            {
                if (g.Icon != null) continue;
                var bytes = await iconProto.GetGameIconAsync(g.TitleId);
                if (bytes == null || bytes.Length == 0) continue;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try { g.Icon = new Bitmap(new MemoryStream(bytes)); } catch { }
                });
                if (_protocol?.IsConnected != true) break;
            }
        }
        catch { }
    }
}
