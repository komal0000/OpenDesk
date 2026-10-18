using System.Net;
using OpenDesk.Core;
using OpenDesk.Relay;
using static OpenDesk.Tests.Program;

namespace OpenDesk.Tests;

internal static class RelayTests
{
    internal static void Register()
    {
        Test("Relay tunnel still rejects a substituted host certificate", async () => {
            await using var relay = new PairingServer(); relay.Start(IPAddress.Loopback, 0);
            int approvals = 0;
            await using var host = new HostService(() => new(8,8,[255,216,255,217]), new RecordingInput(), (_,_) => { approvals++; return Task.FromResult(new Approval(true)); }, 1);
            await host.StartRelayAsync("127.0.0.1", relay.Port);
            await Throws<System.Security.Authentication.AuthenticationException>(() => ViewerSession.ConnectAsync(host.Invitation with { Pin = new string('0',64) }, "Wrong identity", default));
            Check(approvals == 0);
        });
        Test("Two approved viewers through one relay room; per-viewer removal", async () => {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var relay = new PairingServer(); relay.Start(IPAddress.Loopback, 0);
            await using var host = new HostService(() => new(8,8,[255,216,255,217]), new RecordingInput(), (_,_) => Task.FromResult(new Approval(true)), 2);
            await host.StartRelayAsync("127.0.0.1", relay.Port);
            await using var a = await ViewerSession.ConnectAsync(host.Invitation, "A", timeout.Token);
            await using var b = await ViewerSession.ConnectAsync(host.Invitation, "B", timeout.Token);
            Check(host.Coordinator.Viewers.Count == 2);
            var gotA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gotB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ra = a.RunAsync(_ => gotA.TrySetResult(), _ => {}, timeout.Token);
            var rb = b.RunAsync(_ => gotB.TrySetResult(), _ => {}, timeout.Token);
            await Task.WhenAll(gotA.Task, gotB.Task).WaitAsync(timeout.Token);
            host.Coordinator.Remove(host.Coordinator.Viewers.First(v => v.Name == "A").Id);
            await Task.Delay(150, timeout.Token);
            Check(host.Coordinator.Viewers.Count == 1 && host.Coordinator.Viewers[0].Name == "B");
            await host.DisposeAsync();
            try { await Task.WhenAll(ra, rb); } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        });
        Test("Relay rejects unknown rooms without revealing other rooms", async () => {
            await using var relay = new PairingServer(); relay.Start(IPAddress.Loopback, 0);
            using var client = new System.Net.Sockets.TcpClient(); await client.ConnectAsync(IPAddress.Loopback, relay.Port);
            await Throws<IOException>(() => RelayProtocol.JoinViewerAsync(client.GetStream(), new string('0',32), default));
        });
        Test("Relay rooms remain isolated when another host stops", async () => {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await using var relay = new PairingServer(); relay.Start(IPAddress.Loopback, 0);
            await using var h1 = new HostService(() => new(8,8,[255,216,1,217]), new RecordingInput(), (_,_) => Task.FromResult(new Approval(true)), 1);
            await using var h2 = new HostService(() => new(8,8,[255,216,2,217]), new RecordingInput(), (_,_) => Task.FromResult(new Approval(true)), 1);
            await h1.StartRelayAsync("127.0.0.1", relay.Port); await h2.StartRelayAsync("127.0.0.1", relay.Port);
            await using var v1 = await ViewerSession.ConnectAsync(h1.Invitation, "Room 1", timeout.Token);
            await using var v2 = await ViewerSession.ConnectAsync(h2.Invitation, "Room 2", timeout.Token);
            var one = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var two = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var r1 = v1.RunAsync(f => { Check(f.Jpeg[2] == 1); one.TrySetResult(); }, _=>{}, timeout.Token);
            var r2 = v2.RunAsync(f => { Check(f.Jpeg[2] == 2); two.TrySetResult(); }, _=>{}, timeout.Token);
            await Task.WhenAll(one.Task, two.Task).WaitAsync(timeout.Token);
            await h1.DisposeAsync(); Check(h2.Coordinator.Viewers.Count == 1);
            await h2.DisposeAsync();
            try { await Task.WhenAll(r1,r2); } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        });
        Test("Relay rejects duplicate registration, wrong owner and expired joins", async () => {
            await using var relay = new PairingServer(); relay.Start(IPAddress.Loopback, 0);
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            string room = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            string owner = new string('A',64);
            using var registration = await RelayProtocol.ConnectAsync("127.0.0.1", relay.Port, ct.Token);
            await RelayProtocol.SendHelloAsync(registration.GetStream(), new("register",room,owner,DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds()), ct.Token);
            using var duplicate = await RelayProtocol.ConnectAsync("127.0.0.1", relay.Port, ct.Token);
            await Throws<IOException>(() => RelayProtocol.SendHelloAsync(duplicate.GetStream(), new("register",room,owner,DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeMilliseconds()), ct.Token));
            using var wrong = await RelayProtocol.ConnectAsync("127.0.0.1", relay.Port, ct.Token);
            await Throws<IOException>(() => RelayProtocol.SendHelloAsync(wrong.GetStream(), new("host",room,new string('B',64),0), ct.Token));
            await Task.Delay(2100, ct.Token);
            using var viewer = await RelayProtocol.ConnectAsync("127.0.0.1", relay.Port, ct.Token);
            await Throws<IOException>(() => RelayProtocol.JoinViewerAsync(viewer.GetStream(), room, ct.Token));
        });
    }
}
