using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenDesk.Core;

public sealed record IceServer(string[] Urls, string? Username = null, string? Credential = null);

// One v2 signaling message over the service WebSocket; Type selects which fields apply:
// challenge, register, registered, ready, offer, answer, reject, error.
public sealed record Signal(string Type, string? Session = null, string? Name = null, string? Sdp = null, string? Signature = null,
    string? Challenge = null, string? PublicKey = null, string? Id = null, IceServer[]? Ice = null, string? Reason = null)
{
    public const int MaxBytes = 32 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this, Options);
    public static Signal Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxBytes) throw new InvalidDataException("Signaling message is too large.");
        try { return JsonSerializer.Deserialize<Signal>(data, Options) is { Type: not null } signal ? signal : throw new InvalidDataException("Missing signaling type."); }
        catch (JsonException) { throw new InvalidDataException("Invalid signaling message."); }
    }
    public static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 64 && !name.Any(char.IsControl);
}

// What a host signs: its registration, and each answer, which binds both peers' DTLS fingerprints to the device key.
public static class SignalProof
{
    public static byte[] Register(string challenge, string publicKey) =>
        JsonSerializer.SerializeToUtf8Bytes(new { Scope = "OpenDesk signaling v2 register", Challenge = challenge, PublicKey = publicKey });
    public static byte[] Answer(string id, string session, string offer, string answer) =>
        JsonSerializer.SerializeToUtf8Bytes(new { Scope = "OpenDesk signaling v2 answer", Id = id, Session = session, Offer = offer, Answer = answer });
    public static string PublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    public static string Sign(ECDsa key, byte[] data) => Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    public static string Fingerprint(string publicKey) => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(publicKey)));
    // A P-256 SubjectPublicKeyInfo, and nothing else.
    public static ECDsa ImportKey(string publicKey)
    {
        if (publicKey is not { Length: > 0 and <= 256 }) throw new CryptographicException("Invalid public key.");
        byte[] encoded = Convert.FromBase64String(publicKey);
        var key = ECDsa.Create();
        try {
            key.ImportSubjectPublicKeyInfo(encoded, out int used);
            if (used != encoded.Length || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value) throw new CryptographicException("Invalid public key.");
            return key;
        }
        catch { key.Dispose(); throw; }
    }
    public static bool Verify(string publicKey, byte[] data, string? signature)
    {
        try {
            using var key = ImportKey(publicKey);
            byte[] value = Convert.FromBase64String(signature ?? "");
            return value.Length == 64 && key.VerifyData(data, value, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return false; }
    }
}

// coturn "use-auth-secret" credentials: username "expiry:user", password base64(HMAC-SHA1(secret, username)).
public static class TurnCredentials
{
    public static IceServer Create(string[] urls, string secret, string user, DateTimeOffset expires)
    {
        string username = $"{expires.ToUnixTimeSeconds()}:{user}";
        return new(urls, username, Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(username))));
    }
}
