using System.Globalization;

namespace DeadlockScout.Core.Settings;

/// <summary>
/// The settings window's text fields, read and written the way a player types them: game-clock times as "2:00" (or plain
/// seconds), whole numbers held inside a range. A value that does not read is rejected, and the field shows the stored
/// one again, so settings.json never holds a half-typed number.
/// </summary>
public static class SettingsFields
{
    /// <summary>"2:00" → 120, "1:25" → 85, "90" → 90; null for anything else, negative or past <paramref name="maxSeconds"/>.</summary>
    public static int? ReadClock(string? text, int maxSeconds = 3 * 3600)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        int seconds;
        int colon = t.IndexOf(':');
        if (colon < 0)
        {
            if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out seconds)) return null;
        }
        else
        {
            if (!int.TryParse(t[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
                || !int.TryParse(t[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var s)
                || t.Length - colon - 1 != 2 || s > 59) return null;
            seconds = m * 60 + s;
        }
        return seconds <= maxSeconds ? seconds : null;
    }

    /// <summary>120 → "2:00".</summary>
    public static string Clock(int seconds) => $"{seconds / 60}:{seconds % 60:00}";

    /// <summary>A whole number inside [<paramref name="min"/>, <paramref name="max"/>], or null.</summary>
    public static int? ReadInt(string? text, int min, int max)
        => int.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max ? v : null;

    /// <summary>The goal rank as settings.json holds it: "next" for the tier above yours, else a tier name.</summary>
    public const string NextRank = "next";
}
