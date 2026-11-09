using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using OpenDesk.Core;

namespace OpenDesk.UI;

// The shared application: each head supplies its platform's H.264 decoder.
public sealed class ViewerApp : Application
{
    public static Func<IVideoDecoder> CreateDecoder { get; set; } = () => throw new PlatformNotSupportedException("No video decoder for this platform.");
    public static string? DecoderProblem { get; set; }
    public static ViewerSettings? Settings { get; set; }
    // An address to connect to right away (from the command line).
    public static string? ConnectTo { get; set; }

    public override void Initialize() { Styles.Add(new FluentTheme()); RequestedThemeVariant = ThemeVariant.Light; }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            var main = new MainWindow(Settings ?? ViewerSettings.Load(), CreateDecoder, DecoderProblem);
            desktop.MainWindow = main;
            if (ConnectTo is { } address) main.Opened += (_, _) => main.Connect(address);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
