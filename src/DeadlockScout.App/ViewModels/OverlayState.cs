using System.Collections.Concurrent;
using DeadlockScout.App.Services;
using DeadlockScout.Core.Benchmarks;
using DeadlockScout.Core.Builds;
using DeadlockScout.Core.Meter;
using DeadlockScout.Core.Models;
using DeadlockScout.Core.PostMatch;

namespace DeadlockScout.App.ViewModels;

/// <summary>
/// Everything the overlay shows, written by <see cref="MatchSession"/> from background threads and read by the 1 Hz
/// overlay timer on the dispatcher through <see cref="Refresh"/>. Collections are only mutated inside Refresh/Set calls
/// that the window invokes on its thread.
/// </summary>
public sealed class OverlayState
{
    public StatusViewModel Status { get; } = new();
    public PaceStripViewModel Pace { get; } = new();
    public ScoutListViewModel Scout { get; } = new();
    public EnemyPaceViewModel EnemyPace { get; } = new();
    public BuildHintViewModel Build { get; } = new();
    /// <summary>Kept across matches: Reset() does not clear it.</summary>
    public PostMatchViewModel LastMatch { get; } = new();

    public SoulRateMeter Meter { get; private set; } = new();
    public volatile PaceCurve? Curve;
    /// <summary>Your own recent games on the hero, for the second pace line; null when there are too few.</summary>
    public volatile PaceCurve? PersonalCurve;
    public readonly ConcurrentDictionary<uint, PaceCurve> EnemyCurves = new();
    public readonly ConcurrentDictionary<uint, NetWorthTick> EnemyTicks = new();
    public TimeSpan LatestGameTime;
    public DateTimeOffset ScoutVisibleUntil = DateTimeOffset.MinValue;

    private Roster? _roster;
    private IReadOnlyList<ScoutRow>? _rows;
    private uint _me;
    private AssetCatalog? _assets;
    private volatile bool _rosterDirty;
    private readonly ConcurrentDictionary<uint, HeroMatchup> _matchups = new(); // lane opponent → your hero's record against theirs
    private volatile BuildTables? _buildTables;
    private volatile AbilityPlan? _abilityPlan;

    public void Reset()
    {
        Meter = new SoulRateMeter(TimeSpan.FromSeconds(Math.Max(30, WindowSeconds)));
        Curve = null;
        PersonalCurve = null;
        _matchups.Clear();
        _buildTables = null;
        _abilityPlan = null;
        Build.HasData = false;
        EnemyCurves.Clear();
        EnemyTicks.Clear();
        LatestGameTime = TimeSpan.Zero;
        _roster = null; _rows = null;
        _rosterDirty = true;
        Pace.HasData = false;
        Pace.Line = "";
        Pace.AsOf = "";
    }

    public int WindowSeconds { get; set; } = 120;

    public void SetRoster(Roster roster, IReadOnlyList<ScoutRow> rows, uint me, AssetCatalog assets)
    {
        _roster = roster; _rows = rows; _me = me; _assets = assets;
        _rosterDirty = true;
    }

    /// <summary>Formats a finished match's report for the main window. Safe from any thread (scalar bindings only).</summary>
    public void SetLastMatch(PostMatchReport r, AssetCatalog assets)
    {
        var hero = assets.HeroName(r.HeroId);
        LastMatch.Title = $"{hero} · {(r.Won ? "Won" : "Lost")} · {Format.Clock(r.Duration)} · {r.Kills}/{r.Deaths}/{r.Assists} · " +
                          $"{r.NetWorth:N0} souls ({r.SoulsPerMinute:0}/min) · {Format.Ordinal(r.TeamNetWorthRank)} of {r.TeamSize} on team";
        LastMatch.Pace = r.Pace.Count == 0 ? "" : r.BenchmarkProvider is null
            ? "Pace: no benchmark for this hero yet"
            : "Pace at " + string.Join(" · ", r.Pace.Select(p =>
                $"{p.Minute}:00 {(p.Delta is { } d ? Format.Signed(d) : "–")}{(p.Percentile is { } pct ? $" ({Format.Ordinal((int)Math.Round(pct))} pct)" : "")}"));
        LastMatch.Lane = r.Lane is { } lane
            ? $"Lane at {lane.Minute}:00: {Format.SignedCount(lane.Difference)} souls against {string.Join(" & ", lane.OpponentHeroIds.Select(assets.HeroName))}"
            : "";
        LastMatch.Footer = $"Match {r.MatchId} · {r.StartTime.ToLocalTime():ddd HH:mm}" +
                           (r.BenchmarkProvider is { } prov ? $" · benchmark: {prov} ({r.BenchmarkSampleSize} matches, badges {r.Bracket})" : "");
        LastMatch.Won = r.Won;
        LastMatch.HasData = true;
    }

