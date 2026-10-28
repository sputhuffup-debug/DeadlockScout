using System.IO;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using System.Windows.Media.Imaging;

namespace DeadlockScout.App.Services;

/// <summary>
/// Renders a laid-out element to a PNG for the README screenshots (<c>DeadlockScout --screenshots &lt;folder&gt;</c>, run by
/// the Screenshots workflow on a Windows runner). Draws the element through a VisualBrush with an absolute viewbox so its
/// own margin and offset inside the window never shift the image.
/// </summary>
public static class ScreenshotRenderer
{
    /// <summary>
    /// Renders <paramref name="parts"/> top to bottom, each laid out at its natural height for its current width. Used for
    /// a window whose body scrolls: Windows caps a window at the screen size (about 768 px on a CI runner), so the window
    /// itself can never be tall enough to show everything.
    /// </summary>
    public static void RenderStacked(IReadOnlyList<FrameworkElement> parts, Brush backdrop, string path, Thickness padding)
    {
        var sizes = new List<(FrameworkElement Part, double Width, double Height)>();
        foreach (var part in parts)
        {
            double w = Math.Ceiling(part.ActualWidth);
            part.Measure(new System.Windows.Size(w, double.PositiveInfinity));
            double h = Math.Ceiling(part.DesiredSize.Height);
            part.Arrange(new Rect(0, 0, w, h));
            sizes.Add((part, w, h));
        }
        double outW = sizes.Max(s => s.Width) + padding.Left + padding.Right;
        double outH = sizes.Sum(s => s.Height) + padding.Top + padding.Bottom;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(backdrop, null, new Rect(0, 0, outW, outH));
            double y = padding.Top;
            foreach (var (part, w, h) in sizes)
            {
                var brush = new VisualBrush(part) { Viewbox = new Rect(0, 0, w, h), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
                dc.DrawRectangle(brush, null, new Rect(padding.Left, y, w, h));
                y += h;
            }
        }
        Save(visual, (int)outW, (int)outH, path);
    }

    /// <summary>
    /// Renders one laid-out element. <paramref name="scale"/> above 1 makes a close-up: the element is drawn again at a
    /// higher resolution (not stretched), so text stays sharp.
    /// </summary>
    public static void Render(FrameworkElement element, Brush backdrop, string path, Thickness padding = default, double scale = 1)
    {
        double w = Math.Ceiling(element.ActualWidth), h = Math.Ceiling(element.ActualHeight);
        if (w < 1 || h < 1) throw new InvalidOperationException($"{element.Name} has no size; it was not laid out.");
        double outW = w + padding.Left + padding.Right, outH = h + padding.Top + padding.Bottom;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(backdrop, null, new Rect(0, 0, outW, outH));
            var brush = new VisualBrush(element)
            {
                Viewbox = new Rect(0, 0, w, h),
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill,
            };
            dc.DrawRectangle(brush, null, new Rect(padding.Left, padding.Top, w, h));
        }

        Save(visual, (int)outW, (int)outH, path, scale);
    }

    private static void Save(Visual visual, int width, int height, string path, double scale = 1)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        png.Save(file);
    }
}
