using DeadlockScout.Core.Models;

namespace DeadlockScout.Core.Meter;

/// <summary>Both teams' souls at one game time, and the line the overlay shows for it.</summary>
/// <param name="Gap">Your team's souls minus the other's (Amber minus Sapphire when spectating).</param>
/// <param name="Change">How the gap moved over the trend window, in the same direction; null before there is a window.</param>
public sealed record TeamLeadView(TimeSpan GameTime, int Amber, int Sapphire, int Gap, int? Change, string Text);

/// <summary>
/// Both teams' total souls from the broadcast, and how the gap between them moved over the last
/// <see cref="TrendWindow"/>, so a fight or an objective that swung the game shows ("Enemy +4.2k, growing"). Every
/// player's net worth comes from the broadcast already (the same ticks as the enemy pace board); this only adds them up.
/// Totals are kept every <see cref="Step"/> of game time. Nothing is shown until every player of the roster has been
/// seen, so a total is never a partial one. Pure, so tested; a computed value, never stored.
/// </summary>
public sealed class TeamSoulsLead
{
    public static readonly TimeSpan TrendWindow = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(5);
    /// <summary>A gap that moved by less than this over the window is "steady".</summary>
    public const int SteadyWithin = 500;

    private readonly IReadOnlyDictionary<uint, int> _teamOf;
    private readonly Dictionary<uint, int> _netWorth = new();
    private readonly List<(TimeSpan At, int Amber, int Sapphire)> _history = new();
    private readonly object _gate = new();
    private TimeSpan _latest = TimeSpan.MinValue;

    /// <param name="players">Every player of the match by account id, with their team.</param>
    public TeamSoulsLead(IEnumerable<(uint AccountId, int Team)> players)
    {
        _teamOf = players.Where(p => p.AccountId != 0).GroupBy(p => p.AccountId).ToDictionary(g => g.Key, g => g.First().Team);
    }

    /// <summary>One player's broadcast net worth at a game time.</summary>
    public void Observe(NetWorthTick tick)
    {
        lock (_gate)
        {
            if (!_teamOf.ContainsKey(tick.AccountId)) return;
            _netWorth[tick.AccountId] = tick.NetWorth;
            if (tick.GameTime > _latest) _latest = tick.GameTime;
            if (_netWorth.Count < _teamOf.Count) return;
            var (amber, sapphire) = Totals();
            if (_history.Count > 0 && _latest - _history[^1].At < Step) _history[^1] = (_history[^1].At, amber, sapphire);
            else _history.Add((_latest, amber, sapphire));
            // Keep a little more than the window, so its far end can always be read.
            while (_history.Count > 2 && _latest - _history[1].At >= TrendWindow) _history.RemoveAt(0);
        }
    }

    private (int Amber, int Sapphire) Totals()
    {
        int amber = 0, sapphire = 0;
        foreach (var (id, nw) in _netWorth)
            if (_teamOf[id] == Teams.Amber) amber += nw; else sapphire += nw;
        return (amber, sapphire);
    }

    /// <summary>
    /// The line for <paramref name="myTeam"/> (0 when spectating): "Your team +4.2k, growing", "Enemy +1.1k, closing",
    /// "Even, steady", or "Amber +4.2k" spectating. Null until every player has been seen.
    /// </summary>
    public TeamLeadView? View(int myTeam)
    {
        lock (_gate)
        {
            if (_history.Count == 0) return null;
            var now = _history[^1];
            int Gap((TimeSpan At, int Amber, int Sapphire) h) => myTeam == Teams.Sapphire ? h.Sapphire - h.Amber : h.Amber - h.Sapphire;
            int gap = Gap(now);
            int? change = null;
            if (now.At - _history[0].At >= TrendWindow - Step) change = gap - Gap(_history[0]);

            string who = gap == 0 ? "Even"
                : myTeam is Teams.Amber or Teams.Sapphire ? (gap > 0 ? "Your team" : "Enemy")
                : gap > 0 ? "Amber" : "Sapphire";
            string amount = gap == 0 ? "" : $" +{Short(Math.Abs(gap))}";
            string trend = change is not { } c ? ""
                : Math.Abs(c) < SteadyWithin ? ", steady"
                : gap == 0 ? ""
                : (c > 0) == (gap > 0) ? ", growing" : ", closing";
            return new TeamLeadView(now.At, now.Amber, now.Sapphire, gap, change, who + amount + trend);
        }
    }

    /// <summary>4,213 → "4.2k"; under a thousand as is.</summary>
    public static string Short(int souls) => souls >= 1000 ? (souls / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "k" : souls.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
