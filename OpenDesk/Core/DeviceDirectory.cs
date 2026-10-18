using System.Security.Cryptography;
using System.Text.Json;

namespace OpenDesk.Core;

public sealed record DeviceRegistration(string Challenge, string PublicKey, string Signature, Invitation Session, string RelayOwner = "");
public sealed record DeviceAddress(string Id, string Name);
public sealed record DeviceLookup(string Id, Invitation Session);
public sealed record DirectoryInfo(int Protocol, int RelayPort);

public static class DeviceProof
{
    public static byte[] Data(string challenge, string publicKey, Invitation session, string owner = "") =>
        JsonSerializer.SerializeToUtf8Bytes(new { Scope = "OpenDesk directory v1", Challenge = challenge, PublicKey = publicKey, Session = session, Owner = owner });
    public static DeviceRegistration Sign(ECDsa key, string challenge, Invitation session, string owner = "")
    {
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        return new(challenge, publicKey, Convert.ToBase64String(key.SignData(Data(challenge, publicKey, session, owner), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)), session, owner);
    }
    public static string NormalizeId(string text)
    {
        string id = text.Replace(" ", "").Replace("-", "").Trim();
        if (id.Length != 9 || id.Any(c => c < '0' || c > '9') || id[0] == '0') throw new FormatException("Enter a nine-digit device address.");
        return id;
    }
    public static string FormatId(string id) { id = NormalizeId(id); return $"{id[..3]} {id[3..6]} {id[6..]}"; }
}

public sealed class DeviceRegistry
{
    private readonly object gate = new();
    private readonly string path;
    private readonly Func<string,bool> roomOnline;
    private readonly Func<Invitation,string,bool> ownsRoom;
    private readonly Dictionary<string,string> ids;
    private readonly Dictionary<string,DateTimeOffset> challenges = [];
    private readonly Dictionary<string,(Invitation Session,DateTimeOffset Until)> leases = [];
    public DeviceRegistry(string path, Func<string,bool>? roomOnline = null, Func<Invitation,string,bool>? ownsRoom = null)
    {
        this.path = path; this.roomOnline = roomOnline ?? (_ => true); this.ownsRoom = ownsRoom ?? ((_,_) => true);
        ids = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid device registry") : [];
        if (ids.Count > 100_000 || ids.Any(p => !Invitation.IsHex(p.Key,64) || DeviceProof.NormalizeId(p.Value) != p.Value) || ids.Values.Distinct().Count() != ids.Count)
            throw new InvalidDataException("Device registry identities are invalid.");
    }
    public string Challenge(DateTimeOffset now)
    {
        lock (gate) {
            foreach (var expired in challenges.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) challenges.Remove(expired);
            if (challenges.Count >= 2048) throw new InvalidOperationException("Directory is busy. Try again shortly.");
            string value = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); challenges.Add(value, now.AddSeconds(60)); return value;
        }
    }
    public DeviceAddress Register(DeviceRegistration request, DateTimeOffset now)
    {
        lock (gate) {
            if (!Invitation.IsHex(request.Challenge,64) || !challenges.Remove(request.Challenge, out var until) || until <= now)
                throw new UnauthorizedAccessException("Registration challenge is invalid or expired.");
            try {
                if (request.PublicKey is null || request.PublicKey.Length > 256 || request.Signature is null || request.Signature.Length > 128 || request.Session is null) throw new CryptographicException();
                byte[] pub = Convert.FromBase64String(request.PublicKey), sig = Convert.FromBase64String(request.Signature);
                using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(pub, out int used);
                if (used != pub.Length || key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value ||
                    !key.VerifyData(DeviceProof.Data(request.Challenge, request.PublicKey, request.Session, request.RelayOwner), sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new CryptographicException();
                var session = request.Session;
                _ = Invitation.Parse((session with { Expires = now.AddMinutes(1) }).Encode(), now);
                if (session.Mode != "relay" || session.Expires <= now || !roomOnline(session.Room) || !ownsRoom(session, request.RelayOwner)) throw new UnauthorizedAccessException("Host relay registration is not online or not owned by this request.");
                string fingerprint = Convert.ToHexString(SHA256.HashData(pub));
                if (!ids.TryGetValue(fingerprint, out string? id)) {
                    if (ids.Count >= 100_000) throw new InvalidOperationException("Directory is full.");
                    do { id = RandomNumberGenerator.GetInt32(100_000_000,1_000_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture); } while (ids.ContainsValue(id));
                    ids.Add(fingerprint,id);
                    try {
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
                        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(ids)); File.Move(path + ".tmp", path, true);
                    } catch { ids.Remove(fingerprint); throw; }
                }
                if (leases.TryGetValue(id, out var existing) && existing.Until > now && existing.Session.Room != session.Room && roomOnline(existing.Session.Room))
                    throw new InvalidOperationException("This device is already online in another window.");
                leases[id] = (session, now.AddSeconds(60)); return new(id, "OpenDesk device");
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { throw new UnauthorizedAccessException("Device ownership proof is invalid."); }
        }
    }
    public DeviceLookup? Lookup(string id, DateTimeOffset now)
    {
        id = DeviceProof.NormalizeId(id);
        lock (gate) {
            if (!leases.TryGetValue(id, out var lease) || lease.Until <= now || !roomOnline(lease.Session.Room)) return null;
            return new(id, lease.Session with { Expires = lease.Until });
        }
    }
}
