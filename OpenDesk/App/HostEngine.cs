using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using OpenDesk.Core;

namespace OpenDesk.App;

// Online hosting over v2. Keeps the signaling socket to the device service (reconnecting when it
// drops), asks the owner to approve each offer, then lets the hidden host page answer it and signs the
// answer with the device key. Video runs only while someone is watching.
internal sealed class HostEngine : IAsyncDisposable
{
    private static readonly int[] Backoff = [1000, 2000, 5000, 10000];
    // Everything known about one viewer's session, from its offer until its peer connection closes.
    private sealed class Session(CancellationTokenSource cancel, Inbox inbox)
    {
        public CancellationTokenSource Cancel { get; } = cancel;
        public Inbox Inbox { get; } = inbox;
        public TaskCompletionSource<string> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Route { get; set; }
    }
    private readonly WebView2 page;
    private readonly ClientSettings settings;
    private readonly ECDsa key;
    private readonly ChannelBridge bridge;
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<string, Session> sessions = new();
    private readonly object videoGate = new();
    private VideoPipeline? video;
    private Rectangle display;
    private VideoPipeline.Profile profile;
    private SignalingClient? signaling;
    private Task? maintaining;
    private string id = "";
    private int disposed;
    internal PeerHost Peers { get; }
    internal HostCoordinator Coordinator => Peers.Coordinator;
    internal bool IsConnected => signaling is not null;
    internal event Action<string>? Notice;
    internal event Action<bool>? ConnectionChanged;
    // "direct · 23 ms" or "relayed · 80 ms" per viewer, as the host page reports it.
    internal string? Route(Guid viewer) => Peers.SessionOf(viewer) is { } s && sessions.TryGetValue(s, out var session) ? session.Route : null;

    // The host page lives in a 1-pixel WebView2 inside `owner`, created here and disposed with this engine.
    // Input goes to Windows on the selected monitor unless a test supplies its own sink.
    internal HostEngine(Control owner, ClientSettings settings, ECDsa key, IInputSink? input, Func<string, CancellationToken, Task<Approval>> approve,
        int capacity, Rectangle display, VideoPipeline.Profile profile)
    {
        page = new WebView2 { Size = new(1, 1), TabStop = false };
        owner.Controls.Add(page);
        this.settings = settings; this.key = key; this.display = display; this.profile = profile;
        bridge = new(page);
        Peers = new PeerHost(new HostCoordinator(input ?? new WindowsInput(() => this.display), capacity), approve, bridge.SendAsync, ClosePage);
        Peers.KeyframeRequested += () => video?.RequestKeyframe();
        Peers.Notice += text => Notice?.Invoke(text);
        Coordinator.Changed += OnViewersChanged;
    }

