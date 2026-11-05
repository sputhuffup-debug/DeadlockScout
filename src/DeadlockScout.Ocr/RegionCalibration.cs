using DeadlockScout.Core.Settings;
using DeadlockScout.Ocr.Abstractions;

namespace DeadlockScout.Ocr;

/// <summary>One region the calibration tool can set.</summary>
public sealed record RegionSlot(string Key, string Label, string Hint);

/// <summary>
/// The calibration tool's working set: which slots have a box, and the conversion to and from settings. Pure, so the
/// window's code-behind only draws and handles the mouse.
/// </summary>
public sealed class RegionCalibration
{
    public const string SoulsKey = "souls";
    public const string TimerKey = "timer";

    /// <summary>
    /// The two boxes OCR reads. The enemy scoreboard rows of earlier versions are gone (0.3.4): nothing read them, and
    /// the scoreboard shows souls only to the thousand ("11k"), where the broadcast gives every enemy's exact souls.
    /// </summary>
    public static readonly IReadOnlyList<RegionSlot> Slots = new[]
    {
        new RegionSlot(SoulsKey, "Your souls", "The soul counter on your HUD: from the soul icon, wide enough for five digits, tall enough for where it sits with and without haunted souls. The SOULS label and the haunted line are handled."),
        new RegionSlot(TimerKey, "Match clock", "The game clock at the top of the screen."),
    };

    private readonly Dictionary<string, HudRegion> _boxes = new();

    public RegionCalibration(HudRegions? saved = null)
    {
        if (saved is null) return;
        Put(SoulsKey, saved.Souls);
        Put(TimerKey, saved.Timer);
    }

    public HudRegion? Get(string key) => _boxes.GetValueOrDefault(key);
    public bool IsSet(string key) => _boxes.ContainsKey(key);
    public int Count => _boxes.Count;

    public void Set(string key, HudRegion box)
    {
        if (!Slots.Any(s => s.Key == key)) throw new ArgumentException($"Unknown region '{key}'.", nameof(key));
        _boxes[key] = box;
    }

    public void Clear(string key) => _boxes.Remove(key);

    /// <summary>Fills the boxes not drawn yet from <paramref name="preset"/>; a drawn box is never replaced. Returns how many were filled.</summary>
    public int FillFrom(HudPreset preset)
    {
        int filled = 0;
        foreach (var (key, box) in new[] { (SoulsKey, preset.Souls), (TimerKey, preset.Timer) })
            if (!IsSet(key)) { _boxes[key] = box; filled++; }
        return filled;
    }

    /// <summary>The next slot after <paramref name="key"/> with no box, wrapping around; null when all are set.</summary>
    public string? NextUnset(string key)
    {
        int start = Slots.ToList().FindIndex(s => s.Key == key);
        for (int i = 1; i <= Slots.Count; i++)
        {
            var slot = Slots[(start + i + Slots.Count) % Slots.Count];
            if (!IsSet(slot.Key)) return slot.Key;
        }
        return null;
    }

    /// <summary>Settings for the boxes.</summary>
    public HudRegions ToSettings() => new() { Souls = ToRect(Get(SoulsKey)), Timer = ToRect(Get(TimerKey)) };

    /// <summary>What the OCR source would make of a region's text: "12,480" for souls, "14:20" for the clock, null if unreadable.</summary>
    public static string? ReadValue(string key, string? text)
        => key == TimerKey
            ? HudParser.ParseClock(text) is { } t ? $"{(int)t.TotalMinutes}:{t.Seconds:00}" : null
            : HudParser.ParseSouls(text) is { } n ? n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : null;

    public static HudRegion? FromRect(HudRect? r) => r is null || r.Width <= 0 || r.Height <= 0 ? null : new HudRegion(r.X, r.Y, r.Width, r.Height);
    public static HudRect? ToRect(HudRegion? r) => r is null ? null : new HudRect(r.X, r.Y, r.Width, r.Height);

    private void Put(string key, HudRect? rect)
    {
        if (FromRect(rect) is { } box) _boxes[key] = box;
    }
}
