using System.Net.Security;
using System.Net.Sockets;

namespace OpenDesk.Core;

public sealed class ViewerSession : IAsyncDisposable
{
    private readonly SslStream stream;
    private readonly TcpClient client;
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private int disposed;
    public Func<Message,CancellationToken,Task>? MessageReceived { get; set; }
    private SessionStatus currentStatus;
    public SessionStatus Status { get => Volatile.Read(ref currentStatus); private set => Volatile.Write(ref currentStatus, value); }
    private ViewerSession(TcpClient client, SslStream stream, SessionStatus status) { this.client = client; this.stream = stream; currentStatus = status; }

    public static async Task<ViewerSession> ConnectAsync(Invitation invitation, string name, CancellationToken ct)
    {
        _ = Invitation.Parse(invitation.Encode(), DateTimeOffset.UtcNow);
        var client = new TcpClient();
        try {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await client.ConnectAsync(invitation.Address, invitation.Port, deadline.Token); client.NoDelay = true;
            if (invitation.Mode == "relay") await RelayProtocol.JoinViewerAsync(client.GetStream(), invitation.Room, deadline.Token);
            var ssl = await SecureSession.OpenClientAsync(client.GetStream(), invitation, name, ct,true);
            try {
                using var approvalWait = CancellationTokenSource.CreateLinkedTokenSource(ct); approvalWait.CancelAfter(TimeSpan.FromSeconds(65));
                var message = await Wire.ReadAsync(ssl, approvalWait.Token);
                if (message.Kind == MessageKind.Rejected) throw new UnauthorizedAccessException(Wire.Decode<string>(message));
                if (message.Kind != MessageKind.Accepted) throw new InvalidDataException("Unexpected host response.");
                return new(client, ssl, Wire.Decode<SessionStatus>(message));
            } catch { await ssl.DisposeAsync(); throw; }
        }
        catch { client.Dispose(); throw; }
    }
    private async Task WriteAsync(Message message, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await writing.WaitAsync(deadline.Token);
        try { await Wire.WriteAsync(stream, message, deadline.Token); }
        finally { writing.Release(); }
    }
    public async Task RunAsync(Action<ScreenFrame> frame, Action<SessionStatus> status, CancellationToken ct)
    {
        using var running = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        var heartbeat = HeartbeatAsync(running.Token);
        try {
            status(Status);
            var textBudget=new TextBudget();
            while (!running.IsCancellationRequested) {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(running.Token); deadline.CancelAfter(TimeSpan.FromSeconds(35));
                var message = await Wire.ReadAsync(stream, deadline.Token);
                textBudget.Check(message);
                if (message.Kind == MessageKind.Frame) {
                    var decoded = ScreenFrame.Unpack(message);
                    if (decoded.Status.Epoch >= Status.Epoch) { var next=Status with {Control=decoded.Status.Control,Epoch=decoded.Status.Epoch};if(Status!=next){Status=next;status(Status);} }
                    frame(decoded.Frame);
                    if(Status.FrameAcks)await WriteAsync(new(MessageKind.FrameAck,[]),running.Token);
                }
                else if (message.Kind == MessageKind.Status) { var next = Wire.Decode<SessionStatus>(message); if (next.Epoch >= Status.Epoch) { Status = next; status(Status); } }
                else if(message.Kind is MessageKind.Chat or MessageKind.Clipboard or MessageKind.FileOffer or MessageKind.FileChunk or MessageKind.FileFinish or MessageKind.FileAck) {
                    if(message.Kind==MessageKind.Clipboard&&!Status.Clipboard || message.Kind is >=MessageKind.FileOffer and <=MessageKind.FileAck && !Status.Files)throw new InvalidDataException("Unapproved capability.");
                    if(MessageReceived is not null)await MessageReceived(message,running.Token);
                }
                else throw new InvalidDataException("Unexpected host message.");
            }
        }
        finally { running.Cancel(); try { await heartbeat; } catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { } }
    }
    private async Task HeartbeatAsync(CancellationToken ct)
    {
        try {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(ct)) await WriteAsync(new(MessageKind.Ping, []), ct);
        } catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { if (!ct.IsCancellationRequested) stop.Cancel(); }
    }
    public Task SendInputAsync(InputEvent input, CancellationToken ct)
    {
        if (!Status.Control || !input.IsValid || input.Epoch != Status.Epoch) return Task.CompletedTask;
        return WriteAsync(Wire.Json(MessageKind.Input, input), ct);
    }
    public Task SendMessageAsync(Message message,CancellationToken ct)
    {
        if(message.Kind==MessageKind.Clipboard&&!Status.Clipboard || message.Kind is >=MessageKind.FileOffer and <=MessageKind.FileAck && !Status.Files)throw new UnauthorizedAccessException("The host did not grant this capability.");
        if(message.Kind is not (MessageKind.Chat or MessageKind.Clipboard or MessageKind.FileOffer or MessageKind.FileChunk or MessageKind.FileFinish or MessageKind.FileAck))throw new InvalidOperationException("Invalid session action.");
        return WriteAsync(message,ct);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); client.Dispose(); await stream.DisposeAsync();
        // Concurrent input callbacks can still unwind: do not dispose their semaphore.
    }
}
