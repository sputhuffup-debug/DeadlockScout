using System.Text.Json;
using DeadlockScout.Ocr.Abstractions;

namespace DeadlockScout.Ocr;

/// <summary>
/// One character cut out of a HUD box, normalised so the same glyph matches at any size: its ink sampled onto a
/// <see cref="GlyphSegmenter.GridW"/>×<see cref="GlyphSegmenter.GridH"/> grid (the share of each cell that is ink), plus
/// where it sits on the line, which tells "," and "." apart from the digits and from each other.
/// </summary>
/// <param name="Grid">Ink coverage per cell, 0–1, row by row.</param>
/// <param name="Height">The glyph's height over the line's (the tallest glyph's): 1 for a digit, small for "." and ",".</param>
/// <param name="Middle">Where its vertical middle sits on the line, 0 at the top and 1 at the bottom.</param>
/// <param name="Aspect">Width over height.</param>
public sealed record GlyphShape(float[] Grid, float Height, float Middle, float Aspect);

/// <summary>
/// Cuts a HUD crop into glyphs: the ink (whichever of light and dark covers less of the box, the HUD drawing light
/// digits on dark), split at columns without ink. A digit pair that touches would come out as one glyph, which then
/// matches nothing, and the read falls back to Windows OCR. Specks of a few pixels are dropped. Pure.
/// </summary>
public static class GlyphSegmenter
{
    public const int GridW = 8, GridH = 12;
    /// <summary>Glyphs with fewer ink pixels than this are noise.</summary>
    public const int MinInk = 3;

    public static IReadOnlyList<GlyphShape> Segment(CapturedFrame crop)
    {
        int w = crop.Width, h = crop.Height;
        if (w <= 0 || h <= 0) return Array.Empty<GlyphShape>();
        var ink = Ink(crop);

        // Columns with any ink, grouped into runs: one run per glyph.
        var runs = new List<(int X0, int X1)>();
        int start = -1;
        for (int x = 0; x <= w; x++)
        {
            bool any = false;
            if (x < w) for (int y = 0; y < h && !any; y++) any = ink[y * w + x];
            if (any && start < 0) start = x;
            else if (!any && start >= 0) { runs.Add((start, x - 1)); start = -1; }
        }

        var boxes = new List<(int X0, int X1, int Y0, int Y1)>();
        foreach (var (x0, x1) in runs)
        {
            int y0 = h, y1 = -1, count = 0;
            for (int y = 0; y < h; y++)
                for (int x = x0; x <= x1; x++)
                    if (ink[y * w + x]) { count++; y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
            if (count >= MinInk) boxes.Add((x0, x1, y0, y1));
        }
        if (boxes.Count == 0) return Array.Empty<GlyphShape>();

        int lineTop = boxes.Min(b => b.Y0), lineBottom = boxes.Max(b => b.Y1);
        double lineHeight = Math.Max(1, lineBottom - lineTop + 1);
        double tallest = boxes.Max(b => b.Y1 - b.Y0 + 1);
        return boxes.Select(b =>
        {
            int gw = b.X1 - b.X0 + 1, gh = b.Y1 - b.Y0 + 1;
            // Each pixel adds the share of itself that falls in each cell, so the grid does not jump with the glyph's size.
            var grid = new float[GridW * GridH];
            double cw = gw / (double)GridW, ch = gh / (double)GridH;
            for (int y = b.Y0; y <= b.Y1; y++)
                for (int x = b.X0; x <= b.X1; x++)
                {
                    if (!ink[y * w + x]) continue;
                    double px0 = (x - b.X0) / cw, px1 = (x - b.X0 + 1) / cw, py0 = (y - b.Y0) / ch, py1 = (y - b.Y0 + 1) / ch;
                    for (int cy = (int)py0; cy < Math.Min(GridH, (int)Math.Ceiling(py1)); cy++)
                    {
                        double oy = Math.Min(py1, cy + 1) - Math.Max(py0, cy);
                        if (oy <= 0) continue;
                        for (int cx = (int)px0; cx < Math.Min(GridW, (int)Math.Ceiling(px1)); cx++)
                        {
                            double ox = Math.Min(px1, cx + 1) - Math.Max(px0, cx);
                            if (ox > 0) grid[cy * GridW + cx] += (float)(ox * oy);
                        }
                    }
                }
            for (int i = 0; i < grid.Length; i++) grid[i] = Math.Min(1f, grid[i]); // a cell's coverage, 0–1
            float middle = (float)(((b.Y0 + b.Y1) / 2.0 - lineTop) / lineHeight);
            return new GlyphShape(grid, (float)(gh / tallest), middle, (float)gw / gh);
        }).ToList();
    }

    /// <summary>Which pixels are ink: Otsu's threshold on luminance, ink being the side with fewer pixels.</summary>
    internal static bool[] Ink(CapturedFrame crop)
    {
        int w = crop.Width, h = crop.Height;
        var src = crop.Bgra.Span;
        var lum = new byte[w * h];
        var histogram = new int[256];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * crop.Stride + x * 4;
                byte l = (byte)((src[i + 2] * 299 + src[i + 1] * 587 + src[i] * 114) / 1000);
                lum[y * w + x] = l;
                histogram[l]++;
            }
        int threshold = Otsu(histogram, w * h);
        int above = 0;
        for (int v = threshold + 1; v < 256; v++) above += histogram[v];
        bool lightInk = above <= w * h - above;
        var ink = new bool[w * h];
        for (int i = 0; i < ink.Length; i++) ink[i] = lightInk ? lum[i] > threshold : lum[i] <= threshold;
        return ink;
    }

