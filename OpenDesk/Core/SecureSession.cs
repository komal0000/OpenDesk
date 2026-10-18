using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OpenDesk.Core;

public sealed record Hello(int Version, string Secret, string Name, bool FrameAcks = false);
public sealed record AuthenticatedPeer(SslStream Stream, string Name, bool FrameAcks = false);

public static class SecureSession
{
    public static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=OpenDesk session", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // Schannel requires a temporary CNG key container rather than EphemeralKeySet.
        // UserKeySet without PersistKeySet is cleaned up when the certificate is disposed.
        byte[] pfx = created.Export(X509ContentType.Pfx);
        try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
    public static string Pin(X509Certificate2 cert) => Convert.ToHexString(SHA256.HashData(cert.RawData));

    public static async Task<SslStream> OpenClientAsync(Stream transport, Invitation invitation, string name, CancellationToken ct, bool frameAcks = false)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.Any(char.IsControl))
            throw new ArgumentException("Enter a display name of 1–64 characters.");
        var expected = Convert.FromHexString(invitation.Pin);
        var ssl = new SslStream(transport, false, (_, cert, _, _) => cert is not null &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.GetRawCertData()), expected));
        try {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                TargetHost = "OpenDesk session", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, deadline.Token);
            await Wire.WriteAsync(ssl, Wire.Json(MessageKind.Hello, new Hello(1, invitation.Secret, name.Trim(),frameAcks)), deadline.Token);
            return ssl;
        }
        catch { await ssl.DisposeAsync(); throw; }
    }

    public static async Task<AuthenticatedPeer> AcceptAsync(Stream transport, X509Certificate2 cert, Invitation invitation, CancellationToken ct)
    {
        var ssl = new SslStream(transport, false);
        try {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                ServerCertificate = cert, ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, deadline.Token);
            var message = await Wire.ReadAsync(ssl, deadline.Token);
            if (message.Kind != MessageKind.Hello) throw new AuthenticationException("Missing session authentication.");
            var hello = Wire.Decode<Hello>(message);
            if (invitation.Expires <= DateTimeOffset.UtcNow || hello.Version != 1 ||
                !Invitation.IsHex(hello.Secret, 64) || string.IsNullOrWhiteSpace(hello.Name) ||
                hello.Name.Length > 64 || hello.Name.Any(char.IsControl) ||
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hello.Secret), Convert.FromHexString(invitation.Secret)))
                throw new AuthenticationException("Session invitation rejected.");
            return new(ssl, hello.Name.Trim(),hello.FrameAcks);
        }
        catch { await ssl.DisposeAsync(); throw; }
    }
}
