namespace OpenDesk.App;

// W3C KeyboardEvent.code (a physical key position) to a Windows set-1 scan code, so the host's own
// keyboard layout decides the character. Values: scan | 0x100 for E0-extended keys; 0x10000 | VK for
// keys whose scan codes SendInput handles badly (Pause, NumLock).
internal static class KeyCodes
{
    internal const int Extended = 0x100, VirtualKey = 0x10000;
    private static readonly Dictionary<string, int> Codes = Build();
    internal static int? Lookup(string code) => Codes.TryGetValue(code, out int value) ? value : null;

    private static Dictionary<string, int> Build()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal) {
            ["Escape"] = 0x01, ["Minus"] = 0x0C, ["Equal"] = 0x0D, ["Backspace"] = 0x0E, ["Tab"] = 0x0F,
            ["BracketLeft"] = 0x1A, ["BracketRight"] = 0x1B, ["Enter"] = 0x1C, ["ControlLeft"] = 0x1D,
            ["Semicolon"] = 0x27, ["Quote"] = 0x28, ["Backquote"] = 0x29, ["ShiftLeft"] = 0x2A, ["Backslash"] = 0x2B,
            ["Comma"] = 0x33, ["Period"] = 0x34, ["Slash"] = 0x35, ["ShiftRight"] = 0x36, ["NumpadMultiply"] = 0x37,
            ["AltLeft"] = 0x38, ["Space"] = 0x39, ["CapsLock"] = 0x3A, ["ScrollLock"] = 0x46,
            ["Numpad7"] = 0x47, ["Numpad8"] = 0x48, ["Numpad9"] = 0x49, ["NumpadSubtract"] = 0x4A,
            ["Numpad4"] = 0x4B, ["Numpad5"] = 0x4C, ["Numpad6"] = 0x4D, ["NumpadAdd"] = 0x4E,
            ["Numpad1"] = 0x4F, ["Numpad2"] = 0x50, ["Numpad3"] = 0x51, ["Numpad0"] = 0x52, ["NumpadDecimal"] = 0x53,
            ["IntlBackslash"] = 0x56, ["F11"] = 0x57, ["F12"] = 0x58, ["IntlRo"] = 0x73, ["IntlYen"] = 0x7D,
            ["NumpadEnter"] = Extended | 0x1C, ["ControlRight"] = Extended | 0x1D, ["NumpadDivide"] = Extended | 0x35,
            ["PrintScreen"] = Extended | 0x37, ["AltRight"] = Extended | 0x38, ["Home"] = Extended | 0x47,
            ["ArrowUp"] = Extended | 0x48, ["PageUp"] = Extended | 0x49, ["ArrowLeft"] = Extended | 0x4B,
            ["ArrowRight"] = Extended | 0x4D, ["End"] = Extended | 0x4F, ["ArrowDown"] = Extended | 0x50,
            ["PageDown"] = Extended | 0x51, ["Insert"] = Extended | 0x52, ["Delete"] = Extended | 0x53,
            ["MetaLeft"] = Extended | 0x5B, ["MetaRight"] = Extended | 0x5C, ["ContextMenu"] = Extended | 0x5D,
            ["AudioVolumeMute"] = Extended | 0x20, ["AudioVolumeDown"] = Extended | 0x2E, ["AudioVolumeUp"] = Extended | 0x30,
            ["MediaPlayPause"] = Extended | 0x22, ["MediaStop"] = Extended | 0x24,
            ["MediaTrackNext"] = Extended | 0x19, ["MediaTrackPrevious"] = Extended | 0x10,
            ["Pause"] = VirtualKey | 0x13, ["NumLock"] = VirtualKey | 0x90,
        };
        for (int i = 1; i <= 9; i++) map["Digit" + i] = 0x01 + i;
        map["Digit0"] = 0x0B;
        string[] rows = ["QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM"];
        int[] starts = [0x10, 0x1E, 0x2C];
        for (int r = 0; r < rows.Length; r++) for (int i = 0; i < rows[r].Length; i++) map["Key" + rows[r][i]] = starts[r] + i;
        for (int i = 1; i <= 10; i++) map["F" + i] = 0x3A + i;
        for (int i = 13; i <= 23; i++) map["F" + i] = 0x64 + i - 13;
        map["F24"] = 0x76;
        return map;
    }
}
