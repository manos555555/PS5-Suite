using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PS5Upload;

namespace PS5SuiteAndroid.Views;

// Saves: browse mounted, backup (decrypted + raw), restore. Screenshots:
// download to the app's downloads dir.
public partial class MainView
{
    // ---------- Browse mounted ----------

    private async void SaveBrowseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        var (mounted, _, _, _) = await _protocol!.SaveMountStatusAsync();
        if (!mounted)
        {
            await ShowMessageAsync("No save is mounted.\nMount a save image first.", "Saves");
            return;
        }
        _currentPath = PS5Protocol.SaveMountPoint;
        NavigateTo(1);   // File Transfer page
        await RefreshFilesAsync();
    }

    // ---------- Mount helper ----------

    private async Task<(bool mountedOk, bool weMounted)> EnsureSaveMountedAsync(PS5SaveFile save)
    {
        var (mounted, src, _, _) = await _protocol!.SaveMountStatusAsync();
        if (mounted && string.Equals(src, save.Path, StringComparison.OrdinalIgnoreCase))
            return (true, false);
        if (mounted)
            await _protocol.SaveUnmountAsync(m => UpdateStatus(m));
        UpdateStatus($"Mounting {save.SaveName}...");
        var (ok, msg) = await _protocol.SaveMountAsync(save.Path, m => UpdateStatus(m));
        if (!ok) UpdateStatus($"Mount failed: {msg}");
        return (ok, ok);
    }

    private async Task UnmountSaveAsync()
    {
        var (ok, msg) = await _protocol!.SaveUnmountAsync(m => UpdateStatus(m));
        UpdateStatus(ok ? "Save unmounted — image written back" : $"Unmount failed: {msg}");
        if (ok) SaveMountStatusText.Text = "No save mounted";
    }

    // ---------- Backup (decrypted) ----------

    private async void SaveBackupButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        if (SavesListBox.SelectedItem is not PS5SaveFile save)
        { UpdateStatus("Select a save first"); return; }

        string backupRoot = Path.Combine(AppPaths.DownloadsDir, "PS5Suite", "save_backups");
        string backupDir = Path.Combine(backupRoot, $"{save.SaveName}_{save.TitleId}");
        Directory.CreateDirectory(backupDir);

        if (!await ShowConfirmAsync(
            $"Backup decrypted contents of {save.SaveName}?\n\nDestination:\n{backupDir}",
            "Backup save")) return;

        var (mountedOk, weMounted) = await EnsureSaveMountedAsync(save);
        if (!mountedOk) return;

        try
        {
            UpdateStatus("Downloading decrypted save contents...");
            var progress = new Progress<DownloadFolderProgress>(p =>
            {
                if (p.TotalFiles > 0)
                    UpdateStatus($"Backup {save.SaveName}: {p.FilesCompleted}/{p.TotalFiles} files");
            });
            var result = await _protocol!.DownloadFolderAsync(
                PS5Protocol.SaveMountPoint, backupDir, _ps5IpAddress ?? "",
                progress, System.Threading.CancellationToken.None);
            await ShowMessageAsync(
                $"Backup complete!\n\nFiles: {result.filesDownloaded} (failed: {result.filesFailed})\nSize: {FormatSize(result.totalBytes)}\n\nLocation:\n{backupDir}",
                "Backup complete");
        }
        catch (Exception ex) { UpdateStatus($"Backup error: {ex.Message}"); }

        if (weMounted) await UnmountSaveAsync();
    }

    // ---------- Backup (raw image) ----------

    private async void SaveBackupRaw_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        if (SavesListBox.SelectedItem is not PS5SaveFile save)
        { UpdateStatus("Select a save first"); return; }

        string dir = Path.Combine(AppPaths.DownloadsDir, "PS5Suite", "save_backups_raw");
        Directory.CreateDirectory(dir);
        string local = Path.Combine(dir, Path.GetFileName(save.Path));

        if (!await ShowConfirmAsync(
            $"Download the raw (encrypted) save image?\n\n{save.SaveName}\n→ {local}",
            "Raw backup")) return;

        UpdateStatus($"Downloading {save.SaveName} (raw)...");
        try
        {
            bool ok = await _protocol!.DownloadFileAsync(save.Path, local,
                new Progress<UploadProgress>(p =>
                {
                    if (p.TotalBytes > 0)
                        UpdateStatus($"Downloading: {p.BytesSent * 100 / p.TotalBytes}%");
                }));
            UpdateStatus(ok ? $"Raw image saved: {local}" : $"Download failed: {_protocol.LastError}");
        }
        catch (Exception ex) { UpdateStatus($"Raw backup error: {ex.Message}"); }
    }

    // ---------- Restore (decrypted) ----------

    private async void SaveRestoreButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        if (SavesListBox.SelectedItem is not PS5SaveFile save)
        { UpdateStatus("Select a save first"); return; }

        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select backup folder containing decrypted save files"
        });
        if (folders.Count == 0) return;

        string stageRoot;
        string? localPath = folders[0].TryGetLocalPath();
        if (localPath != null && Directory.Exists(localPath))
        {
            stageRoot = localPath;
        }
        else
        {
            // SAF folder — stage it through the cache so we can upload by path.
            stageRoot = Path.Combine(AppPaths.CacheDir, "save_restore_stage");
            if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, true);
            Directory.CreateDirectory(stageRoot);
            UpdateStatus("Copying backup folder to staging...");
            try { await StageFolderAsync(folders[0], stageRoot); }
            catch (Exception ex) { UpdateStatus($"Stage failed: {ex.Message}"); return; }
        }

        if (!await ShowConfirmAsync(
            $"Restore decrypted files into {save.SaveName} ({save.TitleId})?\n\nFrom: {stageRoot}\n\nThe save is mounted, files are overwritten, then the image is written back.",
            "Restore save")) return;

        var (mountedOk, weMounted) = await EnsureSaveMountedAsync(save);
        if (!mountedOk) return;

        string mnt = PS5Protocol.SaveMountPoint;
        int uploaded = 0, failed = 0; long totalBytes = 0;
        try
        {
            var remoteDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = Directory.EnumerateFiles(stageRoot, "*", SearchOption.AllDirectories).ToList();
            foreach (var localFile in files)
            {
                string rel = Path.GetRelativePath(stageRoot, localFile).Replace('\\', '/');
                string? remoteDir = Path.GetDirectoryName(rel)?.Replace('\\', '/');
                if (string.IsNullOrEmpty(remoteDir)) continue;
                string cur = mnt;
                foreach (var seg in remoteDir.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    cur += "/" + seg;
                    if (remoteDirs.Add(cur))
                        try { await _protocol!.CreateDirAsync(cur); } catch { }
                }
            }
            foreach (var localFile in files)
            {
                string rel = Path.GetRelativePath(stageRoot, localFile).Replace('\\', '/');
                string remote = mnt + "/" + rel;
                try
                {
                    if (await _protocol!.UploadFileAsync(localFile, remote))
                    { uploaded++; totalBytes += new FileInfo(localFile).Length; }
                    else failed++;
                }
                catch { failed++; }
                UpdateStatus($"Restoring: {uploaded + failed}/{files.Count} files");
            }
            UpdateStatus($"Restore upload done: {uploaded} ok, {failed} failed, {FormatSize(totalBytes)} — writing back...");
        }
        catch (Exception ex) { UpdateStatus($"Restore error: {ex.Message}"); }

        if (weMounted) await UnmountSaveAsync();
    }

    // Recursively copy a picked SAF folder to a real local dir.
    private static async Task StageFolderAsync(IStorageFolder folder, string localDir)
    {
        Directory.CreateDirectory(localDir);
        await foreach (var item in folder.GetItemsAsync())
        {
            if (item is IStorageFile f)
            {
                string dest = Path.Combine(localDir, f.Name);
                await using var src = await f.OpenReadAsync();
                await using var dst = File.Create(dest);
                await src.CopyToAsync(dst);
            }
            else if (item is IStorageFolder sub)
            {
                await StageFolderAsync(sub, Path.Combine(localDir, sub.Name));
            }
        }
    }

    // ---------- Screenshot download ----------

    private async void ScreenshotDownload_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        if (ScreenshotsListBox.SelectedItem is not PS5Screenshot shot)
        { UpdateStatus("Select a screenshot first"); return; }
        string dir = Path.Combine(AppPaths.DownloadsDir, "PS5Suite", "screenshots");
        Directory.CreateDirectory(dir);
        string local = Path.Combine(dir, shot.FileName);
        try
        {
            bool ok = await _protocol!.DownloadFileAsync(shot.FullPath, local);
            UpdateStatus(ok ? $"Saved to {local}" : $"Download failed: {_protocol.LastError}");
        }
        catch (Exception ex) { UpdateStatus($"Download error: {ex.Message}"); }
    }

    // ---------- Power extras ----------

    private async void RestartUi_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        if (!await ShowConfirmAsync("Restart the PS5 shell UI (SceShellUI)?", "Restart UI")) return;
        var (ok, msg) = await _protocol!.SendTextCommandAsync(PS5Upload.Command.RestartUI, "");
        UpdateStatus(ok ? "Shell UI restarted" : $"Restart failed: {msg}");
    }
}
