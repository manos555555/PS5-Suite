using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PS5Upload;

namespace PS5SuiteAndroid.Views;

// File Transfer actions — upload / download / mkdir / rename / delete.
// Downloads land in AppPaths.DownloadsDir (Android/data/<pkg>/files on
// Android, user-browsable without permissions).
public partial class MainView
{
    private async void FilesRefresh_Click(object? sender, RoutedEventArgs e)
        => await RefreshFilesAsync();

    private async void FileUpload_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        var picked = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select file to upload",
            AllowMultiple = false
        });
        if (picked.Count == 0) return;

        var file = picked[0];
        string remotePath = _currentPath.TrimEnd('/') + "/" + file.Name;
        UpdateStatus($"Uploading {file.Name}...");

        // On Android the picker may return a content:// stream without a
        // local path — stage it through the cache dir.
        string? localPath = file.TryGetLocalPath();
        string? staged = null;
        try
        {
            if (localPath == null || !File.Exists(localPath))
            {
                staged = Path.Combine(AppPaths.CacheDir, file.Name);
                await using var src = await file.OpenReadAsync();
                await using var dst = File.Create(staged);
                await src.CopyToAsync(dst);
                localPath = staged;
            }

            var progress = new Progress<UploadProgress>(p =>
            {
                if (p.TotalBytes > 0)
                    UpdateStatus($"Uploading {file.Name}: {p.BytesSent * 100 / p.TotalBytes}%");
            });

            bool ok = await _protocol!.UploadFileAsync(localPath, remotePath, progress);
            UpdateStatus(ok ? $"Uploaded: {remotePath}" : $"Upload failed: {_protocol.LastError}");
            if (ok) await RefreshFilesAsync();
        }
        catch (Exception ex) { UpdateStatus($"Upload error: {ex.Message}"); }
        finally { if (staged != null) try { File.Delete(staged); } catch { } }
    }

    private async void FileDownload_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        if (FilesListBox.SelectedItem is not FileItem item) { UpdateStatus("Select a file first"); return; }
        if (item.IsDirectory) { UpdateStatus("Folder download: use the Backup flow in Saves"); return; }

        string localDir = Path.Combine(AppPaths.DownloadsDir, "PS5Suite");
        Directory.CreateDirectory(localDir);
        string localPath = Path.Combine(localDir, item.Name);
        UpdateStatus($"Downloading {item.Name}...");
        try
        {
            var progress = new Progress<UploadProgress>(p =>
            {
                if (p.TotalBytes > 0)
                    UpdateStatus($"Downloading {item.Name}: {p.BytesSent * 100 / p.TotalBytes}%");
            });
            bool ok = await _protocol!.DownloadFileAsync(item.FullPath, localPath, progress);
            UpdateStatus(ok ? $"Saved to {localPath}" : $"Download failed: {_protocol.LastError}");
        }
        catch (Exception ex) { UpdateStatus($"Download error: {ex.Message}"); }
    }

    private async void FileNewFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        var name = await ShowInputAsync("New Folder", $"Create folder inside {_currentPath}");
        if (string.IsNullOrWhiteSpace(name)) return;
        string path = _currentPath.TrimEnd('/') + "/" + name.Trim();
        try
        {
            bool ok = await _protocol!.CreateDirAsync(path);
            UpdateStatus(ok ? $"Created {path}" : $"Failed: {_protocol.LastError}");
            if (ok) await RefreshFilesAsync();
        }
        catch (Exception ex) { UpdateStatus($"Mkdir error: {ex.Message}"); }
    }

    private async void FileRename_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        if (FilesListBox.SelectedItem is not FileItem item) { UpdateStatus("Select an item first"); return; }
        var name = await ShowInputAsync("Rename", $"Rename \"{item.Name}\" to:", item.Name);
        if (string.IsNullOrWhiteSpace(name) || name == item.Name) return;
        string newPath = _currentPath.TrimEnd('/') + "/" + name.Trim();
        try
        {
            bool ok = await _protocol!.RenameAsync(item.FullPath, newPath);
            UpdateStatus(ok ? $"Renamed to {name}" : $"Rename failed: {_protocol.LastError}");
            if (ok) await RefreshFilesAsync();
        }
        catch (Exception ex) { UpdateStatus($"Rename error: {ex.Message}"); }
    }

    private async void FileDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireConnection()) return;
        if (FilesListBox.SelectedItem is not FileItem item) { UpdateStatus("Select an item first"); return; }
        string kind = item.IsDirectory ? "folder (recursively)" : "file";
        if (!await ShowConfirmAsync($"Delete {kind} \"{item.Name}\"?\n\n{item.FullPath}", "Confirm delete")) return;
        try
        {
            bool ok = item.IsDirectory
                ? await _protocol!.DeleteDirAsync(item.FullPath)
                : await _protocol!.DeleteFileAsync(item.FullPath);
            UpdateStatus(ok ? $"Deleted {item.Name}" : $"Delete failed: {_protocol.LastError}");
            if (ok) await RefreshFilesAsync();
        }
        catch (Exception ex) { UpdateStatus($"Delete error: {ex.Message}"); }
    }
}
