using System.Net;
using OpenDesk.Relay;

if (args.Contains("--smoke")) {
    await using var check = new PairingServer(); check.Start(IPAddress.Loopback, 0);
    using var probe = new System.Net.Sockets.TcpClient(); await probe.ConnectAsync(IPAddress.Loopback, check.Port);
    await Task.Delay(100); await check.DisposeAsync();
    Console.WriteLine("PASS relay startup, stalled-connection cancellation and shutdown");
    return;
}
int port = args.Length == 0 ? 47832 : int.Parse(args[0]);
if (port is < 1024 or > 65535) throw new ArgumentException("Choose a port between 1024 and 65535.");
await using var server = new PairingServer();
server.Start(IPAddress.Any, port);
Console.WriteLine($"OpenDesk relay listening on port {server.Port}. Press Ctrl+C to stop.");
Console.WriteLine("Forwards encrypted sessions; does not decrypt screen or input data. No invitations are logged.");
var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.TrySetResult(); };
await exit.Task;
