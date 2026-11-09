using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OpenDesk.Core;
using OpenDesk.Transport;
using SIPSorcery.Net;

namespace OpenDesk.UI;

// One session to a remote device: connects, shows the screen, and sends input while this viewer has control.
public sealed class SessionWindow : Window
{
    private readonly ViewerSettings settings;
    private readonly string id;
    private readonly Func<IVideoDecoder> createDecoder;
    private readonly CancellationTokenSource stop = new();
    private readonly RemoteScreen screen = new();
    private readonly TextBlock state = Look.Text("Connecting…", 14, true);
    private readonly TextBlock message = Look.Text("Connecting…", 16, color: Color.FromRgb(214, 214, 218));
    private readonly TextBlock stats = Look.Text("", 12, color: Colors.White);
    private readonly Border statsPanel;
    private readonly CheckBox sendInput = new() { Content = "Send keyboard and mouse", IsEnabled = false, Margin = new Thickness(16, 0) };
    private readonly ToggleButton statsButton = new() { Content = "Stats", Padding = new Thickness(12, 6) };
    private RemoteSession? session;
    private VideoPresenter? presenter;
    private DispatcherTimer? statsTimer;
    private int presentPending;
    private bool connected, hadControl;

    public RemoteSession? Session => session;
    public RemoteScreen Screen => screen;
    public string LastMessage => message.Text ?? "";

