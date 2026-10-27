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
    public static void Render(FrameworkElement element, Brush backdrop, string path, Thickness padding = default)
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

        var bitmap = new RenderTargetBitmap((int)outW, (int)outH, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        png.Save(file);
    }
}
