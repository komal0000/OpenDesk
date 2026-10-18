using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using OpenDesk.Core;

namespace OpenDesk.Relay;

public sealed class PairingServer : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Room> rooms = [];
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim capacity = new(128, 128);
    private readonly ConcurrentDictionary<long, Task> handlers = new();
    private long next;
    private TcpListener? listener;
    private Task? accepting;
    private int disposed;
    public int Port { get; private set; }
    public bool IsRoomOnline(string room) { lock (gate) return rooms.TryGetValue(room, out var r) && r.Expires > DateTimeOffset.UtcNow && !r.Stop.IsCancellationRequested; }
    public bool OwnsRoom(string room, string owner) { lock (gate) return rooms.TryGetValue(room, out var r) && Invitation.IsHex(owner,64) && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(owner),Convert.FromHexString(r.Owner)); }

    private sealed class Room(string owner, DateTimeOffset expires)
    {
        public string Owner { get; } = owner;
        public DateTimeOffset Expires { get; } = expires;
        public CancellationTokenSource Stop { get; } = new();
        public Queue<Slot> Waiting { get; } = new();
        public HashSet<TcpClient> Clients { get; } = [];
    }
    private sealed class Slot
    {
        public TaskCompletionSource<TcpClient> Viewer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Closed { get; set; }
    }
    public void Start(IPAddress bind, int port)
    {
        if (accepting is not null) throw new InvalidOperationException("Already started.");
        listener = new(bind, port); listener.Start(32); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        accepting = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try {
            while (!stop.IsCancellationRequested) {
                var client = await listener!.AcceptTcpClientAsync(stop.Token);
                if (!capacity.Wait(0)) { client.Dispose(); continue; }
                long id = Interlocked.Increment(ref next);
                var handler = HandleAsync(client); handlers[id] = handler;
                _ = handler.ContinueWith(t => { handlers.TryRemove(id, out _); _ = t.Exception; }, TaskScheduler.Default);
            }
        } catch (Exception ex) when (stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task HandleAsync(TcpClient client)
    {
        using (client) {
            try {
                client.NoDelay = true;
                using var initial = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); initial.CancelAfter(TimeSpan.FromSeconds(10));
                var message = await Wire.ReadAsync(client.GetStream(), initial.Token);
                if (message.Kind != MessageKind.Hello) return;
                var hello = Wire.Decode<RelayHello>(message);
                if (!Invitation.IsHex(hello.Room, 32) || hello.Role is not ("register" or "register-id" or "host" or "viewer")) return;
                if (hello.Role is "register" or "register-id") await RegisterAsync(client, hello);
                else if (hello.Role == "host") await HostAsync(client, hello);
                else await ViewerAsync(client, hello);
            } catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or JsonException or ArgumentException or InvalidOperationException or ObjectDisposedException or TimeoutException) { }
            finally { capacity.Release(); }
        }
    }
    private async Task RegisterAsync(TcpClient client, RelayHello hello)
    {
        var expires = hello.Role == "register-id" ? DateTimeOffset.MaxValue : DateTimeOffset.FromUnixTimeMilliseconds(hello.Expires);
        if (!Invitation.IsHex(hello.Owner, 64) || expires <= DateTimeOffset.UtcNow || (hello.Role != "register-id" && expires > DateTimeOffset.UtcNow.AddMinutes(16))) return;
        var room = new Room(hello.Owner, expires);
        lock (gate) {
            if (rooms.Count >= 32 || rooms.ContainsKey(hello.Room)) return;
            rooms.Add(hello.Room, room);
        }
        try {
            await AckAsync(client, stop.Token);
            byte[] heartbeat = new byte[1];
            while (!stop.IsCancellationRequested) {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(35));
                await client.GetStream().ReadExactlyAsync(heartbeat, deadline.Token);
                if (heartbeat[0] != 1) return;
            }
        }
        finally {
            lock (gate) {
                rooms.Remove(hello.Room); room.Stop.Cancel();
                foreach (var member in room.Clients) member.Dispose();
            }
        }
    }
    private static async Task AckAsync(TcpClient client, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await client.GetStream().WriteAsync(new byte[] { 1 }, deadline.Token);
    }
    private async Task HostAsync(TcpClient client, RelayHello hello)
    {
        Room room; var slot = new Slot();
        lock (gate) {
            if (!rooms.TryGetValue(hello.Room, out room!) || room.Expires <= DateTimeOffset.UtcNow ||
                !Invitation.IsHex(hello.Owner, 64) || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hello.Owner), Convert.FromHexString(room.Owner))) return;
            // Remove completed/unpaired slots before enforcing the per-room pending limit.
            var live = room.Waiting.Where(s => !s.Closed).ToArray(); room.Waiting.Clear();
            foreach (var s in live) room.Waiting.Enqueue(s);
            if (room.Waiting.Count >= 14) return;
            room.Waiting.Enqueue(slot); room.Clients.Add(client);
        }
        try {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, room.Stop.Token);
            var viewer = await slot.Viewer.Task.WaitAsync(TimeSpan.FromSeconds(60), linked.Token);
            using (viewer) {
                await AckAsync(client, linked.Token); await AckAsync(viewer, linked.Token);
                var a = PumpAsync(client.GetStream(), viewer.GetStream(), linked.Token);
                var b = PumpAsync(viewer.GetStream(), client.GetStream(), linked.Token);
                await Task.WhenAny(a, b); linked.Cancel();
                try { await Task.WhenAll(a, b); } catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            }
        }
        finally {
            lock (gate) { slot.Closed = true; room.Clients.Remove(client); }
            slot.Done.TrySetResult();
        }
    }
    private async Task ViewerAsync(TcpClient client, RelayHello hello)
    {
        Room room; Slot? slot = null;
        lock (gate) {
            if (!rooms.TryGetValue(hello.Room, out room!) || room.Expires <= DateTimeOffset.UtcNow) return;
            while (room.Waiting.TryDequeue(out var candidate)) { if (!candidate.Closed) { slot = candidate; break; } }
            if (slot is null) return;
            room.Clients.Add(client); slot.Viewer.TrySetResult(client);
        }
        try {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, room.Stop.Token);
            await slot.Done.Task.WaitAsync(linked.Token);
        }
        finally { lock (gate) room.Clients.Remove(client); }
    }
    private static async Task PumpAsync(Stream from, Stream to, CancellationToken ct)
    {
        byte[] buffer = new byte[32 * 1024];
        while (true) {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(45));
            int read = await from.ReadAsync(buffer, deadline.Token); if (read == 0) return;
            await to.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); listener?.Stop();
        if (accepting is not null) await accepting;
        try { await Task.WhenAll(handlers.Values); } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        capacity.Dispose(); stop.Dispose();
    }
}
