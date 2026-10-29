using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DeadlockScout.Core.History;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace DeadlockScout.App.Controls;

public enum TrendKind { WinRate, SoulsPerMinute }

/// <summary>
/// A trend over your games, oldest on the left: the rolling win rate (0–100%, a dashed line at 50%, each game's result
/// as a tick along the bottom), or souls per minute (each game a dot coloured by its result, the rolling average as a
/// line). Hovering a game shows its tooltip from <see cref="Labels"/>. Drawn in OnRender: one element per chart.
/// </summary>
public sealed class TrendChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<TrendPoint>),
        typeof(TrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelsProperty = DependencyProperty.Register(nameof(Labels), typeof(IReadOnlyList<string>),
        typeof(TrendChart), new FrameworkPropertyMetadata(null));
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(TrendKind),
        typeof(TrendChart), new FrameworkPropertyMetadata(TrendKind.WinRate, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<TrendPoint>? Points { get => (IReadOnlyList<TrendPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    /// <summary>One tooltip per point, in the same order.</summary>
    public IReadOnlyList<string>? Labels { get => (IReadOnlyList<string>?)GetValue(LabelsProperty); set => SetValue(LabelsProperty, value); }
    public TrendKind Kind { get => (TrendKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    private const double Left = 40, Right = 10, Top = 10, Bottom = 14;
    private static readonly Brush Win = Frozen(new SolidColorBrush(Color.FromRgb(0x7F, 0xE0, 0x8A)));
    private static readonly Brush Loss = Frozen(new SolidColorBrush(Color.FromRgb(0xF0, 0x7A, 0x6E)));
    private static readonly Brush Neutral = Frozen(new SolidColorBrush(Color.FromRgb(0x5A, 0x63, 0x70)));
    private static readonly Brush Line = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xB0, 0x4B)));
    private static readonly Pen Grid = FrozenPen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly Pen Even = FrozenDashed(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Label = Frozen(new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA3)));

    private double _x0, _dx;

    public TrendChart()
    {
        Height = 120;
        System.Windows.Controls.ToolTipService.SetInitialShowDelay(this, 100);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var points = Points ?? Array.Empty<TrendPoint>();
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(System.Windows.Media.Brushes.Transparent, null, new Rect(0, 0, w, h)); // the whole chart takes the mouse
        if (points.Count == 0 || w < Left + Right + 20 || h < Top + Bottom + 20) return;

        double plotW = w - Left - Right, plotH = h - Top - Bottom;
        _dx = points.Count > 1 ? plotW / (points.Count - 1) : 0;
        _x0 = points.Count > 1 ? Left : Left + plotW / 2;
        double X(int i) => _x0 + i * _dx;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        Brush ResultBrush(bool? won) => won is true ? Win : won is false ? Loss : Neutral;

        if (Kind == TrendKind.WinRate)
        {
            double Y(double v) => Top + plotH - v * plotH;
            foreach (var v in new[] { 0.0, 0.5, 1.0 })
            {
                dc.DrawLine(v == 0.5 ? Even : Grid, new Point(Left, Y(v)), new Point(w - Right, Y(v)));
                var text = Text($"{v * 100:0}%", Label, dpi);
                dc.DrawText(text, new Point(Left - 6 - text.Width, Y(v) - text.Height / 2));
            }
            DrawLine(dc, points.Select((p, i) => p.RollingWinRate is { } r ? new Point(X(i), Y(r)) : (Point?)null));
            double tick = Math.Clamp(_dx * 0.6, 2, 6);
            for (int i = 0; i < points.Count; i++)
                dc.DrawRectangle(ResultBrush(points[i].Won), null, new Rect(X(i) - tick / 2, Top + plotH + 4, tick, 6));
        }
        else
        {
            double peak = points.Max(p => Math.Max(p.SoulsPerMinute, p.RollingSoulsPerMinute));
            double low = points.Min(p => Math.Min(p.SoulsPerMinute, p.RollingSoulsPerMinute));
            double step = peak - low > 600 ? 200 : 100;
            double yMin = Math.Max(0, Math.Floor(low / step) * step), yMax = Math.Max(yMin + step, Math.Ceiling(peak / step) * step);
            double Y(double v) => Top + plotH - (v - yMin) / (yMax - yMin) * plotH;
            for (double v = yMin; v <= yMax + 0.5; v += step)
            {
                dc.DrawLine(Grid, new Point(Left, Y(v)), new Point(w - Right, Y(v)));
                var text = Text($"{v:0}", Label, dpi);
                dc.DrawText(text, new Point(Left - 6 - text.Width, Y(v) - text.Height / 2));
            }
            for (int i = 0; i < points.Count; i++)
                dc.DrawEllipse(ResultBrush(points[i].Won), null, new Point(X(i), Y(points[i].SoulsPerMinute)), 3, 3);
            DrawLine(dc, points.Select((p, i) => (Point?)new Point(X(i), Y(p.RollingSoulsPerMinute))));
        }
    }

    /// <summary>A polyline through the points; a null breaks it (no rolling value yet).</summary>
    private static void DrawLine(DrawingContext dc, IEnumerable<Point?> points)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            bool open = false;
            foreach (var p in points)
            {
                if (p is not { } pt) { open = false; continue; }
                if (!open) { g.BeginFigure(pt, false, false); open = true; }
                else g.LineTo(pt, true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, FrozenPen(Line, 2), geo);
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var labels = Labels;
        if (labels is null || labels.Count == 0) return;
        int i = _dx > 0 ? (int)Math.Round((e.GetPosition(this).X - _x0) / _dx) : 0;
        var tip = labels[Math.Clamp(i, 0, labels.Count - 1)];
        if (!Equals(ToolTip, tip)) ToolTip = tip;
    }

    private static FormattedText Text(string s, Brush brush, double dpi)
        => new(s, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 10, brush, dpi);

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }
    private static Pen FrozenPen(Brush b, double t) { var p = new Pen(b, t); p.Freeze(); return p; }
    private static Pen FrozenDashed(Brush b) { var p = new Pen(b, 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) }; p.Freeze(); return p; }
}
