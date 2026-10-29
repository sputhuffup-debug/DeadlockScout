using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DeadlockScout.Core.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace DeadlockScout.App.Controls;

/// <summary>One purchase on the timeline: when, the souls you had then, and how to draw its marker.</summary>
public sealed record TimelineMarker(TimeSpan At, int Souls, string Initial, Brush Color, Brush Fill, ImageSource? Icon, string Tooltip);

/// <summary>
/// A match's soul timeline: your net worth through the match as a line, with a marker for each item bought where the
/// line was at that moment, coloured by the item's slot (an icon once it has loaded). Five-minute gridlines; markers
/// that would overlap stack upwards. Optionally a dashed line of the souls expected at a chosen rank (the rank gate),
/// labelled at its end. Drawn in OnRender: one element per chart, however many purchases.
/// </summary>
public sealed class SoulTimeline : FrameworkElement
{
    public static readonly DependencyProperty SoulsProperty = DependencyProperty.Register(nameof(Souls), typeof(IReadOnlyList<NetWorthPoint>),
        typeof(SoulTimeline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(nameof(Markers), typeof(IReadOnlyList<TimelineMarker>),
        typeof(SoulTimeline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(nameof(Duration), typeof(TimeSpan),
        typeof(SoulTimeline), new FrameworkPropertyMetadata(TimeSpan.Zero, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ExpectedProperty = DependencyProperty.Register(nameof(Expected), typeof(IReadOnlyList<NetWorthPoint>),
        typeof(SoulTimeline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ExpectedLabelProperty = DependencyProperty.Register(nameof(ExpectedLabel), typeof(string),
        typeof(SoulTimeline), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<NetWorthPoint>? Souls { get => (IReadOnlyList<NetWorthPoint>?)GetValue(SoulsProperty); set => SetValue(SoulsProperty, value); }
    public IReadOnlyList<TimelineMarker>? Markers { get => (IReadOnlyList<TimelineMarker>?)GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    /// <summary>The match length, so the x axis covers it even when the samples stop early.</summary>
    public TimeSpan Duration { get => (TimeSpan)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    /// <summary>The rank gate: the souls a chosen rank averages on this hero through the match. Empty for none.</summary>
    public IReadOnlyList<NetWorthPoint>? Expected { get => (IReadOnlyList<NetWorthPoint>?)GetValue(ExpectedProperty); set => SetValue(ExpectedProperty, value); }
    /// <summary>"Oracle avg", written at the end of the expected line.</summary>
    public string ExpectedLabel { get => (string)GetValue(ExpectedLabelProperty); set => SetValue(ExpectedLabelProperty, value); }

    private const double Left = 44, Right = 12, Top = 26, Bottom = 22, MarkerSize = 16;
    private static readonly Brush Line = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xB0, 0x4B)));
    private static readonly Brush Area = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0xE8, 0xB0, 0x4B)));
    private static readonly Pen Grid = FrozenPen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly Pen Stem = FrozenPen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly Brush Label = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA3)));
    private static readonly Brush Gate = Frozen(new SolidColorBrush(Color.FromRgb(0x8F, 0xC1, 0xFF)));
    private static readonly Pen GatePen = DashedPen(Gate, 2);
    private static readonly Pen PhaseRule = FrozenPen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly Brush Dark = Frozen(new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1B)));

