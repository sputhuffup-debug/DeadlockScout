namespace DeadlockScout.Core.History;

/// <summary>
/// The moments you marked during matches (0.6, the mark hotkey), by match id, in game seconds: the match history's soul
/// timeline shows each one. Kept in local app data only, never sent anywhere. Pure, so tested; the app saves it.
/// </summary>
public sealed record MomentMarks
{
    /// <summary>Two presses closer than this are one mark.</summary>
    public const int SameMarkSeconds = 5;
    public const int MaxPerMatch = 50;
    /// <summary>Matches kept; the oldest go first.</summary>
    public const int MaxMatches = 200;

    /// <summary>Match id → marked game seconds, in order; the dictionary keeps insertion order for trimming.</summary>
    public Dictionary<long, List<int>> Matches { get; init; } = new();

    /// <summary>The marks with one more at <paramref name="at"/>; unchanged when the match id is unknown or one is already within 5 s.</summary>
    public MomentMarks With(long matchId, TimeSpan at)
    {
        if (matchId <= 0 || at < TimeSpan.Zero) return this;
        int s = (int)Math.Round(at.TotalSeconds);
        var marks = Matches.TryGetValue(matchId, out var list) ? new List<int>(list) : new List<int>();
        if (marks.Any(m => Math.Abs(m - s) < SameMarkSeconds) || marks.Count >= MaxPerMatch) return this;
        marks.Add(s);
        marks.Sort();
        var all = Matches.Where(kv => kv.Key != matchId).ToList();
        all.Add(new(matchId, marks));
        return new MomentMarks { Matches = all.Skip(Math.Max(0, all.Count - MaxMatches)).ToDictionary(kv => kv.Key, kv => kv.Value) };
    }

    public IReadOnlyList<TimeSpan> For(long matchId)
        => Matches.TryGetValue(matchId, out var list) ? list.Select(s => TimeSpan.FromSeconds(s)).ToList() : Array.Empty<TimeSpan>();
}
