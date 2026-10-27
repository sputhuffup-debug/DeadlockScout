using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DeadlockScout.App.Interop;
using DeadlockScout.App.Services;
using DeadlockScout.Ocr;
using DeadlockScout.Ocr.Abstractions;
using Brush = System.Windows.Media.Brush;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace DeadlockScout.App.Calibration;

/// <summary>
/// The OCR region tool: a dimmed layer over the game window where you drag a box around each HUD number the OCR source
/// should read. The live preview captures only the selected box, exactly as the OCR source will. Boxes are saved as
/// fractions of the game window, so they survive a resolution change.
/// </summary>
public partial class CalibrationWindow : Window
{
    private static readonly Brush SetBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5F, 0xA8, 0xF5));
    private static readonly Brush SelectedBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF2, 0xB1, 0x34));
    private static readonly Brush MutedBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6A, 0x78, 0x80));

    private sealed record SlotRow(string Key, string Label, string Status, Brush StatusBrush);

    private readonly JsonSettingsMonitor _settings;
    private readonly Func<NativeMethods.RECT?> _gameBounds;
    private readonly RegionCalibration _calibration;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly List<UIElement> _boxVisuals = new();
    private NativeMethods.RECT _surfacePixels;
    private ScreenRegionCapture? _capture;
    private bool _hiddenFromCapture;
    private bool _onGame;
    private Point? _dragStart;
    private string _selected = RegionCalibration.SoulsKey;

    public CalibrationWindow(JsonSettingsMonitor settings, Func<NativeMethods.RECT?> gameBounds)
    {
        InitializeComponent();
        _settings = settings;
        _gameBounds = gameBounds;
        _calibration = new RegionCalibration(settings.CurrentValue.Ocr.Regions);
        _previewTimer.Tick += (_, _) => RefreshPreview();
        Loaded += (_, _) => { CoverSurface(); RefreshSlots(); DrawBoxes(); _previewTimer.Start(); Activate(); Focus(); };
        Closed += (_, _) => _previewTimer.Stop();
        SizeChanged += (_, _) => DrawBoxes();
        KeyDown += OnKey;
    }

    /// <summary>Covers the game's client area (or the primary screen when the game is not running) in screen pixels.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        // Keep this window out of every capture, including our own preview of the pixels under it (Windows 10 2004+).
        _hiddenFromCapture = NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);

        var game = _gameBounds();
        _onGame = game is not null;
        var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        _surfacePixels = game ?? new NativeMethods.RECT { Left = screen.Left, Top = screen.Top, Right = screen.Right, Bottom = screen.Bottom };
        _capture = new ScreenRegionCapture(() => _surfacePixels);
        CoverSurface();
    }

    /// <summary>
    /// Places the window exactly over the surface in screen pixels, so box fractions line up with what the OCR source
    /// captures. Done again once loaded, because showing the window applies WPF's default size after SourceInitialized.
    /// </summary>
    private void CoverSurface()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || _surfacePixels.Width <= 0) return;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, _surfacePixels.Left, _surfacePixels.Top,
            _surfacePixels.Width, _surfacePixels.Height, NativeMethods.SWP_SHOWWINDOW);
    }

    // ---- slots ----

    private void RefreshSlots()
    {
        var rows = RegionCalibration.Slots.Select(s => _calibration.IsSet(s.Key)
            ? new SlotRow(s.Key, s.Label, "set", SetBrush)
            : new SlotRow(s.Key, s.Label, "not set", MutedBrush)).ToList();
        SlotList.ItemsSource = rows;
        SlotList.SelectedItem = rows.First(r => r.Key == _selected);
        SlotHint.Text = RegionCalibration.Slots.First(s => s.Key == _selected).Hint;
    }

    private void SlotChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SlotList.SelectedItem is not SlotRow row || row.Key == _selected) return;
        _selected = row.Key;
        SlotHint.Text = RegionCalibration.Slots.First(s => s.Key == _selected).Hint;
        DrawBoxes();
        RefreshPreview();
    }

    // ---- drawing boxes ----

    private void SurfaceDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Surface);
        Surface.CaptureMouse();
        UpdateRubberBand(_dragStart.Value);
        RubberBand.Visibility = Visibility.Visible;
    }

    private void SurfaceMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not null) UpdateRubberBand(e.GetPosition(Surface));
    }

    private void SurfaceUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not { } start) return;
        _dragStart = null;
        Surface.ReleaseMouseCapture();
        RubberBand.Visibility = Visibility.Collapsed;
        var end = e.GetPosition(Surface);
        if (HudRegion.FromDrag(start.X, start.Y, end.X, end.Y, Surface.ActualWidth, Surface.ActualHeight) is not { } box) return;
        _calibration.Set(_selected, box);
        RefreshPreview(); // show what was just boxed before moving on
        _selected = _calibration.NextUnset(_selected) ?? _selected;
        RefreshSlots();
        DrawBoxes();
    }

    private void UpdateRubberBand(Point current)
    {
        var start = _dragStart ?? current;
        Canvas.SetLeft(RubberBand, Math.Min(start.X, current.X));
        Canvas.SetTop(RubberBand, Math.Min(start.Y, current.Y));
        RubberBand.Width = Math.Abs(current.X - start.X);
        RubberBand.Height = Math.Abs(current.Y - start.Y);
    }

    private void DrawBoxes()
    {
        foreach (var v in _boxVisuals) Surface.Children.Remove(v);
        _boxVisuals.Clear();
        double w = Surface.ActualWidth, h = Surface.ActualHeight;
        if (w <= 0 || h <= 0) return;
        foreach (var slot in RegionCalibration.Slots)
        {
            if (_calibration.Get(slot.Key) is not { } box) continue;
            var brush = slot.Key == _selected ? SelectedBrush : SetBrush;
            var rect = new Rectangle { Width = box.Width * w, Height = box.Height * h, Stroke = brush, StrokeThickness = 2, IsHitTestVisible = false };
            Canvas.SetLeft(rect, box.X * w);
            Canvas.SetTop(rect, box.Y * h);
            var label = new TextBlock
            {
                Text = slot.Label, Foreground = brush, FontSize = 12, FontWeight = FontWeights.SemiBold, IsHitTestVisible = false,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xC0, 0x10, 0x14, 0x18)), Padding = new Thickness(3, 0, 3, 0),
            };
            Canvas.SetLeft(label, box.X * w);
            Canvas.SetTop(label, Math.Max(0, box.Y * h - 18));
            Surface.Children.Add(rect);
            Surface.Children.Add(label);
            _boxVisuals.Add(rect);
            _boxVisuals.Add(label);
        }
    }

    // ---- preview ----

    private void RefreshPreview()
    {
        var box = _calibration.Get(_selected);
        if (box is null || _capture?.Capture(box) is not { } crop)
        {
            Preview.Source = null;
            PreviewEmpty.Visibility = Visibility.Visible;
            PreviewNote.Text = box is null ? "" : "Could not capture this box.";
            return;
        }
        Preview.Source = BitmapSource.Create(crop.Width, crop.Height, 96, 96, PixelFormats.Bgra32, null, crop.Bgra.ToArray(), crop.Stride);
        PreviewEmpty.Visibility = Visibility.Collapsed;
        var notes = new List<string> { $"{crop.Width} × {crop.Height} px, captured 4 times a second while this tool is open." };
        if (!_onGame) notes.Add("Deadlock was not found, so the boxes are placed on the primary screen; start the game first for a real calibration.");
        if (!_hiddenFromCapture) notes.Add("This Windows version cannot hide this tool from capture, so the preview includes its dim layer.");
        PreviewNote.Text = string.Join(" ", notes);
    }

    // ---- panel, keys, buttons ----

    private void MovePanel(object sender, DragDeltaEventArgs e)
    {
        Canvas.SetLeft(Panel, Math.Clamp(Canvas.GetLeft(Panel) + e.HorizontalChange, 0, Math.Max(0, ActualWidth - Panel.ActualWidth)));
        Canvas.SetTop(Panel, Math.Clamp(Canvas.GetTop(Panel) + e.VerticalChange, 0, Math.Max(0, ActualHeight - Panel.ActualHeight)));
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.Enter: Save(); e.Handled = true; break;
            case Key.Delete: ClearSelected(); e.Handled = true; break;
        }
    }

    private void ClearClicked(object sender, RoutedEventArgs e) => ClearSelected();
    private void CancelClicked(object sender, RoutedEventArgs e) => Close();
    private void SaveClicked(object sender, RoutedEventArgs e) => Save();

    private void ClearSelected()
    {
        _calibration.Clear(_selected);
        RefreshSlots();
        DrawBoxes();
        RefreshPreview();
    }

    private void Save()
    {
        var current = _settings.CurrentValue;
        _settings.Save(current with { Ocr = current.Ocr with { Regions = _calibration.ToSettings() } });
        Close();
    }

    /// <summary>
    /// Screenshot mode: the tool at 1080p over <paramref name="backdrop"/> (never game art) with example boxes, laid out
    /// without showing the window. The preview shows a placeholder since there is no game to capture.
    /// </summary>
    public void RenderPreview(string path, Brush backdrop, IReadOnlyList<(string Key, HudRegion Box)> boxes)
    {
        foreach (var (key, box) in boxes) _calibration.Set(key, box);
        _selected = boxes[^1].Key;
        Root.Width = 1920; Root.Height = 1080;
        Root.Measure(new System.Windows.Size(1920, 1080));
        Root.Arrange(new Rect(0, 0, 1920, 1080));
        RefreshSlots();
        DrawBoxes();
        PreviewNote.Text = "Shows the live pixels of the selected box while the tool is open.";
        Root.UpdateLayout();
        ScreenshotRenderer.Render(Root, backdrop, path);
    }
}
