using System.Text.Json;
using OpenDesk.Core;

namespace OpenDesk.UI;

public sealed record RecentDevice(string Id, string Name, DateTimeOffset LastConnected);

// The viewer's own settings file, shared by every desktop head: service, display name, recent devices and
// the device key fingerprint seen for each address (trust on first use).
public sealed class ViewerSettings
{
    // The same public service as the Windows app's defaults.
    public const string DefaultServiceUrl = "https://139.99.135.69:47834";
    public const string DefaultServicePin = "5B80725FF3E6DC733FADB7AF55727DA4C622D5FB82C9AC4EE04B7C60A774AEEF";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string ServiceUrl { get; set; } = DefaultServiceUrl;
    public string ServicePin { get; set; } = DefaultServicePin;
    public string DisplayName { get; set; } = DefaultName();
    public List<RecentDevice> Recent { get; set; } = [];
    public Dictionary<string, string> KnownKeys { get; set; } = [];
    private string path = "";

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "OpenDesk", "viewer.json");
    private static string DefaultName() { string name = Environment.UserName.Trim(); return Signal.ValidName(name) ? name : "Viewer"; }

    // A missing or damaged file gives the defaults; invalid entries are dropped.
    public static ViewerSettings Load(string? file = null)
    {
        file ??= DefaultPath;
        ViewerSettings settings;
        try { settings = JsonSerializer.Deserialize<ViewerSettings>(File.ReadAllBytes(file), Options) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { settings = new(); }
        settings.path = file;
        if (string.IsNullOrWhiteSpace(settings.ServiceUrl)) { settings.ServiceUrl = DefaultServiceUrl; settings.ServicePin = DefaultServicePin; }
        settings.ServicePin ??= "";
        if (!Signal.ValidName(settings.DisplayName)) settings.DisplayName = DefaultName();
        settings.Recent = (settings.Recent ?? []).Where(r => r is not null && IsId(r.Id) && Signal.ValidName(r.Name)).DistinctBy(r => r.Id).Take(50).ToList();
        settings.KnownKeys = (settings.KnownKeys ?? []).Where(p => IsId(p.Key) && Invitation.IsHex(p.Value, 64)).Take(1000).ToDictionary();
        return settings;
    }
    private static bool IsId(string? id) => id is { Length: 9 } && id.All(char.IsAsciiDigit);

    // Checks the service address and fingerprint before keeping them.
    public void SetService(string url, string pin, string displayName)
    {
        url = url.Trim(); pin = pin.Trim().ToUpperInvariant(); displayName = displayName.Trim();
        _ = DirectoryClient.ServiceRoot(url);
        _ = DirectoryClient.PinCheck(pin);
        if (!Signal.ValidName(displayName)) throw new FormatException("The display name must be 1–64 characters.");
        ServiceUrl = url; ServicePin = pin; DisplayName = displayName;
        Save();
    }

    public void Remember(string id, string keyPin)
    {
        var old = Recent.FirstOrDefault(r => r.Id == id);
        Recent.RemoveAll(r => r.Id == id);
        Recent.Insert(0, new(id, old?.Name ?? DeviceProof.FormatId(id), DateTimeOffset.UtcNow));
        if (Recent.Count > 50) Recent.RemoveRange(50, Recent.Count - 50);
        KnownKeys[id] = keyPin;
        Save();
    }
    // Forgetting a device also forgets its key, so a reinstalled computer can be trusted again.
    public void Forget(string id) { Recent.RemoveAll(r => r.Id == id); KnownKeys.Remove(id); Save(); }

    public void Save()
    {
        if (path.Length == 0) path = DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(this, Options));
        File.Move(temporary, path, true);
    }
}
