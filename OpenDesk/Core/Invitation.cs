using System.Security.Cryptography;
using System.Text.Json;

namespace OpenDesk.Core;

public sealed record Invitation(int Version, string Mode, string Address, int Port,
    string Room, string Secret, string Pin, DateTimeOffset Expires)
{
    public static Invitation Create(string mode, string address, int port, string pin) => new(
        1, mode, address, port, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), pin, DateTimeOffset.UtcNow.AddMinutes(15));

    public string Encode() => "opendesk:" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this));

    public static bool IsHex(string? value, int length) => value is not null && value.Length == length && value.All(Uri.IsHexDigit);

    public static Invitation Parse(string text, DateTimeOffset now)
    {
        try {
            text = text.Trim();
            if (text.Length > 4096 || !text.StartsWith("opendesk:", StringComparison.Ordinal)) throw new FormatException();
            var i = JsonSerializer.Deserialize<Invitation>(Convert.FromBase64String(text[9..])) ?? throw new FormatException();
            if (i.Version != 1 || i.Mode is not ("direct" or "relay") || i.Port is < 1 or > 65535 ||
                string.IsNullOrEmpty(i.Address) || i.Address.Length > 253 || i.Address.Any(char.IsWhiteSpace) ||
                Uri.CheckHostName(i.Address) == UriHostNameType.Unknown ||
                !IsHex(i.Room, 32) || !IsHex(i.Secret, 64) || !IsHex(i.Pin, 64) ||
                i.Expires <= now || i.Expires > now.AddMinutes(16)) throw new FormatException();
            return i;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException) {
            throw new FormatException("Invitation is invalid, expired, or uses an unsupported version.");
        }
    }
}
