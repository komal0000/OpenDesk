namespace OpenDesk.Core;

public sealed class HostCoordinator(IInputSink input, int capacity = 5)
{
    public event Action? Changed;
    public int Capacity { get; } = capacity is >= 1 and <= 10 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly object gate = new();
    private readonly Dictionary<Guid, string> viewers = [];
    private readonly Dictionary<Guid,(bool Clipboard,bool Files)> permissions = [];
    private Guid? controller;
    private long epoch, display;
    public IReadOnlyList<ViewerInfo> Viewers { get { lock (gate) return viewers.Select(p => new ViewerInfo(p.Key, p.Value, p.Key == controller)).ToArray(); } }
    public Guid Add(string name, bool control = false, bool clipboard = false, bool files = false)
    {
        Guid id;
        lock (gate) {
            if (viewers.Count >= Capacity) throw new InvalidOperationException("Viewer capacity reached.");
            id = Guid.NewGuid(); viewers.Add(id, name);
            permissions[id] = (clipboard,files);
            if (control) ChangeController(id);
        }
        Changed?.Invoke(); return id;
    }
    public SessionStatus Status(Guid id) { lock (gate) { permissions.TryGetValue(id,out var p);return new(controller == id && viewers.ContainsKey(id), epoch, "Connected",p.Clipboard,p.Files,Display:display); } }
    private void ChangeController(Guid? id)
    {
        if (id.HasValue && !viewers.ContainsKey(id.Value)) throw new InvalidOperationException("Viewer disconnected.");
        if (controller == id) return;
        input.ReleaseAll(); controller = id; epoch++;
    }
    public void SetController(Guid? id)
    {
        lock (gate) ChangeController(id);
        Changed?.Invoke();
    }
    public bool HandleInput(Guid id, InputEvent value)
    {
        lock (gate) {
            if (controller != id || !viewers.ContainsKey(id) || value.Epoch != epoch || !value.IsValid) return false;
            if (value.Kind == "reset") input.ReleaseAll(); else input.Apply(value);
            return true;
        }
    }
    public void Remove(Guid id)
    {
        lock (gate) { if (controller == id) ChangeController(null); viewers.Remove(id); permissions.Remove(id); }
        Changed?.Invoke();
    }
    public void Stop()
    {
        lock (gate) { ChangeController(null); viewers.Clear(); permissions.Clear(); }
        Changed?.Invoke();
    }
    public void ChangeDisplay(Action update)=>ChangeDisplay(_=>update());
    // `update` receives the new display generation, for stamping the video it produces.
    public void ChangeDisplay(Action<long> update){lock(gate){input.ReleaseAll();display++;update(display);epoch++;}Changed?.Invoke();}
}
