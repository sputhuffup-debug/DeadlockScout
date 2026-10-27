using DeadlockScout.Core.Models;
using DeadlockScout.Core.Settings;

namespace DeadlockScout.Core.Scouting;

/// <summary>The flag rules from the design doc, as pure functions so they can be unit tested.</summary>
public static class ScoutRules
{
    /// <summary>Median badge of the players whose rank the feed exposed, or null.</summary>
    public static int? LobbyMedianRank(IEnumerable<RosterPlayer> players)
    {
        var ranks = players.Select(p => p.Rank).Where(r => r.HasValue).Select(r => r!.Value).OrderBy(r => r).ToList();
        if (ranks.Count == 0) return null;
        int mid = ranks.Count / 2;
        return ranks.Count % 2 == 1 ? ranks[mid] : (ranks[mid - 1] + ranks[mid]) / 2;
    }

    public static ScoutFlags Compute(
        FlagSettings cfg,
        RosterPlayer player,
        bool hasData,
        int gamesOnHero,
        double winRateOnHero,
        int totalGames,
        int? lobbyMedianRank,
        int? metBefore)
    {
        var flags = ScoutFlags.None;
        if (!hasData) return ScoutFlags.Private;
        if (totalGames < cfg.UnknownBelowGames) flags |= ScoutFlags.Unknown;
        if (gamesOnHero < cfg.NewOnHeroMaxGames) flags |= ScoutFlags.NewOnHero;

        bool fewGamesHighRank = totalGames < cfg.SmurfMaxTotalGames
                                && player.Rank is { } r && lobbyMedianRank is { } m && r > m;
        bool hotWinRate = gamesOnHero >= cfg.SmurfMinGamesForWinRate && winRateOnHero > cfg.SmurfMinWinRate;
        if (fewGamesHighRank || hotWinRate) flags |= ScoutFlags.SuspectedSmurf;

        if (player.PartyId is > 0) flags |= ScoutFlags.Stack;
        if (metBefore is > 0) flags |= ScoutFlags.MetBefore;
        return flags;
    }

    /// <summary>The single short label the overlay shows, most important first.</summary>
    public static string? Label(ScoutFlags flags)
    {
        if (flags.HasFlag(ScoutFlags.Private)) return "private";
        if (flags.HasFlag(ScoutFlags.SuspectedSmurf)) return "Smurf?";
        if (flags.HasFlag(ScoutFlags.Unknown)) return "unknown";
        if (flags.HasFlag(ScoutFlags.NewOnHero)) return "new on hero";
        if (flags.HasFlag(ScoutFlags.Stack)) return "stack";
        if (flags.HasFlag(ScoutFlags.MetBefore)) return "met before";
        return null;
    }
}
