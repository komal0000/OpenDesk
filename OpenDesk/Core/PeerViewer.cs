namespace OpenDesk.Core;

// Viewer side of a v2 session, independent of the transport: acceptance, status, permissions and messages.
// Video and input do not pass through here; the viewer page handles them directly. A host that declines
// does so over signaling, before any session exists.
public sealed class PeerViewer(Func<string, byte[], CancellationToken, Task> send)
{
    private readonly TaskCompletionSource<SessionStatus> accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBudget budget = new();
    private SessionStatus status = new(false, -1, "Waiting for approval");
    public SessionStatus Status => Volatile.Read(ref status);
    public Task<SessionStatus> Accepted => accepted.Task;
    public event Action<SessionStatus>? StatusChanged;
    public Func<Message, CancellationToken, Task>? MessageReceived { get; set; }

    // Throws InvalidDataException when the host breaks the protocol.
    public async Task ReceiveAsync(string channel, byte[] data, CancellationToken ct)
    {
        var message = Wire.Unpack(data);
        budget.Check(message);
        if (channel == PeerHost.Control && message.Kind is MessageKind.Accepted or MessageKind.Status) {
            var next = Wire.Decode<SessionStatus>(message);
            if (next.Epoch >= Status.Epoch) { Volatile.Write(ref status, next); StatusChanged?.Invoke(next); }
            if (message.Kind == MessageKind.Accepted) accepted.TrySetResult(next);
        }
        else if ((message.Kind is MessageKind.Chat or MessageKind.Clipboard || SessionStatus.IsFile(message.Kind)) && channel == PeerHost.ChannelFor(message.Kind)) {
            if (!Status.Allows(message.Kind)) throw new InvalidDataException("Unapproved capability.");
            if (MessageReceived is not null) await MessageReceived(message, ct);
        }
        else throw new InvalidDataException("Unexpected host message.");
    }

    public Task SendMessageAsync(Message message, CancellationToken ct)
    {
        if (message.Kind is not (MessageKind.Chat or MessageKind.Clipboard) && !SessionStatus.IsFile(message.Kind)) throw new InvalidOperationException("Invalid session action.");
        if (!Status.Allows(message.Kind)) throw new UnauthorizedAccessException("The host did not grant this capability.");
        return send(PeerHost.ChannelFor(message.Kind), Wire.Pack(message), ct);
    }
}
