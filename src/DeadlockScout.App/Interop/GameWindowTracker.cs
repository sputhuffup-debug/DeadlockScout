using System.Diagnostics;

namespace DeadlockScout.App.Interop;

/// <summary>Finds the Deadlock window (process project8, title "Deadlock") and reports its rectangle and whether it is in front.</summary>
public sealed class GameWindowTracker
{
    public const string ProcessName = "project8";
    public const string WindowTitle = "Deadlock";

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

    private static IntPtr Find()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(ProcessName))
            {
                using (p)
                {
                    if (p.MainWindowHandle != IntPtr.Zero && NativeMethods.WindowTitle(p.MainWindowHandle).Contains(WindowTitle, StringComparison.OrdinalIgnoreCase))
                        return p.MainWindowHandle;
                }
            }
        }
        catch (Exception) { }
        return IntPtr.Zero;
    }
}
