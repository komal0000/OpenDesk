using OpenDesk.Core;

namespace OpenDesk.App;

internal sealed class DesktopSource(Rectangle initial,int width=1280,long quality=65) : IDisposable
{
    private readonly object gate=new();
    private sealed record Display(Rectangle Bounds,ScreenCapture Capture);
    private Display display=new(initial,new ScreenCapture(initial,width,quality));
    internal Rectangle Bounds=>Volatile.Read(ref display).Bounds;
    internal void Select(Rectangle bounds,int width=1280,long quality=65){lock(gate){var old=display;Volatile.Write(ref display,new(bounds,new ScreenCapture(bounds,width,quality)));old.Capture.Dispose();}}
    internal ScreenFrame Capture(){lock(gate)return display.Capture.Capture();}
    public void Dispose(){lock(gate)display.Capture.Dispose();}
}
