using System.Net.WebSockets;

namespace OpenDesk.Core;

// A v2 signaling connection to the device service, with the same certificate pinning as DirectoryClient.
// Failures surface as IOException (transport, refusals) or InvalidDataException (malformed messages).
public sealed class SignalingClient : IAsyncDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly SignalSocket signals;
    private SignalingClient() => signals = new(socket);

    // path: "v2/host" or "v2/connect/{id}".
    public static async Task<SignalingClient> ConnectAsync(string serviceUrl, string servicePin, string path, CancellationToken ct)
    {
        var root = DirectoryClient.ServiceRoot(serviceUrl);
        var client = new SignalingClient();
        try {
            client.socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            if (DirectoryClient.PinCheck(servicePin) is { } matches)
                client.socket.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => cert is not null && matches(cert.GetRawCertData());
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await client.socket.ConnectAsync(new UriBuilder(root) { Scheme = "wss", Path = path }.Uri, deadline.Token);
            return client;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) {
            await client.DisposeAsync();
            ct.ThrowIfCancellationRequested();
            throw new IOException("Could not reach the device service. Check the network and the service address in Settings.", ex);
        }
        catch { await client.DisposeAsync(); throw; }
    }
    public Task SendAsync(Signal signal, CancellationToken ct) => signals.SendAsync(signal, ct);
    public async Task<Signal> ReceiveAsync(CancellationToken ct) =>
        await signals.ReceiveAsync(ct) ?? throw new IOException("The device service closed the signaling connection.");
    // With a deadline; an "error" from the service becomes an IOException carrying its reason.
    public async Task<Signal> ReceiveAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout);
        Signal signal;
        try { signal = await ReceiveAsync(deadline.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("The device service did not answer in time."); }
        return signal.Type == "error" ? throw new IOException(signal.Reason ?? "The device service refused the request.") : signal;
    }
    public async ValueTask DisposeAsync() { await signals.CloseAsync(); socket.Dispose(); }
}
