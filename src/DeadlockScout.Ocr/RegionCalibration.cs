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
    public const int EnemyRows = 6;

    public static string EnemyKey(int row) => $"enemy{row}";

    public static readonly IReadOnlyList<RegionSlot> Slots = new[]
        {
            new RegionSlot(SoulsKey, "Your souls", "The soul counter on your HUD. Box the digits only."),
            new RegionSlot(TimerKey, "Match clock", "The game clock at the top of the screen."),
        }
        .Concat(Enumerable.Range(1, EnemyRows).Select(i => new RegionSlot(EnemyKey(i), $"Enemy {i} souls",
            $"Hold the scoreboard open and box the souls of enemy row {i} from the top.")))
        .ToList();

    private readonly Dictionary<string, HudRegion> _boxes = new();

    public RegionCalibration(HudRegions? saved = null)
    {
        if (saved is null) return;
        Put(SoulsKey, saved.Souls);
        Put(TimerKey, saved.Timer);
        for (int i = 0; i < Math.Min(EnemyRows, saved.EnemySouls.Count); i++) Put(EnemyKey(i + 1), saved.EnemySouls[i]);
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

    /// <summary>Settings for the boxes; enemy rows are kept up to the last one set, with nulls for gaps.</summary>
    public HudRegions ToSettings()
    {
        int lastEnemy = Enumerable.Range(1, EnemyRows).LastOrDefault(i => IsSet(EnemyKey(i)));
        return new HudRegions
        {
            Souls = ToRect(Get(SoulsKey)),
            Timer = ToRect(Get(TimerKey)),
            EnemySouls = Enumerable.Range(1, lastEnemy).Select(i => ToRect(Get(EnemyKey(i)))).ToList(),
        };
    }

    public static HudRegion? FromRect(HudRect? r) => r is null || r.Width <= 0 || r.Height <= 0 ? null : new HudRegion(r.X, r.Y, r.Width, r.Height);
    public static HudRect? ToRect(HudRegion? r) => r is null ? null : new HudRect(r.X, r.Y, r.Width, r.Height);

    private void Put(string key, HudRect? rect)
    {
        if (FromRect(rect) is { } box) _boxes[key] = box;
    }
}
