namespace DeadlockScout.Core.Models;

/// <summary>Flags the scout list can show for one player.</summary>
[Flags]
public enum ScoutFlags
{
    None = 0,
    /// <summary>Fewer than N games on the hero.</summary>
    NewOnHero = 1,
    /// <summary>Few total games with a rank above the lobby median, or a very high hero win rate on a small sample.</summary>
    SuspectedSmurf = 2,
    /// <summary>Party size above one.</summary>
    Stack = 4,
    /// <summary>Played with or against before.</summary>
    MetBefore = 8,
    /// <summary>Fewer than 3 recorded matches: numbers are not meaningful.</summary>
    Unknown = 16,
    /// <summary>The API has no data for this account (private profile).</summary>
    Private = 32,
}

/// <summary>What the scout list shows for one roster player.</summary>
/// <param name="GamesOnHero">Recorded matches on the hero they are playing now.</param>
/// <param name="WinRateOnHero">0..1 over <paramref name="GamesOnHero"/>; 0 when none.</param>
/// <param name="TotalGames">Recorded matches across all heroes.</param>
/// <param name="Rank">Badge from the feed when known.</param>
/// <param name="MetBefore">Matches you shared with them (either side) when enemy/mate stats are loaded, else null.</param>
/// <param name="Flag">Short label for the overlay, e.g. "Smurf?", or null.</param>
public sealed record ScoutRow(
    RosterPlayer Player,
    int GamesOnHero,
    double WinRateOnHero,
    int TotalGames,
    int? Rank,
    int? MetBefore,
    string? Flag)
{
    public ScoutFlags Flags { get; init; }
    /// <summary>Your W-L against or with them, when known.</summary>
    public (int Wins, int Losses)? MetRecord { get; init; }
}

/// <summary>Per-hero totals for one account, as returned by players/hero-stats.</summary>
public sealed record PlayerHeroStats(uint AccountId, int HeroId, int Matches, int Wins, int Kills, int Deaths, int Assists)
{
    public double WinRate => Matches == 0 ? 0 : (double)Wins / Matches;
}

/// <summary>Your history with one other account.</summary>
public sealed record SharedHistory(uint AccountId, int Matches, int Wins, int Losses, bool AsEnemy);
