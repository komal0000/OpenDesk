using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenDesk.Core;

public sealed class DirectoryClient : IDisposable
{
    private readonly HttpClient http;
    public Uri Endpoint { get; }
    public DirectoryClient(string url, string servicePin = "")
    {
        if (!Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new FormatException("Service address must be an HTTPS root address.");
        Endpoint = uri;
        if (servicePin.Length != 0 && !Invitation.IsHex(servicePin,64)) throw new FormatException("Service fingerprint must contain 64 hexadecimal characters.");
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (servicePin.Length != 0) {
            byte[] expected = Convert.FromHexString(servicePin);
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.RawData), expected);
        }
        http = new(handler) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(10) };
    }
    private async Task<T> RequestAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(10));ct=deadline.Token;
        using (request) using (var result = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)) {
            if (!result.IsSuccessStatusCode) {
                if (result.StatusCode == System.Net.HttpStatusCode.NotFound) throw new IOException("Device is offline or the address is unknown.");
                throw new IOException($"Device service could not complete the request ({(int)result.StatusCode}).");
            }
            await using var body = await result.Content.ReadAsStreamAsync(ct);
            byte[] bytes = new byte[Wire.MaxControl + 1]; int used = 0;
            while (used < bytes.Length) { int n = await body.ReadAsync(bytes.AsMemory(used), ct); if (n == 0) break; used += n; }
            if (used > Wire.MaxControl) throw new InvalidDataException("Service response is too large.");
            return JsonSerializer.Deserialize<T>(bytes.AsSpan(0,used), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Invalid service response.");
        }
    }
    public Task<DirectoryInfo> InfoAsync(CancellationToken ct) => RequestAsync<DirectoryInfo>(new(HttpMethod.Get,"v1/info"),ct);
    public async Task<DeviceAddress> RegisterAsync(ECDsa key, Invitation session, string relayOwner, CancellationToken ct)
    {
        var challenge = await RequestAsync<ChallengeReply>(new(HttpMethod.Post,"v1/challenge"),ct);
        var proof = DeviceProof.Sign(key,challenge.Challenge,session,relayOwner);
        var result = await RequestAsync<DeviceAddress>(new(HttpMethod.Post,"v1/register") { Content = JsonContent.Create(proof) },ct);
        _ = DeviceProof.NormalizeId(result.Id); return result;
    }
    public async Task<DeviceLookup> LookupAsync(string id, CancellationToken ct)
    {
        id = DeviceProof.NormalizeId(id);
        var result = await RequestAsync<DeviceLookup>(new(HttpMethod.Get,"v1/device/" + id),ct);
        if (result.Id != id || result.Session.Mode != "relay" || !string.Equals(result.Session.Address, Endpoint.Host, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Service returned a mismatched device route.");
        _ = Invitation.Parse(result.Session.Encode(), DateTimeOffset.UtcNow); return result;
    }
    public void Dispose() => http.Dispose();
    public sealed record ChallengeReply(string Challenge);
}
