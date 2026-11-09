using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace OpenDesk.WebRtcProbe;

// One-PC probe of Chromium WebRTC inside WebView2. Each sample: the viewer page sends a click over the
// data channel, the host toggles an on-screen marker, and the viewer times until the marker changes in the
// received video. That covers capture, encode, transport, decode and display, but not real input injection.
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Spelled out: the generated ApplicationConfiguration clashes with the one OpenDesk.App exposes to this probe.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        string Arg(string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        // Chromium keeps only one --enable-features/--disable-features switch, so extra features are merged here.
        string disable = string.Join(',', new[] { "WebRtcHideLocalIpsWithMdns", Arg("--disable", "") }.Where(f => f != ""));
        string enable = Arg("--enable", "");
        string browserArgs = $"--auto-select-desktop-capture-source=\"Entire screen\" --disable-features={disable} " +
            (enable == "" ? "" : $"--enable-features={enable} ") + Arg("--flags", "");
        Application.Run(new ProbeForm(Arg("--mode", "webrtc") == "native", Arg("--encoder", "webcodecs"), Arg("--mft", "NVIDIA"), Arg("--size", "") == "720", Arg("--codec", "H264"), int.Parse(Arg("--samples", "40")), int.Parse(Arg("--fps", "60")), browserArgs.Trim(),
            Path.GetFullPath(Arg("--out", Path.Combine(AppContext.BaseDirectory, "webrtc-probe.json")))));
    }
}

// Native mode skips WebRTC video: Desktop Duplication frames go through shared memory into a WebCodecs
// encoder in the host page and travel over a data channel to a WebCodecs decoder in the viewer page.
internal sealed class ProbeForm : Form
{
    private readonly bool native, mf, small;
    private readonly string mftMatch;
    private MfEncoder? encoder;
    private long sentChunks;
    private CoreWebView2Environment? environment;
    private readonly nint[] frameBuffers = new nint[3];
    private readonly int[] busy = new int[3];
    private readonly List<CoreWebView2SharedBuffer> shared = [];
    private long captured, skipped, copyTicks;
    private long captureStart;
    private readonly Panel marker = new() { Bounds = new(16, 16, 48, 48), BackColor = Color.Black };
    private readonly WebView2 host = new() { Name = "host", Bounds = new(16, 80, 240, 160) };
    private readonly WebView2 viewer = new() { Name = "viewer", Bounds = new(272, 16, 960, 600) };
    private readonly Dictionary<string, JsonElement> results = [];
    private readonly string codec, browserArgs, output;
    private readonly int samples, fps;
    private int ready, toggles;
    private readonly List<string> states = [];
    private volatile bool finished;

    public ProbeForm(bool native, string encoderKind, string mftMatch, bool small, string codec, int samples, int fps, string browserArgs, string output)
    {
        this.native = native; mf = encoderKind == "mf"; this.mftMatch = mftMatch; this.small = small; this.codec = codec; this.samples = samples; this.fps = fps; this.browserArgs = browserArgs; this.output = output;
        Text = "OpenDesk WebRTC probe"; TopMost = true; StartPosition = FormStartPosition.Manual;
        Location = Screen.PrimaryScreen!.Bounds.Location; ClientSize = new(1248, 632);
        Controls.AddRange([marker, host, viewer]);
        // A launcher may ask for a minimized window; the marker must be on screen to be captured.
        Resize += (_, _) => {
            states.Add($"{Environment.TickCount64 % 100000}:{WindowState}");
            if (WindowState == FormWindowState.Minimized && !finished) BeginInvoke(() => WindowState = FormWindowState.Normal);
        };
        Shown += async (_, _) => { states.Add("shown:" + WindowState); WindowState = FormWindowState.Normal; Location = Screen.PrimaryScreen!.Bounds.Location; try { await StartAsync(); } catch (Exception ex) { Finish(ex.Message); } };
        var watchdog = new System.Windows.Forms.Timer { Interval = 120_000 };
        watchdog.Tick += (_, _) => Finish("Timed out after 120 s");
        watchdog.Start();
    }

