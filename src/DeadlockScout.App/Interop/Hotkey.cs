using System.Windows.Input;

namespace DeadlockScout.App.Interop;

/// <summary>Parses "Ctrl+Shift+O" into RegisterHotKey arguments.</summary>
public static class Hotkey
{
    public static bool TryParse(string text, out uint modifiers, out uint vk)
    {
        modifiers = NativeMethods.MOD_NOREPEAT; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= NativeMethods.MOD_CONTROL; break;
                case "shift": modifiers |= NativeMethods.MOD_SHIFT; break;
                case "alt": modifiers |= NativeMethods.MOD_ALT; break;
                case "win": modifiers |= NativeMethods.MOD_WIN; break;
                default:
                    if (!Enum.TryParse<Key>(raw, ignoreCase: true, out var key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0;
    }

    /// <summary>
    /// A single key for a double tap: "`" (or "~", "backtick"), or a WPF key name such as "F8" or "OemTilde"; modifiers
    /// are not allowed. False for "" and for anything else.
    /// </summary>
    public static bool TryParseTapKey(string? text, out int vk)
    {
        vk = 0;
        var t = text?.Trim() ?? "";
        if (t.Length == 0 || t.Contains('+')) return false;
        if (t is "`" or "~" || t.Equals("backtick", StringComparison.OrdinalIgnoreCase)) t = nameof(Key.OemTilde);
        else if (t.Length == 1 && char.IsDigit(t[0])) t = "D" + t;   // "1" is the 1 key, not key code 1
        else if (t.All(char.IsDigit)) return false;
        if (!Enum.TryParse<Key>(t, ignoreCase: true, out var key) || key == Key.None || !Enum.IsDefined(key)) return false;
        vk = KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }
}
