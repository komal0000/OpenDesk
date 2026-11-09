using System.Collections.Concurrent;
using System.Net.WebSockets;
using OpenDesk.Core;

namespace OpenDesk.Service;

public sealed record TurnSettings(string Host, int Port, string Secret);

// v2 signaling. A host keeps one socket open while online; a viewer's socket lives until the host
// answers or rejects its offer. The hub relays offer, answer, reject and cancel between them and never
// sees session content: answers are signed by the host's device key and checked by the viewer.
internal sealed class SignalingHub(DeviceRegistry registry, TurnSettings? turn)
{
    private const int SessionsPerHost = 14; // 10 viewers at most, plus 4 pending approvals
    private readonly ConcurrentDictionary<string, HostLink> hosts = new();

    private class Link(WebSocket socket) : SignalSocket(socket)
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task TrySendAsync(Signal signal)
        {
            try { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await SendAsync(signal, deadline.Token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
    private sealed class HostLink(WebSocket socket) : Link(socket)
    {
        public string PublicKey { get; set; } = "";
        public ConcurrentDictionary<string, Link> Viewers { get; } = new();
    }

    public async Task HostAsync(HttpContext context)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var ct = context.RequestAborted;
        var host = new HostLink(socket);
        string? id = null;
        try {
            // Fresh for this socket and never stored, so unregistered sockets cannot exhaust shared state.
            string challenge = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            await host.SendAsync(new("challenge", Challenge: challenge), ct);
            Signal? register;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct)) { deadline.CancelAfter(TimeSpan.FromSeconds(15)); register = await host.ReceiveAsync(deadline.Token); }
            if (register is not { Type: "register", PublicKey: { } key }) return;
            try { id = registry.RegisterSignaling(challenge, key, register.Signature); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException) { await host.TrySendAsync(new("error", Reason: ex.Message)); return; }
            host.PublicKey = key;
            // The newest proven connection wins, so a host that reconnects is not locked out by its stale socket.
            hosts.AddOrUpdate(id, host, (existing, old) => { _ = Replace(old); return host; });
            await host.SendAsync(new("registered", Id: id), ct);
            while (await host.ReceiveAsync(ct) is { } signal) {
                if (signal.Type is not ("answer" or "reject") || signal.Session is not { } session || !host.Viewers.TryRemove(session, out var viewer)) continue;
                await viewer.TrySendAsync(signal.Type == "answer" ? new("answer", Sdp: signal.Sdp, Signature: signal.Signature) : new("reject", Reason: signal.Reason));
                viewer.Done.TrySetResult();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidDataException or IOException) { }
        finally {
            if (id is not null) hosts.TryRemove(new KeyValuePair<string, HostLink>(id, host));
            foreach (var viewer in host.Viewers.Values) viewer.Done.TrySetResult();
            host.Done.TrySetResult();
        }
        static async Task Replace(HostLink old) { await old.TrySendAsync(new("replaced", Reason: "This device went online from another window.")); await old.CloseAsync(); }
    }

    public async Task ConnectAsync(HttpContext context, string rawId)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var ct = context.RequestAborted;
        var viewer = new Link(socket);
        string session = Guid.NewGuid().ToString("N");
        HostLink? host = null;
        try {
            string id;
            try { id = DeviceProof.NormalizeId(rawId); } catch (FormatException) { await viewer.TrySendAsync(new("error", Reason: "Enter a nine-digit device address.")); return; }
            if (!hosts.TryGetValue(id, out host)) { await viewer.TrySendAsync(new("error", Reason: "Device is offline or the address is unknown.")); return; }
            if (host.Viewers.Count >= SessionsPerHost) { await viewer.TrySendAsync(new("error", Reason: "The device is busy. Try again shortly.")); return; }
            await viewer.SendAsync(new("ready", Session: session, PublicKey: host.PublicKey, Ice: IceFor(session)), ct);
            Signal? offer;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct)) { deadline.CancelAfter(TimeSpan.FromSeconds(30)); offer = await viewer.ReceiveAsync(deadline.Token); }
            if (offer is not { Type: "offer", Sdp: { Length: > 0 } sdp } || !Signal.ValidName(offer.Name) || !host.Viewers.TryAdd(session, viewer)) return;
            // Fresh TURN credentials for the host too: its own registration may be hours old.
            await host.SendAsync(new("offer", Session: session, Name: offer.Name, Sdp: sdp, Ice: IceFor(session)), ct);
            // Owner approval (up to 60 s) and ICE gathering come before the answer.
            using var answerWait = CancellationTokenSource.CreateLinkedTokenSource(ct); answerWait.CancelAfter(TimeSpan.FromSeconds(90));
            var closed = viewer.ReceiveAsync(answerWait.Token); // the viewer gave up when this completes
            await Task.WhenAny(viewer.Done.Task, closed, host.Done.Task).WaitAsync(answerWait.Token);
            // Still listed means no answer was relayed: the host went offline (or the viewer gave up).
            if (host.Done.Task.IsCompleted && host.Viewers.ContainsKey(session)) await viewer.TrySendAsync(new("error", Reason: "The device went offline."));
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidDataException or IOException) { }
        finally {
            // Still pending: the host stops waiting for this viewer, closing any approval dialog.
            if (host is not null && host.Viewers.TryRemove(session, out _)) await host.TrySendAsync(new("cancel", Session: session));
            await viewer.CloseAsync();
        }
    }

    private IceServer[] IceFor(string user)
    {
        if (turn is null) return [];
        string address = $"{turn.Host}:{turn.Port}";
        string[] urls = [$"stun:{address}", $"turn:{address}?transport=udp", $"turn:{address}?transport=tcp"];
        return [TurnCredentials.Create(urls, turn.Secret, user, DateTimeOffset.UtcNow.AddHours(1))];
    }
}
