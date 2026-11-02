using System.Globalization;
using DeadlockScout.Core.Meter;

namespace DeadlockScout.Ocr;

public enum CalibrationLevel { Good, Warn, Bad }

/// <summary>What the calibration tool says about a box's latest reading, and how loudly.</summary>
public sealed record CalibrationReading(CalibrationLevel Level, string Text);

/// <summary>
/// Judges each box's live readings in the calibration tool, so a box that reads wrong is fixed where the problem starts
/// rather than caught by the guards during a match. A reading is bad when it is not a number (or a clock); it is a
/// warning when the box also reads something besides the number (a label, the soul icon, a stray glyph), or when the
/// last few readings jump in a way the game never does: a soul count down by more than a misread or up by more than
/// <see cref="OcrJumpGuard.Plausible"/> allows, a clock that does not follow itself (<see cref="OcrClockGuard.Follows"/>).
/// Both happened on Oct 31, 2026 ("7,689" and "76,890" read for 768; verification note 41). Pure, so tested.
/// </summary>
public sealed class CalibrationCheck
{
    /// <summary>How many recent readings of a box are compared.</summary>
    public const int Window = 6;
    /// <summary>Readings older than this are no longer compared (the game may have moved on while the tool was idle).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(15);

    private readonly Dictionary<string, List<(DateTimeOffset At, int? Souls, TimeSpan? Clock)>> _recent = new();

    /// <summary>Forgets a box's readings: its box was moved, cleared or redrawn.</summary>
    public void Reset(string key) => _recent.Remove(key);

    public CalibrationReading Observe(string key, string? text, DateTimeOffset at)
    {
        bool timer = key == RegionCalibration.TimerKey;
        string raw = string.IsNullOrWhiteSpace(text) ? "nothing" : $"“{text.Trim()}”";
        int? souls = timer ? null : HudParser.ParseSouls(text);
        TimeSpan? clock = timer ? HudParser.ParseClock(text) : null;
        if (souls is null && clock is null)
        {
            Reset(key);
            return new(CalibrationLevel.Bad, $"Reads {raw}: not {(timer ? "a clock" : "a number")} yet. Move or resize the box onto the {(timer ? "match clock" : "number")}.");
        }

        var history = _recent.TryGetValue(key, out var h) ? h : _recent[key] = new();
        history.Add((at, souls, clock));
        history.RemoveAll(r => at - r.At > MaxAge);
        if (history.Count > Window) history.RemoveRange(0, history.Count - Window);

        string value = timer ? Clock(clock!.Value) : souls!.Value.ToString("N0", CultureInfo.InvariantCulture);
        string leftover = timer ? HudParser.ClockLeftover(text) : HudParser.SoulsLeftover(text);
        if (leftover.Length > 0)
            return new(CalibrationLevel.Warn,
                $"Reads {raw} → {value}, but also “{leftover}”: the box takes in more than the {(timer ? "clock" : "number")}. Draw it tight around the digits.");

        if (Jump(history) is { } jump)
            return new(CalibrationLevel.Warn,
                $"Reads {raw} → {value}, but the readings jump ({jump}): something besides the {(timer ? "clock" : "number")} gets read now and then"
                + (timer ? "." : ", such as the soul icon or the +souls pop-ups.") + " Draw the box tight around the digits.");

        return new(CalibrationLevel.Good, $"Reads {raw} → {value}" + (history.Count >= 3 ? " · steady" : ""));
    }

    /// <summary>The first pair of readings in a row that the game could not produce, as "a → b"; null when all follow.</summary>
    private static string? Jump(List<(DateTimeOffset At, int? Souls, TimeSpan? Clock)> history)
    {
        for (int i = 1; i < history.Count; i++)
        {
            var (prevAt, prevSouls, prevClock) = history[i - 1];
            var (at, souls, clock) = history[i];
            if (prevSouls is { } a && souls is { } b && !OcrJumpGuard.Plausible(a, b, (at - prevAt).TotalSeconds))
                return $"{a.ToString("N0", CultureInfo.InvariantCulture)} → {b.ToString("N0", CultureInfo.InvariantCulture)}";
            if (prevClock is { } c && clock is { } d && !OcrClockGuard.Follows(c, prevAt, d, at))
                return $"{Clock(c)} → {Clock(d)}";
        }
        return null;
    }

    private static string Clock(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