    private static int Otsu(int[] histogram, int total)
    {
        double sum = 0;
        for (int v = 0; v < 256; v++) sum += v * (double)histogram[v];
        double sumB = 0, best = -1;
        int wB = 0, threshold = 127;
        for (int v = 0; v < 256; v++)
        {
            wB += histogram[v];
            if (wB == 0) continue;
            int wF = total - wB;
            if (wF == 0) break;
            sumB += v * (double)histogram[v];
            double mB = sumB / wB, mF = (sum - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; threshold = v; }
        }
        return threshold;
    }
}

/// <summary>
/// The glyph templates learned for the HUD's font, and reading with them. A box is read only when every glyph in it
/// matches a template clearly (close to one character and not about as close to another); otherwise <see cref="Read"/>
/// returns null and the caller asks Windows OCR. Templates are learned from boxes whose text is already known (a clean,
/// steady Windows OCR reading in the calibration tool): when the box cuts into exactly as many glyphs as the text has
/// characters, each glyph is kept under its character, a few samples each. Pure apart from its own state; saved and
/// loaded as JSON.
/// </summary>
public sealed class GlyphSet
{
    /// <summary>The characters the HUD's souls count and clock use.</summary>
    public const string Alphabet = "0123456789:.,k";
    /// <summary>Samples kept per character; the oldest goes when a new one comes.</summary>
    public const int SamplesPerChar = 6;
    /// <summary>
    /// The farthest a glyph may be from its best template and still be read. On the synthetic font a glyph is about
    /// 0.01 from its own template at any size and at least 0.14 from any other character (0 and 8 are the nearest pair).
    /// </summary>
    public const float MaxDistance = 0.12f;
    /// <summary>How much nearer the best character must be than the next best different one.</summary>
    public const float MinMargin = 0.04f;

    private readonly Dictionary<char, List<GlyphShape>> _templates = new();
    private readonly object _gate = new();

    /// <summary>The characters learned so far, in alphabet order.</summary>
    public string Known { get { lock (_gate) return new string(Alphabet.Where(_templates.ContainsKey).ToArray()); } }

