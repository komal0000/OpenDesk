using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace OpenDesk.Service;

internal static class ServiceIdentity
{
    internal static X509Certificate2 Load(string folder)
    {
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder,"service-identity.pfx");
        if (!File.Exists(file)) {
            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest("CN=OpenDesk directory",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,true));
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(System.Net.IPAddress.Loopback); request.CertificateExtensions.Add(san.Build());
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddYears(5));
            byte[] pfx = created.Export(X509ContentType.Pfx);
            try { using var output = new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None); output.Write(pfx); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file,UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return X509CertificateLoader.LoadPkcs12FromFile(file,null,X509KeyStorageFlags.UserKeySet);
    }
    internal static void WritePublicConfig(string folder, int port, string pin)
    {
        string file=Path.Combine(folder,"service.json");File.WriteAllText(file+".tmp",JsonSerializer.Serialize(new { Url=$"https://localhost:{port}", Pin=pin }));File.Move(file+".tmp",file,true);
    }
}
