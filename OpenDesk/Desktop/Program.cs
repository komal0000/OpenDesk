using Avalonia;
using OpenDesk.Core;
using OpenDesk.Transport;
using OpenDesk.UI;
using OpenDesk.Video.FFmpeg;

namespace OpenDesk.Desktop;

// Desktop head (Windows, macOS, Linux) of the cross-platform viewer. Video is decoded with FFmpeg here;
// per-platform decoders (Media Foundation, VideoToolbox) can replace it later behind IVideoDecoder.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--probe", var address, ..]) return Probe.RunAsync(address, args[2..]).GetAwaiter().GetResult();
        ViewerApp.CreateDecoder = () => new FfmpegDecoder();
        if (!FfmpegDecoder.IsAvailable(out string problem)) ViewerApp.DecoderProblem = problem;
        if (Environment.GetEnvironmentVariable("OPENDESK_VIEWER_SETTINGS") is { Length: > 0 } file) ViewerApp.Settings = ViewerSettings.Load(file);
        if (args is ["--connect", var id, ..]) { ViewerApp.ConnectTo = id; args = args[2..]; }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<ViewerApp>().UsePlatformDetect().WithInterFont().LogToTrace();
}

// Command-line check without a window: connect with the saved settings, decode video for a while, send
// a few clicks if control was granted, and report what happened. For testing a host from any machine.
internal static class Probe
{
    public static async Task<int> RunAsync(string address, string[] options)
    {
        int seconds = options is ["--seconds", var s, ..] && int.TryParse(s, out int value) ? Math.Clamp(value, 1, 600) : 10;
        string? file = Environment.GetEnvironmentVariable("OPENDESK_VIEWER_SETTINGS");
        var settings = ViewerSettings.Load(string.IsNullOrEmpty(file) ? null : file);
        var started = System.Diagnostics.Stopwatch.StartNew();
        try {
            settings.KnownKeys.TryGetValue(DeviceProof.NormalizeId(address), out var known);
            var progress = new Progress<string>(text => Console.WriteLine($"[{started.ElapsedMilliseconds,6} ms] {text}"));
            await using var session = await RemoteSession.ConnectAsync(new(settings.ServiceUrl, settings.ServicePin, settings.DisplayName), address, known, progress, default);
            Console.WriteLine($"[{started.ElapsedMilliseconds,6} ms] Accepted: control={session.Status.Control} epoch={session.Status.Epoch} display={session.Status.Display}");
            settings.Remember(session.Id, session.KeyPin);
            var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Ended += reason => ended.TrySetResult(reason);
            int frames = 0, keyframes = 0; long firstFrame = -1; DecodedPicture? last = null; int width = 0, height = 0;
            using var decoder = new FfmpegDecoder();
            var decodeTimes = new List<double>();
            var gate = new Lock();
            bool waitingKey = true;
            session.FrameReceived += frame => {
                lock (gate) {
                    if (waitingKey && !frame.Key) return;
                    waitingKey = false;
                    var t = System.Diagnostics.Stopwatch.GetTimestamp();
                    try { last = decoder.Decode(frame.Data); } catch (InvalidDataException) { waitingKey = true; session.RequestKeyframe(); return; }
                    decodeTimes.Add(System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                    if (last is null) return;
                    frames++; if (frame.Key) keyframes++; width = last.Width; height = last.Height;
                    if (firstFrame < 0) firstFrame = started.ElapsedMilliseconds;
                }
            };
            session.RequestKeyframe();
            var deadline = Task.Delay(TimeSpan.FromSeconds(seconds));
            if (session.Status.Control) {
                await Task.Delay(1000);
                for (int i = 0; i < 5 && !deadline.IsCompleted; i++) {
                    var st = session.Status;
                    session.SendInput(new InputEvent("move", 0.5, 0.5, Epoch: st.Epoch));
                    await Task.Delay(200);
                }
            }
            var finished = await Task.WhenAny(deadline, ended.Task);
            lock (gate) {
                decodeTimes.Sort();
                Console.WriteLine($"Frames decoded: {frames} ({keyframes} keyframes), first at {firstFrame} ms, size {width}x{height}" +
                    (decodeTimes.Count > 0 ? $", decode p50 {decodeTimes[decodeTimes.Count / 2]:0.0} ms p95 {decodeTimes[(int)(decodeTimes.Count * 0.95)]:0.0} ms" : ""));
            }
            if (finished == ended.Task) { Console.WriteLine("Session ended: " + ended.Task.Result); return 2; }
            return frames > 0 ? 0 : 3;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or FormatException or InvalidDataException or DllNotFoundException) {
            Console.WriteLine($"[{started.ElapsedMilliseconds,6} ms] Failed: {ex.Message}");
            return 1;
        }
    }
}
