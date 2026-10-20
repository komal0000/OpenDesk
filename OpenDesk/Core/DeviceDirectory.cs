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
        string publicKey = SignalProof.PublicKey(key);
        return new(challenge, publicKey, SignalProof.Sign(key, Data(challenge, publicKey, session, owner)), session, owner);
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
            TakeChallenge(request.Challenge, now);
            try {
                if (request.PublicKey is null || request.Session is null ||
                    !SignalProof.Verify(request.PublicKey, DeviceProof.Data(request.Challenge, request.PublicKey, request.Session, request.RelayOwner), request.Signature)) throw new CryptographicException();
                byte[] pub = Convert.FromBase64String(request.PublicKey);
                var session = request.Session;
                _ = Invitation.Parse((session with { Expires = now.AddMinutes(1) }).Encode(), now);
                if (session.Mode != "relay" || session.Expires <= now || !roomOnline(session.Room) || !ownsRoom(session, request.RelayOwner)) throw new UnauthorizedAccessException("Host relay registration is not online or not owned by this request.");
                string id = IdFor(pub);
                if (leases.TryGetValue(id, out var existing) && existing.Until > now && existing.Session.Room != session.Room && roomOnline(existing.Session.Room))
                    throw new InvalidOperationException("This device is already online in another window.");
                leases[id] = (session, now.AddSeconds(60)); return new(id, "OpenDesk device");
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { throw new UnauthorizedAccessException("Device ownership proof is invalid."); }
        }
    }
    // v2: a host proves its device key over the signaling socket and learns its persistent ID. The
    // caller issues `challenge` fresh for that one socket, which makes it single-use; the socket itself is
    // the lease, so nothing else is stored here.
    public string RegisterSignaling(string challenge, string publicKey, string? signature)
    {
        lock (gate) {
            if (!Invitation.IsHex(challenge,64)) throw new UnauthorizedAccessException("Registration challenge is invalid.");
            if (!SignalProof.Verify(publicKey, SignalProof.Register(challenge, publicKey), signature)) throw new UnauthorizedAccessException("Device ownership proof is invalid.");
            return IdFor(Convert.FromBase64String(publicKey));
        }
    }
    // Each challenge proves freshness once. Call under the lock.
    private void TakeChallenge(string challenge, DateTimeOffset now)
    {
        if (!Invitation.IsHex(challenge,64) || !challenges.Remove(challenge, out var until) || until <= now)
            throw new UnauthorizedAccessException("Registration challenge is invalid or expired.");
    }
    // The persistent ID for a public key, assigned and saved on first use. Call under the lock.
    private string IdFor(byte[] publicKey)
    {
        string fingerprint = Convert.ToHexString(SHA256.HashData(publicKey));
        if (ids.TryGetValue(fingerprint, out string? id)) return id;
        if (ids.Count >= 100_000) throw new InvalidOperationException("Directory is full.");
        do { id = RandomNumberGenerator.GetInt32(100_000_000,1_000_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture); } while (ids.ContainsValue(id));
        ids.Add(fingerprint,id);
        try {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(ids)); File.Move(path + ".tmp", path, true);
        } catch { ids.Remove(fingerprint); throw; }
        return id;
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
