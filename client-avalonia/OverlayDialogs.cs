using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PS5Upload
{
    /// <summary>
    /// Cross-platform modal overlay — Android has no window manager, so
    /// dialogs render as cards inside the root view's DialogLayerHost panel
    /// instead of real windows. Desktop hosts get the same look for free.
    /// </summary>
    internal static class OverlayDialogs
    {
        /// <summary>Views that close themselves (converted Window classes).</summary>
        internal interface IOverlayClosable
        {
            event Action? RequestClose;
        }

        /// <summary>Panel that owns the stacked dialog cards (inside MainWindow).</summary>
        internal static Panel? LayerHost;

        /// <summary>The dimmed overlay container toggled with IsVisible.</summary>
        internal static Control? OverlayRoot;

        /// <summary>
        /// Shows <paramref name="content"/> as a modal card and returns when it closes.
        /// Self-closing views implement <see cref="IOverlayClosable"/>.
        /// </summary>
        public static Task ShowAsync(Control content, bool stretch = false)
            => ShowAsync(_ => content, stretch: stretch);

        /// <summary>
        /// Shows a modal card; the builder receives a close action to wire into
        /// its buttons. Supports nesting (each call pushes another card).
        /// </summary>
        public static async Task ShowAsync(Func<Action, Control> build,
            double maxWidth = 520, double maxHeight = 720, bool stretch = false)
        {
            var host = LayerHost;
            if (host == null) return;

            var tcs = new TaskCompletionSource();
            var close = new Action(() => tcs.TrySetResult());
            var content = build(close);
            if (content is IOverlayClosable closable)
                closable.RequestClose += close;

            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(62, 62, 66)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16)
            };
            if (stretch)
            {
                // Fill the overlay with a small margin — needed for views with
                // star-sized rows (browsers, details) that must get real height.
                card.HorizontalAlignment = HorizontalAlignment.Stretch;
                card.VerticalAlignment = VerticalAlignment.Stretch;
                card.Margin = new Thickness(16);
                card.Child = content;
            }
            else
            {
                card.HorizontalAlignment = HorizontalAlignment.Center;
                card.VerticalAlignment = VerticalAlignment.Center;
                card.MaxWidth = maxWidth;
                card.MaxHeight = maxHeight;
                card.Child = new ScrollViewer
                {
                    Content = content,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                };
            }

            host.Children.Add(card);
            if (OverlayRoot != null) OverlayRoot.IsVisible = true;
            try
            {
                await tcs.Task;
            }
            finally
            {
                host.Children.Remove(card);
                if (host.Children.Count == 0 && OverlayRoot != null)
                    OverlayRoot.IsVisible = false;
            }
        }
    }
}
