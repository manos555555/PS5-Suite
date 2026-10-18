using Avalonia.Controls;

namespace PS5Upload
{
    /// <summary>
    /// Desktop-only shell: hosts the shared <see cref="MainWindow"/> view.
    /// Android embeds the same view directly via ISingleViewApplicationLifetime.
    /// </summary>
    public partial class DesktopWindow : Window
    {
        public DesktopWindow()
        {
            InitializeComponent();
        }
    }
}
