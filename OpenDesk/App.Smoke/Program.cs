using System.Security.Cryptography;
using System.Text.Json;
using OpenDesk.App;
using OpenDesk.Core;
using OpenDesk.Service;

namespace OpenDesk.Smoke;

// End to end on one PC through the real app: local service, two app homes with their own device keys,
// numeric-ID connect, the real approval dialog, WebRTC over loopback, Desktop Duplication and H.264,
// then 20 clicks from the viewer page. Each click toggles an on-screen marker at the host, and the
// viewer page times until the marker's new colour is drawn (click-to-screen). Needs the display on.
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        Application.AddMessageFilter(new RemoteInputFilter());
        if (!RemoteInputFilter.Blocks(0x201, RemoteInputFilter.Marker) || RemoteInputFilter.Blocks(0x201, 0)) throw new Exception("Input filter mismatch");
        using var root = new Form { Opacity = 0, ShowInTaskbar = false };
        root.Shown += async (_, _) => {
            DirectoryHost? service = null; MainForm? host = null, viewerHome = null; ViewerForm? viewer = null;
            var marker = new Form { FormBorderStyle = FormBorderStyle.None, BackColor = Color.Black, TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Bounds = new(40, 40, 64, 64) };
            using var cert = SecureSession.CreateCertificate();
            string identity = "OpenDesk.Smoke." + Guid.NewGuid().ToString("N"), other = identity + ".viewer";
            string folder = Path.Combine(Path.GetTempPath(), identity); Directory.CreateDirectory(folder);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            string report = Path.Combine(AppContext.BaseDirectory, "session-smoke.txt");
            try {
                CheckSettingsDefaults(folder);
                service = await DirectoryHost.StartAsync(Path.Combine(folder, "devices.json"), cert, 0, 0, true);
                using (var a = ClientSettings.LoadDeviceKey(identity)) using (var b = ClientSettings.LoadDeviceKey(identity))
                    if (!a.ExportSubjectPublicKeyInfo().SequenceEqual(b.ExportSubjectPublicKeyInfo())) throw new Exception("Device key did not survive reopen");
                marker.Show(root);
                var hostSettings = new ClientSettings { ServiceUrl = service.Url, ServicePin = SecureSession.Pin(cert), DisplayName = "Smoke host", StorageFile = Path.Combine(folder, "host.json") };
                var viewerSettings = new ClientSettings { ServiceUrl = service.Url, ServicePin = SecureSession.Pin(cert), DisplayName = "Smoke viewer", StorageFile = Path.Combine(folder, "viewer.json") };
                var observed = new ObservedInput(marker);
                host = new MainForm(true, hostSettings, () => ClientSettings.LoadDeviceKey(identity), observed) { Opacity = 0, ShowInTaskbar = false }; host.Show(root);
                viewerHome = new MainForm(true, viewerSettings, () => ClientSettings.LoadDeviceKey(other)) { Opacity = 0, ShowInTaskbar = false }; viewerHome.Show(root);
                while (!host.IsOnline || !viewerHome.IsOnline) await Task.Delay(30, timeout.Token);
                if (host.DeviceAddress == viewerHome.DeviceAddress) throw new Exception("Different devices received the same ID");

                var input = (TextBox)viewerHome.Controls.Find("RemoteAddress", true).Single(); var button = (Button)viewerHome.Controls.Find("Connect", true).Single();
                input.Text = DeviceProof.FormatId(host.DeviceAddress); button.PerformClick();
                ApprovalForm? approval;
                while ((approval = Application.OpenForms.OfType<ApprovalForm>().FirstOrDefault()) is null) await Task.Delay(10, timeout.Token);
                var allow = Descendants(approval).OfType<Button>().Single(b => b.Text == "Allow control");
                var latency = System.Diagnostics.Stopwatch.StartNew(); allow.PerformClick();
                while ((viewer = Application.OpenForms.OfType<ViewerForm>().FirstOrDefault()) is null || viewer.RenderedFrames == 0 || !viewer.InputEnabled) await Task.Delay(10, timeout.Token);
                double firstFrame = latency.Elapsed.TotalMilliseconds;
                viewer.Bounds = new(300, 120, 1000, 700);
                if (!viewerSettings.KnownKeys.TryGetValue(host.DeviceAddress, out var pinned) || pinned.Length != 64) throw new Exception("Host key was not pinned for the address");

                // Click-to-screen: the marker's centre, as a fraction of the captured primary display.
                var screen = Screen.PrimaryScreen!.Bounds;
                double px = (marker.Left + marker.Width / 2.0 - screen.X) / screen.Width, py = (marker.Top + marker.Height / 2.0 - screen.Y) / screen.Height;
                var samples = new List<double>();
                for (int i = 0; i < 20; i++) {
                    bool light = marker.BackColor == Color.Black;
                    double ms = await Probe(viewer, px, py, light);
                    if (ms < 0) throw new Exception($"Click {i + 1}: the marker change never appeared in the video");
                    samples.Add(ms);
                    await Task.Delay(150, timeout.Token);
                }
                var events = observed.Events.ToArray();
                if (events.Length != 40 || events.Where((e, i) => e.Kind != "button" || e.Code != 1 || e.Down != (i % 2 == 0) || Math.Abs(e.X - .5) > .01 || Math.Abs(e.Y - .5) > .01).Any())
                    throw new Exception($"Viewer click forwarding mismatch ({events.Length} events)");
                samples.Sort();
                double p50 = samples[samples.Count / 2], p95 = samples[(int)Math.Ceiling(samples.Count * 0.95) - 1];
                File.WriteAllText(report, $"PASS two app homes -> distinct numeric IDs -> pinned signaling -> real Allow control dialog -> signed answer -> WebRTC (loopback) -> " +
                    $"Desktop Duplication + H.264 -> viewer decode; first frame {firstFrame:F0} ms after approval (target <1000); 20/20 clicks in order; " +
                    $"click-to-screen p50 {p50:F0} ms, p95 {p95:F0} ms, max {samples[^1]:F0} ms (target p50 <= 80); device key pinned for the contact.");
                if (firstFrame >= 1000 || p50 > 80) Environment.ExitCode = 2;
            }
            catch (Exception ex) { Environment.ExitCode = 1; File.WriteAllText(report, "FAIL " + ex); }
            finally {
                viewer?.Close();
                if (host is not null) await host.GoOfflineAsync(); if (viewerHome is not null) await viewerHome.GoOfflineAsync();
                host?.Close(); viewerHome?.Close(); marker.Close(); if (service is not null) await service.DisposeAsync();
                foreach (string name in new[] { identity, other }) if (CngKey.Exists(name)) { using var key = CngKey.Open(name); key.Delete(); }
                root.Close();
            }
        };
        Application.Run(root);
    }

    // The viewer page's probeClick: a click, then the time until the marker pixel changes brightness.
    private static async Task<double> Probe(ViewerForm viewer, double px, double py, bool light)
    {
        string expression = FormattableString.Invariant($"probeClick(0.5, 0.5, {px}, {py}, {(light ? "true" : "false")})");
        string json = await viewer.Page.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression, awaitPromise = true, returnByValue = true }));
        using var result = JsonDocument.Parse(json);
        return result.RootElement.GetProperty("result").GetProperty("value").GetDouble();
    }

    private static void CheckSettingsDefaults(string folder)
    {
        const string serviceUrlForTest = "https://saved.example:47834";
        string defaultsFile = Path.Combine(folder, "defaults.json");
        var defaults = ClientSettings.Load(defaultsFile);
        if (defaults.ServiceUrl != ClientSettings.DefaultServiceUrl || defaults.ServicePin != ClientSettings.DefaultServicePin) throw new Exception("Fresh profile did not use public service defaults");
        File.WriteAllText(defaultsFile, "{\"ServiceUrl\":\"\",\"ServicePin\":\"\",\"DeviceId\":\"123456789\"}");
        defaults = ClientSettings.Load(defaultsFile);
        if (defaults.ServiceUrl != ClientSettings.DefaultServiceUrl || defaults.ServicePin != ClientSettings.DefaultServicePin || defaults.DeviceId.Length != 0) throw new Exception("Empty-service migration mismatch");
        defaults.ServiceUrl = serviceUrlForTest; defaults.ServicePin = new string('A', 64); defaults.Save();
        defaults = ClientSettings.Load(defaultsFile);
        if (defaults.ServiceUrl != serviceUrlForTest || defaults.ServicePin != new string('A', 64)) throw new Exception("Saved service settings were overwritten");
    }
    private static IEnumerable<Control> Descendants(Control root) { foreach (Control c in root.Controls) { yield return c; foreach (var child in Descendants(c)) yield return child; } }

    // Records input instead of injecting it; each left-button press flips the marker's colour.
    private sealed class ObservedInput(Form marker) : IInputSink
    {
        internal System.Collections.Concurrent.ConcurrentQueue<InputEvent> Events { get; } = new();
        public void Apply(InputEvent value)
        {
            Events.Enqueue(value);
            if (value is { Kind: "button", Down: true }) marker.BeginInvoke(() => { marker.BackColor = marker.BackColor == Color.Black ? Color.White : Color.Black; marker.Update(); });
        }
        public void ReleaseAll() { }
    }
}
