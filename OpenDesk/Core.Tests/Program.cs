using System.Buffers.Binary;
using System.Security.Cryptography;
using OpenDesk.Core;

namespace OpenDesk.Tests;

internal static class Program
{
    private static readonly List<(string Name, Func<Task> Run)> Cases = [];
    internal static void Test(string name, Func<Task> run) => Cases.Add((name, run));
    internal static void Check(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
    internal static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    private static async Task<int> Main()
    {
        var pin = Convert.ToHexString(SHA256.HashData("test certificate"u8));
        Test("Invitation round trip and expiry", () => {
            var i = Invitation.Create("direct", "127.0.0.1", 47831, pin);
            Check(Invitation.Parse(i.Encode(), DateTimeOffset.UtcNow) == i);
            return Throws<FormatException>(() => Task.FromResult(Invitation.Parse(i.Encode(), i.Expires.AddSeconds(1))));
        });
        Test("Reject malformed invitation and invalid fields", async () => {
            await Throws<FormatException>(() => Task.FromResult(Invitation.Parse("garbage", DateTimeOffset.UtcNow)));
            var good = Invitation.Create("direct", "localhost", 47831, pin);
            foreach (var bad in new[] { good with { Version = 99 }, good with { Secret = "00" },
                good with { Port = 0 }, good with { Pin = "00" }, good with { Address = "https://evil/a" },
                good with { Mode = "unknown" }, good with { Room = "bad" } })
                await Throws<FormatException>(() => Task.FromResult(Invitation.Parse(bad.Encode(), DateTimeOffset.UtcNow)));
        });
        Test("Wire round trip", async () => {
            using var ms = new MemoryStream();
            await Wire.WriteAsync(ms, new(MessageKind.Status, "hello"u8.ToArray()), default);
            ms.Position = 0;
            var msg = await Wire.ReadAsync(ms, default);
            Check(msg.Kind == MessageKind.Status && msg.Payload.SequenceEqual("hello"u8.ToArray()));
        });
        Test("Reject oversized/negative lengths before allocation", async () => {
            foreach (var length in new[] { -1, Wire.MaxPayload + 1, Wire.MaxControl + 1 }) {
                byte[] h = new byte[5]; h[0] = (byte)MessageKind.Status;
                BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(1), length);
                await Throws<InvalidDataException>(() => Wire.ReadAsync(new MemoryStream(h), default));
            }
        });
        Test("Reject truncated frame and unknown type", async () => {
            await Throws<EndOfStreamException>(() => Wire.ReadAsync(new MemoryStream([1,0,0]), default));
            await Throws<EndOfStreamException>(() => Wire.ReadAsync(new MemoryStream([3,0,0,0,3,1]), default));
            await Throws<InvalidDataException>(() => Wire.ReadAsync(new MemoryStream([255,0,0,0,0]), default));
        });
        Test("Wire honors cancellation", async () => {
            using var cts = new CancellationTokenSource(); cts.Cancel();
            await Throws<OperationCanceledException>(() => Wire.ReadAsync(new MemoryStream([3,0,0,0,0]), cts.Token));
        });
        AddAdditionalTests();
        int failures = 0;
        foreach (var (name, run) in Cases) {
            try { await run().WaitAsync(TimeSpan.FromSeconds(20)); Console.WriteLine($"PASS {name}"); }
            catch (Exception ex) { failures++; Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
        }
        Console.WriteLine($"{Cases.Count - failures}/{Cases.Count} passed");
        return failures == 0 ? 0 : 1;
    }
    private static void AddAdditionalTests() { SecurityTests.Register(); CoordinatorTests.Register(); SessionTests.Register(); RelayTests.Register(); AdversarialTests.Register(); DirectoryTests.Register(); DirectoryIntegrationTests.Register(); SessionToolsTests.Register(); LatencyTests.Register(); }
}
