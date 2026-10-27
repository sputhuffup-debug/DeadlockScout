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
}
