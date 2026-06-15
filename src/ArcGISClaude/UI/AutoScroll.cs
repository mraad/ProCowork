using System.Windows;
using System.Windows.Controls;

namespace ArcGISClaude.UI
{
    /// <summary>
    /// Attached behavior: keeps a <see cref="ScrollViewer"/> pinned to the bottom as
    /// its content grows, so the newest chat message is always visible.
    /// </summary>
    internal static class AutoScroll
    {
        public static readonly DependencyProperty ToEndProperty =
            DependencyProperty.RegisterAttached(
                "ToEnd", typeof(bool), typeof(AutoScroll),
                new PropertyMetadata(false, OnToEndChanged));

        public static bool GetToEnd(DependencyObject o) => (bool)o.GetValue(ToEndProperty);
        public static void SetToEnd(DependencyObject o, bool v) => o.SetValue(ToEndProperty, v);

        private static void OnToEndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer sv && (bool)e.NewValue)
                sv.ScrollChanged += (s, args) =>
                {
                    // Scroll to the bottom whenever content is added/grows (a new
                    // message, streamed text, or a tool result). Re-scrolling itself
                    // changes only the offset, not the extent, so it won't loop.
                    if (args.ExtentHeightChange > 0)
                        sv.ScrollToBottom();
                };
        }
    }
}
