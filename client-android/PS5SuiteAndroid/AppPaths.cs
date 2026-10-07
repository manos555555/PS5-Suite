using System;

namespace PS5SuiteAndroid;

// Platform-provided writable locations. The Android head fills these in
// during startup (external files dir needs no storage permission and is
// browsable by the user at Android/data/<pkg>/files).
public static class AppPaths
{
    public static Func<string>? DownloadsDirProvider;
    public static Func<string>? CacheDirProvider;

    public static string DownloadsDir =>
        DownloadsDirProvider?.Invoke() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public static string CacheDir =>
        CacheDirProvider?.Invoke() ?? System.IO.Path.GetTempPath();
}
