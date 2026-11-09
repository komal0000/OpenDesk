using System.Collections.Concurrent;
using OpenDesk.Core;
using SIPSorcery.Net;

namespace OpenDesk.Transport;

// One WebRTC peer connection carrying the v2 data channels (no media tracks). The viewer creates the
// channels and offers; a host answers. Descriptions carry all ICE candidates (no trickle), gathered for
// at most three seconds, as the browser pages do.
public sealed class WebRtcPeer : IDisposable
{
    public const string Control = PeerHost.Control, Input = PeerHost.Input, Files = PeerHost.Files, Video = "video";
    public static readonly string[] Channels = [Control, Input, Files, Video];
    private const ulong Backlog = 1024 * 1024;
    private static readonly TimeSpan GatherLimit = TimeSpan.FromSeconds(3);
    private readonly RTCPeerConnection pc;
    private readonly ConcurrentDictionary<string, RTCDataChannel> channels = new();
    private readonly TaskCompletionSource controlOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource gathered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string> candidates = [];
    private int disposed;

    // Messages per channel, in order per channel, on the transport's own threads.
    public event Action<string, byte[]>? Received;
    public event Action<RTCPeerConnectionState>? StateChanged;
    public event Action<string>? ChannelOpened;
    public RTCPeerConnectionState State => pc.connectionState;
    public Task ControlOpen => controlOpen.Task;

    public WebRtcPeer(IEnumerable<OpenDesk.Core.IceServer> iceServers)
    {
        var config = new RTCConfiguration { iceServers = [] };
        foreach (var server in iceServers)
            foreach (var url in server.Urls)
                config.iceServers.Add(new RTCIceServer { urls = url, username = server.Username, credential = server.Credential });
        pc = new RTCPeerConnection(config);
        pc.onconnectionstatechange += state => StateChanged?.Invoke(state);
        pc.onicegatheringstatechange += state => { if (state == RTCIceGatheringState.complete) gathered.TrySetResult(); };
        pc.onicecandidate += candidate => { if (candidate is not null) lock (candidates) candidates.Add(candidate.candidate); };
        pc.ondatachannel += Attach;
    }

    // Viewer: creates the four channels and returns the offer to send through signaling.
    public async Task<string> CreateOfferAsync(CancellationToken ct)
    {
        foreach (var label in Channels) Attach(await pc.createDataChannel(label, new RTCDataChannelInit { ordered = true }));
        var offer = pc.createOffer(null);
        await pc.setLocalDescription(offer);
        return await DescriptionAsync(ct);
    }

    // Host (tests and future hosts): applies a viewer's offer and returns the answer.
    public async Task<string> AnswerAsync(string offer, CancellationToken ct)
    {
        if (pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offer }) != SetDescriptionResultEnum.OK)
            throw new InvalidDataException("The offer could not be applied.");
        await pc.setLocalDescription(pc.createAnswer(null));
        return await DescriptionAsync(ct);
    }

    public void ApplyAnswer(string answer)
    {
        var result = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answer });
        if (result != SetDescriptionResultEnum.OK) throw new InvalidDataException("The host's answer could not be applied: " + result);
    }

    // The local description once gathering completes (or three seconds pass), with every candidate found.
    private async Task<string> DescriptionAsync(CancellationToken ct)
    {
        try { await gathered.Task.WaitAsync(GatherLimit, ct); } catch (TimeoutException) { }
        string sdp = pc.localDescription.sdp.ToString();
        string[] found; lock (candidates) found = [.. candidates];
        return WithCandidates(sdp, found);
    }

    // Adds candidates gathered after the description was created (server reflexive, relay) to its media section.
    public static string WithCandidates(string sdp, IEnumerable<string> found)
    {
        var lines = sdp.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
        var missing = found.Select(c => c.StartsWith("candidate:", StringComparison.Ordinal) ? "a=" + c : "a=candidate:" + c)
            .Where(c => !lines.Contains(c)).Distinct().ToList();
        if (missing.Count > 0) {
            int last = lines.FindLastIndex(l => l.StartsWith("a=candidate:", StringComparison.Ordinal));
            if (last < 0) last = lines.FindIndex(l => l.StartsWith("m=", StringComparison.Ordinal));
            if (last < 0) throw new InvalidDataException("The local description has no media section.");
            lines.InsertRange(last + 1, missing);
        }
        return string.Join("\r\n", lines) + "\r\n";
    }

    private void Attach(RTCDataChannel channel)
    {
        if (!Channels.Contains(channel.label) || !channels.TryAdd(channel.label, channel)) { channel.close(); return; }
        channel.onmessage += (_, _, data) => Received?.Invoke(channel.label, data ?? []);
        channel.onopen += () => Opened(channel.label);
        if (channel.readyState == RTCDataChannelState.open) Opened(channel.label);
    }
    private void Opened(string label)
    {
        ChannelOpened?.Invoke(label);
        if (label == Control) controlOpen.TrySetResult();
    }

    public bool IsOpen(string channel) => channels.TryGetValue(channel, out var c) && c.readyState == RTCDataChannelState.open;
    public ulong Buffered(string channel) => channels.TryGetValue(channel, out var c) ? c.bufferedAmount : 0;

    // Sends at once without waiting; false when the channel is not open.
    public bool TrySend(string channel, byte[] data)
    {
        if (!channels.TryGetValue(channel, out var c) || c.readyState != RTCDataChannelState.open) return false;
        try { c.send(data, 0, data.Length); return true; }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or IOException) { return false; }
    }
    // Sends, then waits until the channel has drained below 1 MB, so large transfers cannot pile up.
    public async Task SendAsync(string channel, byte[] data, CancellationToken ct)
    {
        if (!TrySend(channel, data)) throw new IOException("The session channel is closed.");
        while (Buffered(channel) > Backlog) {
            await Task.Delay(5, ct);
            if (!IsOpen(channel)) throw new IOException("The session channel is closed.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1) return;
        try { pc.close(); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or IOException) { }
        pc.Dispose();
    }
}
