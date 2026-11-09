using System.Text.RegularExpressions;

namespace DeadlockScout.Core.History;

/// <summary>
/// The match history's "Add a match by id" box (0.7): reads a match id out of what was pasted, the bare number from the
/// in-game match details or a tracker's link to the match. Pure, so tested.
/// </summary>
public static partial class PastedMatchId
{
    /// <summary>Match ids are well past this; a smaller number is a typo or something else (a score, a level).</summary>
    public const long Min = 1_000_000;
    public const long Max = 99_999_999_999;

    // The last run of 7 to 11 digits that is not part of a longer number (a SteamID64 has 17).
    [GeneratedRegex(@"(?<!\d)\d{7,11}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();

    /// <summary>The match id in <paramref name="text"/>, or null when there is none (or more than one different one).</summary>
    public static long? Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var ids = Digits().Matches(text).Select(m => long.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture))
            .Where(v => v is >= Min and <= Max).Distinct().ToList();
        return ids.Count == 1 ? ids[0] : null;
    }
}
