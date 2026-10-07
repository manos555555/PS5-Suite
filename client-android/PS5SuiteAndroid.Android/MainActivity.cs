using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;

namespace PS5SuiteAndroid.Android;

[Activity(
    Label = "PS5 Suite",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override void OnCreate(global::Android.OS.Bundle? savedInstanceState)
    {
        // Expose user-browsable writable dirs to the shared library before
        // Avalonia starts (Android/data/<pkg>/files — no permission needed).
        PS5SuiteAndroid.AppPaths.DownloadsDirProvider =
            () => GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath;
        PS5SuiteAndroid.AppPaths.CacheDirProvider =
            () => CacheDir!.AbsolutePath;
        base.OnCreate(savedInstanceState);
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