    // Opens the host page and registers this device (together); throws IOException when the service cannot be reached.
    internal async Task<string> StartAsync(CancellationToken ct)
    {
        await Task.WhenAll(WebEngine.OpenAsync(page, "host.html", OnPage), ConnectAsync(ct));
        ConnectionChanged?.Invoke(true);
        maintaining = MaintainAsync();
        return id;
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        var client = await SignalingClient.ConnectAsync(settings.ServiceUrl, settings.ServicePin, "v2/host", ct);
        try {
            var timeout = TimeSpan.FromSeconds(15);
            var challenge = await client.ReceiveAsync(timeout, ct);
            if (challenge is not { Type: "challenge", Challenge: { } value }) throw new IOException("Unexpected device service response.");
            string publicKey = SignalProof.PublicKey(key), signature;
            lock (key) signature = SignalProof.Sign(key, SignalProof.Register(value, publicKey));
            await client.SendAsync(new("register", PublicKey: publicKey, Signature: signature), ct);
            var registered = await client.ReceiveAsync(timeout, ct);
            if (registered is not { Type: "registered", Id: { } assigned }) throw new IOException("Unexpected device service response.");
            id = DeviceProof.NormalizeId(assigned);
            signaling = client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    // Receives offers and cancellations; reconnects with backoff when the socket drops.
    private async Task MaintainAsync()
    {
        int failures = 0;
        while (!stop.IsCancellationRequested) {
            try {
                if (signaling is null) { await ConnectAsync(stop.Token); failures = 0; ConnectionChanged?.Invoke(true); }
                while (true) {
                    var signal = await signaling!.ReceiveAsync(stop.Token);
                    if (signal.Type == "offer") _ = AnswerAsync(signal);
                    else if (signal.Type == "cancel" && signal.Session is { } session && sessions.TryGetValue(session, out var pending)) pending.Cancel.Cancel();
                    else if (signal.Type == "replaced") {
                        // Another window took over this device ID: stay offline here.
                        await DropSignalingAsync();
                        Notice?.Invoke(signal.Reason ?? "This device went online from another window.");
                        return;
                    }
                    else if (signal.Type == "error") throw new IOException(signal.Reason ?? "The device service ended the connection.");
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException) {
                await DropSignalingAsync();
                try { await Task.Delay(Backoff[Math.Min(failures++, Backoff.Length - 1)], stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }
    private async Task DropSignalingAsync()
    {
        if (signaling is null) return;
        await signaling.DisposeAsync(); signaling = null;
        ConnectionChanged?.Invoke(false);
    }

    // Approval first: no peer connection exists, and nothing about this host's network is revealed,
    // until the owner accepts.
    private async Task AnswerAsync(Signal offer)
    {
        if (offer is not { Session: { Length: 32 } sessionId, Sdp: { Length: > 0 } sdp }) return;
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var session = new Session(cancel, new Inbox((channel, data, ct) => Peers.ReceiveAsync(sessionId, channel, data, ct), _ => {
            Notice?.Invoke("A viewer broke the session protocol and was disconnected."); ClosePage(sessionId);
        }, cancel.Token));
        if (!sessions.TryAdd(sessionId, session)) { cancel.Cancel(); return; }
        bool admitted = false;
        try {
            string? reason = await Peers.AdmitAsync(sessionId, offer.Name, cancel.Token);
            if (reason is not null) { await SendAsync(new("reject", Session: sessionId, Reason: reason)); Forget(sessionId); return; }
            admitted = true;
            WebEngine.Post(page, new { t = "offer", session = sessionId, sdp, ice = offer.Ice ?? [] });
            string reply = await session.Answer.Task.WaitAsync(TimeSpan.FromSeconds(15), cancel.Token), signature;
            lock (key) signature = SignalProof.Sign(key, SignalProof.Answer(id, sessionId, sdp, reply));
            await SendAsync(new("answer", Session: sessionId, Sdp: reply, Signature: signature));
            // A viewer that never connects (it gave up, or its network blocks every path) must not keep its slot.
            await session.Opened.Task.WaitAsync(TimeSpan.FromSeconds(30), cancel.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException) {
            if (admitted) ClosePage(sessionId); else Forget(sessionId);
        }
    }
    private async Task SendAsync(Signal signal)
    {
        var client = signaling ?? throw new IOException("Not connected to the device network.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await client.SendAsync(signal, deadline.Token);
    }
    // Ends a session's peer connection; the page then reports it closed.
    private void ClosePage(string session) => WebEngine.Post(page, new { t = "close", session });
    private void Forget(string session)
    {
        if (!sessions.TryRemove(session, out var s)) return;
        s.Inbox.Complete(); s.Cancel.Cancel();
        Peers.Closed(session);
    }

    // Messages from the host page (UI thread). Messages for unknown or ended sessions are dropped.
    private void OnPage(JsonElement m)
    {
        if (bridge.TryAcknowledge(m)) return;
        string type = m.GetProperty("t").GetString() ?? "";
        if (type == "keyframe") { video?.RequestKeyframe(); return; }
        if (type == "error") { Notice?.Invoke("Session engine: " + m.GetProperty("message").GetString()); return; }
        string sessionId = m.TryGetProperty("session", out var s) ? s.GetString() ?? "" : "";
        if (!sessions.TryGetValue(sessionId, out var session)) return;
        switch (type) {
            case "answer": session.Answer.TrySetResult(m.GetProperty("sdp").GetString() ?? ""); break;
            case "open": session.Opened.TrySetResult(); _ = AcceptAsync(sessionId); video?.RequestKeyframe(); break;
            case "recv": if (!session.Inbox.Post(m)) { Notice?.Invoke("A viewer sent data too fast and was disconnected."); ClosePage(sessionId); } break;
            case "closed": Forget(sessionId); break;
            case "route": session.Route = $"{(m.GetProperty("relay").GetBoolean() ? "relayed" : "direct")} · {m.GetProperty("rtt").GetInt32()} ms"; break;
        }
    }

    // Tells the viewer its permissions; a viewer that cannot receive them is disconnected.
    private async Task AcceptAsync(string session)
    {
        try { await Peers.OpenedAsync(session, stop.Token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { ClosePage(session); }
    }

    // Video runs while at least one viewer is approved. The coordinator is read before taking videoGate,
    // because ChangeDisplay takes the two locks in the other order.
    private void OnViewersChanged()
    {
        int viewers = Coordinator.Viewers.Count;
        long generation = Coordinator.Status(Guid.Empty).Display;
        lock (videoGate) {
            if (viewers > 0 && video is null && disposed == 0)
                video = new VideoPipeline(display, profile, generation, OnEncoded, text => Notice?.Invoke(text));
            else if (viewers == 0 && video is not null) { video.Dispose(); video = null; }
        }
    }
    private void OnEncoded(byte[] data, bool keyframe, long generation) => WebEngine.Post(page, new { t = "video", key = keyframe, display = generation, data });
    // Monitor or quality change: input stops, the epoch moves on, and video restarts at the new size.
    internal void ChangeDisplay(Rectangle bounds, VideoPipeline.Profile quality) =>
        Coordinator.ChangeDisplay(generation => { lock (videoGate) { display = bounds; profile = quality; video?.Select(bounds, quality, generation); } });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        if (maintaining is not null) await maintaining;
        if (signaling is not null) { await signaling.DisposeAsync(); signaling = null; }
        Coordinator.Stop();
        lock (videoGate) { video?.Dispose(); video = null; }
        bridge.FailAll();
        Coordinator.Changed -= OnViewersChanged;
        page.Parent?.Controls.Remove(page); page.Dispose();
    }
}
