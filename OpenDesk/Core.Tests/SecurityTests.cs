using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using OpenDesk.Core;
using static OpenDesk.Tests.Program;

namespace OpenDesk.Tests;

internal static class SecurityTests
{
    internal static void Register()
    {
        Test("Pinned TLS and secret authenticate over loopback", () => Pair(false, false));
        Test("Wrong TLS certificate pin rejected", () => Pair(true, false));
        Test("Wrong invitation secret rejected", () => Pair(false, true));
    }
    private static async Task Pair(bool wrongPin, bool wrongSecret)
    {
        using var cert = SecureSession.CreateCertificate();
        var invitation = Invitation.Create("direct", "127.0.0.1", 1234, SecureSession.Pin(cert));
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var connect = new TcpClient();
        await connect.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct.Token);
        using var client = connect;
        using var server = await listener.AcceptTcpClientAsync(ct.Token);
        var accept = SecureSession.AcceptAsync(server.GetStream(), cert, invitation, ct.Token);
        if (wrongPin) {
            await Throws<AuthenticationException>(() => SecureSession.OpenClientAsync(client.GetStream(), invitation with { Pin = new string('0', 64) }, "Tester", ct.Token));
            try { await accept; } catch (Exception ex) when (ex is AuthenticationException or IOException) { }
        } else {
            System.Net.Security.SslStream ssl;
            try { ssl = await SecureSession.OpenClientAsync(client.GetStream(), wrongSecret ? invitation with { Secret = new string('0', 64) } : invitation, "Tester", ct.Token); }
            catch (Exception ex) { throw new Exception($"Client {ex.Message}; server {accept.Exception?.GetBaseException().Message}", ex); }
            using var sslLifetime = ssl;
            if (wrongSecret) await Throws<AuthenticationException>(async () => { await accept; });
            else { var peer = await accept; using var remote = peer.Stream; Check(peer.Name == "Tester" && ssl.IsEncrypted); }
        }
    }
}
