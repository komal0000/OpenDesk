using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OpenDesk.UI;

// The Windows app's palette and a few builders, so views stay short.
internal static class Look
{
    public static readonly Color Ink = Color.FromRgb(41, 45, 50), Accent = Color.FromRgb(201, 39, 49), Paper = Color.FromRgb(247, 248, 250),
        Sidebar = Color.FromRgb(240, 242, 245), Muted = Color.FromRgb(96, 101, 111), Border = Color.FromRgb(207, 211, 218);
    public static IBrush Brush(Color color) => new SolidColorBrush(color);

    public static TextBlock Text(string text, double size = 14, bool bold = false, Color? color = null) => new() {
        Text = text, FontSize = size, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        Foreground = Brush(color ?? Ink), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

    public static Button Button(string text, bool primary = false)
    {
        var button = new Button { Content = text, Padding = new Thickness(16, 8), VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(4) };
        if (primary) { button.Background = Brush(Accent); button.Foreground = Brushes.White; }
        return button;
    }
}
