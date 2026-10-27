using DeadlockScout.Ocr.Abstractions;

namespace DeadlockScout.Ocr;

/// <summary>
/// Prepares a captured HUD crop for a general-purpose OCR engine: grayscale, contrast stretch, dark text on a light
/// background (the HUD draws light digits on dark), enlarged so the digits are tall enough to read, and padded. Pure,
/// so it is unit tested without a screen.
/// </summary>
public static class OcrPreprocess
{
    /// <summary>Crops shorter than this are enlarged; OCR engines read 10–20 px HUD digits poorly.</summary>
    public const int TargetHeight = 64;
    public const int MaxScale = 8;
    /// <summary>Light margin around the text, in output pixels; engines miss glyphs that touch the edge.</summary>
    public const int Padding = 12;

    public static CapturedFrame Prepare(CapturedFrame crop)
    {
        int w = crop.Width, h = crop.Height;
        if (w <= 0 || h <= 0) return crop;
        var src = crop.Bgra.Span;

        // 1. luminance and its histogram
        var lum = new byte[w * h];
        var histogram = new int[256];
        for (int y = 0; y < h; y++)
        {
            int row = y * crop.Stride;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                byte l = (byte)((src[i + 2] * 299 + src[i + 1] * 587 + src[i] * 114) / 1000);
                lum[y * w + x] = l;
                histogram[l]++;
            }
        }

        // 2. stretch the 5th..95th percentile to the full range, and put the text (the minority) dark on light
        int lo = Percentile(histogram, w * h, 0.05), hi = Percentile(histogram, w * h, 0.95), median = Percentile(histogram, w * h, 0.5);
        if (hi <= lo) { lo = 0; hi = 255; }
        bool invert = median < 128; // a dark background means light text
        var map = new byte[256];
        for (int v = 0; v < 256; v++)
        {
            int s = Math.Clamp((v - lo) * 255 / (hi - lo), 0, 255);
            map[v] = (byte)(invert ? 255 - s : s);
        }

        // 3. enlarge (bilinear) and pad with white
        int scale = Math.Clamp((int)Math.Ceiling(TargetHeight / (double)h), 1, MaxScale);
        int sw = w * scale, sh = h * scale;
        int ow = sw + 2 * Padding, oh = sh + 2 * Padding, stride = ow * 4;
        var output = new byte[stride * oh];
        Array.Fill(output, (byte)0xFF);
        for (int y = 0; y < sh; y++)
        {
            double fy = Math.Max(0, (y + 0.5) / scale - 0.5);
            int y0 = Math.Min((int)fy, h - 1), y1 = Math.Min(y0 + 1, h - 1);
            double ty = fy - y0;
            for (int x = 0; x < sw; x++)
            {
                double fx = Math.Max(0, (x + 0.5) / scale - 0.5);
                int x0 = Math.Min((int)fx, w - 1), x1 = Math.Min(x0 + 1, w - 1);
                double tx = fx - x0;
                double top = map[lum[y0 * w + x0]] * (1 - tx) + map[lum[y0 * w + x1]] * tx;
                double bottom = map[lum[y1 * w + x0]] * (1 - tx) + map[lum[y1 * w + x1]] * tx;
                byte g = (byte)Math.Round(top * (1 - ty) + bottom * ty);
                int o = (y + Padding) * stride + (x + Padding) * 4;
                output[o] = output[o + 1] = output[o + 2] = g;
                output[o + 3] = 0xFF;
            }
        }
        return new CapturedFrame(ow, oh, stride, output, crop.At);
    }

    private static int Percentile(int[] histogram, int total, double fraction)
    {
        long target = (long)Math.Ceiling(total * fraction), seen = 0;
        for (int v = 0; v < 256; v++)
        {
            seen += histogram[v];
            if (seen >= Math.Max(1, target)) return v;
        }
        return 255;
    }
}
