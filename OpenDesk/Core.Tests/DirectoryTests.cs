using System.Security.Cryptography;
using OpenDesk.Core;
using static OpenDesk.Tests.Program;

namespace OpenDesk.Tests;

internal static class DirectoryTests
{
    internal static void Register()
    {
        Test("Stable numeric IDs survive registry restart; distinct keys stay distinct", () => {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "opendesk-test-" + Guid.NewGuid().ToString("N"), "devices.json");
            using var a = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var b = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            try {
                var now = DateTimeOffset.UtcNow; var reg = new DeviceRegistry(path);
                var session = Invitation.Create("relay", "localhost", 47832, new string('A',64));
                var aa = reg.Register(DeviceProof.Sign(a, reg.Challenge(now), session), now);
                var bb = reg.Register(DeviceProof.Sign(b, reg.Challenge(now), session), now);
                Check(aa.Id != bb.Id && DeviceProof.NormalizeId(DeviceProof.FormatId(aa.Id)) == aa.Id);
                var restarted = new DeviceRegistry(path);
                Check(restarted.Lookup(aa.Id, now) is null, "Restart must not restore a stale online session");
                var again = restarted.Register(DeviceProof.Sign(a, restarted.Challenge(now), session), now);
                Check(aa.Id == again.Id);
                Check(restarted.Lookup(aa.Id, now)!.Session.Pin == session.Pin);
            } finally { if (File.Exists(path)) File.Delete(path); }
            return Task.CompletedTask;
        });
        Test("Directory rejects forged/replayed challenges and expires offline devices", async () => {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "opendesk-test-" + Guid.NewGuid().ToString("N"), "devices.json");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            try {
                var reg = new DeviceRegistry(path); var now = DateTimeOffset.UtcNow;
                var i = Invitation.Create("relay", "localhost", 47832, new string('A',64));
                var proof = DeviceProof.Sign(key, reg.Challenge(now), i);
                var address = reg.Register(proof, now);
                await Throws<UnauthorizedAccessException>(() => Task.FromResult(reg.Register(proof, now)));
                var bad = DeviceProof.Sign(key, reg.Challenge(now), i) with { Session = i with { Pin = new string('B',64) } };
                await Throws<UnauthorizedAccessException>(() => Task.FromResult(reg.Register(bad, now)));
                Check(reg.Lookup(address.Id, now.AddSeconds(61)) is null);
                Check(new DeviceRegistry(path, _ => false).Lookup(address.Id, now) is null);
                await Throws<FormatException>(() => Task.FromResult(DeviceProof.NormalizeId("12345evil")));
            } finally { if (File.Exists(path)) File.Delete(path); }
        });
    }
}
