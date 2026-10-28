using System.Diagnostics;
using System.IO;

namespace DeadlockScout.App.Interop;

/// <summary>
/// Finds the Deadlock window and reports its rectangle and whether it is in front. The game runs as
/// <c>game\bin\win64\deadlock.exe</c> (<c>project8.exe</c> before 2024's rename); its window is titled "Deadlock".
/// </summary>
public sealed class GameWindowTracker
{
    /// <summary>Executable names, current first. The first real match showed the old "project8" alone never matched.</summary>
    public static readonly string[] ProcessNames = { "deadlock", "project8" };
    public const string WindowTitle = "Deadlock";

    /// <summary>The executable the window was found in ("deadlock.exe"), for the status line; null while not found.</summary>
    public string? FoundIn { get; private set; }

    private IntPtr _hwnd;
    private DateTime _lastSearch = DateTime.MinValue;

    public sealed record Snapshot(bool Found, bool Foreground, NativeMethods.RECT Bounds, bool Minimized);

    public Snapshot Poll()
    {
        if (_hwnd == IntPtr.Zero || !NativeMethods.IsWindow(_hwnd))
        {
            _hwnd = IntPtr.Zero;
            if (DateTime.UtcNow - _lastSearch > TimeSpan.FromSeconds(2))
            {
                _lastSearch = DateTime.UtcNow;
                _hwnd = Find();
            }
        }
        if (_hwnd == IntPtr.Zero) return new Snapshot(false, false, default, false);
        NativeMethods.GetWindowRect(_hwnd, out var rect);
        bool fg = NativeMethods.GetForegroundWindow() == _hwnd;
        return new Snapshot(true, fg, rect, NativeMethods.IsIconic(_hwnd));
    }

    /// <summary>
    /// The game's client area in screen pixels (the part the HUD is drawn in; the same as the window in borderless
    /// mode), or null when the game is not running or minimized.
    /// </summary>
    public NativeMethods.RECT? ClientBounds()
    {
        var snapshot = Poll();
        if (!snapshot.Found || snapshot.Minimized) return null;
        if (!NativeMethods.GetClientRect(_hwnd, out var client)) return null;
        var origin = new NativeMethods.POINT();
        if (!NativeMethods.ClientToScreen(_hwnd, ref origin)) return null;
        if (client.Width <= 0 || client.Height <= 0) return null;
        return new NativeMethods.RECT { Left = origin.X, Top = origin.Y, Right = origin.X + client.Width, Bottom = origin.Y + client.Height };
    }

    private IntPtr Find()
    {
        FoundIn = null;
        try
        {
            foreach (var name in ProcessNames)
                foreach (var p in Process.GetProcessesByName(name))
                {
                    using (p)
                    {
                        if (p.MainWindowHandle != IntPtr.Zero && NativeMethods.WindowTitle(p.MainWindowHandle).Contains(WindowTitle, StringComparison.OrdinalIgnoreCase))
                        {
                            FoundIn = name + ".exe";
                            return p.MainWindowHandle;
                        }
                    }
                }

            // Another rename would not stop us: a top-level window titled exactly "Deadlock" whose program lives in the
            // game's own bin folder.
            var hwnd = NativeMethods.FindWindow(null, WindowTitle);
            if (hwnd != IntPtr.Zero && NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid) != 0)
            {
                using var p = Process.GetProcessById((int)pid);
                var path = p.MainModule?.FileName ?? "";
                if (path.Contains(@"\Deadlock\game\bin\", StringComparison.OrdinalIgnoreCase))
                {
                    FoundIn = Path.GetFileName(path);
                    return hwnd;
                }
            }
        }
        catch (Exception) { }
        return IntPtr.Zero;
    }
}
