using System.Runtime.InteropServices;

namespace DeadlockScout.App.Interop;

/// <summary>The handful of user32 calls the overlay needs. All of them act on our own window or read public window state.</summary>
public static partial class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const uint WDA_NONE = 0x0;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

    [LibraryImport("user32.dll", SetLastError = true)] public static partial int GetWindowLongW(IntPtr hWnd, int nIndex);
    [LibraryImport("user32.dll", SetLastError = true)] public static partial int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    [LibraryImport("user32.dll")] public static partial IntPtr GetForegroundWindow();
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool GetClientRect(IntPtr hWnd, out RECT rect);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool UnregisterHotKey(IntPtr hWnd, int id);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsWindow(IntPtr hWnd);
    [LibraryImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool IsIconic(IntPtr hWnd);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)] public static partial int GetWindowTextW(IntPtr hWnd, Span<char> text, int maxCount);

    public static string WindowTitle(IntPtr hWnd)
    {
        Span<char> buf = stackalloc char[256];
        int n = GetWindowTextW(hWnd, buf, buf.Length);
        return n > 0 ? new string(buf[..n]) : "";
    }
}