    /// <summary>The hero's build tables; the ability plan is reduced once here, the item hint at every refresh.</summary>
    public void SetBuild(BuildTables tables)
    {
        _abilityPlan = BuildHints.Plan(tables.Abilities);
        _buildTables = tables;
    }

    public void SetMatchup(uint opponentAccountId, HeroMatchup matchup)
    {
        _matchups[opponentAccountId] = matchup;
        _rosterDirty = true;
    }

    public void UpdateEnemy(NetWorthTick tick, RosterPlayer player, AssetCatalog assets)
    {
        _assets ??= assets;
        EnemyTicks[tick.AccountId] = tick;
    }

    /// <summary>Called on the UI thread once a second (and on a foreground change): recomputes derived values from samples and curves (never stored).</summary>
    public void Refresh()
    {
        RefreshPace();
        RefreshBuild();
        if (_rosterDirty) { _rosterDirty = false; RebuildScout(); }
        RefreshEnemyPace();
    }

    private void RefreshPace()
    {
        var reading = Meter.Read();
        if (reading is null) { Pace.HasData = false; return; }
        var hero = Status.HeroName is { Length: > 0 } h ? h : "You";
        var window = Meter.Window;
        if (Curve is { } curve)
        {
            var e = PaceMath.Evaluate(curve, reading);
            Pace.Line = PaceMath.FormatLine(hero, e, window);
            Pace.Delta = e.Delta;
        }
        else
        {
            Pace.Line = $"{hero} · {PaceMath.FormatClock(reading.GameTime)} · {reading.WindowSpm:0} spm ({window.TotalMinutes:0}m) · no benchmark yet";
            Pace.Delta = null;
        }
        if (PersonalCurve is { } personal && PaceMath.Evaluate(personal, reading).Delta is { } mine)
        {
            Pace.PersonalLine = $"vs your last {personal.SampleSize} on {hero}: {Format.Signed(mine)}";
            Pace.PersonalDelta = mine;
        }
        else
        {
            Pace.PersonalLine = "";
            Pace.PersonalDelta = null;
        }
        Pace.AsOf = $"as of {PaceMath.FormatClock(reading.GameTime)}";
        Pace.HasData = true;
    }

    private void RefreshBuild()
    {
        var tables = _buildTables;
        var reading = Meter.Read();
        if (tables is null || reading is null) { Build.HasData = false; return; }
        var hero = Status.HeroName is { Length: > 0 } h ? h : "your hero";

        var buys = BuildHints.CommonBuys(tables.Items, reading.Souls);
        if (buys.Count > 0)
        {
            int bucket = buys[0].Bucket;
            Build.Title = $"Bought at {Format.Count(bucket)}–{Format.Count(bucket + BuildHints.BucketWidth)} souls on {hero}";
            Build.Items = string.Join(" · ", buys.Select(b => $"{ItemName(b.ItemId)} {Format.Pct(b.WinRate)}"));
        }
        else
        {
            Build.Title = $"Build on {hero}";
            Build.Items = "";
        }

        if (_abilityPlan is { } plan)
        {
            var parts = new List<string>();
            if (plan.NextAfter(reading.GameTime) is { } next)
                parts.Add($"Next: {ItemName(next.AbilityId)} {next.Rank} (~{Format.Clock(next.TypicalTime)})");
            if (plan.MaxFirst is { } max) parts.Add($"max {ItemName(max)} first");
            Build.Abilities = string.Join(" · ", parts);
        }
        else Build.Abilities = "";

        Build.HasData = Build.Items.Length > 0 || Build.Abilities.Length > 0;
    }

