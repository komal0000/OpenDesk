using System.Net;
using System.Net.Sockets;
using OpenDesk.Core;
using static OpenDesk.Tests.Program;

namespace OpenDesk.Tests;

internal static class AdversarialTests
{
    internal static void Register()
    {
        Test("Pending viewer approvals are bounded at four", async () => {
            int asked = 0;
            await using var host = new HostService(() => throw new Exception("No approved viewers"), new RecordingInput(), (_,ct) => {
                Interlocked.Increment(ref asked);
                return WaitForApproval(ct);
            });
            host.StartDirect("127.0.0.1", 0, IPAddress.Loopback);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var joins = Enumerable.Range(0,5).Select(i => ViewerSession.ConnectAsync(host.Invitation, "Pending " + i, timeout.Token)).ToArray();
            try {
                var rejected = await Task.WhenAny(joins).WaitAsync(timeout.Token);
                await Throws<UnauthorizedAccessException>(async () => { await rejected; });
                Check(asked == 4 && host.Coordinator.Viewers.Count == 0);
            }
            finally {
                await host.DisposeAsync();
                foreach (var join in joins) { try { await join; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { } }
            }
            static async Task<Approval> WaitForApproval(CancellationToken ct) { await Task.Delay(Timeout.Infinite, ct); return new(false); }
        });
        Test("Blocked Windows input revokes control without removing viewers", async () => {
            var blocked = new BlockedInput(); int notices = 0;
            await using var host = new HostService(() => new(8,8,[255,216,255,217]), blocked, (_,_) => Task.FromResult(new Approval(true, true)));
            host.Notice += _ => Interlocked.Increment(ref notices);
            host.StartDirect("127.0.0.1", 0, IPAddress.Loopback);
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, host.Invitation.Port, ct.Token);
            using var ssl = await SecureSession.OpenClientAsync(client.GetStream(), host.Invitation, "Controller", ct.Token);
            var accepted = Wire.Decode<SessionStatus>(await Wire.ReadAsync(ssl, ct.Token));
            Check(accepted.Control);
            await Wire.WriteAsync(ssl, Wire.Json(MessageKind.Input, new InputEvent("key", Code:65, Down:true, Epoch:accepted.Epoch)), ct.Token);
            await Task.Delay(150, ct.Token);
            Check(host.Coordinator.Viewers.Count == 1 && !host.Coordinator.Viewers[0].Control && notices > 0,
                "Blocked input should revoke control and report the restriction while keeping viewing connected");
        });
        Test("Malicious view-only peer cannot bypass host input authorization", async () => {
            var sink = new RecordingInput();
            await using var host = new HostService(() => new(8,8,[255,216,255,217]), sink, (_,_) => Task.FromResult(new Approval(true)));
            host.StartDirect("127.0.0.1", 0, IPAddress.Loopback);
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, host.Invitation.Port, ct.Token);
            using var ssl = await SecureSession.OpenClientAsync(client.GetStream(), host.Invitation, "Raw peer", ct.Token);
            Check((await Wire.ReadAsync(ssl, ct.Token)).Kind == MessageKind.Accepted);
            await Wire.WriteAsync(ssl, Wire.Json(MessageKind.Input, new InputEvent("key", Code:65, Down:true)), ct.Token);
            await Task.Delay(100, ct.Token); Check(sink.Events.Count == 0);
            var id = host.Coordinator.Viewers.Single().Id; host.Coordinator.SetController(id);
            long epoch = host.Coordinator.Status(id).Epoch;
            await Wire.WriteAsync(ssl, Wire.Json(MessageKind.Input, new InputEvent("key", Code:65, Down:true, Epoch:epoch)), ct.Token);
            await Until(() => sink.Events.Count == 1, ct.Token);
            host.Coordinator.SetController(null);
            await Wire.WriteAsync(ssl, Wire.Json(MessageKind.Input, new InputEvent("key", Code:66, Down:true, Epoch:epoch)), ct.Token);
            await Task.Delay(100, ct.Token); Check(sink.Events.Count == 1 && sink.Releases >= 2);
        });
        Test("Five simultaneous viewers accepted; sixth rejected; reconnect asks again", async () => {
            int approvals = 0;
            await using var host = new HostService(() => new(8,8,[255,216,255,217]), new RecordingInput(), (_,_) => { Interlocked.Increment(ref approvals); return Task.FromResult(new Approval(true)); });
            host.StartDirect("127.0.0.1", 0, IPAddress.Loopback);
            var sessions = new List<ViewerSession>();
            try {
                for (int i = 0; i < 5; i++) sessions.Add(await ViewerSession.ConnectAsync(host.Invitation, "Viewer " + i, default));
                Check(host.Coordinator.Viewers.Count == 5);
                await Throws<UnauthorizedAccessException>(() => ViewerSession.ConnectAsync(host.Invitation, "Sixth", default));
                await sessions[0].DisposeAsync();
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await Until(() => host.Coordinator.Viewers.Count == 4, wait.Token);
                sessions.Add(await ViewerSession.ConnectAsync(host.Invitation, "Reconnected", default));
                Check(approvals == 7 && host.Coordinator.Viewers.Count == 5);
            } finally { foreach (var session in sessions) await session.DisposeAsync(); }
        });
        Test("Slow reader does not block another viewer", async () => {
            byte[] largeFrame = new byte[512 * 1024]; largeFrame[0] = 255; largeFrame[1] = 216;
            await using var host = new HostService(() => new(800,600,largeFrame), new RecordingInput(), (_,_) => Task.FromResult(new Approval(true)));
            host.StartDirect("127.0.0.1", 0, IPAddress.Loopback);
            await using var slow = await ViewerSession.ConnectAsync(host.Invitation, "Not reading", default);
            await using var fast = await ViewerSession.ConnectAsync(host.Invitation, "Reading", default);
            int frames = 0; var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var running = fast.RunAsync(_ => { if (Interlocked.Increment(ref frames) >= 8) received.TrySetResult(); }, _ => {}, timeout.Token);
            await received.Task.WaitAsync(timeout.Token);
            await fast.DisposeAsync(); try { await running; } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        });
        Test("Pending approval and stalled TLS handshakes cancel on stop", async () => {
            var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = new HostService(() => throw new Exception("Must not capture"), new RecordingInput(), (_,ct) => { asked.TrySetResult(); return Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => new Approval(false), ct); });
            host.StartDirect("127.0.0.1", 0, IPAddress.Loopback);
            using var stalled = new TcpClient(); await stalled.ConnectAsync(IPAddress.Loopback, host.Invitation.Port);
            var join = ViewerSession.ConnectAsync(host.Invitation, "Waiting", default);
            await asked.Task.WaitAsync(TimeSpan.FromSeconds(4));
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            try { await join; throw new Exception("Unexpected approval"); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        });
        Test("JPEG dimensions bounded before bitmap decoding", async () => {
            byte[] valid = [255,216,255,192,0,17,8,0,8,0,8,3,1,17,0,2,17,0,3,17,0,255,217];
            Check(JpegDimensions.Read(valid) == (8,8));
            valid[9] = 255; valid[10] = 255;
            await Throws<InvalidDataException>(() => Task.FromResult(JpegDimensions.Read(valid)));
            await Throws<InvalidDataException>(() => Task.FromResult(JpegDimensions.Read([255,216,255,192,0,17])));
        });
    }
    private static async Task Until(Func<bool> predicate, CancellationToken ct) { while (!predicate()) await Task.Delay(20, ct); }
    private sealed class BlockedInput : IInputSink
    {
        public void Apply(InputEvent input) => throw new System.ComponentModel.Win32Exception(5, "Blocked by UIPI");
        public void ReleaseAll() { }
    }
}
