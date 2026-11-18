using DeadlockScout.Ocr.Abstractions;

namespace DeadlockScout.Ocr;

/// <summary>A box the finder settled on, with the reading that settled it; null box when none read cleanly.</summary>
public sealed record HudFoundBox(HudRegion? Box, string Reading);

/// <summary>What <see cref="HudAutoFind.FindAsync"/> found for both boxes.</summary>
public sealed record HudFound(HudFoundBox Souls, HudFoundBox Timer);

/// <summary>
/// Finds the souls and clock boxes on its own (0.9), so setting up OCR needs no drawing: it tries boxes around the preset
/// for the window's shape (<see cref="HudPresets"/>), nearest first, shifted by up to half a box and a little larger or
/// smaller, reads each the way the OCR source does, and keeps the first that reads a clean value (a number, or a clock,
/// and nothing else) and stays steady over <see cref="Confirmations"/> more readings a second apart, judged by the
/// calibration tool's own <see cref="CalibrationCheck"/>. Only those rectangles are captured. A box it cannot find is
/// left for the manual tool. Pure but for the capture and the engine it is handed, so tested.
/// </summary>
public static class HudAutoFind
{
    /// <summary>Readings after the first a candidate must pass to be kept.</summary>
    public const int Confirmations = 2;
    public static readonly TimeSpan ConfirmGap = TimeSpan.FromSeconds(1);

    private static readonly double[] Shifts = { 0, -0.25, 0.25, -0.5, 0.5 };
    private static readonly double[] Scales = { 1, 1.25, 0.85 };

    /// <summary>The boxes tried around <paramref name="preset"/>, nearest first, each inside the window.</summary>
    public static IReadOnlyList<HudRegion> Candidates(HudRegion preset)
    {
        var list = new List<(double Cost, HudRegion Box)>();
        foreach (var scale in Scales)
        foreach (var dx in Shifts)
        foreach (var dy in Shifts)
        {
            double w = preset.Width * scale, h = preset.Height * scale;
            // Scaled about the preset's centre, then shifted by a share of its size.
            double x = preset.X + (preset.Width - w) / 2 + dx * preset.Width, y = preset.Y + (preset.Height - h) / 2 + dy * preset.Height;
            x = Math.Clamp(x, 0, Math.Max(0, 1 - w));
            y = Math.Clamp(y, 0, Math.Max(0, 1 - h));
            list.Add((Math.Abs(dx) + Math.Abs(dy) + Math.Abs(scale - 1), new HudRegion(x, y, w, h)));
        }
        return list.OrderBy(c => c.Cost).Select(c => c.Box).Distinct().ToList();
    }

    /// <summary>
    /// Finds both boxes. <paramref name="capture"/> copies one box off the game window (null when it cannot);
    /// <paramref name="read"/> is the OCR engine; <paramref name="wait"/> paces the confirming readings.
    /// </summary>
    public static async Task<HudFound> FindAsync(HudPreset preset, Func<HudRegion, CapturedFrame?> capture,
        Func<CapturedFrame, OcrField, CancellationToken, Task<string?>> read, Func<TimeSpan, CancellationToken, Task> wait, CancellationToken ct)
    {
        var souls = await FindOneAsync(RegionCalibration.SoulsKey, OcrField.Souls, preset.Souls, capture, read, wait, ct);
        var timer = await FindOneAsync(RegionCalibration.TimerKey, OcrField.Clock, preset.Timer, capture, read, wait, ct);
        return new HudFound(souls, timer);
    }

    private static async Task<HudFoundBox> FindOneAsync(string key, OcrField field, HudRegion preset, Func<HudRegion, CapturedFrame?> capture,
        Func<CapturedFrame, OcrField, CancellationToken, Task<string?>> read, Func<TimeSpan, CancellationToken, Task> wait, CancellationToken ct)
    {
        string? lastReading = null;
        foreach (var box in Candidates(preset))
        {
            ct.ThrowIfCancellationRequested();
            var check = new CalibrationCheck();
            var first = await ReadBoxAsync(key, field, box, capture, read, check, ct);
            if (first is null) return new HudFoundBox(null, "Could not capture the game window.");
            if (first.Level != CalibrationLevel.Good) { lastReading ??= first.Text; continue; }

            var last = first;
            for (int i = 0; i < Confirmations && last.Level == CalibrationLevel.Good; i++)
            {
                await wait(ConfirmGap, ct);
                last = await ReadBoxAsync(key, field, box, capture, read, check, ct) ?? new CalibrationReading(CalibrationLevel.Bad, "Could not capture.");
            }
            if (last.Level == CalibrationLevel.Good) return new HudFoundBox(box, last.Text);
            lastReading = last.Text;
        }
        return new HudFoundBox(null, lastReading ?? "Nothing read.");
    }

    private static async Task<CalibrationReading?> ReadBoxAsync(string key, OcrField field, HudRegion box, Func<HudRegion, CapturedFrame?> capture,
        Func<CapturedFrame, OcrField, CancellationToken, Task<string?>> read, CalibrationCheck check, CancellationToken ct)
    {
        if (capture(box) is not { } crop) return null;
        // The souls box is read as the OCR source reads it: its number line only.
        if (field == OcrField.Souls) crop = SoulsLine.Isolate(crop);
        var text = await read(crop, field, ct);
        return check.Observe(key, text, crop.At);
    }
}
