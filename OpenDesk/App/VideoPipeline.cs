using System.Runtime.InteropServices;

namespace OpenDesk.App;

// Host video: Desktop Duplication of one monitor into MfEncoder, on its own thread, at most 60 frames
// per second and only when the encoder can take one. Changes that arrive sooner stay in the duplication
// mirror and are converted on the next turn. The last image is re-encoded when only the cursor moved or
// a keyframe is requested (one per 500 ms at most). Frames carry the display generation they were
// captured for. Lost duplication (secure desktop, display change) is retried every second; an encoder
// that fails is replaced by the next one.
internal sealed unsafe class VideoPipeline : IDisposable
{
    internal sealed record Profile(int Width, uint Bitrate)
    {
        // Never upscaled; even sizes for 4:2:0 chroma.
        internal Size SizeFor(Rectangle display)
        {
            int width = Math.Min(Width, display.Width) & ~1;
            return new(width, (int)((long)display.Height * width / display.Width) & ~1);
        }
    }
    internal static readonly Profile[] Profiles = [new(1024, 4_000_000), new(1280, 8_000_000), new(1600, 14_000_000)];
    private const long FrameInterval = 1000 / MfEncoder.Fps, KeyframeInterval = 500; // ms
    private readonly Action<byte[], bool, long> output; // H.264, keyframe, display generation
    private readonly Action<string> notice;
    private readonly Thread thread;
    private readonly object gate = new();
    private readonly HashSet<string> failedEncoders = [];
    private Rectangle bounds;
    private Profile profile;
    private long generation;
    private int version, keyframe;
    private volatile bool stopping;

    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public int Size, Flags; public nint Cursor; public Point Position; }
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);

    internal VideoPipeline(Rectangle bounds, Profile profile, long generation, Action<byte[], bool, long> output, Action<string> notice)
    {
        this.bounds = bounds; this.profile = profile; this.generation = generation; this.output = output; this.notice = notice;
        thread = new Thread(Run) { IsBackground = true, Name = "OpenDesk video", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }
    internal void Select(Rectangle display, Profile quality, long displayGeneration) { lock (gate) { bounds = display; profile = quality; generation = displayGeneration; version++; } }
    internal void RequestKeyframe() => Interlocked.Exchange(ref keyframe, 1);

    private void Run()
    {
        timeBeginPeriod(1);
        DesktopDuplication? duplication = null; MfEncoder? encoder = null;
        int current = -1, encoded = 0;
        long lastConverted = 0, lastKeyframe = -KeyframeInterval, retryAt = 0, stamp = 0, forcedAt = 0, keyOutAt = 0;
        Point? lastCursor = null; bool dirty = false;
        Action<byte[], bool> emit = (data, key) => { if (key) keyOutAt = Environment.TickCount64; output(data, key, stamp); };
        try {
            while (!stopping) {
                Rectangle display; Profile quality; int wanted; long wantedGeneration;
                lock (gate) { display = bounds; quality = profile; wanted = version; wantedGeneration = generation; }
                if (wanted != current) {
                    duplication?.Dispose(); duplication = null; encoder?.Dispose(); encoder = null;
                    current = wanted; stamp = wantedGeneration;
                }
                if (duplication is null || encoder is null) {
                    if (Environment.TickCount64 < retryAt) { Thread.Sleep(20); continue; }
                    if (duplication is null && (duplication = DesktopDuplication.TryCreate(display)) is null) { retryAt = Environment.TickCount64 + 1000; continue; }
                    if (encoder is null) {
                        var size = quality.SizeFor(display);
                        try { encoder = MfEncoder.Create(size.Width, size.Height, quality.Bitrate, failedEncoders); }
                        catch (IOException ex) { notice(ex.Message); retryAt = Environment.TickCount64 + 5000; continue; }
                        encoded = 0; forcedAt = 0; Interlocked.Exchange(ref keyframe, 1);
                    }
                    dirty = true;
                }
                long now = Environment.TickCount64;
                // An encoder that accepted a keyframe request but produced none within a second is restarted.
                if (forcedAt > keyOutAt && now - forcedAt > 1000) { forcedAt = 0; encoder.Dispose(); encoder = null; continue; }
                Point? cursor = Cursor(display, encoder.Width, encoder.Height);
                bool key = Volatile.Read(ref keyframe) == 1 && now - lastKeyframe >= KeyframeInterval;
                bool due = now - lastConverted >= FrameInterval && encoder.CanTake;
                // Block in the duplication wait only when nothing else is waiting to happen.
                uint wait = encoder.Busy || dirty || key || cursor != lastCursor ? 0u : 10u;
                bool fresh, worked = false;
                byte* data = null; int pitch = 0;
                try {
                    fresh = duplication.TryMap(out data, out pitch, wait);
                    if (fresh && !due) { duplication.Unmap(); fresh = false; dirty = true; }
                    else if (!fresh && due && (dirty || key || cursor != lastCursor)) fresh = duplication.MapCurrent(out data, out pitch);
                }
                catch (IOException) { duplication.Dispose(); duplication = null; retryAt = now + 1000; continue; }
                try {
                    if (fresh) {
                        try { encoder.SetPending(data, pitch, display.Width, display.Height, cursor); } finally { duplication.Unmap(); }
                        lastCursor = cursor; lastConverted = now; dirty = false; worked = true;
                        if (key) {
                            Interlocked.Exchange(ref keyframe, 0); lastKeyframe = now;
                            // A new encoder starts with a keyframe; one that cannot force them is restarted.
                            if (encoded > 0 && !encoder.ForceKeyframe()) { encoder.Dispose(); encoder = null; continue; }
                            if (encoded > 0) forcedAt = now;
                        }
                        encoded++;
                    }
                    if (encoder.Pump(emit)) worked = true;
                }
                catch (IOException ex) {
                    var failed = encoder!; failedEncoders.Add(failed.Name); notice($"Video encoder {failed.Name} stopped ({ex.Message}); trying another.");
                    failed.Dispose(); encoder = null;
                }
                if (!worked && wait == 0) Thread.Sleep(1);
            }
        }
        finally { duplication?.Dispose(); encoder?.Dispose(); timeEndPeriod(1); }
    }
    // The pointer's top-left in encoded pixels, or null when it is hidden or on another monitor.
    private static Point? Cursor(Rectangle display, int width, int height)
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || (info.Flags & 1) == 0 || !display.Contains(info.Position)) return null;
        return new((int)((long)(info.Position.X - display.X) * width / display.Width), (int)((long)(info.Position.Y - display.Y) * height / display.Height));
    }
    public void Dispose() { stopping = true; thread.Join(); }
}