    /// <summary>Whether all ten digits are known, so a souls count or a clock can be read without Windows OCR.</summary>
    public bool HasAllDigits { get { lock (_gate) return "0123456789".All(_templates.ContainsKey); } }

    /// <summary>
    /// Learns the glyphs in <paramref name="crop"/> as the characters of <paramref name="text"/> (spaces ignored).
    /// False, learning nothing, when the text has a character outside <see cref="Alphabet"/> or the box does not cut
    /// into one glyph per character (something besides the text is in it).
    /// </summary>
    public bool Learn(CapturedFrame crop, string text)
    {
        var chars = text.Where(c => !char.IsWhiteSpace(c)).ToArray();
        if (chars.Length == 0 || chars.Any(c => !Alphabet.Contains(c))) return false;
        var glyphs = GlyphSegmenter.Segment(crop);
        if (glyphs.Count != chars.Length) return false;
        lock (_gate)
        {
            for (int i = 0; i < chars.Length; i++)
            {
                var list = _templates.TryGetValue(chars[i], out var l) ? l : _templates[chars[i]] = new();
                list.Add(glyphs[i]);
                if (list.Count > SamplesPerChar) list.RemoveAt(0);
            }
        }
        return true;
    }

    /// <summary>The box's text, or null when any glyph is not clearly one learned character (or the box is empty).</summary>
    public string? Read(CapturedFrame crop)
    {
        var glyphs = GlyphSegmenter.Segment(crop);
        if (glyphs.Count == 0) return null;
        var text = new char[glyphs.Count];
        lock (_gate)
        {
            if (_templates.Count == 0) return null;
            for (int i = 0; i < glyphs.Count; i++)
            {
                char best = '\0';
                float bestD = float.MaxValue, secondD = float.MaxValue;
                foreach (var (c, samples) in _templates)
                {
                    float d = samples.Min(t => Distance(glyphs[i], t));
                    if (d < bestD) { if (best != c) secondD = bestD; bestD = d; best = c; }
                    else if (d < secondD) secondD = d;
                }
                if (bestD > MaxDistance || secondD - bestD < MinMargin) return null;
                text[i] = best;
            }
        }
        return new string(text);
    }

    /// <summary>
    /// How unlike two glyphs are: the mean difference of their ink grids, plus their difference in height and place on
    /// the line (so "." and "," never pass for digits), and a little for shape.
    /// </summary>
    public static float Distance(GlyphShape a, GlyphShape b)
    {
        float grid = 0;
        for (int i = 0; i < a.Grid.Length; i++) grid += Math.Abs(a.Grid[i] - b.Grid[i]);
        grid /= a.Grid.Length;
        float aspect = Math.Abs(MathF.Log(Math.Max(0.05f, a.Aspect)) - MathF.Log(Math.Max(0.05f, b.Aspect)));
        return grid + 0.6f * Math.Abs(a.Height - b.Height) + 0.6f * Math.Abs(a.Middle - b.Middle) + 0.1f * aspect;
    }

    /// <summary>Forgets everything (the HUD font changed: recalibrate).</summary>
    public void Clear() { lock (_gate) _templates.Clear(); }

    private sealed record Saved(int Version, Dictionary<string, List<GlyphShape>> Templates);

    public string ToJson()
    {
        lock (_gate)
            return JsonSerializer.Serialize(new Saved(1, _templates.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)));
    }

    /// <summary>A set from <see cref="ToJson"/>; empty when the text is missing, damaged or from another version.</summary>
    public static GlyphSet FromJson(string? json)
    {
        var set = new GlyphSet();
        if (string.IsNullOrWhiteSpace(json)) return set;
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(json);
            if (saved is not { Version: 1, Templates: not null }) return set;
            foreach (var (key, samples) in saved.Templates)
            {
                if (key.Length != 1 || !Alphabet.Contains(key[0]) || samples is null) continue;
                var valid = samples.Where(s => s?.Grid is { Length: GlyphSegmenter.GridW * GlyphSegmenter.GridH }).TakeLast(SamplesPerChar).ToList();
                if (valid.Count > 0) set._templates[key[0]] = valid;
            }
        }
        catch (JsonException) { }
        return set;
    }
}

