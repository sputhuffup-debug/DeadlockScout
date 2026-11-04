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
    [GeneratedRegex(@"(?<n>[0-9OoIl][0-9OoIl,.]*)\s*[^0-9A-Za-z]{0,3}\s*HAUN", RegexOptions.IgnoreCase)]
    private static partial Regex Haunted();

    /// <summary>
    /// The haunted souls line under the souls count ("1,051 HAUNTED SOULS"): the number before the word "HAUNTED", or
    /// null when the line does not say HAUNTED (then it is not that line, whatever digits it holds).
    /// </summary>
    public static int? ParseHaunted(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || Haunted().Match(text) is not { Success: true } m) return null;
        var digits = new string(m.Groups["n"].Value.Replace('l', '1').Replace('I', '1').Replace('O', '0').Replace('o', '0').Where(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= 1_000_000 ? n : null;
    }

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

    /// <summary>
    /// What the box read besides the souls number it was parsed to ("Souls: 768" → "Souls:", "768 9" → "9"): empty
    /// when the box holds the number alone. A non-empty rest means the box takes in more than the number, which is how
    /// extra digits get read.
    /// </summary>
    public static string SoulsLeftover(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var fixedText = text.Replace('l', '1').Replace('I', '1').Replace('O', '0').Replace('o', '0').Replace('S', '5').Replace('B', '8');
        var best = Number().Matches(fixedText).Cast<Match>().OrderByDescending(m => m.Value.Count(char.IsDigit)).FirstOrDefault();
        return Rest(text, best);
    }

    /// <summary>What the box read besides the clock ("14:20 x" → "x"); empty when it holds the clock alone.</summary>
    public static string ClockLeftover(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var fixedText = text.Replace('l', '1').Replace('I', '1').Replace('O', '0').Replace('o', '0');
        var m = Clock().Match(fixedText);
        return Rest(text, m.Success ? m : null);
    }

    private static string Rest(string text, Match? token)
    {
        // The slip fixes keep every character in place, so the token's span in the fixed text is its span in the original.
        var rest = token is null ? text : text.Remove(token.Index, token.Length);
        return string.Join(" ", rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
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
