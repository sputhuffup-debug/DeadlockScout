using System.Text;

namespace DeadlockScout.Core.Settings;

/// <summary>
/// Your own notes per enemy hero ("Haze: buy Return Fire before 8:00"), kept in settings.json under
/// <see cref="ScoutSettings.MatchupNotes"/> by hero id, written in the match history and shown on the overlay's lane card
/// when you face that hero. Local only: never sent anywhere. Pure, so tested.
/// </summary>
public static class MatchupNotes
{
    /// <summary>A note is one line on the lane card; longer text is cut here.</summary>
    public const int MaxLength = 160;

    /// <summary>At most this many heroes keep a note; more than the game has, so only a hand-edited file reaches it.</summary>
    public const int MaxHeroes = 200;

    /// <summary>The note as stored: trimmed, line breaks and runs of spaces made one space, cut to <see cref="MaxLength"/>; null when empty.</summary>
    public static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var sb = new StringBuilder(Math.Min(text.Length, MaxLength));
        bool space = false;
        foreach (var c in text.Trim())
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        var s = sb.ToString();
        return s.Length <= MaxLength ? s : s[..MaxLength].TrimEnd();
    }

    /// <summary>Your note on <paramref name="heroId"/>, or null.</summary>
    public static string? For(IReadOnlyDictionary<int, string>? notes, int heroId)
        => notes is not null && heroId > 0 && notes.TryGetValue(heroId, out var n) ? Clean(n) : null;

    /// <summary>A copy with <paramref name="heroId"/>'s note set to <paramref name="text"/>; empty text removes it.</summary>
    public static Dictionary<int, string> With(IReadOnlyDictionary<int, string>? notes, int heroId, string? text)
    {
        var copy = new Dictionary<int, string>();
        foreach (var (hero, note) in notes ?? new Dictionary<int, string>())
            if (hero > 0 && hero != heroId && Clean(note) is { } n) copy[hero] = n;
        if (heroId > 0 && Clean(text) is { } t && (copy.Count < MaxHeroes)) copy[heroId] = t;
        return copy;
    }

    /// <summary>True when both hold the same notes (settings records compare the dictionary by reference).</summary>
    public static bool Same(IReadOnlyDictionary<int, string>? a, IReadOnlyDictionary<int, string>? b)
    {
        a ??= new Dictionary<int, string>();
        b ??= new Dictionary<int, string>();
        return a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
    }
}
