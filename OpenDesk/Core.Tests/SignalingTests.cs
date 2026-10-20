using System.Security.Cryptography;
using OpenDesk.Core;
using OpenDesk.Service;
using static OpenDesk.Tests.Program;

namespace OpenDesk.Tests;

// v2: message packing, signaling proofs, registry, the transport-independent session logic and the
// signaling hub over real pinned WebSockets.
internal static class SignalingTests
{
    internal static void Register()
    {
        Test("v2 frame packing round trip and rejects bad lengths", async () => {
            var packed = Wire.Pack(Wire.Json(MessageKind.Chat, "hello"));
            var back = Wire.Unpack(packed);
            Check(back.Kind == MessageKind.Chat && Wire.Decode<string>(back) == "hello");
            foreach (var bad in new[] { packed[..4], packed[..^1], packed.Concat(new byte[] { 0 }).ToArray(), new byte[] { 255, 0, 0, 0, 0 } })
                await Throws<InvalidDataException>(() => Task.FromResult(Wire.Unpack(bad)));
            Check(Wire.Unpack(Wire.Pack(new(MessageKind.Keyframe, []))).Kind == MessageKind.Keyframe);
        });
        Test("Signed answers verify only for the same key and both descriptions", async () => {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string publicKey = SignalProof.PublicKey(key);
            string signature = SignalProof.Sign(key, SignalProof.Answer("123456789", "s1", "offer", "answer"));
            Check(SignalProof.Verify(publicKey, SignalProof.Answer("123456789", "s1", "offer", "answer"), signature));
            Check(!SignalProof.Verify(publicKey, SignalProof.Answer("123456789", "s1", "offer2", "answer"), signature), "Changed offer accepted");
            Check(!SignalProof.Verify(publicKey, SignalProof.Answer("123456789", "s1", "offer", "answer2"), signature), "Changed answer accepted");
            Check(!SignalProof.Verify(publicKey, SignalProof.Answer("987654321", "s1", "offer", "answer"), signature), "Other device ID accepted");
            Check(!SignalProof.Verify(SignalProof.PublicKey(other), SignalProof.Answer("123456789", "s1", "offer", "answer"), signature), "Other key accepted");
            Check(!SignalProof.Verify(publicKey, SignalProof.Answer("123456789", "s1", "offer", "answer"), "not base64!"));
            using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
            await Throws<CryptographicException>(() => Task.FromResult(SignalProof.ImportKey(SignalProof.PublicKey(p384))));
        });
        Test("TURN credentials follow the coturn shared-secret format", () => {
            var expires = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
            var server = TurnCredentials.Create(["turn:example:3478"], "secret-value", "viewer", expires);
            Check(server.Username == "1800000000:viewer");
            Check(server.Credential == Convert.ToBase64String(HMACSHA1.HashData("secret-value"u8.ToArray(), "1800000000:viewer"u8.ToArray())));
            return Task.CompletedTask;
        });
        Test("Signaling registration keeps one ID per key and rejects forgeries", async () => {
            string path = Path.Combine(Path.GetTempPath(), "opendesk-sig-" + Guid.NewGuid().ToString("N"), "devices.json");
            var registry = new DeviceRegistry(path);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string publicKey = SignalProof.PublicKey(key), first = new string('a', 64), second = new string('b', 64);
            string id = registry.RegisterSignaling(first, publicKey, SignalProof.Sign(key, SignalProof.Register(first, publicKey)));
            Check(registry.RegisterSignaling(second, publicKey, SignalProof.Sign(key, SignalProof.Register(second, publicKey))) == id, "Same key got a new ID");
            await Throws<UnauthorizedAccessException>(() => Task.FromResult(registry.RegisterSignaling(second, publicKey, SignalProof.Sign(key, SignalProof.Register(first, publicKey)))));
            await Throws<UnauthorizedAccessException>(() => Task.FromResult(registry.RegisterSignaling(first, publicKey, SignalProof.Sign(other, SignalProof.Register(first, publicKey)))));
            await Throws<UnauthorizedAccessException>(() => Task.FromResult(registry.RegisterSignaling("short", publicKey, SignalProof.Sign(key, SignalProof.Register("short", publicKey)))));
            Check(new DeviceRegistry(path).RegisterSignaling(first, publicKey, SignalProof.Sign(key, SignalProof.Register(first, publicKey))) == id, "ID not persisted");
        });
        Test("PeerHost approval, permissions, input and status", async () => {
            var input = new RecordingInput(); var sent = new List<(string Session, string Channel, Message Message)>(); var closed = new List<string>();
            var answers = new Queue<Approval>([new(false), new(true, true, false, false), new(true)]);
            var host = new PeerHost(new HostCoordinator(input, 2), (_, _) => Task.FromResult(answers.Dequeue()),
                (session, channel, data, _) => { lock (sent) sent.Add((session, channel, Wire.Unpack(data))); return Task.CompletedTask; }, closed.Add);
            Check(await host.AdmitAsync("a", "Bad\nname", default) == "Invalid display name.");
            Check(await host.AdmitAsync("a", "Alice", default) == "The host declined the request.");
            Check(await host.AdmitAsync("a", "Alice", default) is null && host.SessionOf(host.Coordinator.Viewers.Single().Id) == "a");
            await host.OpenedAsync("a", default);
            var accepted = Wire.Decode<SessionStatus>(sent.Last(s => s.Message.Kind == MessageKind.Accepted).Message);
            Check(accepted.Control && !accepted.Clipboard);
            await host.ReceiveAsync("a", PeerHost.Input, Wire.Pack(Wire.Json(MessageKind.Input, new InputEvent("key", Down: true, Epoch: accepted.Epoch, Key: "KeyA"))), default);
            Check(input.Events.Count == 1 && input.Events[0].Key == "KeyA", "Input not delivered");
            await host.ReceiveAsync("a", PeerHost.Input, Wire.Pack(Wire.Json(MessageKind.Input, new InputEvent("key", Down: true, Epoch: accepted.Epoch + 5, Key: "KeyB"))), default);
            Check(input.Events.Count == 1, "Stale epoch input applied");
            await Throws<InvalidDataException>(() => host.ReceiveAsync("a", PeerHost.Control, Wire.Pack(Wire.Json(MessageKind.Clipboard, "x")), default));
            await Throws<InvalidDataException>(() => host.ReceiveAsync("a", PeerHost.Control, Wire.Pack(Wire.Json(MessageKind.FileOffer, "x")), default));
            await Throws<InvalidDataException>(() => host.ReceiveAsync("a", PeerHost.Input, Wire.Pack(Wire.Json(MessageKind.Chat, "x")), default));
            int keyframes = 0; host.KeyframeRequested += () => keyframes++;
            await host.ReceiveAsync("a", PeerHost.Control, Wire.Pack(new(MessageKind.Keyframe, [])), default);
            Check(keyframes == 1);
            Check(await host.AdmitAsync("b", "Bob", default) is null);
            var viewers = host.Coordinator.Viewers;
            host.Coordinator.SetController(viewers.Single(v => v.Name == "Bob").Id);
            Check(sent.Any(s => s.Session == "a" && s.Message.Kind == MessageKind.Status && !Wire.Decode<SessionStatus>(s.Message).Control), "Status not broadcast");
            Check(await host.AdmitAsync("c", "Carol", default) == "The host has reached its viewer limit.");
            host.Coordinator.Remove(viewers.Single(v => v.Name == "Alice").Id);
            Check(closed.Contains("a"), "Removed viewer was not disconnected");
            host.Closed("b"); Check(host.Coordinator.Viewers.Count == 0);
            await Throws<IOException>(() => host.SendMessageAsync(Guid.NewGuid(), Wire.Json(MessageKind.Chat, "x"), default));
        });
        Test("PeerViewer acceptance, status order and permission checks", async () => {
            var sent = new List<(string Channel, Message Message)>();
            var viewer = new PeerViewer((channel, data, _) => { sent.Add((channel, Wire.Unpack(data))); return Task.CompletedTask; });
            await viewer.ReceiveAsync(PeerHost.Control, Wire.Pack(Wire.Json(MessageKind.Accepted, new SessionStatus(false, 3, "Connected", Clipboard: true))), default);
            Check((await viewer.Accepted).Epoch == 3);
            await viewer.ReceiveAsync(PeerHost.Control, Wire.Pack(Wire.Json(MessageKind.Status, new SessionStatus(true, 2, "Old"))), default);
            Check(!viewer.Status.Control && viewer.Status.Epoch == 3, "Older status applied");
            await Throws<InvalidDataException>(() => viewer.ReceiveAsync(PeerHost.Files, Wire.Pack(Wire.Json(MessageKind.FileOffer, "x")), default));
            await Throws<UnauthorizedAccessException>(() => viewer.SendMessageAsync(Wire.Json(MessageKind.FileOffer, "x"), default));
            await viewer.SendMessageAsync(Wire.Json(MessageKind.Clipboard, "copied"), default);
            Check(sent.Single().Channel == PeerHost.Control);
            await Throws<InvalidDataException>(() => viewer.ReceiveAsync(PeerHost.Control, Wire.Pack(Wire.Json(MessageKind.Rejected, "No")), default));
        });
        Test("Signaling hub relays offer and signed answer between pinned sockets", async () => {
            using var cert = SecureSession.CreateCertificate(); using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string path = Path.Combine(Path.GetTempPath(), "opendesk-hub-" + Guid.NewGuid().ToString("N"), "devices.json");
            await using var service = await DirectoryHost.StartAsync(path, cert, 0, 0, true, new TurnSettings("turn.example", 3478, new string('s', 40)));
            string pin = SecureSession.Pin(cert);
            using (var directory = new DirectoryClient(service.Url, pin)) Check(await directory.SupportsSignalingAsync(default));
            await Throws<IOException>(async () => { await using var wrong = await SignalingClient.ConnectAsync(service.Url, new string('0', 64), "v2/host", default); });
            // Host registers.
            await using var host = await SignalingClient.ConnectAsync(service.Url, pin, "v2/host", default);
            var challenge = await host.ReceiveAsync(default);
            string publicKey = SignalProof.PublicKey(key);
            await host.SendAsync(new("register", PublicKey: publicKey, Signature: SignalProof.Sign(key, SignalProof.Register(challenge.Challenge!, publicKey))), default);
            var registered = await host.ReceiveAsync(default);
            Check(registered.Type == "registered" && registered.Id is { Length: 9 }, "Registration failed: " + registered.Reason);
            // Unknown device.
            await using (var nobody = await SignalingClient.ConnectAsync(service.Url, pin, "v2/connect/111111111", default)) Check((await nobody.ReceiveAsync(default)).Type == "error");
            // Viewer offer reaches the host with fresh TURN credentials; the signed answer comes back.
            await using var viewer = await SignalingClient.ConnectAsync(service.Url, pin, "v2/connect/" + registered.Id, default);
            var ready = await viewer.ReceiveAsync(default);
            Check(ready.Type == "ready" && ready.PublicKey == publicKey && ready.Ice is [{ Username: { } } ice] && ice.Urls.Length == 3);
            await viewer.SendAsync(new("offer", Name: "Viewer", Sdp: "v=0 offer"), default);
            var offer = await host.ReceiveAsync(default);
            Check(offer is { Type: "offer", Name: "Viewer", Sdp: "v=0 offer" } && offer.Session == ready.Session && offer.Ice is [_]);
            string signature = SignalProof.Sign(key, SignalProof.Answer(registered.Id!, offer.Session!, offer.Sdp!, "v=0 answer"));
            await host.SendAsync(new("answer", Session: offer.Session, Sdp: "v=0 answer", Signature: signature), default);
            var answer = await viewer.ReceiveAsync(default);
            Check(answer.Type == "answer" && SignalProof.Verify(ready.PublicKey!, SignalProof.Answer(registered.Id!, ready.Session!, "v=0 offer", answer.Sdp!), answer.Signature));
            // A viewer that gives up before the answer cancels the pending request at the host.
            await using (var leaving = await SignalingClient.ConnectAsync(service.Url, pin, "v2/connect/" + registered.Id, default)) {
                await leaving.ReceiveAsync(default);
                await leaving.SendAsync(new("offer", Name: "Leaving", Sdp: "v=0"), default);
                Check((await host.ReceiveAsync(default)).Type == "offer");
            }
            Check((await host.ReceiveAsync(default)).Type == "cancel", "Host was not told the viewer left");
            // A second window with the same key takes over; the first is told why.
            await using var second = await SignalingClient.ConnectAsync(service.Url, pin, "v2/host", default);
            var again = await second.ReceiveAsync(default);
            await second.SendAsync(new("register", PublicKey: publicKey, Signature: SignalProof.Sign(key, SignalProof.Register(again.Challenge!, publicKey))), default);
            Check((await second.ReceiveAsync(default)).Id == registered.Id);
            var replaced = await host.ReceiveAsync(default);
            Check(replaced.Type == "replaced", "First window was not told it was replaced");
            // A forged registration is refused.
            using var forger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await using var forged = await SignalingClient.ConnectAsync(service.Url, pin, "v2/host", default);
            var forgedChallenge = await forged.ReceiveAsync(default);
            await forged.SendAsync(new("register", PublicKey: publicKey, Signature: SignalProof.Sign(forger, SignalProof.Register(forgedChallenge.Challenge!, publicKey))), default);
            Check((await forged.ReceiveAsync(default)).Type == "error");
            // A captured registration replayed on a new socket fails: every socket gets its own challenge.
            await using var replay = await SignalingClient.ConnectAsync(service.Url, pin, "v2/host", default);
            await replay.ReceiveAsync(default);
            await replay.SendAsync(new("register", PublicKey: publicKey, Signature: SignalProof.Sign(key, SignalProof.Register(challenge.Challenge!, publicKey))), default);
            Check((await replay.ReceiveAsync(default)).Type == "error", "Replayed registration accepted");
        });
    }
}
