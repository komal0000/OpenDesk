using System.Security.Cryptography;
using System.Text.Json;

namespace OpenDesk.App;

internal sealed record Contact(string Id, string Name, bool Favorite, DateTimeOffset LastConnected);
internal sealed class ClientSettings
{
    internal const string DefaultServiceUrl = "https://139.99.135.69:47834";
    internal const string DefaultServicePin = "5B80725FF3E6DC733FADB7AF55727DA4C622D5FB82C9AC4EE04B7C60A774AEEF";
    [System.Text.Json.Serialization.JsonIgnore] public string? StorageFile { get; set; }
    internal static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenDesk");
    public string ServiceUrl { get; set; } = DefaultServiceUrl;
    public string ServicePin { get; set; } = DefaultServicePin;
    public string DeviceId { get; set; } = "";
    public string DisplayName { get; set; } = Environment.UserName;
    public int VideoQuality {get;set;}=1;
    public List<Contact> Contacts { get; set; } = [];
    // Device address -> SHA-256 of the device key first seen there (trust on first use). Only removing
    // the contact forgets it, so renaming or re-adding a device cannot silently reset trust.
    public Dictionary<string,string> KnownKeys { get; set; } = [];
    public static ClientSettings Load(string? storageFile = null)
    {
        string file = storageFile ?? Path.Combine(Folder,"settings.json");
        ClientSettings settings;
        try { settings = File.Exists(file) ? JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(file)) ?? new() : new(); }
        catch (JsonException) { settings = new(); }
        settings.StorageFile = storageFile;
        settings.ServiceUrl??="";settings.ServicePin??="";settings.DeviceId??="";settings.DisplayName=string.IsNullOrWhiteSpace(settings.DisplayName)?Environment.UserName:settings.DisplayName;
        if(settings.DeviceId.Length!=9||settings.DeviceId.Any(c=>c<'0'||c>'9'))settings.DeviceId="";
        settings.Contacts=(settings.Contacts??[]).Where(c=>c is not null&&c.Id is{Length:9}&&c.Id.All(d=>d>='0'&&d<='9')&&!string.IsNullOrWhiteSpace(c.Name)).Take(200).ToList();
        settings.KnownKeys=(settings.KnownKeys??[]).Where(p=>p.Key is{Length:9}&&p.Key.All(d=>d>='0'&&d<='9')&&OpenDesk.Core.Invitation.IsHex(p.Value,64)).Take(1000).ToDictionary();
        if (string.IsNullOrWhiteSpace(settings.ServiceUrl)) {
            settings.ServiceUrl = DefaultServiceUrl;
            settings.ServicePin = DefaultServicePin;
            settings.DeviceId = "";
        }
        return settings;
    }
    public bool TryLocalService()
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenDeskServer","service.json");
        if (!File.Exists(local)) return false;
        try{
            using var file=new FileStream(local,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var json=JsonDocument.Parse(file);
            string url=json.RootElement.GetProperty("Url").GetString()??"",pin=json.RootElement.GetProperty("Pin").GetString()??"";
            using var check=new OpenDesk.Core.DirectoryClient(url,pin);ServiceUrl=url;ServicePin=pin;return true;
        }catch(Exception ex)when(ex is IOException or JsonException or KeyNotFoundException or FormatException){return false;}
    }
    public void Save()
    {
        string file = StorageFile ?? Path.Combine(Folder,"settings.json");Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file + ".tmp",JsonSerializer.Serialize(this)); File.Move(file + ".tmp",file,true);
    }
    public void Remember(string id, string name, string keyPin)
    {
        var old = Contacts.FirstOrDefault(c => c.Id == id);
        Contacts.RemoveAll(c => c.Id == id);
        KnownKeys[id] = keyPin;
        Contacts.Insert(0,new(id,old?.Name ?? name,old?.Favorite ?? false,DateTimeOffset.UtcNow));
        Contacts = Contacts.Take(200).ToList(); Save();
    }
    public static ECDsa LoadDeviceKey(string name="OpenDesk.DeviceIdentity.v1")
    {
        var key = CngKey.Exists(name) ? CngKey.Open(name) : CngKey.Create(CngAlgorithm.ECDsaP256,name,new CngKeyCreationParameters {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider, KeyUsage = CngKeyUsages.Signing, ExportPolicy = CngExportPolicies.None
        });
        using (key) return new ECDsaCng(key);
    }
}