    private string ItemName(long id) => _assets?.ItemName(id) ?? $"Item {id}";

    private void RebuildScout()
    {
        Scout.Amber.Clear(); Scout.Sapphire.Clear(); Scout.LaneOpponents.Clear();
        if (_roster is null || _rows is null) return;
        var me = _roster.Find(_me);
        foreach (var r in _rows)
        {
            var vm = new ScoutRowViewModel
            {
                AccountId = r.Player.AccountId,
                Name = r.Player.Name,
                Hero = _assets?.HeroName(r.Player.HeroId) ?? r.Player.HeroId.ToString(),
                Team = r.Player.Team,
                IsMe = r.Player.AccountId == _me,
                GamesOnHero = r.Flags.HasFlag(ScoutFlags.Private) ? "private" : $"{r.GamesOnHero} on hero / {r.TotalGames}",
                WinRate = r.GamesOnHero > 0 ? Format.Pct(r.WinRateOnHero) : "–",
                Rank = Format.Rank(r.Rank),
                Met = r.MetRecord is { } m ? $"{m.Wins}–{m.Losses}" : "",
                Flag = r.Flag ?? "",
                Lane = r.Player.Lane is { } l ? $"L{l}" : "",
                Matchup = _matchups.TryGetValue(r.Player.AccountId, out var matchup) ? Format.Matchup(matchup) : "",
            };
            (r.Player.Team == Teams.Sapphire ? Scout.Sapphire : Scout.Amber).Add(vm);
            if (me is not null && r.Player.Team != me.Team && r.Player.Lane is not null && r.Player.Lane == me.Lane) Scout.LaneOpponents.Add(vm);
        }
        Scout.LaneTitle = me?.Lane is { } lane ? $"Lane {lane} opponents" : "Lane opponents";
        var amber = _roster.Team(Teams.Amber).Select(p => p.Rank).Where(r => r is > 0).Select(r => r!.Value).ToList();
        var sapphire = _roster.Team(Teams.Sapphire).Select(p => p.Rank).Where(r => r is > 0).Select(r => r!.Value).ToList();
        Scout.RankSpread = $"Amber {Spread(amber)} · Sapphire {Spread(sapphire)}";
    }

    private static string Spread(List<int> badges) => badges.Count == 0 ? "–" : $"{Format.Rank(badges.Min())} – {Format.Rank(badges.Max())}";

    private void RefreshEnemyPace()
    {
        if (_roster is null) return;
        foreach (var (accountId, tick) in EnemyTicks)
        {
            var row = EnemyPace.Rows.FirstOrDefault(r => r.AccountId == accountId);
            if (row is null)
            {
                var p = _roster.Find(accountId);
                if (p is null) continue;
                row = new EnemyPaceRowViewModel { AccountId = accountId, Name = p.Name, Hero = _assets?.HeroName(p.HeroId) ?? p.HeroId.ToString() };
                EnemyPace.Rows.Add(row);
            }
            if (EnemyCurves.TryGetValue(accountId, out var curve) && curve.Points.Count > 1)
            {
                double expected = PaceMath.NetWorthAt(curve, tick.GameTime);
                row.Lead = expected > 0 ? tick.NetWorth / expected - 1 : 0;
                row.Line = $"{row.Hero} · {tick.NetWorth:N0} · {Format.Signed(row.Lead)} vs curve";
            }
            else
            {
                row.Lead = 0;
                row.Line = $"{row.Hero} · {tick.NetWorth:N0}";
            }
        }
        // sort by lead, descending
        var sorted = EnemyPace.Rows.OrderByDescending(r => r.Lead).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int current = EnemyPace.Rows.IndexOf(sorted[i]);
            if (current != i) EnemyPace.Rows.Move(current, i);
        }
    }
}
