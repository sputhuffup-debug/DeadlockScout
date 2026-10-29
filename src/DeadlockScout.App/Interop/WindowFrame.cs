using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;

namespace DeadlockScout.App.Interop;

/// <summary>
/// The other half of the ScoutWindow style (Themes/Theme.xaml): its caption buttons send the SystemCommands, which a
/// window does not handle by itself, and once a window has a handle Windows is asked for a dark frame (Windows 10 2004+)
/// with round corners and a border in the theme's line colour (Windows 11). Older builds refuse the attributes and keep
/// the square frame; nothing depends on them. A maximised chrome window hangs over the screen edge by its sizing frame,
/// so the frame is inset by that much while maximised.
/// </summary>
public static class WindowFrame
{
    private const int LineColor = 0x3D332A; // #2A333D as COLORREF (0x00BBGGRR)

    public static void Register()
    {
        Bind(SystemCommands.MinimizeWindowCommand, SystemCommands.MinimizeWindow);
        Bind(SystemCommands.MaximizeWindowCommand, SystemCommands.MaximizeWindow);
        Bind(SystemCommands.RestoreWindowCommand, SystemCommands.RestoreWindow);
        Bind(SystemCommands.CloseWindowCommand, SystemCommands.CloseWindow);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
    }

    private static void Bind(RoutedCommand command, Action<Window> action)
        => CommandManager.RegisterClassCommandBinding(typeof(Window), new CommandBinding(command, (s, _) => action((Window)s)));

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window w || WindowChrome.GetWindowChrome(w) is null) return;
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        Set(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        Set(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, NativeMethods.DWMWCP_ROUND);
        Set(hwnd, NativeMethods.DWMWA_BORDER_COLOR, LineColor);
        w.StateChanged -= Inset;
        w.StateChanged += Inset;
        Inset(w, EventArgs.Empty);
    }

    private static void Inset(object? sender, EventArgs e)
    {
        if (sender is not Window w || w.Template?.FindName("Frame", w) is not FrameworkElement frame) return;
        if (w.WindowState != WindowState.Maximized) { frame.Margin = default; return; }
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
        int padded = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXPADDEDBORDER);
        frame.Margin = new Thickness(
            (NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSIZEFRAME) + padded) / dpi.DpiScaleX,
            (NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSIZEFRAME) + padded) / dpi.DpiScaleY,
            (NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSIZEFRAME) + padded) / dpi.DpiScaleX,
            (NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSIZEFRAME) + padded) / dpi.DpiScaleY);
    }

    private static void Set(IntPtr hwnd, int attribute, int value)
        => _ = NativeMethods.DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
}
