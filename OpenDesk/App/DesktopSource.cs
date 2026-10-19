using OpenDesk.Core;

namespace OpenDesk.App;

internal sealed class DesktopSource(Rectangle initial,int width=1280,long quality=65) : IScreenSource, IDisposable
{
    private readonly object gate=new();
    private sealed record Display(Rectangle Bounds,ScreenCapture Capture);
    private Display display=new(initial,new ScreenCapture(initial,width,quality));
    internal Rectangle Bounds=>Volatile.Read(ref display).Bounds;
    // Sequences continue across displays, so every viewer is sent the whole new screen.
    internal void Select(Rectangle bounds,int width=1280,long quality=65){lock(gate){var old=display;Volatile.Write(ref display,new(bounds,new ScreenCapture(bounds,width,quality,old.Capture.Sequence)));old.Capture.Dispose();}}
    public long Capture(){lock(gate)return display.Capture.Capture();}
    public ScreenFrame Frame(){lock(gate)return display.Capture.Frame();}
    public TileUpdate Changes(long since){lock(gate)return display.Capture.Changes(since);}
    public void Dispose(){lock(gate)display.Capture.Dispose();}
}
