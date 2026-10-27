using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using System.Windows.Threading;
using DeadlockScout.App.Interop;
using DeadlockScout.App.Services;
using DeadlockScout.App.ViewModels;
using DeadlockScout.Core.Settings;
using Microsoft.Extensions.Options;

namespace DeadlockScout.App.Overlay;

/// <summary>
/// A transparent, click-through, never-activated top-level window placed over the game window. It is drawn by Windows,
/// not inside the game, which keeps it VAC-safe and limits it to borderless windowed mode. One DispatcherTimer at 1 Hz
/// refreshes the view models and follows the game window; a foreground-change event shows or hides it at once when you
/// switch windows, so that needs no faster polling. There is no per-frame work.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int HotkeyToggleId = 0x4A01, HotkeyLayoutId = 0x4A02;

    private readonly OverlayState _state;
    private readonly IOptionsMonitor<ScoutSettings> _settings;
    private readonly GameWindowTracker _tracker = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private NativeMethods.WinEventProc? _onForeground; // kept in a field: the hook holds only a native pointer to it
    private IntPtr _foregroundHook;
    private HwndSource? _source;
    private bool _userHidden;
    private bool _scoutPinned;
    private bool _warnedFullscreen;

    public OverlayWindow(OverlayState state, IOptionsMonitor<ScoutSettings> settings)
    {
        InitializeComponent();
        _state = state;
        _settings = settings;
        DataContext = state;
        Width = 1920; Height = 1080; Left = 0; Top = 0;
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            UnregisterHotkeys();
            if (_foregroundHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_foregroundHook);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = NativeMethods.GetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE,
            ex | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
        ApplyCaptureAffinity(hwnd);
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        RegisterHotkeys(hwnd);
        // Out-of-context hook: Windows calls back on this (UI) thread when any window comes to the front.
        _onForeground = (_, _, _, _, _, _, _) => Tick();
        _foregroundHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _onForeground, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        _settings.OnChange((_, _) => Dispatcher.BeginInvoke(() => { UnregisterHotkeys(); RegisterHotkeys(hwnd); ApplyCaptureAffinity(hwnd); }));
    }

    private void ApplyCaptureAffinity(IntPtr hwnd)
        => NativeMethods.SetWindowDisplayAffinity(hwnd, _settings.CurrentValue.Overlay.ExcludeFromCapture ? NativeMethods.WDA_EXCLUDEFROMCAPTURE : NativeMethods.WDA_NONE);

    private void RegisterHotkeys(IntPtr hwnd)
    {
        var o = _settings.CurrentValue.Overlay;
        if (Hotkey.TryParse(o.HotkeyToggle, out var m1, out var k1)) NativeMethods.RegisterHotKey(hwnd, HotkeyToggleId, m1, k1);
        if (Hotkey.TryParse(o.HotkeyLayout, out var m2, out var k2)) NativeMethods.RegisterHotKey(hwnd, HotkeyLayoutId, m2, k2);
    }

    private void UnregisterHotkeys()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.UnregisterHotKey(hwnd, HotkeyToggleId);
        NativeMethods.UnregisterHotKey(hwnd, HotkeyLayoutId);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case HotkeyToggleId: _userHidden = !_userHidden; handled = true; break;
                case HotkeyLayoutId: _scoutPinned = !_scoutPinned; handled = true; break;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Screenshot mode: lays the panels out at 1080p without showing the window and renders them over
    /// <paramref name="backdrop"/>, a stand-in for the game (never game art). Every panel that has data is shown.
    /// </summary>
    public void RenderPreview(string path, Brush backdrop)
    {
        _timer.Stop();
        _state.Refresh();
        ScoutPanel.Visibility = _state.Scout.Amber.Count + _state.Scout.Sapphire.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PacePanel.Visibility = _state.Pace.HasData ? Visibility.Visible : Visibility.Collapsed;
        LanePanel.Visibility = _state.Scout.LaneOpponents.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EnemyPanel.Visibility = _state.EnemyPace.Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildPanel.Visibility = _state.Build.HasData ? Visibility.Visible : Visibility.Collapsed;
        Root.Width = 1920; Root.Height = 1080;
        Root.Measure(new System.Windows.Size(1920, 1080));
        Root.Arrange(new Rect(0, 0, 1920, 1080));
        Root.UpdateLayout();
        ScreenshotRenderer.Render(Root, backdrop, path);
    }

    /// <summary>Public so the tray menu can toggle too.</summary>
    public void ToggleVisible() => _userHidden = !_userHidden;

    private void Tick()
    {
        _state.Refresh();

        var o = _settings.CurrentValue.Overlay;
        var game = _tracker.Poll();
        bool show = o.Enabled && !_userHidden && game.Found && game.Foreground && !game.Minimized;
        if (show)
        {
            FollowGameWindow(game.Bounds);
            if (!IsVisible) Show();
        }
        else if (IsVisible)
        {
            Hide();
        }

        bool scout = o.Panels.Scout switch
        {
            ScoutPanelMode.On => true,
            ScoutPanelMode.Off => false,
            _ => _scoutPinned || DateTimeOffset.UtcNow < _state.ScoutVisibleUntil,
        };
        ScoutPanel.Visibility = scout && (_state.Scout.Amber.Count + _state.Scout.Sapphire.Count) > 0 ? Visibility.Visible : Visibility.Collapsed;
        PacePanel.Visibility = o.Panels.Pace && _state.Pace.HasData ? Visibility.Visible : Visibility.Collapsed;
        LanePanel.Visibility = o.Panels.Lane && _state.Scout.LaneOpponents.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EnemyPanel.Visibility = o.Panels.EnemyPace && _state.EnemyPace.Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildPanel.Visibility = o.Panels.Build && _state.Build.HasData ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FollowGameWindow(NativeMethods.RECT bounds)
    {
        var source = PresentationSource.FromVisual(this);
        double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        Left = bounds.Left / dpiX; Top = bounds.Top / dpiY;
        Width = Math.Max(1, bounds.Width / dpiX); Height = Math.Max(1, bounds.Height / dpiY);
        // HUD-relative positions: 1080p = 1.0, so panels land in the same place at any resolution.
        double scale = Math.Max(0.5, Height / 1080.0);
        Scale.ScaleX = Scale.ScaleY = scale;
        Root.Width = Width / scale; Root.Height = Height / scale;

        // Exclusive fullscreen covers the screen with no border and hides every topmost window; warn once.
        var screen = SystemParameters.PrimaryScreenHeight;
        if (!_warnedFullscreen && Math.Abs(Height - screen) < 2 && bounds.Top == 0)
        {
            _warnedFullscreen = true;
            _state.Status.Warning = "If you cannot see the overlay, set Deadlock to borderless windowed mode; exclusive fullscreen hides external windows.";
        }
    }
}
