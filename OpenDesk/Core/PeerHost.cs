using System.Collections.Concurrent;
using System.Text.Json;

namespace OpenDesk.Core;

// Host side of v2 sessions, independent of the transport: admission and approval, permissions,
// and routing of channel messages. The transport delivers payloads per session and channel, and
// closes a session when asked; HostCoordinator still owns viewers, control and epochs.
public sealed class PeerHost
{
    public const string Control = "control", Input = "input", Files = "files";
    private readonly Func<string, CancellationToken, Task<Approval>> approve;
    private readonly Func<string, string, byte[], CancellationToken, Task> send; // session, channel, packed message
    private readonly Action<string> close;
    private readonly SemaphoreSlim approvals = new(4, 4);
    // Opened: the viewer's control channel is open, so status updates can reach it.
    private sealed class Peer(Guid viewer) { public Guid Viewer { get; } = viewer; public TextBudget Budget { get; } = new(); public volatile bool Opened; }
    private readonly ConcurrentDictionary<string, Peer> sessions = new();
    public HostCoordinator Coordinator { get; }
    public Func<Guid, Message, CancellationToken, Task>? MessageReceived { get; set; }
    public event Action<Guid>? PeerDisconnected;
    public event Action<string>? Notice;
    public event Action? KeyframeRequested;

    public PeerHost(HostCoordinator coordinator, Func<string, CancellationToken, Task<Approval>> approve,
        Func<string, string, byte[], CancellationToken, Task> send, Action<string> close)
    {
        Coordinator = coordinator; this.approve = approve; this.send = send; this.close = close;
        coordinator.Changed += OnChanged;
    }

    // Asks the host to approve a connection request. Null when approved; otherwise the reason to tell the viewer.
    public async Task<string?> AdmitAsync(string session, string? name, CancellationToken ct)
    {
        if (!Signal.ValidName(name)) return "Invalid display name.";
        if (Coordinator.Viewers.Count >= Coordinator.Capacity) return "The host has reached its viewer limit.";
        if (!approvals.Wait(0)) return "Too many pending requests. Try again shortly.";
        Approval decision;
        try {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(60));
            decision = await approve(name!.Trim(), deadline.Token).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return "The host did not answer in time."; }
        finally { approvals.Release(); }
        if (!decision.Accept) return "The host declined the request.";
        try { sessions[session] = new(Coordinator.Add(name!.Trim(), decision.Control, decision.Clipboard, decision.Files)); }
        catch (InvalidOperationException) { return "The host has reached its viewer limit."; }
        return null;
    }
    public string? SessionOf(Guid viewer) => sessions.FirstOrDefault(p => p.Value.Viewer == viewer).Key;

    // The viewer's control channel opened: tell it what it may do. Status changes from now on reach it too;
    // marking it first means none is missed between reading the status and sending it.
    public Task OpenedAsync(string session, CancellationToken ct)
    {
        if (!sessions.TryGetValue(session, out var s)) return Task.CompletedTask;
        s.Opened = true;
        return send(session, Control, Wire.Pack(Wire.Json(MessageKind.Accepted, Coordinator.Status(s.Viewer))), ct);
    }

    // Throws InvalidDataException when the viewer breaks the protocol; the caller then closes the session.
    public async Task ReceiveAsync(string session, string channel, byte[] data, CancellationToken ct)
    {
        if (!sessions.TryGetValue(session, out var s)) return;
        var message = Wire.Unpack(data);
        s.Budget.Check(message);
        if (channel == Input && message.Kind == MessageKind.Input) {
            InputEvent input;
            try { input = Wire.Decode<InputEvent>(message); } catch (JsonException) { throw new InvalidDataException("Invalid input event."); }
            try { Coordinator.HandleInput(s.Viewer, input); }
            catch (System.ComponentModel.Win32Exception) {
                Coordinator.SetController(null);
                Notice?.Invoke("Windows blocked remote input. Control was revoked; viewing continues. Elevated and secure desktops are unsupported.");
            }
            return;
        }
        if (channel == Control && message.Kind == MessageKind.Keyframe) { KeyframeRequested?.Invoke(); return; }
        if ((message.Kind is MessageKind.Chat or MessageKind.Clipboard || SessionStatus.IsFile(message.Kind)) && channel == ChannelFor(message.Kind)) {
            if (!Coordinator.Status(s.Viewer).Allows(message.Kind)) throw new InvalidDataException("This capability was not approved.");
            if (MessageReceived is not null) await MessageReceived(s.Viewer, message, ct);
            return;
        }
        throw new InvalidDataException("Unexpected viewer message.");
    }

    // The transport ended this session.
    public void Closed(string session)
    {
        if (!sessions.TryRemove(session, out var s)) return;
        Coordinator.Remove(s.Viewer); PeerDisconnected?.Invoke(s.Viewer);
    }

    public async Task SendMessageAsync(Guid viewer, Message message, CancellationToken ct)
    {
        string session = SessionOf(viewer) ?? throw new IOException("Viewer disconnected.");
        if (!Coordinator.Status(viewer).Allows(message.Kind)) throw new UnauthorizedAccessException("The host did not grant this capability.");
        await send(session, ChannelFor(message.Kind), Wire.Pack(message), ct);
    }
    // File transfer has its own channel so a large file never delays chat or status.
    public static string ChannelFor(MessageKind kind) => SessionStatus.IsFile(kind) ? Files : Control;

    // Removed viewers are disconnected; every open session learns the new control state and epoch.
    // A session still connecting gets the current status in its Accepted message instead.
    private void OnChanged()
    {
        var connected = Coordinator.Viewers.Select(v => v.Id).ToHashSet();
        foreach (var (session, s) in sessions) {
            if (!connected.Contains(s.Viewer)) close(session);
            else if (s.Opened) _ = SendQuietly(session, Wire.Pack(Wire.Json(MessageKind.Status, Coordinator.Status(s.Viewer))));
        }
    }
    private async Task SendQuietly(string session, byte[] packet)
    {
        try { await send(session, Control, packet, CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { close(session); }
    }
}
