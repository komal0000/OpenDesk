using System.Text.Json;
using System.Threading.Channels;
using OpenDesk.Core;
using SIPSorcery.Net;

namespace OpenDesk.Transport;

public sealed record ViewerOptions(string ServiceUrl, string ServicePin, string DisplayName);

// The viewer side of one v2 session, for viewers without a browser engine: signaling through the
// device service, the host's signed answer, then the WebRTC data channels. Same flow as the Windows
// app's ViewerForm and viewer.html.
public sealed class RemoteSession : IAsyncDisposable
{
    private const int InboxSize = 256;
    private readonly WebRtcPeer peer;
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<(string Channel, byte[] Data)> inbox = Channel.CreateBounded<(string, byte[])>(new BoundedChannelOptions(InboxSize) { SingleReader = true });
    private readonly VideoReassembler video = new();
    private readonly Task reading;
    private int ended;

    public string Id { get; }
    // SHA-256 of the host's device key, to remember for this address (trust on first use).
    public string KeyPin { get; }
    public PeerViewer Viewer { get; }
    public SessionStatus Status => Viewer.Status;
    // Complete frames, on a transport thread; handlers must return quickly.
    public event Action<EncodedFrame>? FrameReceived;
    // The session ended, with the reason to show.
    public event Action<string>? Ended;
    public event Action<RTCPeerConnectionState>? ConnectionChanged;

    private RemoteSession(string id, string keyPin, WebRtcPeer peer)
    {
        Id = id; KeyPin = keyPin; this.peer = peer;
        Viewer = new PeerViewer((channel, data, ct) => peer.SendAsync(channel, data, ct));
        peer.Received += OnReceived;
        peer.StateChanged += state => {
            ConnectionChanged?.Invoke(state);
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed) End("Disconnected · connect again to resume.");
        };
        reading = Task.Run(ReadAsync);
    }

    // Finds the device, asks its owner for approval and returns once the host accepted the session.
    // knownKeyPin: the key fingerprint saved for this address, if any. Throws UnauthorizedAccessException
    // when the host declines or its identity does not match, IOException for network failures and
    // TimeoutException when a step takes too long.
    public static async Task<RemoteSession> ConnectAsync(ViewerOptions options, string address, string? knownKeyPin, IProgress<string>? progress, CancellationToken ct)
    {
        string id = DeviceProof.NormalizeId(address);
        if (!Signal.ValidName(options.DisplayName)) throw new FormatException("Set a display name of 1–64 characters in Settings.");
        progress?.Report($"Finding {DeviceProof.FormatId(id)}…");
        await using var signaling = await SignalingClient.ConnectAsync(options.ServiceUrl, options.ServicePin, "v2/connect/" + id, ct);
        var ready = await signaling.ReceiveAsync(TimeSpan.FromSeconds(15), ct);
        if (ready is not { Type: "ready", Session: { } session, PublicKey: { } publicKey }) throw new IOException("Unexpected device service response.");
        string keyPin = SignalProof.Fingerprint(publicKey);
        if (knownKeyPin is not null && !string.Equals(knownKeyPin, keyPin, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("This address now belongs to a different device key. If that computer was reinstalled, remove it from your recent devices and connect again. Otherwise, do not continue.");
        var peer = new WebRtcPeer(ready.Ice ?? []);
        RemoteSession? result = null;
        try {
            string offer = await peer.CreateOfferAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
            await signaling.SendAsync(new("offer", Name: options.DisplayName.Trim(), Sdp: offer), ct);
            progress?.Report("Waiting for the remote host to approve…");
            var reply = await signaling.ReceiveAsync(TimeSpan.FromSeconds(95), ct);
            if (reply.Type == "reject") throw new UnauthorizedAccessException(reply.Reason ?? "The host declined the request.");
            // The signature binds both descriptions, and so both DTLS fingerprints, to the host's device key.
            if (reply is not { Type: "answer", Sdp: { } answer } || !SignalProof.Verify(publicKey, SignalProof.Answer(id, session, offer, answer), reply.Signature))
                throw new UnauthorizedAccessException("The device's answer was not signed by its key, so the connection was stopped.");
            result = new RemoteSession(id, keyPin, peer);
            peer.ApplyAnswer(answer);
            progress?.Report("Approved · connecting…");
            await result.Viewer.Accepted.WaitAsync(TimeSpan.FromSeconds(30), ct);
            return result;
        }
        catch {
            if (result is not null) await result.DisposeAsync(); else peer.Dispose();
            throw;
        }
    }

    private void OnReceived(string channel, byte[] data)
    {
        if (channel == WebRtcPeer.Video) {
            EncodedFrame? frame;
            try { frame = video.Add(data); }
            catch (InvalidDataException) { End("The host sent invalid video; the session was closed."); return; }
            if (frame is not null) FrameReceived?.Invoke(frame);
        }
        else if (channel is WebRtcPeer.Control or WebRtcPeer.Files) {
            if (!inbox.Writer.TryWrite((channel, data))) End("The host sent data too fast; the session was closed.");
        }
        else End("The host sent data on an unexpected channel; the session was closed.");
    }
    // Control and file messages, in order, one at a time.
    private async Task ReadAsync()
    {
        try {
            await foreach (var (channel, data) in inbox.Reader.ReadAllAsync(stop.Token))
                await Viewer.ReceiveAsync(channel, data, stop.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidDataException or JsonException) { End("The host sent invalid data; the session was closed."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { End(ex.Message); }
    }

    // Input goes straight to the host, which checks control and epoch again. False when it could not be sent.
    public bool SendInput(InputEvent input)
    {
        if (!input.IsValid) throw new ArgumentException("Invalid input event.", nameof(input));
        return peer.TrySend(WebRtcPeer.Input, Wire.Pack(Wire.Json(MessageKind.Input, input)));
    }
    // After a decoder error or skipped frames; the host paces keyframes itself.
    public bool RequestKeyframe() => peer.TrySend(WebRtcPeer.Control, Wire.Pack(new(MessageKind.Keyframe, [])));

    private void End(string reason)
    {
        if (Interlocked.Exchange(ref ended, 1) == 1) return;
        stop.Cancel(); inbox.Writer.TryComplete();
        Ended?.Invoke(reason);
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref ended, 1);
        stop.Cancel(); inbox.Writer.TryComplete();
        peer.Dispose();
        try { await reading; } catch (OperationCanceledException) { }
        stop.Dispose();
    }
}
