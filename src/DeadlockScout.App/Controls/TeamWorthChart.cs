using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DeadlockScout.Core.History;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace DeadlockScout.App.Controls;

/// <summary>
/// The team graph on an opened game (0.7): your team's total souls less the enemy's through the match, filled above
/// the zero line in the "ahead" colour and below it in the "behind" one, with the biggest swing as a band. A click picks
/// a moment, shared with the soul timeline above, which shows every player's net worth there. Drawn in OnRender.
/// </summary>
public sealed class TeamWorthChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<TeamWorthPoint>),
        typeof(TeamWorthChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SwingProperty = DependencyProperty.Register(nameof(Swing), typeof(TeamSwing),
        typeof(TeamWorthChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PickedTimeProperty = DependencyProperty.Register(nameof(PickedTime), typeof(TimeSpan?),
        typeof(TeamWorthChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>Both teams' totals through the match, oldest first.</summary>
    public IReadOnlyList<TeamWorthPoint>? Points { get => (IReadOnlyList<TeamWorthPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    /// <summary>The biggest swing, drawn as a band; null for none.</summary>
    public TeamSwing? Swing { get => (TeamSwing?)GetValue(SwingProperty); set => SetValue(SwingProperty, value); }
    /// <summary>The moment picked here or on the soul timeline.</summary>
    public TimeSpan? PickedTime { get => (TimeSpan?)GetValue(PickedTimeProperty); set => SetValue(PickedTimeProperty, value); }

    private const double Left = 44, Right = 12, Top = 10, Bottom = 22;
    private static readonly Pen Grid = FrozenPen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly Pen Zero = FrozenPen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly Pen PickPen = FrozenPen(new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)), 1.5);
    private static readonly Brush Label = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA3)));
    private static readonly Brush Band = Frozen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));

    public TeamWorthChart()
    {
        Height = 130;
        Cursor = System.Windows.Input.Cursors.Cross;
        System.Windows.Controls.ToolTipService.SetInitialShowDelay(this, 100);
        // A click picks the moment; neither press nor release reaches the card, which opens and closes on a release.
        MouseLeftButtonDown += (_, e) => { if (TimeAt(e.GetPosition(this).X) is { } t) PickedTime = t; e.Handled = true; };
        MouseLeftButtonUp += (_, e) => e.Handled = true;
    }

    private double PlotSeconds() => Points is { Count: > 0 } p ? Math.Max(60, p[^1].At.TotalSeconds) : 60;

    private TimeSpan? TimeAt(double x)
    {
        double plotW = ActualWidth - Left - Right;
        if (plotW <= 0 || x < Left || x > Left + plotW) return null;
        return TimeSpan.FromSeconds(Math.Round((x - Left) / plotW * PlotSeconds()));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var points = Points ?? Array.Empty<TeamWorthPoint>();
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, w, h)); // the whole chart takes the mouse
        if (points.Count < 2 || w < Left + Right + 20 || h < Top + Bottom + 20) return;

        double seconds = PlotSeconds(), plotW = w - Left - Right, plotH = h - Top - Bottom;
        int peak = Math.Max(1000, points.Max(p => Math.Abs(p.Gap)));
        int step = SoulTimeline.NiceStep(peak * 2); // two or three lines each side of even
        double yMax = Math.Ceiling(peak / (double)step) * step;
        double X(TimeSpan t) => Left + t.TotalSeconds / seconds * plotW;
        double Y(double v) => Top + plotH / 2 - v / yMax * (plotH / 2);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (Swing is { } sw)
            dc.DrawRectangle(Band, null, new Rect(new Point(X(sw.From), Top), new Point(X(sw.To), Top + plotH)));
        for (double v = -yMax; v <= yMax + 0.5; v += step)
        {
            dc.DrawLine(v == 0 ? Zero : Grid, new Point(Left, Y(v)), new Point(w - Right, Y(v)));
            var text = Text(v == 0 ? "even" : $"{(v > 0 ? "+" : "−")}{Math.Abs(v) / 1000:0.#}k", 10, Label, dpi);
            dc.DrawText(text, new Point(Left - 6 - text.Width, Y(v) - text.Height / 2));
        }
        for (int min = 0; min * 60 <= seconds; min += 5)
        {
            double x = X(TimeSpan.FromMinutes(min));
            dc.DrawLine(Grid, new Point(x, Top), new Point(x, Top + plotH));
            var text = Text($"{min}:00", 10, Label, dpi);
            dc.DrawText(text, new Point(x - text.Width / 2, Top + plotH + 4));
        }

        // The area between the gap and the zero line: the part above zero in the "ahead" colour, below in "behind".
        var area = new StreamGeometry();
        var line = new StreamGeometry();
        using (var a = area.Open())
        using (var l = line.Open())
        {
            a.BeginFigure(new Point(X(points[0].At), Y(0)), true, true);
            l.BeginFigure(new Point(X(points[0].At), Y(points[0].Gap)), false, false);
            foreach (var p in points)
            {
                a.LineTo(new Point(X(p.At), Y(p.Gap)), false, false);
                l.LineTo(new Point(X(p.At), Y(p.Gap)), true, true);
            }
            a.LineTo(new Point(X(points[^1].At), Y(0)), false, false);
        }
        area.Freeze(); line.Freeze();
        dc.PushClip(new RectangleGeometry(new Rect(Left, Top, plotW, Y(0) - Top)));
        dc.DrawGeometry(Tint(Themes.Palette.Good), null, area);
        dc.DrawGeometry(null, FrozenPen(Themes.Palette.Good, 2), line);
        dc.Pop();
        dc.PushClip(new RectangleGeometry(new Rect(Left, Y(0), plotW, Top + plotH - Y(0))));
        dc.DrawGeometry(Tint(Themes.Palette.Bad), null, area);
        dc.DrawGeometry(null, FrozenPen(Themes.Palette.Bad, 2), line);
        dc.Pop();

        if (PickedTime is { } pick && pick.TotalSeconds <= seconds)
            dc.DrawLine(PickPen, new Point(X(pick), Top), new Point(X(pick), Top + plotH));
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        string? tip = null;
        if (TimeAt(e.GetPosition(this).X) is { } t && Points is { Count: > 0 } pts)
        {
            var p = pts.MinBy(x => Math.Abs((x.At - t).TotalSeconds))!;
            tip = $"{(int)p.At.TotalMinutes}:{p.At.Seconds:00} · your team {Format(p.Team)} vs {Format(p.Enemy)} " +
                  $"({(p.Gap >= 0 ? "+" : "−")}{Format(Math.Abs(p.Gap))}) · click for every player";
        }
        if (!Equals(ToolTip, tip)) ToolTip = tip;
    }

    private static string Format(int souls) => souls >= 1000 ? $"{souls / 1000.0:0.#}k" : souls.ToString(CultureInfo.InvariantCulture);

    private static Brush Tint(Brush b)
    {
        if (b is not SolidColorBrush s) return b;
        var c = s.Color;
        return Frozen(new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B)));
    }

    private static FormattedText Text(string s, double size, Brush brush, double dpi)
        => new(s, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, brush, dpi);

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }
    private static Pen FrozenPen(Brush b, double t) { var p = new Pen(b, t); p.Freeze(); return p; }
}
