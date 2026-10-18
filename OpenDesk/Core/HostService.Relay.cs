using System.Net.Sockets;
using System.Security.Cryptography;

namespace OpenDesk.Core;

public sealed partial class HostService
{
    public string RelayOwner { get; private set; } = "";
    public async Task StartRelayAsync(string address, int port, bool persistentAddress = false)
    {
        if (acceptTask is not null) throw new InvalidOperationException("Already hosting.");
        Invitation = Core.Invitation.Create("relay", address, port, SecureSession.Pin(certificate));
        _ = Core.Invitation.Parse(Invitation.Encode(), DateTimeOffset.UtcNow);
        if (persistentAddress) Invitation = Invitation with { Expires = DateTimeOffset.MaxValue };
        var owner = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        RelayOwner = owner;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var registration = await RelayProtocol.ConnectAsync(address, port, deadline.Token);
        try {
            await RelayProtocol.SendHelloAsync(registration.GetStream(), new(persistentAddress ? "register-id" : "register", Invitation.Room, owner, persistentAddress ? 0 : Invitation.Expires.ToUnixTimeMilliseconds()), deadline.Token);
        } catch { registration.Dispose(); throw; }
        var workers = Enumerable.Range(0, Coordinator.Capacity + 4).Select(_ => RelaySlotAsync(owner)).ToArray();
        acceptTask = Task.WhenAll(workers.Append(MaintainRegistrationAsync(registration)));
        captureTask = Task.Run(CaptureAsync);
    }
    private async Task MaintainRegistrationAsync(TcpClient registration)
    {
        using (registration) {
            try {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
                while (await timer.WaitForNextTickAsync(stop.Token)) await registration.GetStream().WriteAsync(new byte[] { 1 }, stop.Token);
            } catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) {
                if (!stop.IsCancellationRequested) {
                    stop.Cancel(); Coordinator.Stop();
                    Notice?.Invoke("Relay disconnected. Stop and restart hosting to reconnect.");
                }
            }
        }
    }
    private async Task RelaySlotAsync(string owner)
    {
        while (!stop.IsCancellationRequested && Invitation.Expires > DateTimeOffset.UtcNow) {
            try {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); wait.CancelAfter(TimeSpan.FromSeconds(70));
                using var client = await RelayProtocol.ConnectAsync(Invitation.Address, Invitation.Port, wait.Token);
                await RelayProtocol.SendHelloAsync(client.GetStream(), new("host", Invitation.Room, owner, 0), wait.Token);
                await ServeAsync(client.GetStream(), stop.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or System.Security.Authentication.AuthenticationException or System.Text.Json.JsonException or InvalidOperationException or ObjectDisposedException) { }
            if (!stop.IsCancellationRequested) { try { await Task.Delay(250, stop.Token); } catch (OperationCanceledException) { } }
        }
    }
}
