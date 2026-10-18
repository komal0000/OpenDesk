using System.Net.Sockets;

namespace OpenDesk.Core;

public sealed record RelayHello(string Role, string Room, string Owner, long Expires);

public static class RelayProtocol
{
    public static async Task SendHelloAsync(Stream stream, RelayHello hello, CancellationToken ct)
    {
        await Wire.WriteAsync(stream, Wire.Json(MessageKind.Hello, hello), ct);
        byte[] result = new byte[1]; await stream.ReadExactlyAsync(result, ct);
        if (result[0] != 1) throw new IOException("Relay room is unavailable, expired, or at capacity.");
    }
    public static Task JoinViewerAsync(Stream stream, string room, CancellationToken ct) => SendHelloAsync(stream, new("viewer", room, "", 0), ct);
    public static async Task<TcpClient> ConnectAsync(string address, int port, CancellationToken ct)
    {
        var client = new TcpClient();
        try { await client.ConnectAsync(address, port, ct); client.NoDelay = true; return client; }
        catch { client.Dispose(); throw; }
    }
}
