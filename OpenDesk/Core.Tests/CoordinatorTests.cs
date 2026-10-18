using OpenDesk.Core;
using static OpenDesk.Tests.Program;

namespace OpenDesk.Tests;

internal sealed class RecordingInput : IInputSink
{
    public List<InputEvent> Events { get; } = [];
    public int Releases { get; private set; }
    public void Apply(InputEvent input) => Events.Add(input);
    public void ReleaseAll() => Releases++;
}
internal static class CoordinatorTests
{
    internal static void Register()
    {
        Test("Multiple viewers, capacity, independent removal", async () => {
            var c = new HostCoordinator(new RecordingInput(), 2);
            var a = c.Add("A"); var b = c.Add("B");
            Check(c.Viewers.Count == 2 && !c.Status(a).Control && !c.Status(b).Control);
            await Throws<InvalidOperationException>(() => Task.FromResult(c.Add("C")));
            c.Remove(a); Check(c.Viewers.Count == 1 && c.Viewers[0].Id == b);
            c.Stop(); Check(c.Viewers.Count == 0);
        });
        Test("Exclusive control, revoked epoch, invalid input and cleanup", () => {
            var sink = new RecordingInput(); var c = new HostCoordinator(sink);
            var a = c.Add("A"); var b = c.Add("B");
            Check(!c.HandleInput(a, new("key", Code: 65, Down: true)));
            c.SetController(a); long old = c.Status(a).Epoch;
            Check(c.HandleInput(a, new("key", Code:65, Down:true, Epoch:old)));
            Check(!c.HandleInput(b, new("key", Code:65, Epoch:old)));
            c.SetController(b); Check(sink.Releases >= 1 && !c.Status(a).Control && c.Status(b).Control);
            c.SetController(a);
            Check(!c.HandleInput(a, new("key", Code:66, Epoch:old)), "Stale input must fail even after returning control");
            Check(!c.HandleInput(a, new("move", X:double.NaN, Epoch:c.Status(a).Epoch)));
            Check(!c.HandleInput(a, new("key", Code:999, Epoch:c.Status(a).Epoch)));
            c.Remove(a); Check(!c.Status(b).Control && sink.Releases >= 3);
            return Task.CompletedTask;
        });
        Test("Letterbox and negative monitor coordinate mapping", () => {
            Check(ViewerCoordinates.Normalize(1, 1, 1000, 1000, 1600, 900) is null);
            var p = ViewerCoordinates.Normalize(500, 500, 1000, 1000, 1600, 900)!.Value;
            Check(Math.Abs(p.X - .5) < .001 && Math.Abs(p.Y - .5) < .001);
            Check(ViewerCoordinates.DesktopPoint(0, 1, -1920, -200, 1920, 1080) == (-1920, 879));
            Check(ViewerCoordinates.DesktopPoint(1, 0, -1920, -200, 1920, 1080) == (-1, -200));
            return Task.CompletedTask;
        });
        Test("Frame bounds and permission metadata", async () => {
            var f = new ScreenFrame(800, 600, [0xff,0xd8,0xff,0xd9]);
            var parsed = ScreenFrame.Unpack(f.Pack(new(true, 9, "Connected")));
            Check(parsed.Frame.Width == 800 && parsed.Status.Control && parsed.Status.Epoch == 9);
            await Throws<InvalidDataException>(() => Task.FromResult(new ScreenFrame(2000, 1, f.Jpeg).Pack(new(false, 0, ""))));
        });
    }
}