    public SessionWindow(ViewerSettings settings, string id, Func<IVideoDecoder> createDecoder)
    {
        this.settings = settings; this.id = id; this.createDecoder = createDecoder;
        Title = $"OpenDesk · {DeviceProof.FormatId(id)}"; Width = 1100; Height = 760; MinWidth = 600; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; Background = Look.Brush(Color.FromRgb(31, 32, 35));

        var fullscreen = new Button { Content = "Fullscreen", Padding = new Thickness(12, 6) };
        fullscreen.Click += (_, _) => ToggleFullscreen();
        var disconnect = new Button { Content = "Disconnect", Padding = new Thickness(12, 6), Foreground = Look.Brush(Look.Accent) };
        disconnect.Click += (_, _) => Close();
        statsButton.IsCheckedChanged += (_, _) => statsPanel!.IsVisible = statsButton.IsChecked == true;
        sendInput.IsCheckedChanged += (_, _) => { if (sendInput.IsChecked != true) screen.Reset(); };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { statsButton, fullscreen, disconnect } };
        var top = new DockPanel { Background = Brushes.White, Height = 52, LastChildFill = true };
        DockPanel.SetDock(tools, Dock.Right); top.Children.Add(new Border { Padding = new Thickness(0, 0, 12, 0), Child = tools, [DockPanel.DockProperty] = Dock.Right });
        DockPanel.SetDock(sendInput, Dock.Right); top.Children.Add(sendInput);
        top.Children.Add(new Border { Padding = new Thickness(16, 0), Child = state });
        var hint = new Border { Background = Brushes.White, Padding = new Thickness(16, 8),
            Child = Look.Text("Click the screen to focus. Ctrl+Alt+F12 releases remote input. Windows secure desktops are unavailable.", 12, color: Look.Muted) };
        statsPanel = new Border { Background = new SolidColorBrush(Color.FromArgb(166, 0, 0, 0)), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8), IsVisible = false, IsHitTestVisible = false, Child = stats };
        message.HorizontalAlignment = HorizontalAlignment.Center; message.TextAlignment = TextAlignment.Center; message.MaxWidth = 640; message.IsHitTestVisible = false;
        var center = new Grid { Children = { screen, message, statsPanel } };
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(top); root.Children.Add(hint); root.Children.Add(center);
        Content = root;

        screen.ActiveEpoch = () => {
            var s = session; if (s is null || !connected || sendInput.IsChecked != true) return null;
            var status = s.Status;
            // Input waits until the picture shown is of the host's current monitor, so coordinates match it.
            return status.Control && screen.ShownDisplay == status.Display ? status.Epoch : null;
        };
        screen.Send = input => session?.SendInput(input) ?? false;
        screen.Hint += () => state.Text = session?.Status.Control == true ? "Input paused · turn on Send keyboard and mouse" : "View only · ask the host to allow control";
        screen.ReleaseRequested += () => sendInput.IsChecked = false;
        screen.EscapePressed += () => { if (WindowState == WindowState.FullScreen) ToggleFullscreen(); };
        screen.FirstPicture += () => message.IsVisible = false;
        Deactivated += (_, _) => screen.Reset();
        Opened += async (_, _) => await ConnectAsync();
        Closing += (_, _) => stop.Cancel();
        Closed += async (_, _) => {
            statsTimer?.Stop();
            if (session is { } s) { screen.Reset(); await s.DisposeAsync(); }
            presenter?.Dispose();
        };
    }

    private async Task ConnectAsync()
    {
        try {
            settings.KnownKeys.TryGetValue(id, out var known);
            var progress = new Progress<string>(Show);
            var options = new ViewerOptions(settings.ServiceUrl, settings.ServicePin, settings.DisplayName);
            session = await RemoteSession.ConnectAsync(options, id, known, progress, stop.Token);
            presenter = new VideoPresenter(createDecoder, session.RequestKeyframe);
            presenter.PictureReady += () => {
                if (Interlocked.Exchange(ref presentPending, 1) == 0)
                    Dispatcher.UIThread.Post(() => { Volatile.Write(ref presentPending, 0); screen.Present(presenter); }, DispatcherPriority.Render);
            };
            presenter.Failed += text => Dispatcher.UIThread.Post(() => Fail("Video cannot be shown: " + text));
            session.FrameReceived += presenter.Enqueue;
            session.Viewer.StatusChanged += status => Dispatcher.UIThread.Post(() => ApplyStatus(status));
            session.Ended += reason => Dispatcher.UIThread.Post(() => Fail(reason));
            session.ConnectionChanged += connection => Dispatcher.UIThread.Post(() => {
                if (connection == RTCPeerConnectionState.disconnected && connected) state.Text = "Connection interrupted · waiting for the network…";
                else if (connection == RTCPeerConnectionState.connected && connected) ApplyStatus(session.Status);
            });
            connected = true;
            settings.Remember(id, session.KeyPin);
            Show("Waiting for the first picture…");
            ApplyStatus(session.Status);
            session.RequestKeyframe();
            statsTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStats());
            statsTimer.Start();
            screen.Focus();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) {
            Fail(ex is TimeoutException ? "The connection timed out. Both devices must be online; on different networks the device service needs TURN." : ex.Message);
        }
    }

    private void ApplyStatus(SessionStatus status)
    {
        if (!connected) return;
        state.Text = status.Control ? "Connected · You have control" : "Connected · View only";
        sendInput.IsEnabled = status.Control;
        if (status.Control && !hadControl) sendInput.IsChecked = true;
        if (!status.Control) sendInput.IsChecked = false;
        hadControl = status.Control;
    }
    private void UpdateStats()
    {
        if (presenter is null) return;
        var (frames, decodeMs, bytes, skipped) = presenter.Stats();
        stats.Text = $"{frames} fps · decode {(frames > 0 ? decodeMs.ToString("0.0") : "–")} ms\n{bytes * 8 / 1000} kbit/s{(skipped > 0 ? $" · {skipped} skipped" : "")}\n{screen.PictureWidth}×{screen.PictureHeight}";
    }
    private void Show(string text) { state.Text = text; message.Text = text; message.IsVisible = true; }
    private void Fail(string text)
    {
        if (!connected && stop.IsCancellationRequested) return;
        connected = false; sendInput.IsChecked = false; sendInput.IsEnabled = false;
        Show(text); stop.Cancel();
    }
    private void ToggleFullscreen() => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
}