    private async Task StartAsync()
    {
        // Picks the screen without the picker; keeps real local IPs so loopback ICE does not depend on mDNS.
        var options = new CoreWebView2EnvironmentOptions(browserArgs);
        environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Path.GetTempPath(), "opendesk-webrtc-probe"), options);
        foreach (var view in new[] { host, viewer }) {
            await view.EnsureCoreWebView2Async(environment);
            view.CoreWebView2.SetVirtualHostNameToFolderMapping("probe.opendesk", Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.Allow);
            view.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Allow;
            view.CoreWebView2.WebMessageReceived += (_, e) => OnMessage(view, e.WebMessageAsJson);
        }
        var screen = Screen.PrimaryScreen!.Bounds;
        var (outWidth, outHeight) = OutputSize();
        string prefix = native ? "native-" : "", query = $"codec={Uri.EscapeDataString(codec)}&fps={fps}&w={screen.Width}&h={screen.Height}" +
            $"&ow={outWidth}&oh={outHeight}&encoder={(mf ? "mf" : "webcodecs")}";
        host.CoreWebView2.Navigate($"https://probe.opendesk/{prefix}host.html?{query}");
        viewer.CoreWebView2.Navigate($"https://probe.opendesk/{prefix}viewer.html?{query}");
    }

    private async void OnMessage(WebView2 from, string json)
    {
        try {
            using var document = JsonDocument.Parse(json);
            var message = document.RootElement;
            switch (message.GetProperty("type").GetString()) {
                case "ready":
                    // getDisplayMedia needs a user gesture; DevTools evaluation can grant one.
                    if (++ready != 2) break;
                    if (native && !mf) {
                        var bounds = Screen.PrimaryScreen!.Bounds;
                        for (int i = 0; i < frameBuffers.Length; i++) {
                            var buffer = environment!.CreateSharedBuffer((ulong)bounds.Width * (ulong)bounds.Height * 4);
                            shared.Add(buffer); frameBuffers[i] = buffer.Buffer;
                            host.CoreWebView2.PostSharedBufferToScript(buffer, CoreWebView2SharedBufferAccess.ReadOnly, $"{{\"index\":{i}}}");
                        }
                    }
                    await host.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", """{"expression":"start()","userGesture":true}""");
                    break;
                case "signal": (from == host ? viewer : host).CoreWebView2.PostWebMessageAsJson(json); break;
                case "click": toggles++; marker.BackColor = marker.BackColor == Color.Black ? Color.White : Color.Black; marker.Update(); break;
                case "capture": new Thread(mf ? EncodeLoop : CaptureLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal }.Start(); break;
                case "free": Volatile.Write(ref busy[message.GetProperty("i").GetInt32()], 0); break;
                case "capturing": results["capture"] = message.Clone(); states.Add($"{Environment.TickCount64 % 100000}:capturing"); break;
                case "live":
                    var centre = marker.PointToScreen(new Point(marker.Width / 2, marker.Height / 2));
                    var screen = Screen.PrimaryScreen!.Bounds;
                    var above = WindowFromPoint(centre);
                    if (above != marker.Handle) {
                        var name = new System.Text.StringBuilder(256); GetClassName(above, name, name.Capacity);
                        GetWindowThreadProcessId(GetAncestor(above, 2), out uint pid);
                        string owner = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
                        Finish($"Marker at {centre} is covered by window 0x{above:X} class '{name}' of {owner} (pid {pid}, probe {Environment.ProcessId}) (marker 0x{marker.Handle:X}, form {Bounds}, screen {screen})"); break;
                    }
                    viewer.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new {
                        type = "marker", x = (centre.X - screen.X) / (double)screen.Width, y = (centre.Y - screen.Y) / (double)screen.Height, samples }));
                    break;
                case "result": results["viewer"] = message.Clone(); host.CoreWebView2.PostWebMessageAsJson("""{"type":"stats"}"""); break;
                case "hostStats": results["host"] = message.Clone(); Finish(results["viewer"].GetProperty("failed").GetString()); break;
                case "error": Finish(from.Name + ": " + message.GetProperty("message").GetString()); break;
            }
        }
        catch (Exception ex) { Finish(ex.Message); }
    }

    private void Finish(string? error)
    {
        if (finished) return;
        finished = true;
        var report = new Dictionary<string, object?> { ["codec"] = codec, ["fps"] = fps, ["browserArgs"] = browserArgs, ["error"] = error, ["toggles"] = toggles, ["states"] = states };
        if (results.TryGetValue("viewer", out var viewerResult)) {
            report["clickToScreenMs"] = Summary(viewerResult.GetProperty("clicks"));
            report["dataChannelRttMs"] = Summary(viewerResult.GetProperty("dcRtt"));
        }
        if (native) {
            double seconds = captureStart == 0 ? 0 : System.Diagnostics.Stopwatch.GetElapsedTime(captureStart).TotalSeconds;
            report["nativeCapture"] = new { captured, skipped, fps = seconds > 0 ? Math.Round(captured / seconds, 1) : 0,
                avgCopyMs = captured > 0 ? Math.Round(copyTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / captured, 2) : 0, size = OutputSize().ToString() };
            if (encoder is { } e) {
                double[] sorted = e.EncodeMs.Order().ToArray();
                report["mfEncoder"] = new { e.Name, e.Available, e.Settings, e.Replaced, sentChunks, framesEncoded = sorted.Length,
                    avgEncodeMs = sorted.Length > 0 ? Math.Round(sorted.Average(), 2) : 0, p95EncodeMs = sorted.Length > 0 ? Math.Round(sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1], 2) : 0 };
            }
        }
        foreach (var (key, value) in results) report[key] = value;
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Close();
    }

    // Polls Desktop Duplication, which only yields a frame when the screen changed, and hands each
    // frame to a free shared buffer. A frame is dropped when the page has not released all buffers.
    private unsafe void CaptureLoop()
    {
        timeBeginPeriod(1);
        try {
            var bounds = Screen.PrimaryScreen!.Bounds;
            using var duplication = OpenDesk.App.DesktopDuplication.TryCreate(bounds) ?? throw new IOException("Desktop Duplication is unavailable.");
            int next = 0, row = bounds.Width * 4;
            captureStart = System.Diagnostics.Stopwatch.GetTimestamp();
            while (!finished) {
                if (!duplication.TryMap(out byte* data, out int pitch)) { Thread.Sleep(1); continue; }
                int slot = next;
                if (Interlocked.CompareExchange(ref busy[slot], 1, 0) != 0) { skipped++; duplication.Unmap(); continue; }
                next = (next + 1) % busy.Length;
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                byte* target = (byte*)frameBuffers[slot];
                for (int y = 0; y < bounds.Height; y++) Buffer.MemoryCopy(data + (long)y * pitch, target + (long)y * row, row, row);
                duplication.Unmap();
                copyTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start; captured++;
                BeginInvoke(() => { if (!finished) host.CoreWebView2.PostWebMessageAsJson($"{{\"type\":\"frame\",\"i\":{slot}}}"); });
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) {
            try { BeginInvoke(() => Finish("Capture: " + ex.Message)); } catch (InvalidOperationException) { }
        }
        finally { timeEndPeriod(1); }
    }
    private (int Width, int Height) OutputSize() { var s = Screen.PrimaryScreen!.Bounds; return small ? (1280, 720) : (s.Width, s.Height); }

    // Media Foundation variant of CaptureLoop: each captured frame becomes the encoder's pending input,
    // and encoded H.264 goes to the host page, which forwards it over the data channel.
    private unsafe void EncodeLoop()
    {
        timeBeginPeriod(1);
        try {
            var bounds = Screen.PrimaryScreen!.Bounds;
            var (outWidth, outHeight) = OutputSize();
            using var duplication = OpenDesk.App.DesktopDuplication.TryCreate(bounds) ?? throw new IOException("Desktop Duplication is unavailable.");
            using var created = new MfEncoder(mftMatch, outWidth, outHeight, fps, 12_000_000);
            encoder = created;
            captureStart = System.Diagnostics.Stopwatch.GetTimestamp();
            while (!finished) {
                bool idle = true;
                if (duplication.TryMap(out byte* data, out int pitch)) {
                    long start = System.Diagnostics.Stopwatch.GetTimestamp();
                    created.SetPending(data, pitch, bounds.Width, bounds.Height);
                    duplication.Unmap();
                    copyTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start; captured++; idle = false;
                }
                if (created.Pump((bytes, key, time) => {
                    sentChunks++;
                    string json = $"{{\"type\":\"chunk\",\"key\":{(key ? "true" : "false")},\"t\":{time},\"data\":\"{Convert.ToBase64String(bytes)}\"}}";
                    BeginInvoke(() => { if (!finished) host.CoreWebView2.PostWebMessageAsJson(json); });
                })) idle = false;
                if (idle) Thread.Sleep(1);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) {
            try { BeginInvoke(() => Finish("Encode: " + ex.Message)); } catch (InvalidOperationException) { }
        }
        finally { timeEndPeriod(1); }
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int capacity);

    // Timeouts arrive as null and count as attempts without a result.
    private static object Summary(JsonElement values)
    {
        double[] sorted = values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number).Select(v => v.GetDouble()).Order().ToArray();
        int attempts = values.GetArrayLength();
        if (sorted.Length == 0) return new { attempts, received = 0 };
        return new { attempts, received = sorted.Length, p50 = Math.Round(sorted[sorted.Length / 2], 1),
            p95 = Math.Round(sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * 0.95) - 1)], 1), max = Math.Round(sorted[^1], 1) };
    }
}