    public SoulTimeline()
    {
        Height = 200;
        System.Windows.Controls.ToolTipService.SetInitialShowDelay(this, 100);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var souls = Souls ?? Array.Empty<NetWorthPoint>();
        var markers = Markers ?? Array.Empty<TimelineMarker>();
        var expected = Expected ?? Array.Empty<NetWorthPoint>();
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, w, h)); // so the whole chart takes the mouse for tooltips
        if (w < Left + Right + 20 || h < Top + Bottom + 20) return;

        double seconds = Math.Max(Duration.TotalSeconds, souls.Count > 0 ? souls[^1].GameTime.TotalSeconds : 0);
        if (seconds <= 0) seconds = 60;
        int peak = Math.Max(Math.Max(souls.Count > 0 ? souls.Max(p => p.NetWorth) : 0, markers.Count > 0 ? markers.Max(m => m.Souls) : 0),
            expected.Count > 0 ? expected.Max(p => p.NetWorth) : 0);
        int step = NiceStep(peak);
        double yMax = Math.Max(step, Math.Ceiling(peak / (double)step) * step);
        double plotW = w - Left - Right, plotH = h - Top - Bottom;
        double X(TimeSpan t) => Left + t.TotalSeconds / seconds * plotW;
        double Y(double v) => Top + plotH - v / yMax * plotH;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (double v = 0; v <= yMax + 0.5; v += step)
        {
            dc.DrawLine(Grid, new Point(Left, Y(v)), new Point(w - Right, Y(v)));
            var text = Text(v >= 1000 ? $"{v / 1000:0.#}k" : $"{v:0}", 10, Label, dpi);
            dc.DrawText(text, new Point(Left - 6 - text.Width, Y(v) - text.Height / 2));
        }
        for (int min = 0; min * 60 <= seconds; min += 5)
        {
            double x = X(TimeSpan.FromMinutes(min));
            dc.DrawLine(Grid, new Point(x, Top), new Point(x, Top + plotH));
            var text = Text($"{min}:00", 10, Label, dpi);
            dc.DrawText(text, new Point(x - text.Width / 2, Top + plotH + 4));
        }

        if (souls.Count > 1)
        {
            var line = new StreamGeometry();
            var area = new StreamGeometry();
            using (var g = line.Open())
            using (var a = area.Open())
            {
                g.BeginFigure(new Point(X(souls[0].GameTime), Y(souls[0].NetWorth)), false, false);
                a.BeginFigure(new Point(X(souls[0].GameTime), Y(0)), true, true);
                a.LineTo(new Point(X(souls[0].GameTime), Y(souls[0].NetWorth)), false, false);
                foreach (var p in souls.Skip(1))
                {
                    g.LineTo(new Point(X(p.GameTime), Y(p.NetWorth)), true, true);
                    a.LineTo(new Point(X(p.GameTime), Y(p.NetWorth)), false, false);
                }
                a.LineTo(new Point(X(souls[^1].GameTime), Y(0)), false, false);
            }
            line.Freeze(); area.Freeze();
            dc.DrawGeometry(Area, null, area);
            dc.DrawGeometry(null, FrozenPen(Line, 2), line);
        }

        // The rank gate under the markers, dashed so it never reads as your own line, with the phases it is judged by.
        if (expected.Count > 1)
        {
            double phaseFrom = 0;
            foreach (var (phase, until) in new[] { ("LANING", 10.0), ("MID GAME", 20.0), ("LATE GAME", double.MaxValue) })
            {
                double fromX = X(TimeSpan.FromMinutes(phaseFrom)), toX = X(TimeSpan.FromSeconds(Math.Min(until * 60, seconds)));
                if (toX - fromX < 40) break;
                if (until < double.MaxValue) dc.DrawLine(PhaseRule, new Point(toX, Top - 6), new Point(toX, Top + plotH));
                var name = Text(phase, 9, Label, dpi, bold: true);
                dc.DrawText(name, new Point(fromX + (toX - fromX - name.Width) / 2, Top - name.Height - 6));
                phaseFrom = until;
                if (until * 60 >= seconds) break;
            }

            var gate = new StreamGeometry();
            using (var g = gate.Open())
            {
                g.BeginFigure(new Point(X(expected[0].GameTime), Y(expected[0].NetWorth)), false, false);
                foreach (var p in expected.Skip(1)) g.LineTo(new Point(X(p.GameTime), Y(p.NetWorth)), true, true);
            }
            gate.Freeze();
            dc.DrawGeometry(null, GatePen, gate);
            if (ExpectedLabel is { Length: > 0 } label)
            {
                var text = Text(label, 10, Gate, dpi, bold: true);
                var end = new Point(X(expected[^1].GameTime), Y(expected[^1].NetWorth));
                // Above the end of the line, or below it when your own line ends higher and would sit on the label.
                bool below = souls.Count > 0 && souls[^1].NetWorth > expected[^1].NetWorth;
                double y = below ? end.Y + 4 : end.Y - text.Height - 4;
                dc.DrawText(text, new Point(Math.Max(Left, end.X - text.Width - 2), Math.Clamp(y, 0, h - Bottom - text.Height)));
            }
        }

        // Markers sit on the line; one that would overlap the previous one moves up a row.
        var placed = new List<Rect>();
        foreach (var m in markers.OrderBy(m => m.At))
        {
            double cx = X(m.At), cy = Y(m.Souls);
            var r = new Rect(cx - MarkerSize / 2, cy - MarkerSize - 6, MarkerSize, MarkerSize);
            while (placed.Any(p => p.IntersectsWith(r)) && r.Top > 0) r.Offset(0, -(MarkerSize + 2));
            placed.Add(r);
            dc.DrawLine(Stem, new Point(cx, r.Bottom), new Point(cx, cy));
            dc.DrawEllipse(m.Color, null, new Point(cx, cy), 2.5, 2.5);
            dc.DrawRoundedRectangle(Dark, null, r, 3, 3);
            if (m.Icon is not null)
                dc.DrawImage(m.Icon, r);
            else
            {
                dc.DrawRoundedRectangle(m.Fill, FrozenPen(m.Color, 1), r, 3, 3);
                var letter = Text(m.Initial, 10, m.Color, dpi, bold: true);
                dc.DrawText(letter, new Point(r.Left + (r.Width - letter.Width) / 2, r.Top + (r.Height - letter.Height) / 2));
            }
        }
        _hit = placed.Zip(markers.OrderBy(m => m.At), (r, m) => (r, m.Tooltip)).ToList();
    }

    private List<(Rect Rect, string Tooltip)> _hit = new();

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var at = e.GetPosition(this);
        var tip = _hit.FirstOrDefault(h => { var r = h.Rect; r.Inflate(2, 2); return r.Contains(at); }).Tooltip;
        if (!Equals(ToolTip, tip)) ToolTip = tip;
    }

    /// <summary>A round gridline step that gives three to five lines up to <paramref name="peak"/>.</summary>
    public static int NiceStep(int peak)
    {
        foreach (var s in new[] { 1000, 2000, 5000, 10000, 20000, 50000 })
            if (peak / (double)s <= 5) return s;
        return 100000;
    }

    private static FormattedText Text(string s, double size, Brush brush, double dpi, bool bold = false)
        => new(s, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            size, brush, dpi);

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }
    private static Pen FrozenPen(Brush b, double t) { var p = new Pen(b, t); p.Freeze(); return p; }
    private static Pen DashedPen(Brush b, double t) { var p = new Pen(b, t) { DashStyle = new DashStyle(new double[] { 3, 2 }, 0) }; p.Freeze(); return p; }
}
