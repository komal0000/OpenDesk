using Avalonia.Input;

namespace OpenDesk.UI;

// Avalonia's physical keys are named after W3C KeyboardEvent.code, which InputEvent carries; only letters
// ("A" → "KeyA") and the numeric keypad ("NumPad1" → "Numpad1") are spelled differently.
public static class KeyCodes
{
    public static string? ToCode(PhysicalKey key)
    {
        if (key == PhysicalKey.None) return null;
        string name = key.ToString();
        if (key is >= PhysicalKey.A and <= PhysicalKey.Z) return "Key" + name;
        if (name.StartsWith("NumPad", StringComparison.Ordinal)) return "Numpad" + name[6..];
        return name.Length is >= 2 and <= 24 && name.All(char.IsAsciiLetterOrDigit) ? name : null;
    }
}
