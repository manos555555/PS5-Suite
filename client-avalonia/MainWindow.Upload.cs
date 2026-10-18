using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace PS5Upload
{
    public partial class MainWindow
    {
        // IProgress that runs Report() inline on the caller's thread instead
        // of marshaling to the captured SynchronizationContext. Progress<T>
        // was flooding the UI thread with a callback per send-buffer flush ×
        // 27+ parallel lanes — the window froze (wait cursor, dead tabs).
        private sealed class InlineProgress<T> : IProgress<T>
        {
            private readonly Action<T> _action;
            public InlineProgress(Action<T> action) => _action = action;
            public void Report(T value) => _action(value);
        }

        private void UiUpdateTimer_Tick(object? sender, EventArgs e)
        {
            int completed;
            lock (_progressLock) { completed = _completedFiles; }
            UpdateUploadStats(completed, _activeTaskCount);
            FlushLog();
            FlushPendingHistory();
        }

        // Move queued transfer-history items into the bound collections in
        // one batch (bounded at 2000 entries so the list can't grow forever).
        private readonly List<TransferHistoryItem> _pendingHistory = new();
        private readonly object _pendingHistoryLock = new();
        private void FlushPendingHistory()
        {
            List<TransferHistoryItem>? pending = null;
            lock (_pendingHistoryLock)
            {
                if (_pendingHistory.Count > 0)
                {
                    pending = new List<TransferHistoryItem>(_pendingHistory);
                    _pendingHistory.Clear();
                }
            }
            if (pending == null) return;
            foreach (var it in pending)
            {
                var target = it.Status.StartsWith("❌") ? _failedTransfers : _completedTransfers;
                target.Add(it);
                while (target.Count > 2000) target.RemoveAt(0);
            }
        }

        private void UpdateUploadStats(int completedFiles, int activeTaskCount)
        {
            Dispatcher.UIThread.Post(() =>
            {
                int remainingFiles = Math.Max(0, _totalFilesToUpload - completedFiles);
                FilesRemainingText.Text = $"Files: {completedFiles} / {_totalFilesToUpload} ({remainingFiles} remaining)";

                double totalPercent = _totalBytesToUpload > 0 ? (double)_totalBytesUploaded / _totalBytesToUpload * 100 : 0;
                TotalProgressBar.Value = totalPercent;
                TotalProgressText.Text = $"Total: {completedFiles} / {_totalFilesToUpload} files ({FormatFileSize(_totalBytesUploaded)} / {FormatFileSize(_totalBytesToUpload)}) [{totalPercent:F1}%]";

                var elapsed = DateTime.Now - _uploadStartTime;
                long currentBytes = Interlocked.Read(ref _totalBytesUploaded);
                var now = DateTime.Now;
                _speedWindowBytes[_speedWindowIndex] = currentBytes;
                _speedWindowTimes[_speedWindowIndex] = now;
                _speedWindowIndex = (_speedWindowIndex + 1) % SpeedWindowSize;
                if (_speedWindowCount < SpeedWindowSize) _speedWindowCount++;

                double realtimeSpeed = 0;
                if (_speedWindowCount >= 2)
                {
                    int oldestIndex = (_speedWindowIndex - _speedWindowCount + SpeedWindowSize) % SpeedWindowSize;
                    long bytesDelta = currentBytes - _speedWindowBytes[oldestIndex];
                    double timeDelta = (now - _speedWindowTimes[oldestIndex]).TotalSeconds;
                    if (timeDelta > 0.1) realtimeSpeed = bytesDelta / timeDelta;
                }

                if (_currentSpeed <= 0) _currentSpeed = realtimeSpeed;
                else if (realtimeSpeed > 0) _currentSpeed = (_currentSpeed * 0.7) + (realtimeSpeed * 0.3);

                double avgSpeed = elapsed.TotalSeconds > 0 ? currentBytes / elapsed.TotalSeconds : 0;
                double etaSpeed = (_currentSpeed * 0.6) + (avgSpeed * 0.4);
                long remainingBytes = _totalBytesToUpload - currentBytes;
                TimeSpan rawETA = etaSpeed > 0 ? TimeSpan.FromSeconds(remainingBytes / etaSpeed) : TimeSpan.Zero;

                if (_smoothedETA == TimeSpan.Zero) _smoothedETA = rawETA;
                else if (rawETA.TotalSeconds > 0)
                {
                    double smoothedSeconds = (_smoothedETA.TotalSeconds * (1 - ETASmoothingFactor)) + (rawETA.TotalSeconds * ETASmoothingFactor);
                    _smoothedETA = TimeSpan.FromSeconds(Math.Max(0, smoothedSeconds));
                }
                TimeSpan eta = _smoothedETA;

                UploadSpeedText.Text = $"Speed: {FormatFileSize((long)_currentSpeed)}/s (avg {FormatFileSize((long)avgSpeed)}/s) | {activeTaskCount} active";
                string etaStr = eta.TotalHours >= 1 ? $"{(int)eta.TotalHours}h {eta.Minutes:D2}m {eta.Seconds:D2}s"
                    : eta.TotalMinutes >= 1 ? $"{(int)eta.TotalMinutes}m {eta.Seconds:D2}s" : $"{eta.Seconds}s";
                UploadETAText.Text = $"ETA: {etaStr} | Elapsed: {elapsed:hh\\:mm\\:ss}";
                if (activeTaskCount > 0) UploadFileNameText.Text = $"Uploading {activeTaskCount} files in parallel...";

                // Per-file bar — the progress callbacks only update these
                // fields now; the timer paints them (no per-report UI posts).
                long fb = Interlocked.Read(ref _currentFileBytes);
                long ft = Interlocked.Read(ref _currentFileTotalBytes);
                double fpct = ft > 0 ? (double)fb / ft * 100 : 0;
                UploadProgressBar.Value = fpct;
                UploadProgressText.Text = $"{FormatFileSize(fb)} / {FormatFileSize(ft)} ({fpct:F1}%)";
            });
        }

        private async void UploadButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_localFiles.Count == 0) { await ShowMessageAsync("No files selected for upload"); return; }
            if (!_protocol.IsConnected) { await ShowMessageAsync("Not connected to PS5", "Error"); return; }

            // Queued a WHOLE NAS share? Enumerating it recursively can mean
            // thousands of files over SMB — confirm before burning the time.
            var shareRoots = _localFiles.Where(f => f.IsDirectory && LocalIo.IsUnc(f.FullPath)
                && NasManager.Normalize(f.FullPath).TrimEnd('\\').Equals(NasManager.ShareRoot(f.FullPath), StringComparison.OrdinalIgnoreCase)).ToList();
            if (shareRoots.Count > 0)
            {
                if (!await ShowConfirmAsync(
                    "You're uploading ENTIRE share(s):\n  " + string.Join("\n  ", shareRoots.Select(f => f.FullPath)) +
                    "\n\nThis will enumerate every file on the share (can take a while).\nTip: double-click it and pick the files/folders you actually want.\n\nContinue anyway?",
                    "Upload Entire Share?"))
                    return;
            }

            UploadButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
            ProgressPanel.IsVisible = true;
            _uploadCancellation = new CancellationTokenSource();

            Log("========== UPLOAD STARTED ==========");
            var localFilesCopy = _localFiles.ToList();
            string currentPath = _currentPS5Path;
            UploadFileNameText.Text = "Preparing upload...";
            TotalProgressText.Text = "Collecting files...";

            Log("Collecting files...");
            var ct = _uploadCancellation.Token;
            var allFiles = await Task.Run(() =>
            {
                var files = new List<(string localPath, string remotePath)>();
                foreach (var item in localFilesCopy)
                {
                    string targetBasePath = item.RemotePathOverride ?? (currentPath + "/" + item.Name);
                    if (item.IsDirectory) CollectFilesFromDirectory(item.FullPath, targetBasePath, files, ct);
                    else files.Add((item.FullPath, targetBasePath));
                }
                return files;
            }, ct);

            Log("Checking for existing files...");
            var filesToUpload = await FilterDuplicateFilesAsync(allFiles);
            if (filesToUpload.Count == 0)
            {
                Log("⚠️ No files to upload (all skipped)");
                await ShowMessageAsync("No files to upload. All files were skipped.");
                UploadButton.IsEnabled = true; CancelButton.IsEnabled = false; ProgressPanel.IsVisible = false;
                return;
            }
            allFiles = filesToUpload;

            _totalFilesToUpload = allFiles.Count;
            _totalBytesToUpload = await Task.Run(() => { long total = 0; foreach (var f in allFiles) { try { total += LocalIo.GetLength(f.localPath); } catch { } } return total; });
            _totalBytesUploaded = 0; _completedFiles = 0; _uploadStartTime = DateTime.Now;
            _fileProgressBytes.Clear(); _fileChunkProgressBytes.Clear(); _chunkLogLastBytes.Clear();
            _smoothedETA = TimeSpan.Zero; _speedWindowIndex = 0; _speedWindowCount = 0; _currentSpeed = 0;
            _smallFileBatchRemainder = 0; _smallFileCompletedTotal = 0; _smallFileBatchBytes = 0; _smallFileTotalBytes = 0;
            _activeLargeUploads = 0; _activeHugeUploads = 0;
            DrainConnectionPool();
            Log($"📊 Total: {_totalFilesToUpload} files, {FormatFileSize(_totalBytesToUpload)}");
            TotalProgressText.Text = $"Total: 0 / {_totalFilesToUpload} files ({FormatFileSize(0)} / {FormatFileSize(_totalBytesToUpload)})";
            TotalProgressBar.Value = 0;

            try
            {
                // Create directories
                Log("Creating directories...");
                var directories = allFiles.Select(f => Path.GetDirectoryName(f.remotePath)?.Replace("\\", "/"))
                    .Where(d => !string.IsNullOrEmpty(d) && d != _currentPS5Path).Distinct().OrderBy(d => d?.Length ?? 0).ToList();

                if (!_protocol.IsConnected)
                {
                    Log("⚠️ Connection lost before directory creation, reconnecting...");
                    if (!await _protocol.ConnectAsync(_ps5IpAddress)) throw new Exception("Failed to reconnect to PS5 before directory creation");
                    Log("✅ Reconnected successfully");
                }

                int dirCreated = 0, dirTotal = directories.Count;
                int dirLogInterval = Math.Max(1, dirTotal / 10);
                foreach (var dir in directories)
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    if (dirCreated % dirLogInterval == 0 || dirCreated == 0)
                        Dispatcher.UIThread.Post(() => UploadFileNameText.Text = $"Creating directories... ({dirCreated}/{dirTotal})");
                    try
                    {
                        using var dirCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        var dirTask = _protocol.CreateDirAsync(dir);
                        var doneTask = await Task.WhenAny(dirTask, Task.Delay(-1, dirCts.Token));
                        if (doneTask != dirTask) throw new TimeoutException($"Timeout creating directory: {dir}");
                        await dirTask;
                    }
                    catch (Exception ex)
                    {
                        Log($"⚠️ Failed to create dir {dir}: {ex.Message}, retrying...");
                        try { _protocol.Disconnect(); await Task.Delay(500); if (await _protocol.ConnectAsync(_ps5IpAddress)) await _protocol.CreateDirAsync(dir); else throw new Exception("Reconnect failed"); }
                        catch (Exception retryEx) { Log($"❌ Directory creation failed permanently: {dir} - {retryEx.Message}"); }
                    }
                    dirCreated++;
                }
                Log($"✅ Created {dirCreated}/{dirTotal} directories");

                if (!_protocol.IsConnected)
                {
                    Log("⚠️ Connection lost after directory creation, reconnecting...");
                    if (!await _protocol.ConnectAsync(_ps5IpAddress)) throw new Exception("Failed to reconnect to PS5 after directory creation");
                    Log("✅ Reconnected successfully");
                }

                _uiUpdateTimer.Start();

                // Pre-warm connection pool
                int preWarmCount = Math.Min(MaxParallelUploads - 1, allFiles.Count);
                Log($"🚀 Starting parallel upload with {MaxParallelUploads} connections (pre-warming {preWarmCount})...");
                var preWarmTasks = new List<Task<PS5Protocol?>>();
                for (int i = 0; i < preWarmCount; i++)
                    preWarmTasks.Add(Task.Run(async () => { try { var c = new PS5Protocol(); if (await c.ConnectAsync(_ps5IpAddress)) { Interlocked.Increment(ref _currentPoolConnections); return c; } c.Dispose(); } catch { } return (PS5Protocol?)null; }));
                await Task.WhenAll(preWarmTasks);
                int pooled = 0;
                foreach (var t in preWarmTasks) { if (t.Result != null) { _connectionPool.Enqueue(t.Result); pooled++; } }
                Log($"✅ {pooled} connections ready");

                // Parallel upload loop
                var fileQueue = new Queue<(string localPath, string remotePath)>(allFiles);
                var activeTasks = new List<Task>();
                var taskToConnection = new Dictionary<Task, PS5Protocol>();
                var taskToFilePath = new Dictionary<Task, string>();
                var taskToRemotePath = new Dictionary<Task, string>();
                var fileChunkCounts = new Dictionary<string, int>();
                var fileChunksCompleted = new Dictionary<string, int>();
                var completedFilesSet = new HashSet<string>();
                var failedFiles = new Dictionary<string, int>();
                var taskIsLargeFile = new Dictionary<Task, bool>();
                var taskIsHugeFile = new Dictionary<Task, bool>();
                const int MAX_RETRIES = 3;

                UploadFileNameText.Text = $"Parallel upload: {MaxParallelUploads} connections";

                while (fileQueue.Count > 0 || activeTasks.Count > 0)
                {
                    if (_uploadCancellation.Token.IsCancellationRequested) { Log("⚠️ Upload cancelled by user"); break; }

                    while (fileQueue.Count > 0 && activeTasks.Count < MaxParallelUploads)
                    {
                        var (localPath, remotePath) = fileQueue.Dequeue();
                        long fileSize = LocalIo.GetLength(localPath);
                        bool isLargeFile = fileSize > LargeFileThresholdBytes;
                        bool isHugeFile = fileSize >= HugeFileThresholdBytes;

                        if (isLargeFile && Volatile.Read(ref _activeLargeUploads) >= MaxParallelLargeFiles) { fileQueue.Enqueue((localPath, remotePath)); break; }
                        if (isHugeFile && Volatile.Read(ref _activeHugeUploads) >= MaxParallelHugeFiles) { fileQueue.Enqueue((localPath, remotePath)); break; }

                        PS5Protocol connection;
                        try { connection = await AcquireUploadConnectionAsync(); }
                        catch (Exception ex) { Log($"❌ Unable to acquire connection: {ex.Message}. Requeueing {Path.GetFileName(localPath)}"); fileQueue.Enqueue((localPath, remotePath)); await Task.Delay(1000); break; }

                        if (isLargeFile) Interlocked.Increment(ref _activeLargeUploads);
                        if (isHugeFile) Interlocked.Increment(ref _activeHugeUploads);

                        var task = UploadFileParallelAsync(connection, localPath, remotePath, _uploadCancellation.Token);
                        activeTasks.Add(task); taskToConnection[task] = connection; taskToFilePath[task] = localPath; taskToRemotePath[task] = remotePath;
                        _activeTaskCount = activeTasks.Count;
                        taskIsLargeFile[task] = isLargeFile; taskIsHugeFile[task] = isHugeFile;
                        fileChunkCounts[localPath] = 1; fileChunksCompleted[localPath] = 0;
                    }

                    if (activeTasks.Count > 0)
                    {
                        var completedTask = await Task.WhenAny(activeTasks);
                        bool taskSucceeded = false;
                        try { await completedTask; taskSucceeded = true; }
                        catch (Exception ex)
                        {
                            Log($"❌ Task exception: {ex.Message}");
                            if (taskToFilePath.TryGetValue(completedTask, out var fp) && taskToRemotePath.TryGetValue(completedTask, out var rp))
                            {
                                if (!failedFiles.ContainsKey(fp)) failedFiles[fp] = 0;
                                failedFiles[fp]++;
                                if (failedFiles[fp] <= MAX_RETRIES)
                                {
                                    Log($"🔄 Requeueing ({failedFiles[fp]}/{MAX_RETRIES}): {Path.GetFileName(fp)}");
                                    try { await _protocol.DeleteFileAsync(rp); } catch { }
                                    fileQueue.Enqueue((fp, rp)); fileChunksCompleted[fp] = 0;
                                    await Task.Delay(3000);
                                }
                                else Log($"❌ Max retries exceeded: {Path.GetFileName(fp)}");
                            }
                            Dispatcher.UIThread.Post(() => UploadFileNameText.Text = $"Upload error: {ex.Message}");
                        }

                        if (taskToFilePath.TryGetValue(completedTask, out var filePath))
                        {
                            if (taskSucceeded)
                            {
                                lock (_progressLock)
                                {
                                    fileChunksCompleted[filePath]++;
                                    if (fileChunksCompleted[filePath] >= fileChunkCounts[filePath] && !completedFilesSet.Contains(filePath))
                                    {
                                        completedFilesSet.Add(filePath);
                                        int completed = Interlocked.Increment(ref _completedFiles);
                                        Log($"✅ File {completed}/{_totalFilesToUpload} completed");
                                    }
                                }
                            }
                            taskToFilePath.Remove(completedTask); taskToRemotePath.Remove(completedTask);
                        }

                        activeTasks.Remove(completedTask);
                        if (taskToConnection.TryGetValue(completedTask, out var completedConn))
                        {
                            taskToConnection.Remove(completedTask);
                            if (taskIsLargeFile.TryGetValue(completedTask, out var wasLarge) && wasLarge) { Interlocked.Decrement(ref _activeLargeUploads); taskIsLargeFile.Remove(completedTask); }
                            if (taskIsHugeFile.TryGetValue(completedTask, out var wasHuge) && wasHuge) { Interlocked.Decrement(ref _activeHugeUploads); taskIsHugeFile.Remove(completedTask); }
                            if (taskSucceeded && completedConn.IsConnected) ReleaseUploadConnection(completedConn); else DestroyConnection(completedConn);
                        }
                        _activeTaskCount = activeTasks.Count;
                        // No UpdateUploadStats here — the 500ms UI timer
                        // refreshes stats; a UI post per completed file was
                        // another flood source on 100k+ uploads.
                    }
                    else if (fileQueue.Count > 0) { Log($"⚠️ No active tasks but {fileQueue.Count} files remain - retrying..."); await Task.Delay(500); }
                }

                Log("🔄 Upload loop finished"); FlushSmallFileBatch(); _uiUpdateTimer.Stop();
                foreach (var conn in taskToConnection.Values.ToList()) { try { conn.Disconnect(); conn.Dispose(); } catch { } }
                taskToConnection.Clear(); DrainConnectionPool(); Log("✅ Cleanup complete");

                if (!_uploadCancellation.Token.IsCancellationRequested)
                {
                    Log($"🎉 Upload completed! {_totalFilesToUpload} files, {FormatFileSize(_totalBytesUploaded)}");
                    await ShowMessageAsync($"Upload completed!\n\n{_totalFilesToUpload} files uploaded\nTotal: {FormatFileSize(_totalBytesUploaded)}", "Success");
                    _localFiles.Clear();
                }
                else { Log("⚠️ Upload cancelled"); await ShowMessageAsync("Upload cancelled by user", "Cancelled"); }

                // Reconnect if needed
                if (!_protocol.IsConnected) { try { if (await _protocol.ConnectAsync(_ps5IpAddress)) { Log("✅ Reconnected"); await LoadPS5DirectoryAsync(_currentPS5Path); } } catch { } }
                else { Log("✅ Main connection still active"); await LoadPS5DirectoryAsync(_currentPS5Path); }
            }
            catch (OperationCanceledException) { Log("❌ Upload cancelled"); await ShowMessageAsync("Upload cancelled by user", "Cancelled"); if (!_protocol.IsConnected) { try { if (await _protocol.ConnectAsync(_ps5IpAddress)) await LoadPS5DirectoryAsync(_currentPS5Path); } catch { } } else await LoadPS5DirectoryAsync(_currentPS5Path); }
            catch (Exception ex) { Log($"❌ Upload failed: {ex.Message}\n{ex.StackTrace}"); await ShowMessageAsync($"Upload failed: {ex.Message}", "Error"); }
            finally
            {
                Log("========== UPLOAD FINISHED ==========");
                FlushLog();
                _uiUpdateTimer.Stop();
                FlushPendingHistory();
                ProgressPanel.IsVisible = false;
                UploadButton.IsEnabled = true; CancelButton.IsEnabled = false;
                _uploadCancellation?.Dispose(); _uploadCancellation = null;
                await LoadPS5DirectoryAsync(_currentPS5Path);
            }
        }

        private void CancelButton_Click(object? sender, RoutedEventArgs e) { _uploadCancellation?.Cancel(); CancelButton.IsEnabled = false; }

        private async Task UploadFileParallelAsync(PS5Protocol connection, string localPath, string remotePath, CancellationToken cancellationToken)
        {
            try
            {
                string fileName = Path.GetFileName(localPath);
                long fileLen = LocalIo.GetLength(localPath);

                if (fileLen > ChunkThresholdBytes)
                {
                    int maxParallelChunks = fileLen >= HugeFileThresholdBytes ? MaxParallelChunksForHugeFile : MaxParallelChunksForLargeFile;
                    long chunkSize = fileLen >= HugeFileThresholdBytes ? HugeFileChunkSizeBytes : DefaultChunkSizeBytes;

                    // Scale chunk size so medium files still get parallel lanes:
                    // a 140MB file shouldn't fall back to 1 chunk / 1 lane while
                    // a 10GB file grabs 10 workers.
                    long scaledChunk = fileLen / maxParallelChunks;
                    long minChunk = 64L * 1024 * 1024;
                    if (scaledChunk < minChunk) scaledChunk = minChunk;
                    if (scaledChunk < chunkSize) chunkSize = scaledChunk;

                    long totalChunks = (fileLen + chunkSize - 1) / chunkSize;
                    int workerCount = (int)Math.Min(totalChunks, Math.Max(1, maxParallelChunks));
                    Log($"⬆️ Uploading (chunked): {fileName} ({FormatFileSize(fileLen)}) {workerCount} lanes");
                    _fileProgressBytes[localPath] = 0;
                    _fileChunkProgressBytes[localPath] = new ConcurrentDictionary<long, long>();
                    _chunkLogLastBytes[localPath] = 0;

                    int nextChunkIndex = -1;
                    var chunk0Ready = new SemaphoreSlim(0, 1);
                    bool chunk0Released = false;
                    var chunk0ReleaseLock = new object();

                    // Release the chunk-0 gate exactly once, no matter whether
                    // chunk 0 succeeded or failed. Without this, a failed first
                    // chunk deadlocked every other worker forever.
                    void ReleaseChunk0Once()
                    {
                        lock (chunk0ReleaseLock)
                        {
                            if (!chunk0Released)
                            {
                                chunk0Released = true;
                                chunk0Ready.Release();
                            }
                        }
                    }

                    var workerTasks = new List<Task>(workerCount);
                    for (int wid = 0; wid < workerCount; wid++) workerTasks.Add(RunWorker(wid));
                    await Task.WhenAll(workerTasks);

                    async Task RunWorker(int workerId)
                    {
                        PS5Protocol? wConn = null; bool owns = workerId != 0;
                        try
                        {
                            wConn = owns ? await AcquireUploadConnectionAsync() : connection;
                            while (true)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                int ci = Interlocked.Increment(ref nextChunkIndex);
                                if (ci >= totalChunks) break;
                                if (ci > 0) { await chunk0Ready.WaitAsync(cancellationToken); chunk0Ready.Release(); }
                                Action? cb = ci == 0 ? ReleaseChunk0Once : null;
                                try
                                {
                                    await DoChunk(wConn!, ci, cb);
                                }
                                finally
                                {
                                    // Guarantee the gate opens even if chunk 0 throws
                                    if (ci == 0) ReleaseChunk0Once();
                                }
                            }
                        }
                        finally { if (owns && wConn != null) { if (wConn.IsConnected) ReleaseUploadConnection(wConn); else DestroyConnection(wConn); } }
                    }

                    async Task DoChunk(PS5Protocol wConn, int chunkIndex, Action? readyCallback)
                    {
                        long offset = chunkIndex * chunkSize;
                        long size = Math.Min(chunkSize, fileLen - offset);
                        long humanIdx = chunkIndex + 1;
                        var prog = MakeChunkProgress(offset, size, humanIdx, totalChunks);
                        bool ok = await wConn.UploadFileAsync(localPath, remotePath, prog, cancellationToken, offset, size, readyCallback);
                        if (!ok) throw new Exception($"Chunk {humanIdx}/{totalChunks} failed for {fileName}");
                        if (_fileChunkProgressBytes.TryGetValue(localPath, out var cm)) { cm[offset] = size; long agg = cm.Values.Sum(); long prev = _fileProgressBytes.GetOrAdd(localPath, 0); long d = agg - prev; if (d > 0) { Interlocked.Add(ref _totalBytesUploaded, d); _fileProgressBytes[localPath] = agg; } _currentFileBytes = agg; _currentFileTotalBytes = fileLen; }
                    }

                    IProgress<UploadProgress> MakeChunkProgress(long chunkOffset, long chunkLength, long chunkNumber, long totalChunkCount)
                    {
                        int callCount = 0;
                        // Runs on the worker thread — counters + display fields
                        // only, no UI posts (the 500ms timer paints the bar).
                        return new InlineProgress<UploadProgress>(p =>
                        {
                            callCount++;
                            long sent = p.BytesSent - chunkOffset; if (sent < 0) sent = 0; if (sent > chunkLength) sent = chunkLength;
                            var map = _fileChunkProgressBytes.GetOrAdd(localPath, _ => new ConcurrentDictionary<long, long>());
                            map[chunkOffset] = sent;
                            if (callCount % 2 != 0 && sent != chunkLength) return;
                            long agg = map.Values.Sum();
                            long prev = _fileProgressBytes.GetOrAdd(localPath, 0); long delta = agg - prev;
                            if (delta != 0) { Interlocked.Add(ref _totalBytesUploaded, delta); _fileProgressBytes[localPath] = agg; }
                            _currentFileName = fileName;
                            Interlocked.Exchange(ref _currentFileBytes, agg);
                            Interlocked.Exchange(ref _currentFileTotalBytes, fileLen);
                        });
                    }
                }
                else
                {
                    int callCount = 0;
                    var progress = new InlineProgress<UploadProgress>(p =>
                    {
                        callCount++;
                        if (callCount % 2 != 0 && p.BytesSent != p.TotalBytes) return;
                        _currentFileName = fileName;
                        Interlocked.Exchange(ref _currentFileBytes, p.BytesSent);
                        Interlocked.Exchange(ref _currentFileTotalBytes, fileLen);
                        long prev = _fileProgressBytes.GetOrAdd(localPath, 0); long add = p.BytesSent - prev;
                        if (add > 0) { Interlocked.Add(ref _totalBytesUploaded, add); _fileProgressBytes[localPath] = p.BytesSent; }
                    });
                    bool success = await connection.UploadFileAsync(localPath, remotePath, progress, cancellationToken);
                    if (!success)
                    {
                        string detail = connection.LastError;
                        throw new Exception(string.IsNullOrEmpty(detail) ? $"Upload failed for {fileName}" : $"Upload failed for {fileName}: {detail}");
                    }
                    long prevB = _fileProgressBytes.GetOrAdd(localPath, 0); long d2 = fileLen - prevB;
                    if (d2 > 0) { Interlocked.Add(ref _totalBytesUploaded, d2); _fileProgressBytes[localPath] = fileLen; }
                }

                _fileProgressBytes.TryRemove(localPath, out _); _fileChunkProgressBytes.TryRemove(localPath, out _); _chunkLogLastBytes.TryRemove(localPath, out _);
                if (fileLen < 10 * 1024 * 1024) TrackSmallFileCompletion(fileName, fileLen); else Log($"✅ Upload complete: {fileName}");
                // Batch into pending list — the UI timer drains them. A post +
                // collection-change + layout pass per file froze the window
                // on 100k-file uploads.
                lock (_pendingHistoryLock)
                    _pendingHistory.Add(new TransferHistoryItem { FileName = fileName, Status = "✅ Completed", Size = FormatFileSize(fileLen), Timestamp = DateTime.Now });
            }
            catch (Exception ex)
            {
                string fn = Path.GetFileName(localPath);
                Log($"❌ Exception uploading {fn}: {ex.Message}");
                _fileProgressBytes.TryRemove(localPath, out _); _fileChunkProgressBytes.TryRemove(localPath, out _); _chunkLogLastBytes.TryRemove(localPath, out _);
                long failSize = 0; try { failSize = LocalIo.GetLength(localPath); } catch { }
                lock (_pendingHistoryLock)
                    _pendingHistory.Add(new TransferHistoryItem { FileName = fn, Status = "❌ Failed", Size = FormatFileSize(failSize), Timestamp = DateTime.Now, LocalPath = localPath, RemotePath = remotePath });
                throw;
            }
        }

        private async Task<PS5Protocol> AcquireUploadConnectionAsync()
        {
            while (_connectionPool.TryDequeue(out var p)) { if (p.IsConnected) return p; DestroyConnection(p); }
            var c = new PS5Protocol();
            for (int i = 0; i < 3; i++)
            {
                if (await c.ConnectAsync(_ps5IpAddress))
                {
                    // Verify the session was actually accepted. When the server
                    // is over MAX_CLIENT_SESSIONS it replies RESP_ERROR and
                    // closes — a bare TCP connect still "succeeds" and poisons
                    // the first command with the stale error frame.
                    try { if (await c.PingAsync()) { Interlocked.Increment(ref _currentPoolConnections); return c; } }
                    catch { }
                    Log($"⚠️ Connection rejected by PS5 (session cap?) - retrying ({i + 1}/3)...");
                    c.Disconnect();
                }
                await Task.Delay(1000);
            }
            c.Dispose(); throw new InvalidOperationException("Unable to acquire upload connection");
        }

        private void ReleaseUploadConnection(PS5Protocol c) { if (!c.IsConnected) { DestroyConnection(c); return; } _connectionPool.Enqueue(c); }
        private void DestroyConnection(PS5Protocol c) { try { c.Disconnect(); c.Dispose(); } catch { } finally { Interlocked.Decrement(ref _currentPoolConnections); } }
        private void DrainConnectionPool() { while (_connectionPool.TryDequeue(out var c)) DestroyConnection(c); }

        private void CollectFilesFromDirectory(string localDir, string remoteDir, List<(string localPath, string remotePath)> files, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var e in LocalIo.Enumerate(localDir))
            {
                ct.ThrowIfCancellationRequested();
                if (e.IsDirectory) CollectFilesFromDirectory(e.FullPath, remoteDir + "/" + e.Name, files, ct);
                else
                {
                    files.Add((e.FullPath, remoteDir + "/" + e.Name));
                    // Live collect counter — recursive NAS enumeration over
                    // thousands of files must not look like a freeze.
                    if (files.Count % 200 == 0)
                    {
                        int n = files.Count;
                        Dispatcher.UIThread.Post(() =>
                        {
                            TotalProgressText.Text = $"Collecting files... ({n} found)";
                            UploadFileNameText.Text = e.Name;
                        });
                    }
                }
            }
        }

        private void TrackSmallFileCompletion(string fileName, long fileSize)
        {
            lock (_smallFileLogLock) { _smallFileCompletedTotal++; _smallFileTotalBytes += fileSize; _smallFileBatchRemainder++; _smallFileBatchBytes += fileSize; if (_smallFileBatchRemainder >= SmallFileLogBatchSize) { Log($"✅ {SmallFileLogBatchSize} small files completed (batch {FormatFileSize(_smallFileBatchBytes)}, total {FormatFileSize(_smallFileTotalBytes)})"); _smallFileBatchRemainder = 0; _smallFileBatchBytes = 0; } }
        }

        private void FlushSmallFileBatch()
        {
            lock (_smallFileLogLock) { if (_smallFileBatchRemainder > 0) { Log($"✅ {_smallFileBatchRemainder} small files completed (batch {FormatFileSize(_smallFileBatchBytes)}, total {FormatFileSize(_smallFileTotalBytes)})"); _smallFileBatchRemainder = 0; _smallFileBatchBytes = 0; } }
        }

        private async Task<List<(string localPath, string remotePath)>> FilterDuplicateFilesAsync(List<(string localPath, string remotePath)> allFiles)
        {
            var filesToUpload = new List<(string localPath, string remotePath)>();
            _duplicateAction = DuplicateAction.Ask;
            var filesByDir = allFiles.GroupBy(f => Path.GetDirectoryName(f.remotePath)?.Replace("\\", "/") ?? "").ToList();
            Log($"🔍 Checking {filesByDir.Count} directories for duplicates...");

            bool uploadsIdle = (_uploadCancellation == null || _uploadCancellation.IsCancellationRequested) && _activeTaskCount == 0;
            PS5Protocol? dupConn = null; bool disposeDup = false;
            if (!uploadsIdle) { dupConn = new PS5Protocol(); if (await dupConn.ConnectAsync(_ps5IpAddress)) disposeDup = true; else dupConn = null; }

            int dirIndex = 0; const int LogInterval = 50;
            foreach (var dirGroup in filesByDir)
            {
                dirIndex++; string remoteDir = dirGroup.Key;
                Dictionary<string, long> existingFiles;
                try
                {
                    if (dirIndex == 1 || dirIndex % LogInterval == 0 || dirIndex == filesByDir.Count) Log($"📂 Checking dir ({dirIndex}/{filesByDir.Count}): {remoteDir}");
                    var proto = dupConn ?? _protocol;
                    var entries = await proto.ListDirAsync(remoteDir);
                    existingFiles = entries.Where(en => !en.IsDirectory).ToDictionary(en => en.Name, en => en.Size);
                }
                catch { filesToUpload.AddRange(dirGroup); continue; }

                foreach (var file in dirGroup)
                {
                    string? fn = Path.GetFileName(file.remotePath);
                    if (existingFiles.ContainsKey(fn!))
                    {
                        if (_duplicateAction == DuplicateAction.ReplaceAll) { try { await _protocol.DeleteFileAsync(file.remotePath); } catch { } filesToUpload.Add(file); }
                        else if (_duplicateAction == DuplicateAction.SkipAll) { /* skip */ }
                        else
                        {
                            long localSize = LocalIo.GetLength(file.localPath);
                            long remoteSize = existingFiles[fn!];
                            var dlg = new DuplicateFileDialog(fn!, localSize, remoteSize);
                            await OverlayDialogs.ShowAsync(dlg);
                            switch (dlg.UserAction)
                            {
                                case DuplicateFileDialog.FileAction.Replace: try { await _protocol.DeleteFileAsync(file.remotePath); } catch { } filesToUpload.Add(file); break;
                                case DuplicateFileDialog.FileAction.Skip: break;
                                case DuplicateFileDialog.FileAction.ReplaceAll: _duplicateAction = DuplicateAction.ReplaceAll; try { await _protocol.DeleteFileAsync(file.remotePath); } catch { } filesToUpload.Add(file); break;
                                case DuplicateFileDialog.FileAction.SkipAll: _duplicateAction = DuplicateAction.SkipAll; break;
                            }
                        }
                    }
                    else filesToUpload.Add(file);
                }
            }
            if (disposeDup) dupConn?.Dispose();
            return filesToUpload;
        }
    }
}
