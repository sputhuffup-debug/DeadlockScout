using DeadlockScout.Core.Abstractions;

namespace DeadlockScout.Core.Caching;

/// <summary>
/// Default patch key: the ISO week-ish bucket (7-day periods counted from 2024-01-01), so every cache keyed on it rolls
/// over weekly. A fixed key from settings wins; a client-version provider can replace this once the assets API exposes one.
/// </summary>
public sealed class DatePatchKeyProvider : IPatchKeyProvider
{
    private static readonly DateOnly Epoch = new(2024, 1, 1);
    private readonly IClock _clock;
    private readonly string? _fixed;

    public DatePatchKeyProvider(IClock clock, string? fixedKey = null)
    {
        _clock = clock;
        _fixed = string.IsNullOrWhiteSpace(fixedKey) || fixedKey == "auto" ? null : fixedKey;
    }

    public static string ForDate(DateOnly date)
    {
        int days = date.DayNumber - Epoch.DayNumber;
        return $"w{Math.Floor(days / 7.0):0}";
    }

    public ValueTask<string> GetAsync(CancellationToken ct)
        => ValueTask.FromResult(_fixed ?? ForDate(DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime)));
}
