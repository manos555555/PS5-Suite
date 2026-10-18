using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PS5Upload
{
    public partial class DuplicateFileDialog : UserControl, OverlayDialogs.IOverlayClosable
    {
        /// <summary>Raised when the view wants its host overlay to close.</summary>
        public event Action? RequestClose;

        public enum FileAction { Replace, Skip, ReplaceAll, SkipAll }
        public FileAction UserAction { get; private set; } = FileAction.Skip;

        public DuplicateFileDialog()
        {
            InitializeComponent();
        }

        public DuplicateFileDialog(string fileName, long localSize, long remoteSize) : this()
        {
            FileNameText.Text = fileName;
            LocalSizeText.Text = $"Local file size: {FileUtils.FormatFileSize(localSize)}";
            RemoteSizeText.Text = $"Remote file size: {FileUtils.FormatFileSize(remoteSize)}";
        }

        private void ReplaceButton_Click(object? sender, RoutedEventArgs e)
        {
            UserAction = FileAction.Replace;
            RequestClose?.Invoke();
        }

        private void SkipButton_Click(object? sender, RoutedEventArgs e)
        {
            UserAction = FileAction.Skip;
            RequestClose?.Invoke();
        }

        private void ReplaceAllButton_Click(object? sender, RoutedEventArgs e)
        {
            UserAction = FileAction.ReplaceAll;
            RequestClose?.Invoke();
        }

        private void SkipAllButton_Click(object? sender, RoutedEventArgs e)
        {
            UserAction = FileAction.SkipAll;
            RequestClose?.Invoke();
        }
    }
}
