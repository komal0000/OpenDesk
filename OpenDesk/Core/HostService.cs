using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;

namespace OpenDesk.Core;

public sealed partial class HostService : IAsyncDisposable
{
    private readonly IScreenSource source;
    private readonly bool tiles = true;
    private readonly Func<string, CancellationToken, Task<Approval>> approve;
    private readonly X509Certificate2 certificate = SecureSession.CreateCertificate();
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<Guid, Peer> peers = new();
    private readonly ConcurrentDictionary<long, Task> connections = new();
    private readonly SemaphoreSlim admission;
    private readonly SemaphoreSlim approvalSlots = new(4, 4);
    private TcpListener? listener;
    private Task? acceptTask, captureTask;
    private long nextConnection;
    private int disposed;
    public HostCoordinator Coordinator { get; }
    public Invitation Invitation { get; private set; } = null!;
    public bool IsRunning => acceptTask is not null && !stop.IsCancellationRequested;
    public event Action<string>? Notice;
    public Func<Guid,Message,CancellationToken,Task>? MessageReceived { get; set; }
    public event Action<Guid>? PeerDisconnected;

    private sealed class Peer(Guid id, CancellationTokenSource cancel, bool frameAcks, bool tiles)
    {
        public bool FrameAcks {get;}=frameAcks;
        // Tile viewers get deltas against what they already hold, paced by FramePacer.
        public bool Tiles {get;}=frameAcks&&tiles;
        public FramePacer Pacer {get;}=new();
        public long Sent, SentEpoch=-1, LastSent;
        public int InFlight;
        // Whole frames replace each other; deltas must wait until the previous one is sent and paced.
        public bool Ready => !Tiles || Volatile.Read(ref Frame) is null && Pacer.CanSend();
        public Guid Id { get; } = id;
        public CancellationTokenSource Cancel { get; } = cancel;
        public void Stop() { try { Cancel.Cancel(); } catch (ObjectDisposedException) { } }
        public Channel<Message> Reliable { get; } = Channel.CreateBounded<Message>(32);
        public Channel<byte> Wake { get; } = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode=BoundedChannelFullMode.DropWrite });
        public Message? Frame;
        public void Post(Message message) { if(!Reliable.Writer.TryWrite(message))Stop(); else Wake.Writer.TryWrite(1); }
        public void Publish(Message message) { Interlocked.Exchange(ref Frame,message);Wake.Writer.TryWrite(1); }
    }

    public HostService(Func<ScreenFrame> capture, IInputSink input, Func<string, CancellationToken, Task<Approval>> approve, int capacity = 5)
        : this(new FrameSource(capture), input, approve, capacity) { tiles = false; }
    public HostService(IScreenSource source, IInputSink input, Func<string, CancellationToken, Task<Approval>> approve, int capacity = 5)
    {
        this.source = source; this.approve = approve;
        Coordinator = new(input, capacity); admission = new(capacity + 4, capacity + 4);
        Coordinator.Changed += OnCoordinatorChanged;
    }
    public void StartDirect(string advertisedAddress, int port, IPAddress? bind = null)
    {
        if (acceptTask is not null) throw new InvalidOperationException("Already hosting.");
        listener = new(bind ?? IPAddress.Any, port); listener.Start(16);
        Invitation = Core.Invitation.Create("direct", advertisedAddress, ((IPEndPoint)listener.LocalEndpoint).Port, SecureSession.Pin(certificate));
        _ = Core.Invitation.Parse(Invitation.Encode(), DateTimeOffset.UtcNow);
        acceptTask = AcceptDirectAsync(); captureTask = Task.Run(CaptureAsync);
    }
    private void Track(Task task)
    {
        long id = Interlocked.Increment(ref nextConnection); connections[id] = task;
        _ = task.ContinueWith(t => { connections.TryRemove(id, out _); _ = t.Exception; }, TaskScheduler.Default);
    }
    private async Task AcceptDirectAsync()
    {
        try {
            while (!stop.IsCancellationRequested) {
                var client = await listener!.AcceptTcpClientAsync(stop.Token);
                if (!admission.Wait(0)) { client.Dispose(); continue; }
                Track(HandleTransportAsync(client));
            }
        } catch (Exception ex) when (stop.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task HandleTransportAsync(TcpClient client)
    {
        using (client) {
            try { client.NoDelay = true; await ServeAsync(client.GetStream(), stop.Token); }
            catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or JsonException or InvalidOperationException or ObjectDisposedException) {
                if (!stop.IsCancellationRequested) Notice?.Invoke("A connection ended or could not be authorized.");
            }
            finally { admission.Release(); }
        }
    }
    private async Task ServeAsync(Stream transport, CancellationToken ct)
    {
        var authenticated = await SecureSession.AcceptAsync(transport, certificate, Invitation, ct);
        await using var ssl = authenticated.Stream;
        using var peerStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!approvalSlots.Wait(0)) { await Wire.WriteAsync(ssl, Wire.Json(MessageKind.Rejected, "Too many pending requests."), ct); return; }
        Approval decision;
        try {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            decision = await approve(authenticated.Name, deadline.Token).WaitAsync(deadline.Token);
        } finally { approvalSlots.Release(); }
        if (!decision.Accept) { await Wire.WriteAsync(ssl, Wire.Json(MessageKind.Rejected, "The host declined the request."), ct); return; }
        if (Invitation.Expires <= DateTimeOffset.UtcNow) { await Wire.WriteAsync(ssl, Wire.Json(MessageKind.Rejected, "Invitation expired. Ask for a new invitation."), ct); return; }
        Guid id;
        try { id = Coordinator.Add(authenticated.Name, decision.Control,decision.Clipboard,decision.Files); }
        catch (InvalidOperationException) { await Wire.WriteAsync(ssl, Wire.Json(MessageKind.Rejected, "Viewer capacity reached."), ct); return; }
        var peer = new Peer(id, peerStop,authenticated.FrameAcks,authenticated.Tiles&&tiles); peers[id] = peer;
        try {
            using var initialWrite = CancellationTokenSource.CreateLinkedTokenSource(peerStop.Token);
            initialWrite.CancelAfter(TimeSpan.FromSeconds(5));
            await Wire.WriteAsync(ssl, Wire.Json(MessageKind.Accepted, StatusFor(peer)), initialWrite.Token);
            var sending = SendAsync(); var reading = ReadAsync();
            await Task.WhenAny(sending, reading); peerStop.Cancel(); await Task.WhenAll(sending, reading);

            async Task SendAsync()
            {
                while(await peer.Wake.Reader.WaitToReadAsync(peerStop.Token)) {
                    peer.Wake.Reader.TryRead(out _);
                    for(int batch=0;batch<4&&peer.Reliable.Reader.TryRead(out var reliable);batch++)await Write(reliable);
                    if(peer.Tiles||!peer.FrameAcks||Volatile.Read(ref peer.InFlight)<2){
                        var frame=Interlocked.Exchange(ref peer.Frame,null);
                        if(frame is not null){if(peer.Tiles)peer.Pacer.OnSend(frame.Payload.Length+5,FramePacer.Now);else if(peer.FrameAcks)Interlocked.Increment(ref peer.InFlight);await Write(frame);}
                    }
                    if(peer.Reliable.Reader.TryPeek(out _))peer.Wake.Writer.TryWrite(1);
                }
                async Task Write(Message message) { using var deadline=CancellationTokenSource.CreateLinkedTokenSource(peerStop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(5));await Wire.WriteAsync(ssl,message,deadline.Token); }
            }
            async Task ReadAsync()
            {
                var textBudget=new TextBudget();
                while (!peerStop.IsCancellationRequested) {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(peerStop.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(35));
                    var message = await Wire.ReadAsync(ssl, deadline.Token);
                    textBudget.Check(message);
                    if (message.Kind == MessageKind.Input) {
                        try { Coordinator.HandleInput(id, Wire.Decode<InputEvent>(message)); }
                        catch (System.ComponentModel.Win32Exception) {
                            Coordinator.SetController(null);
                            Notice?.Invoke("Windows blocked remote input. Control was revoked; viewing continues. Elevated and secure desktops are unsupported.");
                        }
                    }
                    else if(message.Kind==MessageKind.FrameAck){
                        // Tile viewers report milliseconds spent receiving and holding the frame.
                        bool valid=peer.Tiles
                            ?message.Payload.Length==4&&peer.Pacer.OnAck(FramePacer.Now,BinaryPrimitives.ReadUInt16BigEndian(message.Payload),BinaryPrimitives.ReadUInt16BigEndian(message.Payload.AsSpan(2)))
                            :peer.FrameAcks&&message.Payload.Length==0&&Interlocked.Decrement(ref peer.InFlight)>=0;
                        if(!valid)throw new InvalidDataException("Unexpected frame acknowledgment.");
                        peer.Wake.Writer.TryWrite(1);
                    }
                    else if(message.Kind is MessageKind.Chat or MessageKind.Clipboard or MessageKind.FileOffer or MessageKind.FileChunk or MessageKind.FileFinish or MessageKind.FileAck) {
                        var permission=Coordinator.Status(id);
                        if(message.Kind==MessageKind.Clipboard&&!permission.Clipboard || message.Kind is >=MessageKind.FileOffer and <=MessageKind.FileAck && !permission.Files)throw new InvalidDataException("This capability was not approved.");
                        if(MessageReceived is not null)await MessageReceived(id,message,peerStop.Token);
                    }
                    else if (message.Kind != MessageKind.Ping) throw new InvalidDataException("Unexpected viewer message.");
                }
            }
        }
        finally { peerStop.Cancel(); peers.TryRemove(id, out _); Coordinator.Remove(id);PeerDisconnected?.Invoke(id); }
    }
    private SessionStatus StatusFor(Peer peer) => Coordinator.Status(peer.Id) with { FrameAcks = peer.FrameAcks, Tiles = peer.Tiles };
    private void OnCoordinatorChanged()
    {
        var connected = Coordinator.Viewers.Select(v => v.Id).ToHashSet();
        foreach (var peer in peers.Values) {
            if (!connected.Contains(peer.Id)) peer.Stop();
            else peer.Post(Wire.Json(MessageKind.Status, StatusFor(peer)));
        }
    }
    private async Task CaptureAsync()
    {
        try {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
            var updates=new Dictionary<long,TileUpdate>();
            while (await timer.WaitForNextTickAsync(stop.Token)) {
                // Capture only when some viewer can take a frame; duplication merges the changes meanwhile.
                if (peers.All(p => !p.Value.Ready)) continue;
                long epoch=Coordinator.Status(Guid.Empty).Epoch,sequence=source.Capture(),now=Environment.TickCount64;
                ScreenFrame? full=null;updates.Clear();
                foreach (var peer in peers.Values){
                    var status=Coordinator.Status(peer.Id);
                    // An unchanged screen is resent every 2 s: it carries the epoch and keeps idle sessions alive.
                    if(status.Epoch!=epoch||!peer.Ready||peer.Sent==sequence&&peer.SentEpoch==epoch&&now-peer.LastSent<2000)continue;
                    if(peer.Tiles){if(!updates.TryGetValue(peer.Sent,out var update))updates[peer.Sent]=update=source.Changes(peer.Sent);peer.Publish(update.Pack(status));peer.Sent=update.Sequence;}
                    else{peer.Publish((full??=source.Frame()).Pack(status));peer.Sent=sequence;}
                    peer.SentEpoch=epoch;peer.LastSent=now;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception) {
            stop.Cancel(); listener?.Stop(); Coordinator.Stop();
            Notice?.Invoke("Screen capture stopped. Check the selected display, then stop and restart hosting.");
        }
    }
    // Whole-frame source (tests, old callers): a new frame object means new content; no tiles.
    private sealed class FrameSource(Func<ScreenFrame> capture) : IScreenSource
    {
        private ScreenFrame? last;
        private long sequence;
        public long Capture(){var next=capture();if(!ReferenceEquals(next,last)){last=next;sequence++;}return sequence;}
        public ScreenFrame Frame()=>last??=capture();
        public TileUpdate Changes(long since)=>throw new NotSupportedException();
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); listener?.Stop(); Coordinator.Stop();
        if (acceptTask is not null) { try { await acceptTask; } catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { } }
        if (captureTask is not null) await captureTask;
        try { await Task.WhenAll(connections.Values); } catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        Coordinator.Changed -= OnCoordinatorChanged;
        certificate.Dispose(); admission.Dispose(); approvalSlots.Dispose(); stop.Dispose();
    }
    public async Task SendMessageAsync(Guid id,Message message,CancellationToken ct)
    {
        if(!peers.TryGetValue(id,out var peer))throw new IOException("Viewer disconnected.");
        var permission=Coordinator.Status(id);
        if(message.Kind==MessageKind.Clipboard&&!permission.Clipboard || message.Kind is >=MessageKind.FileOffer and <=MessageKind.FileAck && !permission.Files)throw new UnauthorizedAccessException("The host did not grant this capability.");
        using var wait=CancellationTokenSource.CreateLinkedTokenSource(ct,stop.Token,peer.Cancel.Token);wait.CancelAfter(TimeSpan.FromSeconds(5));
        await peer.Reliable.Writer.WriteAsync(message,wait.Token);peer.Wake.Writer.TryWrite(1);
    }
}
