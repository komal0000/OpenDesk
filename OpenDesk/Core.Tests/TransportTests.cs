using System.Collections.Concurrent;
using System.Security.Cryptography;
using OpenDesk.Core;
using OpenDesk.Service;
using OpenDesk.Transport;
using OpenDesk.Video.FFmpeg;
using static OpenDesk.Tests.Program;

namespace OpenDesk.Tests;

// A v2 host built from Core's PeerHost and the .NET WebRTC peer: registers with the service, approves
// viewers through a callback, answers with a signed description and fans out video like host.html.
internal sealed class SimulatedHost : IAsyncDisposable
{
    private readonly SignalingClient signaling;
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ConcurrentDictionary<string, WebRtcPeer> peers = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    public string Id { get; private set; } = "";
    public string PublicKey => SignalProof.PublicKey(key);
    public PeerHost Host { get; }
    public RecordingInput Input { get; } = new();
    public int Keyframes;
    public bool CorruptSignature { get; set; }

    private SimulatedHost(SignalingClient signaling, Func<string, Approval> approve)
    {
        this.signaling = signaling;
        Host = new PeerHost(new HostCoordinator(Input, 4), (name, _) => Task.FromResult(approve(name)),
            (session, channel, data, ct) => peers.TryGetValue(session, out var p) ? p.SendAsync(channel, data, ct) : throw new IOException("Viewer disconnected."),
            session => { if (peers.TryRemove(session, out var p)) p.Dispose(); Host?.Closed(session); });
        Host.KeyframeRequested += () => Interlocked.Increment(ref Keyframes);
        loop = Task.Run(RunAsync);
    }
    public static async Task<SimulatedHost> StartAsync(string url, string pin, Func<string, Approval> approve)
    {
        var signaling = await SignalingClient.ConnectAsync(url, pin, "v2/host", default);
        var host = new SimulatedHost(signaling, approve);
        var challenge = await signaling.ReceiveAsync(default);
        await signaling.SendAsync(new("register", PublicKey: host.PublicKey, Signature: SignalProof.Sign(host.key, SignalProof.Register(challenge.Challenge!, host.PublicKey))), default);
        host.registered.TrySetResult();
        await host.ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return host;
    }
    private readonly TaskCompletionSource registered = new(TaskCreationOptions.RunContinuationsAsynchronously), ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task RunAsync()
    {
        await registered.Task;
        try {
            while (!stop.IsCancellationRequested) {
                var signal = await signaling.ReceiveAsync(stop.Token);
                if (signal.Type == "registered") { Id = signal.Id!; ready.TrySetResult(); }
                else if (signal is { Type: "offer", Session: { } session, Sdp: { } offer }) _ = AnswerAsync(session, signal.Name, offer, signal.Ice ?? []);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
    }
    private async Task AnswerAsync(string session, string? name, string offer, IceServer[] ice)
    {
        if (await Host.AdmitAsync(session, name, stop.Token) is { } reason) { await signaling.SendAsync(new("reject", Session: session, Reason: reason), stop.Token); return; }
        var peer = new WebRtcPeer(ice);
        peers[session] = peer;
        peer.Received += (channel, data) => {
            try { Host.ReceiveAsync(session, channel, data, stop.Token).GetAwaiter().GetResult(); }
            catch (InvalidDataException) { if (peers.TryRemove(session, out var p)) p.Dispose(); Host.Closed(session); }
        };
        peer.ChannelOpened += label => { if (label == WebRtcPeer.Control) _ = Host.OpenedAsync(session, stop.Token); };
        string answer = await peer.AnswerAsync(offer, stop.Token);
        string signature = SignalProof.Sign(key, SignalProof.Answer(Id, session, offer, CorruptSignature ? answer + " " : answer));
        await signaling.SendAsync(new("answer", Session: session, Sdp: answer, Signature: signature), stop.Token);
    }

    // Like host.html: every peer with an open video channel gets the frame, in 60 KB pieces.
    public int SendVideo(EncodedFrame frame)
    {
        int sent = 0;
        foreach (var peer in peers.Values) {
            if (!peer.IsOpen(WebRtcPeer.Video)) continue;
            foreach (var piece in VideoReassembler.Split(frame)) peer.TrySend(WebRtcPeer.Video, piece);
            sent++;
        }
        return sent;
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        foreach (var peer in peers.Values) peer.Dispose();
        await signaling.DisposeAsync();
        try { await loop; } catch (OperationCanceledException) { }
        key.Dispose();
    }
}

internal static class TransportTests
{
    internal static void Register()
    {
        Test("Video reassembly round trip, one and many pieces", () => {
            var assembler = new VideoReassembler();
            foreach (int size in new[] { 10, VideoReassembler.Piece - VideoReassembler.Header, 200_000 }) {
                var frame = new EncodedFrame(size == 10, 7, 1234.5, RandomNumberGenerator.GetBytes(size));
                EncodedFrame? back = null; int pieces = 0;
                foreach (var piece in VideoReassembler.Split(frame)) { Check(back is null, "Frame completed early"); back = assembler.Add(piece); pieces++; }
                Check(back is { } b && b.Key == frame.Key && b.Display == 7 && b.Timestamp == 1234.5 && b.Data.SequenceEqual(frame.Data), "Frame changed");
                Check(pieces == (size + VideoReassembler.Header + VideoReassembler.Piece - 1) / VideoReassembler.Piece);
            }
            return Task.CompletedTask;
        });
        Test("Video reassembly rejects malformed pieces", async () => {
            var good = VideoReassembler.Split(new EncodedFrame(true, 1, 0, new byte[100])).Single();
            var badFlags = (byte[])good.Clone(); badFlags[0] = 9;
            var zeroLength = (byte[])good.Clone(); zeroLength[17] = zeroLength[18] = zeroLength[19] = zeroLength[20] = 0;
            var huge = (byte[])good.Clone(); huge[17] = 0xFF;
            var overrun = good.Concat(new byte[] { 1 }).ToArray();
            foreach (var bad in new[] { good[..10], badFlags, zeroLength, huge, overrun, new byte[VideoReassembler.Piece + 1] })
                await Throws<InvalidDataException>(() => Task.FromResult(new VideoReassembler().Add(bad)));
        });
        Test("Late ICE candidates are added to the description once", () => {
            string sdp = "v=0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\na=candidate:1 1 udp 1 10.0.0.1 5000 typ host\r\na=end\r\n";
            string merged = WebRtcPeer.WithCandidates(sdp, ["candidate:2 1 udp 1 1.2.3.4 6000 typ srflx", "candidate:1 1 udp 1 10.0.0.1 5000 typ host"]);
            Check(merged == "v=0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\na=candidate:1 1 udp 1 10.0.0.1 5000 typ host\r\na=candidate:2 1 udp 1 1.2.3.4 6000 typ srflx\r\na=end\r\n", merged);
            return Task.CompletedTask;
        });
        Test("FFmpeg decodes an H.264 stream to BGRA pictures", async () => {
            if (!FfmpegDecoder.IsAvailable(out string why)) { Console.WriteLine("  skipped: " + why); return; }
            var units = H264Sample.AccessUnits(await H264Sample.CreateAsync(320, 240, 12));
            Check(units.Count == 12, $"Expected 12 access units, got {units.Count}");
            using var decoder = new FfmpegDecoder();
            int pictures = 0; DecodedPicture? last = null;
            foreach (var unit in units) if (decoder.Decode(unit) is { } p) { pictures++; last = p; }
            Check(pictures == 12, $"Low-delay decoding returned {pictures} of 12 pictures");
            Check(last is { Width: 320, Height: 240, Stride: 1280 }, "Wrong picture size");
            // testsrc2 is colourful: the picture must not be one flat colour, and alpha is opaque.
            var px = last!.Pixels;
            Check(px.Where((_, i) => i % 4 == 3).All(a => a == 255), "Alpha not opaque");
            Check(Enumerable.Range(0, 320 * 240).Select(i => px[i * 4] | px[i * 4 + 1] << 8 | px[i * 4 + 2] << 16).Distinct().Count() > 50, "Picture looks empty");
            await Throws<InvalidDataException>(() => Task.FromResult(new FfmpegDecoder().Decode(new byte[] { 0, 0, 0, 1, 0x67, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 1, 0x68, 0xFF, 0xFF, 0, 0, 0, 1, 0x65, 0xFF, 0xFF, 0xFF })));
        });
        Test(".NET viewer connects through the service, gets approval, video and control", async () => {
            using var cert = SecureSession.CreateCertificate();
            string path = Path.Combine(Path.GetTempPath(), "opendesk-transport-" + Guid.NewGuid().ToString("N"), "devices.json");
            await using var service = await DirectoryHost.StartAsync(path, cert, 0, 0, true);
            string pin = SecureSession.Pin(cert);
            var names = new ConcurrentQueue<string>();
            await using var host = await SimulatedHost.StartAsync(service.Url, pin, name => { names.Enqueue(name); return new(true, Control: true); });
            var options = new ViewerOptions(service.Url, pin, "Linux viewer");
            var progress = new List<string>();
            await using var session = await RemoteSession.ConnectAsync(options, host.Id, null, new SyncProgress(progress.Add), default);
            Check(names.Single() == "Linux viewer", "Approval did not see the viewer's name");
            Check(session.Status.Control && session.KeyPin == SignalProof.Fingerprint(host.PublicKey), "Accepted status or key pin wrong");
            Check(progress.Any(p => p.StartsWith("Waiting for the remote host")));
            // Video: one small frame and one that spans several pieces.
            var frames = new ConcurrentQueue<EncodedFrame>();
            session.FrameReceived += frames.Enqueue;
            await Until(() => host.SendVideo(new(true, session.Status.Display, 1, new byte[] { 0, 0, 0, 1, 0x65 })) == 1, "Video channel never opened");
            var big = new EncodedFrame(false, session.Status.Display, 2, RandomNumberGenerator.GetBytes(150_000));
            host.SendVideo(big);
            await Until(() => frames.Count >= 2, "Frames not received");
            Check(frames.ToArray()[1].Data.SequenceEqual(big.Data), "Large frame corrupted");
            // Input reaches the host's coordinator with the current epoch; stale epochs are dropped there.
            Check(session.SendInput(new InputEvent("button", 0.25, 0.75, Code: 1, Down: true, Epoch: session.Status.Epoch)));
            Check(session.SendInput(new InputEvent("key", Down: true, Epoch: session.Status.Epoch, Key: "KeyA")));
            Check(session.SendInput(new InputEvent("key", Down: true, Epoch: session.Status.Epoch + 9, Key: "KeyB")));
            await Until(() => host.Input.Events.Count >= 2, "Input not delivered");
            await Task.Delay(200);
            Check(host.Input.Events.Count == 2 && host.Input.Events[0] is { Kind: "button", X: 0.25, Y: 0.75 } && host.Input.Events[1].Key == "KeyA", "Wrong input applied");
            Check(session.RequestKeyframe());
            await Until(() => host.Keyframes > 0, "Keyframe request not delivered");
            // The host takes control away: the viewer sees the new status.
            host.Host.Coordinator.SetController(null);
            await Until(() => !session.Status.Control, "Status change not delivered");
            // Chat both ways on the control channel.
            var chats = new ConcurrentQueue<string>();
            session.Viewer.MessageReceived = (m, _) => { chats.Enqueue(Wire.Decode<string>(m)); return Task.CompletedTask; };
            await host.Host.SendMessageAsync(host.Host.Coordinator.Viewers.Single().Id, Wire.Json(MessageKind.Chat, "hello viewer"), default);
            await Until(() => chats.Contains("hello viewer"), "Chat not delivered");
            // The host removes the viewer: the session ends.
            var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Ended += reason => ended.TrySetResult(reason);
            host.Host.Coordinator.Remove(host.Host.Coordinator.Viewers.Single().Id);
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(15));
        });
        Test(".NET viewer refuses declines, changed keys and unsigned answers", async () => {
            using var cert = SecureSession.CreateCertificate();
            string path = Path.Combine(Path.GetTempPath(), "opendesk-transport-" + Guid.NewGuid().ToString("N"), "devices.json");
            await using var service = await DirectoryHost.StartAsync(path, cert, 0, 0, true);
            string pin = SecureSession.Pin(cert);
            bool accept = false;
            await using var host = await SimulatedHost.StartAsync(service.Url, pin, _ => new(accept));
            var options = new ViewerOptions(service.Url, pin, "Viewer");
            await Throws<UnauthorizedAccessException>(async () => { await using var s = await RemoteSession.ConnectAsync(options, host.Id, null, null, default); });
            accept = true;
            await Throws<UnauthorizedAccessException>(async () => { await using var s = await RemoteSession.ConnectAsync(options, host.Id, new string('A', 64), null, default); });
            host.CorruptSignature = true;
            await Throws<UnauthorizedAccessException>(async () => { await using var s = await RemoteSession.ConnectAsync(options, host.Id, null, null, default); });
            await Throws<IOException>(async () => { await using var s = await RemoteSession.ConnectAsync(options, "111111111", null, null, default); });
            await Throws<FormatException>(async () => { await using var s = await RemoteSession.ConnectAsync(options with { DisplayName = " " }, host.Id, null, null, default); });
        });
    }
    private sealed class SyncProgress(Action<string> report) : IProgress<string> { public void Report(string value) { lock (this) report(value); } }
    internal static async Task Until(Func<bool> condition, string message, int timeoutMs = 10000)
    {
        var started = Environment.TickCount64;
        while (!condition()) {
            if (Environment.TickCount64 - started > timeoutMs) throw new Exception(message);
            await Task.Delay(20);
        }
    }
}

// Test video made by the ffmpeg command line: H.264 without B-frames, one access unit per frame, each
// starting with an access unit delimiter, like the Windows host's Media Foundation encoder output.
internal static class H264Sample
{
    public static async Task<byte[]> CreateAsync(int width, int height, int frames)
    {
        string file = Path.Combine(Path.GetTempPath(), $"opendesk-sample-{Guid.NewGuid():N}.h264");
        var info = new System.Diagnostics.ProcessStartInfo("ffmpeg", ["-v", "error", "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate=30", "-frames:v", frames.ToString(),
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-bf", "0", "-g", "1000", "-pix_fmt", "yuv420p", "-x264-params", "aud=1", "-f", "h264", file]) { RedirectStandardError = true };
        using var process = System.Diagnostics.Process.Start(info)!;
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception("ffmpeg failed: " + error);
        try { return await File.ReadAllBytesAsync(file); } finally { File.Delete(file); }
    }
    // Splits an Annex B stream at access unit delimiters (NAL type 9).
    public static List<byte[]> AccessUnits(byte[] stream)
    {
        var starts = new List<int>();
        for (int i = 0; i + 4 < stream.Length; i++)
            if (stream[i] == 0 && stream[i + 1] == 0 && stream[i + 2] == 0 && stream[i + 3] == 1 && (stream[i + 4] & 0x1F) == 9) starts.Add(i);
        return starts.Select((s, n) => stream[s..(n + 1 < starts.Count ? starts[n + 1] : stream.Length)]).ToList();
    }
    public static bool IsKey(byte[] unit)
    {
        for (int i = 0; i + 4 < unit.Length; i++)
            if (unit[i] == 0 && unit[i + 1] == 0 && unit[i + 2] == 1 && (unit[i + 3] & 0x1F) == 5) return true;
        return false;
    }
}
