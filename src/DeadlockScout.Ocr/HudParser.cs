using System.Globalization;
using System.Text.RegularExpressions;

namespace DeadlockScout.Ocr;

/// <summary>Turns OCR text into numbers. Pure, so the parse rules are unit tested without a screen.</summary>
public static partial class HudParser
{
    [GeneratedRegex(@"(?<!\d)(\d{1,3}(?:[,.\s]\d{3})+|\d+)(?!\d)")]
    private static partial Regex Number();

    [GeneratedRegex(@"(?<m>\d{1,3})\s*[:;.]\s*(?<s>\d{2})(?!\d)")]
    private static partial Regex Clock();

    /// <summary>"12,480" / "12 480" / "12480" / "l2480" (common OCR slip) → 12480. Null when nothing numeric is there.</summary>
    public static int? ParseSouls(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var fixedText = text.Replace('l', '1').Replace('I', '1').Replace('O', '0').Replace('o', '0').Replace('S', '5').Replace('B', '8');
        // Letters in labels ("Souls:") turn into stray digits after the slip fixes, so take the match with the most digits.
        var matches = Number().Matches(fixedText);
        if (matches.Count == 0) return null;
        var best = matches.Cast<Match>().Select(m => new string(m.Value.Where(char.IsDigit).ToArray())).OrderByDescending(d => d.Length).First();
        var digits = best;
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= 1_000_000 ? n : null;
    }

    /// <summary>"14:20" → 14 min 20 s. Null when not a clock.</summary>
    public static TimeSpan? ParseClock(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var fixedText = text.Replace('l', '1').Replace('I', '1').Replace('O', '0').Replace('o', '0');
        var m = Clock().Match(fixedText);
        if (!m.Success) return null;
        int min = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        int sec = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
        if (sec >= 60 || min > 180) return null;
        return new TimeSpan(0, min, sec);
    }
}
