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
    private static Brush BadBrush => Themes.Palette.Bad;

    private sealed record SlotRow(string Key, string Label, string Status, Brush StatusBrush);

    private readonly JsonSettingsMonitor _settings;
    private readonly Func<NativeMethods.RECT?> _gameBounds;
    private readonly IOcrEngine _engine;
    private readonly GlyphBook? _glyphs;
    private bool _glyphsLearned;
    private bool _reading;
    private readonly CalibrationCheck _check = new();
    private RegionCalibration _calibration;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromSeconds(1) }; // a drawn box also refreshes at once
    private readonly List<UIElement> _boxVisuals = new();
    private NativeMethods.RECT _surfacePixels;
    private ScreenRegionCapture? _capture;
    private bool _hiddenFromCapture;
    private bool _onGame;
    private Point? _dragStart;
    private string _selected = RegionCalibration.SoulsKey;

    /// <param name="engine">Windows OCR: the tool's readings, and the labels the glyph reader learns from.</param>
    /// <param name="glyphs">The glyph reader's templates, one set per box, taught from each steady reading; null when it is not in use.</param>
    /// <param name="saveGlyphs">Writes the templates to disk; called when the tool closes.</param>
    public CalibrationWindow(JsonSettingsMonitor settings, Func<NativeMethods.RECT?> gameBounds, IOcrEngine engine, GlyphBook? glyphs = null, Action? saveGlyphs = null)
    {
        _glyphs = glyphs;
        InitializeComponent();
        _settings = settings;
        _gameBounds = gameBounds;
        _engine = engine;
        _calibration = new RegionCalibration(settings.CurrentValue.Ocr.Regions);
        UseOcr.IsChecked = settings.CurrentValue.Ocr.Enabled;
        UseOcr.IsEnabled = engine.IsAvailable;
        EngineNote.Text = engine.IsAvailable
            ? $"Engine: {engine.Name}."
            : $"{engine.Name}: add a language with OCR support (for example English) in Windows Settings, Time & language.";
        _previewTimer.Tick += (_, _) => RefreshPreview();
        Loaded += (_, _) => { CoverSurface(); RefreshSlots(); DrawBoxes(); _previewTimer.Start(); Activate(); Focus(); };
        Closed += (_, _) =>
        {
            _previewTimer.Stop();
            if (_glyphsLearned) saveGlyphs?.Invoke();
        };
        ShowGlyphs(null);
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
        // Boxes drawn on a window of another shape are shown where they now read (the same carry the OCR source makes).
        if (_onGame && HudCarry.Moves(_settings.CurrentValue.Ocr.Regions, _surfacePixels.Width, _surfacePixels.Height))
            _calibration = new RegionCalibration(HudCarry.For(_settings.CurrentValue.Ocr.Regions, _surfacePixels.Width, _surfacePixels.Height));
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
        var preset = HudPresets.For(_surfacePixels.Width, _surfacePixels.Height);
        bool anyUnset = RegionCalibration.Slots.Any(s => !_calibration.IsSet(s.Key));
        PresetButton.Visibility = preset is not null && anyUnset ? Visibility.Visible : Visibility.Collapsed;
        PresetButton.Content = preset is { Measured: true }
            ? $"Use the preset boxes ({_surfacePixels.Width}×{_surfacePixels.Height})"
            : $"Use the preset boxes ({_surfacePixels.Width}×{_surfacePixels.Height}, estimated)";
    }

    /// <summary>Fills the boxes not drawn yet from the preset for the game window's shape, then shows the first one's preview.</summary>
    private void PresetClicked(object sender, RoutedEventArgs e)
    {
        if (HudPresets.For(_surfacePixels.Width, _surfacePixels.Height) is not { } preset || _calibration.FillFrom(preset) == 0) return;
        foreach (var slot in RegionCalibration.Slots) _check.Reset(slot.Key);
        RefreshSlots();
        DrawBoxes();
        RefreshPreview();
    }

    private void SlotChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SlotList.SelectedItem is not SlotRow row || row.Key == _selected) return;
        _selected = row.Key;
        _check.Reset(_selected);
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
        _check.Reset(_selected);
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
            ReadingLine.Text = "";
            return;
        }
        _ = ReadAsync(_selected, crop);
        // The souls box shows what is read: the count outlined in blue, a skipped soul icon in grey, haunted souls in red.
        bool souls = _selected == RegionCalibration.SoulsKey;
        var shown = souls ? SoulsLine.Outline(crop, _glyphs?.Souls) : crop;
        Preview.Source = BitmapSource.Create(shown.Width, shown.Height, 96, 96, PixelFormats.Bgra32, null, shown.Bgra.ToArray(), shown.Stride);
        PreviewEmpty.Visibility = Visibility.Collapsed;
        var notes = new List<string> { $"{crop.Width} × {crop.Height} px, captured once a second while this tool is open." };
        if (souls) notes.Add("Outlined: the count that is read (blue), a soul icon it skips (grey), haunted souls (red). Anything outside them is ignored.");
        if (!_onGame) notes.Add("Deadlock was not found, so the boxes are placed on the primary screen; start the game first for a real calibration.");
        if (!_hiddenFromCapture) notes.Add("This Windows version cannot hide this tool from capture, so the preview includes its dim layer.");
        PreviewNote.Text = string.Join(" ", notes);
    }

    /// <summary>Runs the OCR engine on the crop and shows what the OCR source would read; skipped while a read is in flight.</summary>
    private async Task ReadAsync(string key, CapturedFrame crop)
    {
        if (!_engine.IsAvailable || _reading) return;
        // The souls box is read the way the OCR source reads it: its number (SoulsLine), and the haunted souls line under it.
        CapturedFrame? below = null;
        if (key == RegionCalibration.SoulsKey) (crop, below) = SoulsLine.Split(crop);
        var raw = crop; // what the glyphs learn from: the icon is noted there, then cut off
        if (SetFor(key) is { } known) crop = known.TrimIcon(crop);
        _reading = true;
        try
        {
            var text = await _engine.RecognizeAsync(crop, CancellationToken.None);
            if (key != _selected) return; // the selection moved on while reading
            var reading = _check.Observe(key, text, DateTimeOffset.UtcNow);
            ReadingLine.Text = reading.Text;
            if (below is not null && HudParser.ParseHaunted(await _engine.RecognizeWordsAsync(below, CancellationToken.None)) is { } haunted && key == _selected)
                ReadingLine.Text += $" · plus {haunted:N0} haunted souls (counted as unspent)";
            ReadingLine.Foreground = reading.Level switch
            {
                CalibrationLevel.Good => SetBrush,
                CalibrationLevel.Warn => SelectedBrush,
                _ => BadBrush,
            };
            // A clean reading that agrees with the ones before it teaches the glyph reader each character in the box.
            // Each box teaches its own set: the clock and the souls count are drawn in different fonts.
            if (_glyphs is not null && reading.Steady && text is not null && SetFor(key) is { } set)
            {
                bool hadIcon = set.Icon is not null;
                if (set.LearnBox(raw, text.Trim()) || (!hadIcon && set.Icon is not null)) _glyphsLearned = true;
            }
            ShowGlyphs(crop);
        }
        catch (Exception ex) { ReadingLine.Text = $"OCR failed: {ex.Message}"; }
        finally { _reading = false; }
    }

    /// <summary>The glyph set the selected box learns and reads with; null for boxes the glyph reader does not read.</summary>
    private GlyphSet? SetFor(string key) => _glyphs is null ? null : key switch
    {
        RegionCalibration.SoulsKey => _glyphs.Souls,
        RegionCalibration.TimerKey => _glyphs.Clock,
        _ => null,
    };

    /// <summary>What the glyph reader knows of the selected box's font, and what it reads there once it knows every digit.</summary>
    private void ShowGlyphs(CapturedFrame? crop)
    {
        var set = SetFor(_selected);
        if (set is null) { GlyphLine.Visibility = Visibility.Collapsed; RelearnButton.Visibility = Visibility.Collapsed; return; }
        GlyphLine.Visibility = Visibility.Visible;
        RelearnButton.Visibility = set.Known.Length > 0 || set.Icon is not null ? Visibility.Visible : Visibility.Collapsed;
        bool clock = _selected == RegionCalibration.TimerKey;
        string which = clock ? "the clock's digits" : "the souls count's digits";
        var digits = new string(set.Known.Where(char.IsDigit).ToArray());
        if (!set.HasAllDigits)
        {
            var missing = string.Join(" ", "0123456789".Where(d => !digits.Contains(d)));
            GlyphLine.Text = clock
                ? $"Glyph reader ({which}): learning, still missing {missing}. Keep this box selected while the clock ticks; a steady reading teaches it."
                : $"Glyph reader ({which}): learning, still missing {missing}. It learns as your souls change, here or during a match.";
            return;
        }
        var read = crop is null ? null : set.Read(crop);
        GlyphLine.Text = read is null
            ? $"Glyph reader ({which}): knows every digit, but cannot read this box clearly; Windows OCR reads it instead."
            : $"Glyph reader ({which}): knows every digit, and reads this box as “{read}” itself.";
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

    /// <summary>Forgets the selected box's glyphs (and icon) so the next steady readings teach them afresh; saved on close.</summary>
    private void RelearnClicked(object sender, RoutedEventArgs e)
    {
        if (SetFor(_selected) is not { } set) return;
        set.Clear();
        _check.Reset(_selected); // "steady" is earned again before anything is learned
        _glyphsLearned = true;   // the emptied set is written when the tool closes
        ShowGlyphs(null);
    }
    private void CancelClicked(object sender, RoutedEventArgs e) => Close();
    private void SaveClicked(object sender, RoutedEventArgs e) => Save();

    private void ClearSelected()
    {
        _calibration.Clear(_selected);
        _check.Reset(_selected);
        RefreshSlots();
        DrawBoxes();
        RefreshPreview();
    }

    private void Save()
    {
        var current = _settings.CurrentValue;
        // The window the boxes were drawn on is saved with them, so they can follow it to another shape later; over the
        // screen (no game running) the size is that of the screen, which the game would cover in borderless mode.
        var regions = _calibration.ToSettings() with { DrawnOn = new DeadlockScout.Core.Settings.HudSize(_surfacePixels.Width, _surfacePixels.Height) };
        _settings.Save(current with { Ocr = current.Ocr with { Regions = regions, Enabled = UseOcr.IsChecked == true } });
        Close();
    }

    /// <summary>
    /// Screenshot mode: the tool at 1080p over <paramref name="backdrop"/> (never game art) with example boxes, laid out
    /// without showing the window. The preview shows a placeholder since there is no game to capture.
    /// </summary>
    /// <summary>A souls box drawn from plain text for the screenshot: the count, the label beside it, a haunted line.</summary>
    private static CapturedFrame DemoSoulsCrop()
    {
        const int w = 260, h = 78;
        var canvas = new Canvas { Width = w, Height = h, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1B, 0x20, 0x24)) };
        var teal = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7F, 0xE0, 0xC8));
        void Text(string s, double size, Brush brush, double x, double y, FontWeight weight)
        {
            var t = new TextBlock { Text = s, FontSize = size, Foreground = brush, FontWeight = weight, FontFamily = new System.Windows.Media.FontFamily("Segoe UI") };
            Canvas.SetLeft(t, x); Canvas.SetTop(t, y);
            canvas.Children.Add(t);
        }
        Text("2,340", 34, teal, 10, 0, FontWeights.Bold);
        Text("SOULS", 11, teal, 122, 24, FontWeights.SemiBold);
        Text("1,051 HAUNTED SOULS", 13, new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0x55, 0x55)), 12, 52, FontWeights.SemiBold);
        canvas.Measure(new System.Windows.Size(w, h));
        canvas.Arrange(new Rect(0, 0, w, h));
        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var px = new byte[w * h * 4];
        bitmap.CopyPixels(px, w * 4, 0);
        return new CapturedFrame(w, h, w * 4, px, DateTimeOffset.UnixEpoch);
    }

    public void RenderPreview(string path, Brush backdrop, IReadOnlyList<(string Key, HudRegion Box)> boxes)
    {
        foreach (var (key, box) in boxes) _calibration.Set(key, box);
        _selected = boxes[^1].Key;
        Root.Width = 1920; Root.Height = 1080;
        _surfacePixels = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        Root.Measure(new System.Windows.Size(1920, 1080));
        Root.Arrange(new Rect(0, 0, 1920, 1080));
        RefreshSlots();
        DrawBoxes();
        PreviewNote.Text = "Shows the live pixels of the selected box while the tool is open.";
        ReadingLine.Text = "Reads “9,850” → 9,850 · steady";
        ReadingLine.Foreground = SetBrush;
        if (_selected == RegionCalibration.SoulsKey)
        {
            // A made-up souls box (plain text, no game art): the count, its label, and a haunted souls line, outlined.
            var demo = DemoSoulsCrop();
            var shown = SoulsLine.Outline(demo, null);
            Preview.Source = BitmapSource.Create(shown.Width, shown.Height, 96, 96, PixelFormats.Bgra32, null, shown.Bgra.ToArray(), shown.Stride);
            PreviewEmpty.Visibility = Visibility.Collapsed;
            PreviewNote.Text = "Outlined: the count that is read (blue), a soul icon it skips (grey, once learned), haunted souls (red). Anything outside them is ignored.";
            ReadingLine.Text = "Reads “2,340” → 2,340 · steady · plus 1,051 haunted souls (counted as unspent)";
        }
        UseOcr.IsChecked = true;
        UseOcr.IsEnabled = true;
        EngineNote.Text = "Engine: Windows OCR (en-US).";
        GlyphLine.Visibility = Visibility.Visible;
        RelearnButton.Visibility = Visibility.Visible;
        GlyphLine.Text = _selected == RegionCalibration.SoulsKey
            ? "Glyph reader (the souls count's digits): knows every digit, and reads this box as “2,340” itself."
            : "Glyph reader: knows every digit, and reads this box as “9,850” itself.";
        Root.UpdateLayout();
        ScreenshotRenderer.Render(Root, backdrop, path);
    }
}