/// <summary>
/// The OCR engine the soul source uses: the glyph reader first, once it knows every digit, and Windows OCR (the
/// <see cref="Fallback"/>) for any box it cannot read clearly. While the glyphs keep reading, Windows OCR is never
/// asked, so its engine never loads. Counts both, and says when most recent reads fell back, which means the HUD's font
/// changed and the calibration tool should teach the glyphs again.
/// </summary>
public sealed class GlyphOcrEngine : IOcrEngine
{
    /// <summary>How many recent reads the fallback share is taken over.</summary>
    public const int RecentReads = 60;

    private readonly GlyphSet _glyphs;
    private readonly Func<bool> _enabled;
    private readonly Queue<bool> _recent = new(); // true: a glyph read; false: it fell back
    private readonly object _gate = new();

    public GlyphOcrEngine(GlyphSet glyphs, IOcrEngine fallback, Func<bool>? enabled = null)
    {
        _glyphs = glyphs;
        Fallback = fallback;
        _enabled = enabled ?? (() => true);
    }

    public IOcrEngine Fallback { get; }
    public GlyphSet Glyphs => _glyphs;

    /// <summary>Whether the glyphs are being tried at all: switched on and every digit learned.</summary>
    public bool GlyphsActive => _enabled() && _glyphs.HasAllDigits;

    public string Name => GlyphsActive ? $"glyph reader, {Fallback.Name} as fallback" : Fallback.Name;
    public bool IsAvailable => GlyphsActive || Fallback.IsAvailable;

    /// <summary>Reads the glyphs answered, and reads that went to <see cref="Fallback"/> while they were active.</summary>
    public long GlyphReads { get; private set; }
    public long FallbackReads { get; private set; }

    /// <summary>Most of the last <see cref="RecentReads"/> reads fell back: the glyphs no longer match the HUD.</summary>
    public bool NeedsRecalibration
    {
        get { lock (_gate) return _recent.Count >= RecentReads && _recent.Count(r => !r) > RecentReads / 2; }
    }

    public async Task<string?> RecognizeAsync(CapturedFrame region, CancellationToken ct)
    {
        if (GlyphsActive)
        {
            var text = _glyphs.Read(region);
            lock (_gate)
            {
                if (text is not null) GlyphReads++; else FallbackReads++;
                _recent.Enqueue(text is not null);
                while (_recent.Count > RecentReads) _recent.Dequeue();
            }
            if (text is not null) return text;
        }
        return Fallback.IsAvailable ? await Fallback.RecognizeAsync(region, ct).ConfigureAwait(false) : null;
    }

    /// <summary>Words are never glyphs: straight to Windows OCR, kept out of the glyph statistics.</summary>
    public Task<string?> RecognizeWordsAsync(CapturedFrame region, CancellationToken ct)
        => Fallback.IsAvailable ? Fallback.RecognizeWordsAsync(region, ct) : Task.FromResult<string?>(null);

    /// <summary>"knows 10 of 10 digits · 96% of reads, Windows OCR for the rest" for Diagnostics.</summary>
    public string Status
    {
        get
        {
            if (!_enabled()) return "off";
            int digits = _glyphs.Known.Count(char.IsDigit);
            if (digits < 10) return $"learning, knows {digits} of 10 digits (calibration tool: keep the clock box selected while it ticks)";
            long all;
            long glyph;
            lock (_gate) { glyph = GlyphReads; all = GlyphReads + FallbackReads; }
            string share = all == 0 ? "no reads yet" : $"{(double)glyph / all:P0} of reads, Windows OCR for the rest";
            return NeedsRecalibration ? $"{share}; most reads fall back now, teach it again in the calibration tool" : $"ready · {share}";
        }
    }
}
