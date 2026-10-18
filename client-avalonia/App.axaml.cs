using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace PS5Upload
{
    public partial class App : Application
    {
        private static readonly string LogFilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            $"ps5suite_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");

        private static readonly object _logLock = new object();

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
            LogToFile("=== Application Started ===");
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new DesktopWindow();
            }
            else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            {
                // Android/iOS single-view lifetime: the shared MainWindow is a
                // plain UserControl, so it hosts directly without any Window.
                singleView.MainView = new MainWindow();
            }

            base.OnFrameworkInitializationCompleted();
        }

        public static void LogToFile(string message)
        {
            try
            {
                lock (_logLock)
                {
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    File.AppendAllText(LogFilePath, $"[{timestamp}] {message}\n");
                }
            }
            catch { /* Ignore logging errors */ }
        }
    }
}
