namespace DeadlockScout.Core.Laning;

/// <summary>
/// One broadcast snapshot for the camp timers (Direct feed only): the alive neutral creatures, and where each player's
/// hero stands (account id to map position), so the panel can show the cleared camps nearest you.
/// </summary>
public sealed record CampSample(TimeSpan GameTime, IReadOnlyList<NeutralSighting> Alive, IReadOnlyDictionary<uint, (float X, float Y)> Players);
