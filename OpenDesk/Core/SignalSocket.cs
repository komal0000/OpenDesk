using System.Net.WebSockets;

namespace OpenDesk.Core;

// Either end of a v2 signaling WebSocket: whole JSON signals of at most Signal.MaxBytes, one sender at
// a time. Transport failures surface as IOException.
public class SignalSocket(WebSocket socket)
{
    private readonly SemaphoreSlim sending = new(1, 1);
    private readonly byte[] buffer = new byte[Signal.MaxBytes];

    public async Task SendAsync(Signal signal, CancellationToken ct)
    {
        await sending.WaitAsync(ct);
        try { await socket.SendAsync(signal.Encode(), WebSocketMessageType.Text, true, ct); }
        catch (WebSocketException ex) { throw new IOException("The signaling connection failed.", ex); }
        finally { sending.Release(); }
    }
    // Null when the other end closed the connection.
    public async Task<Signal?> ReceiveAsync(CancellationToken ct)
    {
        int used = 0;
        try {
            while (true) {
                var result = await socket.ReceiveAsync(buffer.AsMemory(used), ct);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                used += result.Count;
                if (result.EndOfMessage) return Signal.Decode(buffer.AsSpan(0, used));
                if (used == buffer.Length) throw new InvalidDataException("Signaling message is too large.");
            }
        }
        catch (WebSocketException ex) { throw new IOException("The signaling connection failed.", ex); }
    }
    public async Task CloseAsync()
    {
        if (socket.State != WebSocketState.Open) return;
        try { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, deadline.Token); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException) { }
    }
}
