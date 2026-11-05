using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.History;

/// <summary>
/// What the match history shows: every game, or only one hero's, ranked games, or the games with one friend (the match
/// ids the teammate record has for them). A view over the page already loaded; nothing is fetched again.
/// </summary>
public sealed record HistoryFilter(int? HeroId = null, bool RankedOnly = false, IReadOnlySet<long>? WithMatches = null)
{
    public static readonly HistoryFilter All = new();
    public bool IsAll => HeroId is null && !RankedOnly && WithMatches is null;

    public bool Keeps(MatchHistoryEntry e)
        => (HeroId is not { } hero || e.HeroId == hero)
           && (!RankedOnly || e.RankedBadge is > 0)
           && (WithMatches is not { } ids || ids.Contains(e.MatchId));

    /// <summary>
    /// The page with only the kept games: its cards, the summary over them (win rate, KDA, main heroes, last results)
    /// and the trend rows. The rank line stays the account's (a filter does not change your rank); games still being
    /// processed show only without a filter, since the API has not said which hero or friend they had.
    /// </summary>
    public MatchHistoryPage Apply(MatchHistoryPage page)
    {
        if (IsAll) return page;
        var games = page.Games.Where(g => Keeps(g.Entry)).ToList();
        return page with
        {
            Summary = HistoryMath.Summarize(games.Select(g => g.Entry).ToList()),
            Games = games,
            History = page.History?.Where(Keeps).ToList(),
            Processing = Array.Empty<PlayedMatch>(),
        };
    }
}
