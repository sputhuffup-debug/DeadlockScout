using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Cursors = System.Windows.Input.Cursors;
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

    /// <summary>The panels the layout editor can move, by their x:Name (also the first part of their position key).</summary>
    private static readonly string[] Movable = { "PacePanel", "LanePanel", "ScoutPanel", "EnemyPanel", "BuildPanel", "SessionPanel" };

    private readonly OverlayState _state;
    private readonly JsonSettingsMonitor _settings;
    // Where each movable panel lives in the default columns, and every column child's original order, so a panel can
    // go back to its exact place when its saved position is reset.
    private readonly Dictionary<FrameworkElement, System.Windows.Controls.Panel> _homes = new();
    private readonly Dictionary<UIElement, int> _homeOrder = new();
    private string? _appliedLayout;
    private bool _wasInMatch;
    private FrameworkElement? _dragging;
    private Point _dragStart, _dragOrigin;
    private readonly GameWindowTracker _tracker = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private NativeMethods.WinEventProc? _onForeground; // kept in a field: the hook holds only a native pointer to it
    private IntPtr _foregroundHook;
    private HwndSource? _source;
    private bool _userHidden;
    private bool _scoutPinned;
    private bool _warnedFullscreen;

    public OverlayWindow(OverlayState state, JsonSettingsMonitor settings)
    {
        InitializeComponent();
        _state = state;
        _settings = settings;
        foreach (var name in Movable)
        {
            var panel = (FrameworkElement)FindName(name);
            var column = (System.Windows.Controls.Panel)panel.Parent;
            _homes[panel] = column;
            for (int i = 0; i < column.Children.Count; i++) _homeOrder.TryAdd(column.Children[i], i);
            panel.MouseLeftButtonDown += PanelDown;
            panel.MouseMove += PanelMove;
            panel.MouseLeftButtonUp += PanelUp;
            panel.LostMouseCapture += (_, _) => FinishDrag(); // alt-tab mid-drag still saves where the panel is
        }
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
        ApplyClickThrough(hwnd); // a preview switched on before the window first showed
        ApplyCaptureAffinity(hwnd);
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        RegisterHotkeys(hwnd);
        // Out-of-context hook: Windows calls back on this (UI) thread when any window comes to the front.
        _onForeground = (_, _, _, _, _, _, _) => Tick();
        _foregroundHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _onForeground, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        _settings.OnChange((_, _) => Dispatcher.BeginInvoke(() => { UnregisterHotkeys(); RegisterHotkeys(hwnd); ApplyCaptureAffinity(hwnd); ApplyLayout(); }));
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
        ApplyLayout();
        Root.Measure(new System.Windows.Size(1920, 1080));
        Root.Arrange(new Rect(0, 0, 1920, 1080));
        Root.UpdateLayout();
        ScreenshotRenderer.Render(Root, backdrop, path);
    }

    /// <summary>
    /// Screenshot mode: a close-up of the enemy pace board at twice its size, to show the power-spike alert and chips.
    /// Call after <see cref="RenderPreview"/>, which lays the panels out.
    /// </summary>
    public void RenderSpikeCloseUp(string path, Brush backdrop)
    {
        var at = EnemyPanel.TranslatePoint(new Point(0, 0), Root);
        var region = new Rect(at, new System.Windows.Size(EnemyPanel.ActualWidth, EnemyPanel.ActualHeight));
        region.Inflate(12, 12);
        ScreenshotRenderer.RenderRegion(Root, region, backdrop, path, scale: 2);
    }

    /// <summary>
    /// Preview: show the overlay whether or not the game is in front, over the game window if it is running and over the
    /// primary screen if not, with every panel that has data shown. It is also the layout editor: only during a preview
    /// does the overlay take the mouse, over its panels and the editor bar (transparent pixels still pass clicks on), and
    /// the movable panels get a gold outline and can be dragged. Outside it the overlay is always click-through.
    /// </summary>
    public bool Preview { get; private set; }
    public event Action<bool>? PreviewChanged;

    public void SetPreview(bool on)
    {
        if (on == Preview) return;
        if (!on) FinishDrag();
        Preview = on;
        if (on) _userHidden = false;
        ApplyClickThrough(new WindowInteropHelper(this).Handle);
        IsHitTestVisible = on;
        ShowEditVisuals(on);
        Tick();
        PreviewChanged?.Invoke(on);
    }

    private void ApplyClickThrough(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        int ex = NativeMethods.GetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongW(hwnd, NativeMethods.GWL_EXSTYLE, Preview ? ex & ~NativeMethods.WS_EX_TRANSPARENT : ex | NativeMethods.WS_EX_TRANSPARENT);
    }

    /// <summary>Public so the tray menu can toggle too.</summary>
    public void ToggleVisible() => _userHidden = !_userHidden;

    // ---- layout editor (part of the preview) ----

    /// <summary>Screenshot mode: renders the overlay as the preview shows it, with the editor bar and outlines.</summary>
    public void RenderEditorPreview(string path, Brush backdrop)
    {
        ShowEditVisuals(true);
        RenderPreview(path, backdrop);
        ShowEditVisuals(false);
    }

    private void ShowEditVisuals(bool on)
    {
        EditBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        foreach (var panel in _homes.Keys.OfType<Border>())
        {
            if (on)
            {
                panel.BorderBrush = (Brush)FindResource("Gold");
                panel.BorderThickness = new Thickness(2);
                panel.Cursor = Cursors.SizeAll;
            }
            else
            {
                panel.ClearValue(Border.BorderBrushProperty);
                panel.ClearValue(Border.BorderThicknessProperty);
                panel.ClearValue(CursorProperty);
            }
        }
    }

    private void StopPreview(object sender, RoutedEventArgs e) => SetPreview(false);

    private void ResetLayout(object sender, RoutedEventArgs e)
    {
        var s = _settings.CurrentValue;
        _settings.Save(s with { Overlay = s.Overlay.WithoutPositions(OverlayLayout.SizeSuffix(Root.Width, Root.Height)) });
    }

    /// <summary>
    /// Puts each movable panel where the settings say: at its saved position for this overlay size, or back in its default
    /// column. Cheap to call every tick: it only moves anything when the positions or the size changed.
    /// </summary>
    private void ApplyLayout()
    {
        double w = Root.Width, h = Root.Height;
        if (double.IsNaN(w) || double.IsNaN(h)) return;
        var positions = _settings.CurrentValue.Overlay.Positions ?? new();
        var signature = $"{w:0}x{h:0}|" + string.Join(";", Movable.Select(n =>
            positions.TryGetValue(OverlayLayout.Key(n, w, h), out var p) ? $"{n}={p.X},{p.Y}" : n));
        if (signature == _appliedLayout) return;
        _appliedLayout = signature;
        foreach (var (panel, column) in _homes)
        {
            if (positions.TryGetValue(OverlayLayout.Key(panel.Name, w, h), out var p))
            {
                MoveToFreeLayer(panel);
                Canvas.SetLeft(panel, p.X);
                Canvas.SetTop(panel, p.Y);
            }
            else if (panel.Parent != column)
            {
                ((System.Windows.Controls.Panel)panel.Parent).Children.Remove(panel);
                int order = _homeOrder[panel];
                int at = column.Children.Cast<UIElement>().Count(c => _homeOrder.TryGetValue(c, out var o) && o < order);
                column.Children.Insert(at, panel);
            }
        }
    }

    private void MoveToFreeLayer(FrameworkElement panel)
    {
        if (panel.Parent == FreeLayer) return;
        ((System.Windows.Controls.Panel)panel.Parent).Children.Remove(panel);
        FreeLayer.Children.Add(panel);
    }

    private void PanelDown(object sender, MouseButtonEventArgs e)
    {
        if (!Preview || sender is not FrameworkElement panel) return;
        // Lift the panel out of its column at the spot it is drawn now, then drag it from there. Canvas places the
        // margin box, so the margin comes off the drawn corner.
        var at = panel.TranslatePoint(new Point(-panel.Margin.Left, -panel.Margin.Top), Root);
        MoveToFreeLayer(panel);
        Canvas.SetLeft(panel, at.X);
        Canvas.SetTop(panel, at.Y);
        _dragging = panel;
        _dragStart = e.GetPosition(Root);
        _dragOrigin = at;
        panel.CaptureMouse();
        e.Handled = true;
    }

    private void PanelMove(object sender, MouseEventArgs e)
    {
        if (_dragging is null || !ReferenceEquals(sender, _dragging)) return;
        var now = e.GetPosition(Root);
        var p = OverlayLayout.Place(_dragOrigin.X + now.X - _dragStart.X, _dragOrigin.Y + now.Y - _dragStart.Y,
            _dragging.ActualWidth, _dragging.ActualHeight, Root.Width, Root.Height);
        Canvas.SetLeft(_dragging, p.X);
        Canvas.SetTop(_dragging, p.Y);
    }

    private void PanelUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging is null || !ReferenceEquals(sender, _dragging)) return;
        FinishDrag();
        e.Handled = true;
    }

    private void FinishDrag()
    {
        if (_dragging is not { } panel) return;
        _dragging = null;
        panel.ReleaseMouseCapture();
        var p = new PanelPosition(Canvas.GetLeft(panel), Canvas.GetTop(panel));
        var s = _settings.CurrentValue;
        _settings.Save(s with { Overlay = s.Overlay.WithPosition(OverlayLayout.Key(panel.Name, Root.Width, Root.Height), p) });
    }

    /// <summary>Shows or hides the lobby scout list, like its hotkey.</summary>
    public void ToggleScout() { _scoutPinned = !_scoutPinned; Tick(); }

    private void Tick()
    {
        _state.Refresh();
        // A preview left on ends when a match starts, so its panels never catch clicks meant for the game. One opened
        // during a match is left alone: that was on purpose.
        if (_state.InMatch && !_wasInMatch && Preview) SetPreview(false);
        _wasInMatch = _state.InMatch;

        var o = _settings.CurrentValue.Overlay;
        var game = _tracker.Poll();
        bool gameOnScreen = game.Found && !game.Minimized;
        bool show = !_userHidden && (Preview || (o.Enabled && gameOnScreen && game.Foreground)); // the hotkey still hides a preview
        if (show)
        {
            if (gameOnScreen) FollowGameWindow(game.Bounds, warnFullscreen: !Preview);
            else FollowGameWindow(PrimaryScreen(), warnFullscreen: false);
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
            ScoutPanelMode.Hotkey => Preview || _scoutPinned,
            _ => Preview || _scoutPinned || DateTimeOffset.UtcNow < _state.ScoutVisibleUntil,
        };
        ScoutPanel.Visibility = scout && (_state.Scout.Amber.Count + _state.Scout.Sapphire.Count) > 0 ? Visibility.Visible : Visibility.Collapsed;
        PacePanel.Visibility = o.Panels.Pace && _state.Pace.HasData ? Visibility.Visible : Visibility.Collapsed;
        Sparkline.Visibility = o.Panels.Sparkline && _state.Pace.HasSparkline ? Visibility.Visible : Visibility.Collapsed;
        LanePanel.Visibility = o.Panels.Lane && _state.Scout.LaneOpponents.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EnemyPanel.Visibility = o.Panels.EnemyPace && _state.EnemyPace.Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildPanel.Visibility = o.Panels.Build && _state.Build.HasData ? Visibility.Visible : Visibility.Collapsed;
        SessionPanel.Visibility = o.Panels.Session && !_state.InMatch && _state.Session.HasData ? Visibility.Visible : Visibility.Collapsed;
    }

    private static NativeMethods.RECT PrimaryScreen()
    {
        var s = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        return new NativeMethods.RECT { Left = s.Left, Top = s.Top, Right = s.Right, Bottom = s.Bottom };
    }

    private void FollowGameWindow(NativeMethods.RECT bounds, bool warnFullscreen = true)
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
        ApplyLayout();

        // Exclusive fullscreen covers the screen with no border and hides every topmost window; warn once.
        var screen = SystemParameters.PrimaryScreenHeight;
        if (warnFullscreen && !_warnedFullscreen && Math.Abs(Height - screen) < 2 && bounds.Top == 0)
        {
            _warnedFullscreen = true;
            _state.Status.Warning = "If you cannot see the overlay, set Deadlock to borderless windowed mode; exclusive fullscreen hides external windows.";
        }
    }
}
