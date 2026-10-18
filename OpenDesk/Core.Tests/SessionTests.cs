using System.Net;
using OpenDesk.Core;
using static OpenDesk.Tests.Program;

namespace OpenDesk.Tests;

internal static class SessionTests
{
    internal static void Register()
    {
        Test("No capture before approval; multiple viewers share one capture source", async () => {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            int captures = 0, approvals = 0;
            var allow = new TaskCompletionSource<Approval>(TaskCreationOptions.RunContinuationsAsynchronously);
            var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = new HostService(() => { Interlocked.Increment(ref captures); return new(8, 8, [255,216,255,217]); },
                new RecordingInput(), (_, _) => { Interlocked.Increment(ref approvals); asked.TrySetResult(); return allow.Task; });
            host.StartDirect("127.0.0.1", 0, IPAddress.Loopback);
            var joining = ViewerSession.ConnectAsync(host.Invitation, "A", timeout.Token);
            await asked.Task.WaitAsync(timeout.Token);
            Check(captures == 0 && host.Coordinator.Viewers.Count == 0);
            allow.SetResult(new(true));
            await using var a = await joining;
            await using var b = await ViewerSession.ConnectAsync(host.Invitation, "B", timeout.Token);
            Check(host.Coordinator.Viewers.Count == 2 && approvals == 2);
            var fa = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var fb = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runA = a.RunAsync(_ => fa.TrySetResult(), _ => {}, timeout.Token);
            var runB = b.RunAsync(_ => fb.TrySetResult(), _ => {}, timeout.Token);
            await Task.WhenAll(fa.Task, fb.Task).WaitAsync(timeout.Token);
            Check(captures > 0);
            host.Coordinator.SetController(host.Coordinator.Viewers[0].Id);
            host.Coordinator.Remove(host.Coordinator.Viewers.First(v => v.Name == "A").Id);
            await Task.Delay(100, timeout.Token);
            Check(host.Coordinator.Viewers.Count == 1);
            await host.DisposeAsync();
            try { await Task.WhenAll(runA, runB); } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            Check(host.Coordinator.Viewers.Count == 0);
        });
        Test("Host rejection closes connection without capturing", async () => {
            int captures = 0;
            await using var host = new HostService(() => { captures++; return new(8,8,[255,216,255,217]); },
                new RecordingInput(), (_, _) => Task.FromResult(new Approval(false)));
            host.StartDirect("127.0.0.1", 0, IPAddress.Loopback);
            await Throws<UnauthorizedAccessException>(() => ViewerSession.ConnectAsync(host.Invitation, "Rejected", default));
            Check(captures == 0 && host.Coordinator.Viewers.Count == 0);
        });
    }
}
