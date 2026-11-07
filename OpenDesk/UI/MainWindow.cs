using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using OpenDesk.Core;

namespace OpenDesk.UI;

// Address-first home: type a nine-digit address and connect; recent devices; service settings.
public sealed class MainWindow : Window
{
    private readonly ViewerSettings settings;
    private readonly Func<IVideoDecoder> createDecoder;
    private readonly TextBox address = new() { Watermark = "123 456 789", FontSize = 20, Padding = new Thickness(12, 8), MinWidth = 260 };
    private readonly TextBlock notice = Look.Text("", 13, color: Look.Muted);
    private readonly StackPanel recent = new() { Spacing = 6 };
    private readonly TextBox serviceUrl = new(), servicePin = new(), displayName = new();

    public MainWindow(ViewerSettings settings, Func<IVideoDecoder> createDecoder, string? decoderProblem = null)
    {
        this.settings = settings; this.createDecoder = createDecoder;
        Title = "OpenDesk Viewer"; Width = 720; Height = 640; MinWidth = 420; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = Look.Brush(Look.Paper);

        var connect = Look.Button("Connect", primary: true);
        connect.Click += (_, _) => Connect(address.Text ?? "");
        address.KeyDown += (_, e) => { if (e.Key == Key.Enter) Connect(address.Text ?? ""); };
        var addressRow = new DockPanel { Children = { connect, address } };
        DockPanel.SetDock(connect, Dock.Right); connect.Margin = new Thickness(8, 0, 0, 0);
        var card = Card(Look.Text("Remote device", 18, true), Look.Text("Enter the address shown on the other device. Its owner approves each connection.", 13, color: Look.Muted), addressRow, notice);

        serviceUrl.Text = settings.ServiceUrl; servicePin.Text = settings.ServicePin; displayName.Text = settings.DisplayName;
        var save = Look.Button("Save settings");
        save.Click += (_, _) => {
            try { settings.SetService(serviceUrl.Text ?? "", servicePin.Text ?? "", displayName.Text ?? ""); notice.Text = "Settings saved."; }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException) { notice.Text = ex.Message; }
        };
        var settingsPanel = new Expander { Header = "Settings", HorizontalAlignment = HorizontalAlignment.Stretch, Content = new StackPanel { Spacing = 6, Children = {
            Look.Text("Your name, shown to the host", 12, color: Look.Muted), displayName,
            Look.Text("Device service (HTTPS address)", 12, color: Look.Muted), serviceUrl,
            Look.Text("Service certificate fingerprint (SHA-256, empty for normal certificate checks)", 12, color: Look.Muted), servicePin, save } } };

        if (decoderProblem is not null) notice.Text = decoderProblem;
        RefreshRecent();
        Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children = {
            Look.Text("OpenDesk", 26, true, Look.Accent), card, Card(Look.Text("Recent devices", 16, true), recent), settingsPanel } } };
        notice.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) notice.IsVisible = !string.IsNullOrEmpty(notice.Text); };
        notice.IsVisible = !string.IsNullOrEmpty(notice.Text);
        Opened += (_, _) => address.Focus();
    }

    private static Border Card(params Control[] children) => new() {
        Background = Brushes.White, BorderBrush = Look.Brush(Look.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(20), Child = new StackPanel { Spacing = 10, Children = { } }.With(children) };

    private void RefreshRecent()
    {
        recent.Children.Clear();
        if (settings.Recent.Count == 0) { recent.Children.Add(Look.Text("Devices you connect to appear here.", 13, color: Look.Muted)); return; }
        foreach (var device in settings.Recent.Take(10)) {
            var open = Look.Button(DeviceProof.FormatId(device.Id)); open.Click += (_, _) => Connect(device.Id);
            var forget = new Button { Content = "Forget", Padding = new Thickness(10, 6) };
            forget.Click += (_, _) => { settings.Forget(device.Id); RefreshRecent(); };
            recent.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { open, forget,
                Look.Text("Last connected " + device.LastConnected.ToLocalTime().ToString("g"), 12, color: Look.Muted) } });
        }
    }

    public SessionWindow? Connect(string text)
    {
        string id;
        try { id = DeviceProof.NormalizeId(text); }
        catch (FormatException) { notice.Text = "Enter the nine-digit address shown on the other device."; return null; }
        notice.Text = "";
        var window = new SessionWindow(settings, id, createDecoder);
        window.Closed += (_, _) => RefreshRecent();
        window.Show();
        return window;
    }
}

internal static class PanelExtensions
{
    public static StackPanel With(this StackPanel panel, IEnumerable<Control> children) { panel.Children.AddRange(children); return panel; }
}
